namespace FlashSale.Domain.Enums;

/// <summary>How the buyer chose to pay. Recorded on the order for the receipt.</summary>
public enum PaymentMethod
{
    Card = 0,
    PayPal = 1,
    ApplePay = 2,
    GooglePay = 3
}