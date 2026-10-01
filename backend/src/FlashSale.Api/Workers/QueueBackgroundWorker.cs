using FlashSale.Api.Contracts;
using FlashSale.Api.Hubs;
using FlashSale.Domain.Entities;
using FlashSale.Domain.Enums;
using FlashSale.Infrastructure.Identity;
using FlashSale.Infrastructure.Persistence;
using FlashSale.Infrastructure.Queueing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FlashSale.Api.Workers;

/// <summary>
/// Gatekeeper for the virtual waiting room.
/// <para>
/// Every <see cref="QueueOptions.TickSeconds"/> seconds it wakes up, takes the buyers at
/// the front of each active queue, marks them <see cref="QueueStatus.Admitted"/>, mints a
/// short-lived checkout pass for each, and pushes a <c>"QueueStatusChanged"</c> event to
/// those specific clients through <see cref="IHubContext{THub}"/>. Everyone still waiting
/// also receives a refreshed position so the UI animates forward.
/// </para>
/// Design notes:
/// <list type="bullet">
///   <item><description>Pushes go to the <c>user:{id}</c> group, so only admitted buyers are disturbed.</description></item>
///   <item><description>The batch size is clamped by <see cref="QueueOptions.MaxAdmissionsPerTick"/>,
///   a circuit breaker against a misconfigured sale flooding checkout.</description></item>
///   <item><description>Every tick is isolated per event: one failure logs and the loop continues.</description></item>
///   <item><description>A fresh <c>DbContext</c> per tick via <c>IServiceScopeFactory</c> —
///   a background service is a singleton, so injecting a scoped context directly is illegal.</description></item>
/// </list>
/// </summary>
public sealed class QueueBackgroundWorker : BackgroundService
{
    private readonly IQueueStore _queueStore;
    private readonly IHubContext<QueueHub> _hubContext;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly JwtProvider _jwtProvider;
    private readonly QueueOptions _options;
    private readonly ILogger<QueueBackgroundWorker> _logger;

