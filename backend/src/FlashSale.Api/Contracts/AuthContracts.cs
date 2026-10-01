using System.ComponentModel.DataAnnotations;

namespace FlashSale.Api.Contracts;

/// <summary>Login request.</summary>
public sealed class LoginRequest
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MinLength(1)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Password. Minimum 8 characters, validated here so a weak password is
    /// rejected by the API even if the client is bypassed.
    /// </summary>
    [Required]
    [MinLength(8, ErrorMessage = "Password must be at least 8 characters.")]
    [MaxLength(128)]
    public string Password { get; set; } = string.Empty;

    /// <summary>Sale the buyer intends to queue for. Optional.</summary>
    public Guid? EventId { get; set; }
}

/// <summary>Successful login response. Token carries the CheckoutAllowed claim.</summary>
public sealed record AuthResponse(
    string AccessToken,
    DateTimeOffset ExpiresAtUtc,
    Guid UserId,
    string Email,
    string DisplayName,
    bool CheckoutAllowed);