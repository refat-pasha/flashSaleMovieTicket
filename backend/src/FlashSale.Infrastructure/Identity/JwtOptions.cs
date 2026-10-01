namespace FlashSale.Infrastructure.Identity;

/// <summary>
/// Strongly-typed binding for the <c>Jwt</c> configuration section.
/// Bound from appsettings.json / user secrets, never hard-coded.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>Token issuer, e.g. "flash-sale-api".</summary>
    public string Issuer { get; set; } = "flash-sale-api";

    /// <summary>Expected audience, verified on every inbound token.</summary>
    public string Audience { get; set; } = "flash-sale-client";

    /// <summary>
    /// HMAC-SHA256 signing key. Must be at least 32 bytes; validated at startup by
    /// <c>JwtProvider</c> so a short key fails fast rather than at first request.
    /// </summary>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>Lifetime of the session token minted at login.</summary>
    public int AccessTokenMinutes { get; set; } = 60;

    /// <summary>
    /// Lifetime of the temporary checkout pass. Deliberately short (seconds) because
    /// it is a one-shot grant handed out by the waiting room, not a session credential.
    /// </summary>
    public int CheckoutPassSeconds { get; set; } = 120;

    /// <summary>Clock skew tolerated when validating <c>exp</c>/<c>nbf</c>.</summary>
    public int ClockSkewSeconds { get; set; } = 30;
}