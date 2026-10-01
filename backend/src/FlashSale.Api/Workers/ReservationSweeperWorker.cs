using FlashSale.Domain.Enums;
using FlashSale.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FlashSale.Api.Workers;

/// <summary>
/// Reclaims abandoned seat holds.
/// <para>
/// A buyer who reaches checkout but abandons it leaves a seat stuck in
/// <c>Reserved</c> forever. This worker periodically returns any hold whose
/// <c>ReservedUntilUtc</c> has passed to <c>Available</c> so the inventory does not leak.
/// Without it, a flash sale would slowly sell out against phantom holds.
/// </para>
/// Runs on a slower cadence than the queue worker because it is not latency-sensitive.
/// </summary>
public sealed class ReservationSweeperWorker : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReservationSweeperWorker> _logger;

    public ReservationSweeperWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<ReservationSweeperWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Reservation sweeper starting.");

        using var timer = new PeriodicTimer(SweepInterval);

        while (await SafeWaitAsync(timer, stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reservation sweep failed; the next tick will retry.");
            }
        }
    }

    /// <summary>Awaits the timer, translating cancellation into a clean loop exit.</summary>
    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken token)
    {
        try
        {
            return await timer.WaitForNextTickAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Bounded batch: never lock the Tickets table for long under a live sale.
        var expired = await dbContext.Tickets
            .Where(t => t.Status == TicketStatus.Reserved.ToString()
                        && t.ReservedUntilUtc != null
                        && t.ReservedUntilUtc <= now)
            .Take(500)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (expired.Count == 0)
        {
            return;
        }

        foreach (var ticket in expired)
        {
            ticket.Status = TicketStatus.Available.ToString();
            ticket.AssignedUserId = null;
            ticket.ReservedUntilUtc = null;
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Reclaimed {Count} expired seat holds.", expired.Count);
    }
}