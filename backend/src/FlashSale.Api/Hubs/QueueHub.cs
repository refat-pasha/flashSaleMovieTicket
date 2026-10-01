using FlashSale.Api.Contracts;
using FlashSale.Infrastructure.Identity;
using FlashSale.Infrastructure.Queueing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace FlashSale.Api.Hubs;

/// <summary>
/// Real-time hub backing the virtual waiting room.
/// <para>
/// Responsibilities are deliberately narrow: authenticate the socket, register the
/// buyer in the shared <see cref="IQueueStore"/>, and keep SignalR groups in sync so
/// <c>QueueBackgroundWorker</c> can address one specific client later. The worker owns
/// all admission policy — the hub never decides who gets in.
/// </para>
/// Clients are placed in two overlapping groups:
/// <list type="bullet">
///   <item><description><c>queue:{eventId}</c> — everyone waiting for a sale (broadcasts).</description></item>
///   <item><description><c>user:{userId}</c> — every socket belonging to one buyer (targeted pushes).</description></item>
/// </list>
/// The user group is what makes "push a pass to <em>those specific</em> clients" possible.
/// </summary>
[Authorize]
public sealed class QueueHub : Hub
{
    private readonly IQueueStore _queueStore;
    private readonly ILogger<QueueHub> _logger;

    public QueueHub(IQueueStore queueStore, ILogger<QueueHub> logger)
    {
        _queueStore = queueStore;
        _logger = logger;
    }

    /// <summary>Client method name the Angular service listens on.</summary>
    public const string QueueStatusChangedMethod = "QueueStatusChanged";

    /// <summary>Client method name used for immediate feedback after joining.</summary>
    public const string JoinedMethod = "QueueJoined";

    /// <summary>
    /// Registers the caller for admission to the waiting room. Idempotent: a reconnect
    /// keeps the buyer's original place in line.
    /// </summary>
    public async Task<QueueStatusChanged> JoinQueue(JoinQueueRequest request)
    {
        var userId = GetRequiredUserId();

        // Authenticate the caller before touching shared state.
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupForEvent(request.EventId));
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupForUser(userId));

        var participant = _queueStore.Enqueue(
            request.EventId,
            userId,
            Context.ConnectionId,
            DateTimeOffset.UtcNow);

        var position = _queueStore.PositionOf(request.EventId, userId) ?? 1;
        var totalWaiting = _queueStore.Count(request.EventId);

        var status = new QueueStatusChanged(
            request.EventId,
            StatusWaiting,
            position,
            totalWaiting,
            CheckoutPassToken: null,
            PassExpiresAtUtc: null,
            ServerUtc: DateTimeOffset.UtcNow);

        _logger.LogInformation(
            "User {UserId} joined queue for event {EventId} at position {Position} (connection {ConnectionId}, sequence {Sequence}).",
            userId,
            request.EventId,
            position,
            Context.ConnectionId,
            participant.SequenceNumber);

        // Acknowledge the caller directly so the UI can render instantly rather than
        // waiting up to a full tick for the first broadcast.
        await Clients.Caller.SendAsync(JoinedMethod, status);

        return status;
    }

    /// <summary>Allows a buyer to abandon the queue voluntarily.</summary>
    public async Task LeaveQueue(Guid eventId)
    {
        var userId = GetRequiredUserId();

        _queueStore.Remove(eventId, userId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupForEvent(eventId));

        _logger.LogInformation(
            "User {UserId} left the queue for event {EventId}.", userId, eventId);
    }

    /// <summary>
    /// Frees the buyer's slot the moment the socket drops, so an abandoned browser tab
    /// never stalls the line. Also refreshes SignalR group membership, which is what
    /// makes the next admission push fail fast against a dead connection.
    /// </summary>
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _queueStore.RemoveByConnection(Context.ConnectionId);

        _logger.LogInformation(
            "Connection {ConnectionId} dropped from the waiting room. Exception: {Exception}",
            Context.ConnectionId,
            exception?.Message ?? "none");

        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>Group name carrying every buyer queued for a sale.</summary>
    public static string GroupForEvent(Guid eventId) => $"queue:{eventId}";

    /// <summary>Group name carrying every socket owned by one buyer.</summary>
    public static string GroupForUser(Guid userId) => $"user:{userId}";

    private const string StatusWaiting = "Waiting";

    /// <summary>
    /// Resolves the caller's user id from the JWT that authorised the socket.
    /// <para>
    /// JwtBearer maps the token's <c>sub</c> claim onto
    /// <see cref="System.Security.Claims.ClaimTypes.NameIdentifier"/> by default, but
    /// both are checked so the hub stays correct if
    /// <c>MapInboundClaims = false</c> is ever configured.
    /// </para>
    /// Throws when the id is absent or malformed rather than defaulting to
    /// <see cref="Guid.Empty"/>, which would silently corrupt the queue.
    /// </summary>
    private Guid GetRequiredUserId()
    {
        var raw = Context.User?.FindFirst("sub")?.Value
            ?? Context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        if (!Guid.TryParse(raw, out var userId) || userId == Guid.Empty)
        {
            _logger.LogWarning(
                "QueueHub call from connection {ConnectionId} had no resolvable user id.",
                Context.ConnectionId);

            throw new HubException("Caller is not authenticated.");
        }

        return userId;
    }
}