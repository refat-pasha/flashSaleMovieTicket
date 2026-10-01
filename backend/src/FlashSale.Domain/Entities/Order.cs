using FlashSale.Domain.Enums;

namespace FlashSale.Domain.Entities;

/// <summary>
/// A basket of seats being bought together, together with the payment that pays for it.
/// One order can hold several seats, which is what lets a buyer take three seats for
/// friends in a single flash-sale attempt.
/// </summary>
public class Order
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid EventId { get; set; }

    /// <summary>One of <see cref="OrderStatus"/>.</summary>
    public string Status { get; set; } = nameof(OrderStatus.Pending);

    /// <summary>How the buyer paid. Null until <see cref="OrderStatus.Paid"/>.</summary>
    public string? PaymentMethod { get; set; }

    /// <summary>Order total in minor units, summed from its seats.</summary>
    public long TotalMinorUnits { get; set; }

    public string CurrencyCode { get; set; } = "GBP";

    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>When the hold lapses and the sweeper reclaims the seats.</summary>
    public DateTimeOffset ExpiresAtUtc { get; set; }

    public DateTimeOffset? PaidAtUtc { get; set; }

    /// <summary>Optimistic concurrency token, mirroring <see cref="Ticket.RowVersion"/>.</summary>
    public byte[] RowVersion { get; set; } = [];

    public User User { get; set; } = null!;

    public Event Event { get; set; } = null!;

    public ICollection<OrderSeat> Seats { get; set; } = new List<OrderSeat>();
}