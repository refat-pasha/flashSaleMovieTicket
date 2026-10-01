namespace FlashSale.Domain.Enums;

/// <summary>Lifecycle of a basket of seats, from hold through to payment.</summary>
public enum OrderStatus
{
    /// <summary>Seats are held for the buyer; payment has not been attempted.</summary>
    Pending = 0,

    /// <summary>Payment captured. Terminal.</summary>
    Paid = 1,

    /// <summary>Buyer abandoned the order; the seats were released.</summary>
    Cancelled = 2,

    /// <summary>The hold window elapsed before payment; seats were released.</summary>
    Expired = 3
}