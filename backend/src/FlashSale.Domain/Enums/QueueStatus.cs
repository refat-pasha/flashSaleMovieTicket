namespace FlashSale.Domain.Enums;

/// <summary>
/// Where a buyer sits in the virtual waiting room.
/// </summary>
public enum QueueStatus
{
    /// <summary>Connected to the hub and holding a place in line.</summary>
    Waiting = 0,

    /// <summary>
    /// Admitted past the gate by <c>QueueBackgroundWorker</c>; holds a short-lived
    /// checkout pass that unlocks the booking endpoint.
    /// </summary>
    Admitted = 1,

    /// <summary>Checkout pass expired or was never used. Buyer must re-queue.</summary>
    Expired = 2
}