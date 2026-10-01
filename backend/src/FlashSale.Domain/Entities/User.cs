namespace FlashSale.Domain.Entities;

/// <summary>
/// A registered buyer. Authentication itself is delegated to an external
/// identity provider; this is the local projection used for authorisation
/// and ownership checks.
/// </summary>
public class User
{
    public Guid Id { get; set; }

    public string Email { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Password hash, formatted as <c>{iterations}.{salt}.{hash}</c>.
    /// Never the raw password. Null for accounts created before this field existed.
    /// </summary>
    public string? PasswordHash { get; set; }

    /// <summary>
    /// True once the buyer has ever been admitted through the waiting room.
    /// Mirrors the <c>CheckoutAllowed</c> claim minted by the token engine.
    /// </summary>
    public bool IsCheckoutAllowed { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>Optimistic concurrency token, mirroring <see cref="Ticket.RowVersion"/>.</summary>
    public byte[] RowVersion { get; set; } = [];

    public ICollection<Ticket> Tickets { get; set; } = new List<Ticket>();

    public ICollection<QueueEntry> QueueEntries { get; set; } = new List<QueueEntry>();

    public ICollection<Order> Orders { get; set; } = new List<Order>();
}