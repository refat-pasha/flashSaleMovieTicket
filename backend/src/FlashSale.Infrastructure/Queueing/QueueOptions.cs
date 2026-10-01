namespace FlashSale.Infrastructure.Queueing;

/// <summary>
/// Tuning knobs for the virtual waiting room.
/// </summary>
public sealed class QueueOptions
{
    public const string SectionName = "Queue";

    /// <summary>
    /// How often the background worker sweeps the queue and releases the next batch
    /// of buyers. The brief calls for 10 seconds.
    /// </summary>
    public int TickSeconds { get; set; } = 10;

    /// <summary>
    /// Hard cap on how many buyers are admitted per tick. Acts as a circuit breaker so a
    /// misconfigured batch size cannot flood the checkout tier at once.
    /// </summary>
    public int MaxAdmissionsPerTick { get; set; } = 100;

    /// <summary>
    /// Default checkout window (seconds) when the sale itself does not specify one.
    /// </summary>
    public int DefaultCheckoutWindowSeconds { get; set; } = 120;
}