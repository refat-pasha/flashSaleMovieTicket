namespace FlashSale.Domain.Entities;

/// <summary>
/// A sale (concert, match, festival) whose seats are sold under flash-sale pressure.
/// </summary>
public class Event
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Short description rendered on the waiting-room screen.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Venue shown to the buyer, purely informational.</summary>
    public string Venue { get; set; } = string.Empty;

    public DateTimeOffset StartsAtUtc { get; set; }

    /// <summary>Price in minor units (pence/cents) to avoid floating point money.</summary>
    public long PriceInMinorUnits { get; set; }

    /// <summary>Currency code as an ISO-4217 string, e.g. "GBP".</summary>
    public string CurrencyCode { get; set; } = "GBP";

    /// <summary>How many buyers the waiting room may admit per release window.</summary>
    public int AdmitBatchSize { get; set; } = 50;

    /// <summary>How long an admitted buyer keeps checkout access, in seconds.</summary>
    public int CheckoutWindowSeconds { get; set; } = 120;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public ICollection<Ticket> Tickets { get; set; } = new List<Ticket>();

    public ICollection<QueueEntry> QueueEntries { get; set; } = new List<QueueEntry>();

    public ICollection<Order> Orders { get; set; } = new List<Order>();
}