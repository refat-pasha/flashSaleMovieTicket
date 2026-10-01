namespace FlashSale.Domain.Entities;

/// <summary>
/// One seat within an <see cref="Order"/>. The seat number and price are copied in
/// so the receipt stays accurate even if the underlying ticket row later changes.
/// </summary>
public class OrderSeat
{
    public Guid Id { get; set; }

    public Guid OrderId { get; set; }

    /// <summary>The seat this line refers to.</summary>
    public Guid TicketId { get; set; }

    /// <summary>Copied from the ticket at hold time, e.g. "3-14".</summary>
    public string SeatNumber { get; set; } = string.Empty;

    /// <summary>Unit price in minor units, copied at hold time.</summary>
    public long UnitPriceMinorUnits { get; set; }

    public Order Order { get; set; } = null!;
}