    public QueueBackgroundWorker(
        IQueueStore queueStore,
        IHubContext<QueueHub> hubContext,
        IServiceScopeFactory scopeFactory,
        JwtProvider jwtProvider,
        IOptions<QueueOptions> options,
        ILogger<QueueBackgroundWorker> logger)
    {
        _queueStore = queueStore;
        _hubContext = hubContext;
        _scopeFactory = scopeFactory;
        _jwtProvider = jwtProvider;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Survive a transient database or hub outage at boot instead of crashing the host.
        var tick = TimeSpan.FromSeconds(Math.Max(1, _options.TickSeconds));
        _logger.LogInformation("Queue worker starting with a {Tick} cadence.", tick);

        using var timer = new PeriodicTimer(tick);

        // Run one sweep immediately so a freshly started API does not idle for a full tick.
        await SafeSweepAsync(stoppingToken).ConfigureAwait(false);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await SafeSweepAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on graceful shutdown.
            _logger.LogInformation("Queue worker cancelled.");
        }
    }

    /// <summary>Wraps one sweep so a single failure never kills the loop.</summary>
    private async Task SafeSweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SweepAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Queue sweep failed; the next tick will retry.");
        }
    }
    /// <summary>One full pass over every active queue.</summary>
    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        var activeEventIds = _queueStore.ActiveEventIds();
        if (activeEventIds.Count == 0)
        {
            return;
        }

        foreach (var eventId in activeEventIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await ProcessQueueAsync(eventId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Isolate per-event failures: one bad sale must not stall every other queue.
                _logger.LogError(ex, "Failed to process the queue for event {EventId}.", eventId);
            }
        }
    }

    /// <summary>
    /// Admits the next batch for one sale: dequeue, persist, mint passes, push.
    /// </summary>
    private async Task ProcessQueueAsync(Guid eventId, CancellationToken cancellationToken)
    {
        var totalWaiting = _queueStore.Count(eventId);
        if (totalWaiting == 0)
        {
            return;
        }

        // Determine batch size from the sale's own configuration, clamped globally.
        var (admitBatchSize, checkoutWindowSeconds) = await LoadSaleSettingsAsync(eventId, cancellationToken)
            .ConfigureAwait(false);

        var batchLimit = Math.Clamp(
            Math.Min(admitBatchSize, _options.MaxAdmissionsPerTick),
            1,
            _options.MaxAdmissionsPerTick);

        // Atomic swap: removes the batch from the live queue in one step.
        var admitted = _queueStore.DequeueBatch(eventId, batchLimit);
        if (admitted.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        // Scope per tick: the worker is a singleton and cannot hold a scoped DbContext.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var admittedIds = admitted.Select(p => p.UserId).ToList();

        var entries = await dbContext.QueueEntries
            .Where(q => q.EventId == eventId && admittedIds.Contains(q.UserId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var entriesByUser = entries.ToDictionary(e => e.UserId);

        var users = await dbContext.Users
            .Where(u => admittedIds.Contains(u.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var usersById = users.ToDictionary(u => u.Id);

        // Admissions queued for delivery, sent only after the database write commits.
        var admittedPushes = new List<(Guid UserId, QueueStatusChanged Payload)>();

        foreach (var participant in admitted)
        {
            var passExpiry = now.AddSeconds(
                checkoutWindowSeconds > 0
                    ? checkoutWindowSeconds
                    : _options.DefaultCheckoutWindowSeconds);

            // Mint the temporary pass that unlocks POST /api/bookings/reserve.
            var pass = _jwtProvider.CreateCheckoutPassToken(participant.UserId, eventId, passExpiry);
            if (!pass.Succeeded)
            {
                _logger.LogWarning(
                    "Could not mint a checkout pass for user {UserId}; re-queuing them.", participant.UserId);

                // Never silently drop a buyer: put them back at the front.
                _queueStore.Enqueue(eventId, participant.UserId, participant.ConnectionId, now);
                continue;
            }

            if (entriesByUser.TryGetValue(participant.UserId, out var entry))
            {
                entry.Status = QueueStatus.Admitted.ToString();
                entry.AdmittedAtUtc = now;
                entry.CheckoutPassExpiresAtUtc = pass.ExpiresAtUtc;
            }

            if (usersById.TryGetValue(participant.UserId, out var user))
            {
                // Persist the admission so the next session token mints CheckoutAllowed = true.
                user.IsCheckoutAllowed = true;
            }

            // Targeted push, deferred until AFTER the database write below.
            //
            // Order matters. The client reacts to this push by immediately calling
            // /api/auth/refresh, which reads Users.IsCheckoutAllowed from the database.
            // If we pushed before SaveChangesAsync committed, the refresh could read
            // the pre-admission value (false), the guard would bounce the buyer back to
            // the waiting room, and they would never reach checkout. So: persist first,
            // then notify.
            admittedPushes.Add((
                participant.UserId,
                new QueueStatusChanged(
                    eventId,
                    StatusAdmitted,
                    Position: null,
                    TotalWaiting: _queueStore.Count(eventId),
                    CheckoutPassToken: pass.Token,
                    PassExpiresAtUtc: pass.ExpiresAtUtc,
                    ServerUtc: now)));
        }

        // Commit the admission BEFORE telling anyone about it.
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Only now is it safe for clients to react.
        foreach (var (userId, payload) in admittedPushes)
        {
            await _hubContext.Clients
                .Group(QueueHub.GroupForUser(userId))
                .SendAsync(QueueHub.QueueStatusChangedMethod, payload, cancellationToken)
                .ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Admitted {AdmittedCount} buyers for event {EventId}; {Remaining} still waiting.",
            admitted.Count,
            eventId,
            _queueStore.Count(eventId));

        await BroadcastPositionsAsync(eventId, now, cancellationToken).ConfigureAwait(false);
    }
    /// <summary>
    /// Pushes a refreshed position to everyone still waiting, so the UI animates
    /// forward as the line shortens. Failure here must never fail the sweep — the
    /// important admission pushes have already been sent.
    /// </summary>
    private async Task BroadcastPositionsAsync(
        Guid eventId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var waiting = _queueStore.Snapshot(eventId);
        if (waiting.Count == 0)
        {
            return;
        }

        try
        {
            // One batched broadcast, then one message per buyer carrying their own rank.
            // SignalR sends are cheap; a per-buyer round-trip would be not.
            await _hubContext.Clients
                .Group(QueueHub.GroupForEvent(eventId))
                .SendAsync(
                    "QueueSnapshot",
                    new QueueSnapshotEvent(eventId, waiting.Count, now),
                    cancellationToken)
                .ConfigureAwait(false);

            foreach (var participant in waiting)
            {
                var position = _queueStore.PositionOf(eventId, participant.UserId);
                if (position is null)
                {
                    continue;
                }

                await _hubContext.Clients
                    .Client(participant.ConnectionId)
                    .SendAsync(
                        QueueHub.QueueStatusChangedMethod,
                        new QueueStatusChanged(
                            eventId,
                            StatusWaiting,
                            position,
                            waiting.Count,
                            CheckoutPassToken: null,
                            PassExpiresAtUtc: null,
                            ServerUtc: now),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not broadcast positions for event {EventId}.", eventId);
        }
    }

    /// <summary>
    /// Reads the sale's admission policy, falling back to safe defaults if the event
    /// has disappeared (deleted mid-sale).
    /// </summary>
    private async Task<(int AdmitBatchSize, int CheckoutWindowSeconds)> LoadSaleSettingsAsync(
        Guid eventId,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var evt = await dbContext.Events
            .AsNoTracking()
            .Where(e => e.Id == eventId)
            .Select(e => new { e.AdmitBatchSize, e.CheckoutWindowSeconds })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return evt is null
            ? (_options.MaxAdmissionsPerTick, _options.DefaultCheckoutWindowSeconds)
            : (evt.AdmitBatchSize, evt.CheckoutWindowSeconds);
    }

    private const string StatusWaiting = "Waiting";

    private const string StatusAdmitted = "Admitted";
}

/// <summary>Broadcast to the whole waiting room so it can render "N people ahead".</summary>
/// <param name="EventId">Sale concerned.</param>
/// <param name="TotalWaiting">Buyers still queued.</param>
/// <param name="ServerUtc">Server clock for drift correction.</param>
public sealed record QueueSnapshotEvent(Guid EventId, int TotalWaiting, DateTimeOffset ServerUtc);