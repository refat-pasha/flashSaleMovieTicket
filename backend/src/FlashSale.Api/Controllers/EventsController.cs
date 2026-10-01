using System.Security.Claims;
using FlashSale.Api.Contracts;
using FlashSale.Domain.Entities;
using FlashSale.Domain.Enums;
using FlashSale.Infrastructure.Persistence;
using FlashSale.Infrastructure.Queueing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FlashSale.Api.Controllers;

/// <summary>
/// Sale catalogue and waiting-room state. Read-only from the buyer's perspective,
/// apart from joining a queue.
/// </summary>
[ApiController]
[Route("api/events")]
public sealed class EventsController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IQueueStore _queueStore;
    private readonly ILogger<EventsController> _logger;

    public EventsController(
        ApplicationDbContext context,
        IQueueStore queueStore,
        ILogger<EventsController> logger)
    {
        _context = context;
        _queueStore = queueStore;
        _logger = logger;
    }

    /// <summary>Lists sales with live availability counts.</summary>
    [HttpGet]
    [AllowAnonymous]
    public async Task<ActionResult<IEnumerable<object>>> List(CancellationToken cancellationToken)
    {
        var events = await _context.Events
            .AsNoTracking()
            .OrderBy(e => e.StartsAtUtc)
            .Select(e => new
            {
                e.Id,
                e.Name,
                e.Description,
                e.Venue,
                e.StartsAtUtc,
                e.PriceInMinorUnits,
                e.CurrencyCode,
                e.AdmitBatchSize,
                e.CheckoutWindowSeconds,
                TotalSeats = e.Tickets.Count,
                SeatsRemaining = e.Tickets.Count(t => t.Status == "Available")
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Ok(events);
    }

    /// <summary>Returns one sale plus its seat map.</summary>
    [HttpGet("{eventId:guid}")]
    [AllowAnonymous]
    public async Task<ActionResult<object>> Get(Guid eventId, CancellationToken cancellationToken)
    {
        var evt = await _context.Events
            .AsNoTracking()
            .Where(e => e.Id == eventId)
            .Select(e => new
            {
                e.Id,
                e.Name,
                e.Description,
                e.Venue,
                e.StartsAtUtc,
                e.PriceInMinorUnits,
                e.CurrencyCode,
                e.AdmitBatchSize,
                e.CheckoutWindowSeconds,
                Seats = e.Tickets
                    .OrderBy(t => t.SeatNumber)
                    .Select(t => new { t.Id, t.SeatNumber, t.Status })
                    .ToList()
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return evt is null ? NotFound(new ApiError("not_found", "No such sale.")) : Ok(evt);
    }
    /// <summary>
    /// Joins the virtual waiting room over HTTP.
    /// <para>
    /// REST equivalent of <c>QueueHub.JoinQueue</c>. It lets a client — or a load-test
    /// script — enter the queue and read its position without holding a live socket.
    /// Queue state itself lives in the shared <see cref="IQueueStore"/>.
    /// </para>
    /// </summary>
    [HttpPost("{eventId:guid}/queue")]
    [Authorize]
    public async Task<ActionResult<object>> JoinQueue(Guid eventId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized(new ApiError("unauthenticated", "The token carries no usable subject claim."));
        }

        if (!await _context.Events.AnyAsync(e => e.Id == eventId, cancellationToken).ConfigureAwait(false))
        {
            return NotFound(new ApiError("not_found", "No such sale."));
        }

        // HTTP callers have no SignalR connection, so they are tracked under a synthetic
        // id. Their position is genuine; they are simply not pushed to over the socket.
        var participant = _queueStore.Enqueue(eventId, userId, $"http:{userId}", DateTimeOffset.UtcNow);

        var entry = await _context.QueueEntries
            .SingleOrDefaultAsync(q => q.EventId == eventId && q.UserId == userId, cancellationToken)
            .ConfigureAwait(false);

        if (entry is null)
        {
            entry = new QueueEntry
            {
                Id = Guid.NewGuid(),
                EventId = eventId,
                UserId = userId,
                Status = QueueStatus.Waiting.ToString(),
                SequenceNumber = participant.SequenceNumber,
                EnqueuedAtUtc = participant.EnqueuedAtUtc
            };

            _context.QueueEntries.Add(entry);

            try
            {
                await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                // Lost a race to a simultaneous join; the unique index did its job.
                _logger.LogDebug(
                    "Queue entry already existed for user {UserId} on event {EventId}.", userId, eventId);
            }
        }

        return Ok(new
        {
            EventId = eventId,
            Position = _queueStore.PositionOf(eventId, userId),
            TotalWaiting = _queueStore.Count(eventId),
            Status = QueueStatus.Waiting.ToString(),
            ServerUtc = DateTimeOffset.UtcNow
        });
    }

    /// <summary>Current 1-based position for the authenticated buyer.</summary>
    [HttpGet("{eventId:guid}/queue/me")]
    [Authorize]
    public ActionResult<object> MyPosition(Guid eventId)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized(new ApiError("unauthenticated", "The token carries no usable subject claim."));
        }

        return Ok(new
        {
            EventId = eventId,
            Position = _queueStore.PositionOf(eventId, userId),
            TotalWaiting = _queueStore.Count(eventId),
            ServerUtc = DateTimeOffset.UtcNow
        });
    }

    /// <summary>Resolves the caller id from either claim mapping.</summary>
    private bool TryGetUserId(out Guid userId)
    {
        var raw = User.FindFirst("sub")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out userId) && userId != Guid.Empty;
    }
}