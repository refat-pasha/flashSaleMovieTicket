namespace FlashSale.Api.Contracts;

/// <summary>
/// Holds several seats at once, creating one order. Partial success is normal in a
/// flash sale: some seats may be gone, so both the winners and the losers are returned.
/// </summary>
public sealed class HoldSeatsRequest
{
    /// <summary>Seats to hold. De-duplicated and capped server-side.</summary>
    public List<Guid> TicketIds { get; set; } = [];
}

/// <summary>A single seat line on an order.</summary>
/// <param name="TicketId">Underlying seat.</param>
/// <param name="SeatNumber">Copied seat label, e.g. "3-14".</param>
/// <param name="UnitPriceMinorUnits">Unit price in minor units.</param>
public sealed record OrderSeatDto(Guid TicketId, string SeatNumber, long UnitPriceMinorUnits);

/// <summary>An order: the basket of seats plus how it was paid for.</summary>
public sealed record OrderDto
{
    public required Guid OrderId { get; init; }

    public required Guid EventId { get; init; }

    /// <summary>"Pending" while held, "Paid" once settled.</summary>
    public required string Status { get; init; }

    public string? PaymentMethod { get; init; }

    public required List<OrderSeatDto> Seats { get; init; }

    public required long TotalMinorUnits { get; init; }

    public required string CurrencyCode { get; init; }

    public required DateTimeOffset ExpiresAtUtc { get; init; }

    public DateTimeOffset? PaidAtUtc { get; init; }
}

/// <summary>Result of a batch hold: what was won and what was lost.</summary>
/// <param name="Order">The order created. Null when every seat was lost.</param>
/// <param name="HeldSeats">Seats successfully held.</param>
/// <param name="UnavailableSeats">Seats another buyer took first.</param>
public sealed record HoldSeatsResponse(
    OrderDto? Order,
    List<OrderSeatDto> HeldSeats,
    List<string> UnavailableSeats);

/// <summary>Payload for settling an order.</summary>
public sealed class PayOrderRequest
{
    /// <summary>One of <c>Card</c>, <c>PayPal</c>, <c>ApplePay</c>, <c>GooglePay</c>.</summary>
    public string PaymentMethod { get; set; } = "Card";

    /// <summary>Display name on the card. Only used for the receipt.</summary>
    public string? CardholderName { get; set; }

    /// <summary>
    /// Last four digits only, for the receipt. Never store or log a full card number:
    /// a real deployment would hand this to a payment provider and keep no PAN at all.
    /// </summary>
    public string? CardLast4 { get; set; }
}