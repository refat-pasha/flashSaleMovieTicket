using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace FlashSale.Infrastructure.Identity;

/// <summary>
/// Result of minting a token. Returned instead of a bare string so callers are
/// forced by the compiler to handle the failure case.
/// </summary>
/// <param name="Token">The encoded JWT, or null when issuance failed.</param>
/// <param name="ExpiresAtUtc">Absolute expiry, so the client can refresh proactively.</param>
/// <param name="Error">Human-readable reason when <paramref name="Token"/> is null.</param>
/// <summary>
/// Result of minting a token. Returned instead of a bare string so callers are
/// forced by the compiler to handle the failure case.
/// </summary>
/// <param name="Token">The encoded JWT, or null when issuance failed.</param>
/// <param name="ExpiresAtUtc">Absolute expiry, so the client can refresh proactively.</param>
/// <param name="Error">Human-readable reason when <paramref name="Token"/> is null.</param>
public readonly record struct IssuedToken(string? Token, DateTimeOffset ExpiresAtUtc, string? Error = null)
{
    public bool Succeeded => !string.IsNullOrEmpty(Token);

    public static IssuedToken Fail(string error) => new(null, DateTimeOffset.UtcNow, error);
}

/// <summary>
/// Stateless JWT token engine.
/// <para>
/// Issues the session token (<see cref="CreateSessionToken"/>) and the short-lived
/// checkout pass (<see cref="CreateCheckoutPassToken"/>) that the virtual waiting room
/// hands out. <see cref="CheckoutAllowedClaim"/> is the single source of truth the
/// booking endpoint authorises against: <c>"true"</c> means the buyer has been let
/// through the queue, <c>"false"</c> means they have not.
/// </para>
/// </summary>
public sealed class JwtProvider
{
    /// <summary>
    /// Custom claim written as <c>"CheckoutAllowed": "true" | "false"</c>.
    /// A string claim (not a bool) so the Angular guard can compare the raw decoded
    /// value directly.
    /// </summary>
    public const string CheckoutAllowedClaim = "CheckoutAllowed";

    /// <summary>Custom claim carrying the unique id of a granted checkout pass.</summary>
    public const string CheckoutPassClaim = "CheckoutPass";

    /// <summary>Custom claim naming the sale this token is scoped to.</summary>
    public const string EventIdClaim = "event_id";

    private readonly JwtOptions _options;
    private readonly SigningCredentials _signingCredentials;
    private readonly TimeProvider _timeProvider;

    public JwtProvider(IOptions<JwtOptions> options, TimeProvider timeProvider)
    {
        _options = options.Value;
        _timeProvider = timeProvider;

        // Fail fast at startup rather than throwing on the first login attempt.
        if (string.IsNullOrWhiteSpace(_options.SigningKey))
        {
            throw new InvalidOperationException(
                "Jwt:SigningKey is not configured. Supply it via appsettings.json or user secrets.");
        }

        var keyBytes = Encoding.UTF8.GetBytes(_options.SigningKey);
        if (keyBytes.Length < 32)
        {
            throw new InvalidOperationException(
                $"Jwt:SigningKey must be at least 32 bytes for HMAC-SHA256 (was {keyBytes.Length}).");
        }

        // Pinning the algorithm prevents 'alg' confusion / downgrade attacks.
        _signingCredentials = new SigningCredentials(
            new SymmetricSecurityKey(keyBytes),
            SecurityAlgorithms.HmacSha256);
    }
    /// <summary>
    /// Mints the buyer's session token issued at login.
    /// </summary>
    /// <param name="userId">Subject of the token.</param>
    /// <param name="email">Standard <c>email</c> claim.</param>
    /// <param name="displayName">Standard <c>name</c> claim.</param>
    /// <param name="checkoutAllowed">
    /// True only once the buyer has been admitted by the virtual waiting room.
    /// This is the exact value read by the booking endpoint and the Angular guard.
    /// </param>
    /// <param name="eventId">Optional sale the token is scoped to.</param>
    public IssuedToken CreateSessionToken(
        Guid userId,
        string email,
        string displayName,
        bool checkoutAllowed,
        Guid? eventId = null)
    {
        var now = _timeProvider.GetUtcNow();
        var expires = now.AddMinutes(_options.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(JwtRegisteredClaimNames.Email, email ?? string.Empty),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, displayName ?? string.Empty),
            new(JwtRegisteredClaimNames.Jti, NewJti(), ClaimValueTypes.String),

            // The waiting-room gate, materialised as a claim.
            new(CheckoutAllowedClaim, checkoutAllowed ? "true" : "false"),
        };

        if (eventId is not null)
        {
            claims.Add(new Claim(EventIdClaim, eventId.Value.ToString()));
        }

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: _signingCredentials);

        return new IssuedToken(new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    /// <summary>
    /// Mints the temporary checkout pass pushed over SignalR by
    /// <c>QueueBackgroundWorker</c> the moment the buyer's turn arrives.
    /// It carries <see cref="CheckoutAllowedClaim"/> = "true" and expires in seconds,
    /// so a leaked pass is worthless moments later.
    /// </summary>
    /// <param name="userId">Buyer being admitted.</param>
    /// <param name="eventId">Sale the pass is valid for.</param>
    /// <param name="absoluteExpiryUtc">
    /// Optional caller-supplied expiry (the sale's configured checkout window).
    /// Clamped to never outlive <see cref="JwtOptions.CheckoutPassSeconds"/>.
    /// </param>
    public IssuedToken CreateCheckoutPassToken(Guid userId, Guid eventId, DateTimeOffset? absoluteExpiryUtc = null)
    {
        var now = _timeProvider.GetUtcNow();
        var expires = absoluteExpiryUtc ?? now.AddSeconds(_options.CheckoutPassSeconds);

        // Defensive: never mint a pass that outlives the configured ceiling.
        var ceiling = now.AddSeconds(_options.CheckoutPassSeconds);
        if (expires > ceiling)
        {
            expires = ceiling;
        }

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(JwtRegisteredClaimNames.Jti, NewJti(), ClaimValueTypes.String),
            new(CheckoutAllowedClaim, "true"),
            new(EventIdClaim, eventId.ToString()),
            new(CheckoutPassClaim, NewJti(), ClaimValueTypes.String)
        };

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: _signingCredentials);

        return new IssuedToken(new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    /// <summary>Generates a cryptographically random, uppercase hex identifier.</summary>
    private static string NewJti() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
}