using System.Security.Claims;
using FlashSale.Api.Contracts;
using FlashSale.Domain.Entities;
using FlashSale.Domain.Enums;
using FlashSale.Infrastructure.Identity;
using FlashSale.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FlashSale.Api.Controllers;

/// <summary>
/// Multi-seat orders: hold a basket of seats, pay for it, or abandon it.
/// <para>
/// A buyer may hold several seats in one order (up to <see cref="MaxSeatsPerOrder"/>),
/// which is what makes "three seats with friends" possible. The whole basket lands in
/// one transaction; if a seat is lost to another buyer the buyer keeps whatever seats
/// they did win rather than losing everything.
/// </para>
/// </summary>
[ApiController]
[Route("api/orders")]
[Authorize]
public sealed class OrdersController : ControllerBase
{
    /// <summary>Upper bound on seats per order, mirroring typical venue limits.</summary>
    private const int MaxSeatsPerOrder = 6;

    /// <summary>How long a basket stays held while the buyer pays.</summary>
    private static readonly TimeSpan HoldWindow = TimeSpan.FromMinutes(5);

    private readonly ApplicationDbContext _context;
    private readonly ILogger<OrdersController> _logger;
    private readonly TimeProvider _timeProvider;

    public OrdersController(
        ApplicationDbContext context,
        ILogger<OrdersController> logger,
        TimeProvider timeProvider)
    {
        _context = context;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <summary>Holds one or more seats and creates a single order for them.</summary>
    [HttpPost("hold")]
    [ProducesResponseType(typeof(HoldSeatsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<HoldSeatsResponse>> Hold(
        [FromBody] HoldSeatsRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized(new ApiError("unauthenticated", "No usable subject claim."));
        }

        // The waiting-room gate, identical to the single-seat endpoint.
        if (!IsCheckoutAllowed())
        {
            return ProblemResponse(
                StatusCodes.Status403Forbidden,
                "checkout_not_allowed",
                "You have not been admitted through the waiting room yet.");
        }

        // De-duplicate and cap, so a client cannot ask for the same seat twice.
        var ticketIds = request.TicketIds.Distinct().Take(MaxSeatsPerOrder).ToList();

        if (ticketIds.Count == 0)
        {
            return ProblemResponse(
                StatusCodes.Status400BadRequest, "invalid_request", "Select at least one seat.");
        }

        var now = _timeProvider.GetUtcNow();

        // The DbContext uses a retrying execution strategy, which does not permit a
        // user-initiated transaction. Running the whole hold inside the strategy makes
        // it a retriable unit rather than an error.
        // The context uses a retrying execution strategy, which forbids a user-initiated
        // transaction. The extension overload wraps the whole hold in the strategy so
        // it becomes one retriable unit, and rolls back the change tracker between
        // attempts.
        var response = await _context.Database
            .CreateExecutionStrategy()
            .ExecuteAsync(
                async ct => await HoldCoreAsync(userId, ticketIds, now, ct).ConfigureAwait(false),
                cancellationToken)
            .ConfigureAwait(false);

        return Ok(response);
    }

    /// <summary>
    /// Holds the requested seats in a single transaction, creating one order.
    /// Runs inside the retrying execution strategy.
    /// </summary>
    private async Task<HoldSeatsResponse> HoldCoreAsync(
        Guid userId,
        IReadOnlyCollection<Guid> ticketIds,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _context.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var tickets = await _context.Tickets
            .Where(t => ticketIds.Contains(t.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (tickets.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new HoldSeatsResponse(null, [], []);
        }

        var eventId = tickets[0].EventId;

        // A buyer has at most one live basket per sale. Releasing the previous one
        // first also stops a re-hold from adding a second OrderSeat row for a seat
        // they already hold, which would violate the unique index on TicketId.
        await ReleaseExistingBasketAsync(userId, eventId, cancellationToken).ConfigureAwait(false);

        // Seats already sitting in a live order are excluded from the new basket.
        var alreadyOrdered = await _context.OrderSeats
            .Where(s => _context.Orders.Any(o =>
                o.Id == s.OrderId
                && o.UserId == userId
                && o.Status == OrderStatus.Pending.ToString()))
            .Select(s => s.TicketId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var alreadyOrderedSet = alreadyOrdered.ToHashSet();
        var held = new List<Ticket>();

        foreach (var ticket in tickets)
        {
            // All seats must belong to the same sale.
            if (ticket.EventId != eventId)
            {
                continue;
            }

            if (alreadyOrderedSet.Contains(ticket.Id))
            {
                continue;
            }

            var claimable = ticket.Status == nameof(TicketStatus.Available)
                || (ticket.Status == nameof(TicketStatus.Reserved)
                    && ticket.ReservedUntilUtc is not null
                    && ticket.ReservedUntilUtc <= now);

            if (!claimable || (ticket.AssignedUserId.HasValue && ticket.AssignedUserId != userId))
            {
                continue;
            }

            ticket.Status = TicketStatus.Reserved.ToString();
            ticket.AssignedUserId = userId;
            ticket.ReservedUntilUtc = now.Add(HoldWindow);
            held.Add(ticket);
        }

        if (held.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return new HoldSeatsResponse(null, [], tickets.Select(t => t.SeatNumber).ToList());
        }

        var order = BuildOrder(userId, eventId, held, now);

        try
        {
            // The rowversion token is what decides any race for these seats.
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Someone beat us to at least one seat. Retry seat-by-seat so the buyer
            // keeps whatever is still winnable instead of losing the whole basket.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            DetachTickets();

            _logger.LogInformation("Basket hold for user {UserId} lost a race; retrying per seat.", userId);

            return await HoldOneByOneAsync(userId, ticketIds, cancellationToken).ConfigureAwait(false);
        }

        var unavailable = tickets.Where(t => !held.Contains(t)).Select(t => t.SeatNumber).ToList();

        if (unavailable.Count > 0)
        {
            _logger.LogInformation(
                "Partial hold for user {UserId}: {Held} won, {Lost} unavailable.",
                userId, held.Count, unavailable.Count);
        }

        return new HoldSeatsResponse(ToDto(order), ToSeatDtos(held), unavailable);
    }
    /// <summary>
    /// The buyer's live order for a sale, so a refresh or a new tab restores the basket.
    /// </summary>
    [HttpGet("current")]
    [ProducesResponseType(typeof(OrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult<OrderDto>> Current(
        [FromQuery] Guid eventId,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized(new ApiError("unauthenticated", "No usable subject claim."));
        }

        var order = await _context.Orders
            .AsNoTracking()
            .Include(o => o.Seats)
            .Where(o => o.UserId == userId
                        && o.EventId == eventId
                        && (o.Status == OrderStatus.Pending.ToString()
                            || o.Status == OrderStatus.Paid.ToString()))
            .OrderByDescending(o => o.Status == OrderStatus.Paid.ToString())
            .ThenByDescending(o => o.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return order is null ? NoContent() : Ok(ToDto(order));
    }

    /// <summary>
    /// Settles an order: marks it paid and flips its seats to <c>Sold</c>.
    /// Simulated capture. A real deployment would call a payment provider here and
    /// only settle once the provider confirmed.
    /// </summary>
    [HttpPost("{orderId:guid}/pay")]
    [ProducesResponseType(typeof(OrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OrderDto>> Pay(
        Guid orderId,
        [FromBody] PayOrderRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized(new ApiError("unauthenticated", "No usable subject claim."));
        }

        if (!Enum.TryParse<PaymentMethod>(request.PaymentMethod, ignoreCase: true, out var method))
        {
            return ProblemResponse(
                StatusCodes.Status400BadRequest,
                "invalid_payment_method",
                $"'{request.PaymentMethod}' is not a supported payment method.");
        }

        var order = await _context.Orders
            .Include(o => o.Seats)
            .SingleOrDefaultAsync(o => o.Id == orderId, cancellationToken)
            .ConfigureAwait(false);

        if (order is null || order.UserId != userId)
        {
            return ProblemResponse(StatusCodes.Status404NotFound, "order_not_found", "No such order.");
        }

        // Idempotent: paying twice must never charge twice.
        if (order.Status == OrderStatus.Paid.ToString())
        {
            return Ok(ToDto(order));
        }

        if (order.Status != OrderStatus.Pending.ToString())
        {
            return ProblemResponse(
                StatusCodes.Status409Conflict,
                "order_not_payable",
                $"This order is {order.Status.ToLowerInvariant()}.");
        }

        var now = _timeProvider.GetUtcNow();

        if (order.ExpiresAtUtc <= now)
        {
            order.Status = OrderStatus.Expired.ToString();
            await ReleaseSeatsAsync(order, cancellationToken).ConfigureAwait(false);
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return ProblemResponse(
                StatusCodes.Status409Conflict,
                "hold_expired",
                "The hold on these seats has expired. Please choose again.");
        }

        var ticketIds = order.Seats.Select(s => s.TicketId).ToList();
        var tickets = await _context.Tickets
            .Where(t => ticketIds.Contains(t.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Every seat must still belong to this buyer, or the order cannot be settled.
        var stolen = tickets
            .Where(t => t.AssignedUserId != userId)
            .Select(t => t.SeatNumber)
            .ToList();

        if (stolen.Count > 0)
        {
            order.Status = OrderStatus.Cancelled.ToString();
            await ReleaseSeatsAsync(order, cancellationToken).ConfigureAwait(false);
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return ProblemResponse(
                StatusCodes.Status409Conflict,
                "seats_unavailable",
                $"Seat(s) {string.Join(", ", stolen)} are no longer yours. The order was cancelled.");
        }

        foreach (var ticket in tickets)
        {
            ticket.Status = TicketStatus.Sold.ToString();
            ticket.ReservedUntilUtc = null;
        }

        order.Status = OrderStatus.Paid.ToString();
        order.PaymentMethod = method.ToString();
        order.PaidAtUtc = now;

        try
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ProblemResponse(
                StatusCodes.Status409Conflict,
                "payment_conflict",
                "Those seats changed while we were taking payment. Nothing was charged.");
        }

        _logger.LogInformation(
            "Order {OrderId} paid by user {UserId} via {Method} for {Seats} seat(s).",
            order.Id, userId, method, order.Seats.Count);

        return Ok(ToDto(order));
    }
    /// <summary>Abandons an order and returns its seats to the pool.</summary>
    [HttpPost("{orderId:guid}/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(Guid orderId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized(new ApiError("unauthenticated", "No usable subject claim."));
        }

        var order = await _context.Orders
            .Include(o => o.Seats)
            .SingleOrDefaultAsync(o => o.Id == orderId, cancellationToken)
            .ConfigureAwait(false);

        if (order is null || order.UserId != userId)
        {
            return ProblemResponse(StatusCodes.Status404NotFound, "order_not_found", "No such order.");
        }

        if (order.Status == OrderStatus.Paid.ToString())
        {
            return ProblemResponse(
                StatusCodes.Status409Conflict, "already_paid", "A paid order cannot be cancelled.");
        }

        order.Status = OrderStatus.Cancelled.ToString();
        await ReleaseSeatsAsync(order, cancellationToken).ConfigureAwait(false);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>Returns every still-reserved seat on the order to the available pool.</summary>
    private async Task ReleaseSeatsAsync(Order order, CancellationToken cancellationToken)
    {
        var ticketIds = order.Seats.Select(s => s.TicketId).ToList();

        var tickets = await _context.Tickets
            .Where(t => ticketIds.Contains(t.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var ticket in tickets)
        {
            // A confirmed seat is never reclaimed.
            if (ticket.Status == TicketStatus.Sold.ToString())
            {
                continue;
            }

            ticket.Status = TicketStatus.Available.ToString();
            ticket.AssignedUserId = null;
            ticket.ReservedUntilUtc = null;
        }
    }

    /// <summary>
    /// Fallback when a batch hold loses a race: claim each seat individually so the
    /// buyer keeps whatever is still winnable instead of losing the whole basket.
    /// </summary>
    private async Task<HoldSeatsResponse> HoldOneByOneAsync(
        Guid userId,
        IReadOnlyCollection<Guid> ticketIds,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var held = new List<Ticket>();
        var unavailable = new List<string>();

        var tickets = await _context.Tickets
            .Where(t => ticketIds.Contains(t.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var ticket in tickets)
        {
            var claimable = ticket.Status == nameof(TicketStatus.Available)
                || (ticket.Status == nameof(TicketStatus.Reserved)
                    && ticket.ReservedUntilUtc is not null
                    && ticket.ReservedUntilUtc <= now);

            if (!claimable || (ticket.AssignedUserId.HasValue && ticket.AssignedUserId != userId))
            {
                unavailable.Add(ticket.SeatNumber);
                continue;
            }

            ticket.Status = TicketStatus.Reserved.ToString();
            ticket.AssignedUserId = userId;
            ticket.ReservedUntilUtc = now.Add(HoldWindow);
            held.Add(ticket);
        }

        if (held.Count == 0)
        {
            return new HoldSeatsResponse(null, [], unavailable);
        }

        var order = BuildOrder(userId, held[0].EventId, held, now);

        try
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            DetachTickets();
            return new HoldSeatsResponse(null, [], unavailable);
        }

        return new HoldSeatsResponse(ToDto(order), ToSeatDtos(held), unavailable);
    }

    /// <summary>
    /// Cancels this buyer's existing pending order for a sale and returns its seats
    /// to the pool, so a new basket can be created without colliding with it.
    /// </summary>
    private async Task ReleaseExistingBasketAsync(
        Guid userId,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        var existing = await _context.Orders
            .Include(o => o.Seats)
            .Where(o => o.UserId == userId
                        && o.EventId == eventId
                        && o.Status == OrderStatus.Pending.ToString())
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (existing.Count == 0)
        {
            return;
        }

        foreach (var order in existing)
        {
            order.Status = OrderStatus.Cancelled.ToString();
            await ReleaseSeatsAsync(order, cancellationToken).ConfigureAwait(false);

            // Drop the cancelled lines so their seats can be re-ordered.
            _context.OrderSeats.RemoveRange(order.Seats);
        }
    }

    /// <summary>Builds a pending order with one seat line per held ticket.</summary>
    private Order BuildOrder(Guid userId, Guid eventId, List<Ticket> held, DateTimeOffset now)
    {
        var order = new Order
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EventId = eventId,
            Status = OrderStatus.Pending.ToString(),
            CurrencyCode = "GBP",
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(HoldWindow),
            TotalMinorUnits = held.Sum(t => t.PriceInMinorUnits)
        };

        foreach (var ticket in held)
        {
            order.Seats.Add(new OrderSeat
            {
                Id = Guid.NewGuid(),
                TicketId = ticket.Id,
                SeatNumber = ticket.SeatNumber,
                UnitPriceMinorUnits = ticket.PriceInMinorUnits
            });
        }

        _context.Orders.Add(order);
        return order;
    }

    /// <summary>
    /// Detaches stale ticket rows so the scoped DbContext stays usable after a
    /// concurrency failure.
    /// </summary>
    private void DetachTickets()
    {
        foreach (var entry in _context.ChangeTracker.Entries<Ticket>())
        {
            entry.State = EntityState.Detached;
        }
    }

    private static List<OrderSeatDto> ToSeatDtos(IEnumerable<Ticket> tickets) =>
        tickets.Select(t => new OrderSeatDto(t.Id, t.SeatNumber, t.PriceInMinorUnits)).ToList();

    private static OrderDto ToDto(Order order) => new()
    {
        OrderId = order.Id,
        EventId = order.EventId,
        Status = order.Status,
        PaymentMethod = order.PaymentMethod,
        Seats = order.Seats
            .Select(s => new OrderSeatDto(s.TicketId, s.SeatNumber, s.UnitPriceMinorUnits))
            .ToList(),
        TotalMinorUnits = order.TotalMinorUnits,
        CurrencyCode = order.CurrencyCode,
        ExpiresAtUtc = order.ExpiresAtUtc,
        PaidAtUtc = order.PaidAtUtc
    };

    /// <summary>Mirrors the server-side waiting-room gate.</summary>
    private bool IsCheckoutAllowed() =>
        string.Equals(
            User.FindFirst(JwtProvider.CheckoutAllowedClaim)?.Value,
            "true",
            StringComparison.OrdinalIgnoreCase);

    private bool TryGetUserId(out Guid userId)
    {
        var raw = User.FindFirst("sub")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out userId) && userId != Guid.Empty;
    }

    private ObjectResult ProblemResponse(int statusCode, string code, string message) =>
        StatusCode(statusCode, new ApiError(code, message, HttpContext.TraceIdentifier));
}
