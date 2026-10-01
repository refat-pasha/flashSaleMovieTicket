using FlashSale.Api.Contracts;
using FlashSale.Domain.Entities;
using FlashSale.Infrastructure.Identity;
using FlashSale.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FlashSale.Api.Controllers;

/// <summary>
/// Issues session tokens. Stands in for a real identity provider (Entra ID, Auth0, …)
/// so the waiting-room and booking flows can be exercised end to end.
/// </summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly JwtProvider _jwtProvider;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        ApplicationDbContext context,
        JwtProvider jwtProvider,
        ILogger<AuthController> logger)
    {
        _context = context;
        _jwtProvider = jwtProvider;
        _logger = logger;
    }

    /// <summary>
    /// Signs a buyer in, creating the account on first contact.
    /// <para>
    /// Registration and sign-in share one endpoint because the sale has no
    /// pre-registered buyers: the first sign-in creates the account and stores a
    /// PBKDF2 hash, later sign-ins verify against it. A wrong password on an
    /// existing account is rejected.
    /// </para>
    /// The returned token carries <c>CheckoutAllowed = "false"</c>, which is what
    /// sends the Angular guard to the waiting room.
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToLowerInvariant();

        var user = await _context.Users
            .SingleOrDefaultAsync(u => u.Email == email, cancellationToken)
            .ConfigureAwait(false);

        if (user is null)
        {
            user = new User
            {
                Id = Guid.NewGuid(),
                Email = email,
                DisplayName = request.DisplayName.Trim(),
                PasswordHash = PasswordHasher.Hash(request.Password),
                IsCheckoutAllowed = false,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };

            _context.Users.Add(user);
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Registered new buyer {UserId} ({Email}).", user.Id, email);
        }
        else if (!PasswordHasher.Verify(user.PasswordHash, request.Password))
        {
            // Do not reveal whether the account exists.
            _logger.LogWarning("Failed sign-in attempt for {Email}.", email);

            return Unauthorized(new ApiError(
                "invalid_credentials", "That email and password do not match."));
        }

        // The gate claim mirrors the persisted admission flag.
        var issued = _jwtProvider.CreateSessionToken(
            user.Id,
            user.Email,
            user.DisplayName,
            user.IsCheckoutAllowed,
            request.EventId);

        if (!issued.Succeeded)
        {
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new ApiError("token_issue_failed", issued.Error ?? "Could not issue a token.", HttpContext.TraceIdentifier));
        }

        return Ok(new AuthResponse(
            issued.Token!,
            issued.ExpiresAtUtc,
            user.Id,
            user.Email,
            user.DisplayName,
            user.IsCheckoutAllowed));
    }

    /// <summary>
    /// Re-issues a token reflecting current admission state. Called by the client after
    /// the waiting room pushes a pass, so the guard sees <c>CheckoutAllowed = "true"</c>.
    /// </summary>
    [HttpPost("refresh")]
    [Authorize]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AuthResponse>> Refresh(CancellationToken cancellationToken)
    {
        var raw = User.FindFirst("sub")?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        if (!Guid.TryParse(raw, out var userId) || userId == Guid.Empty)
        {
            return Unauthorized(new ApiError("unauthenticated", "The token carries no usable subject claim."));
        }

        var user = await _context.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(u => u.Id == userId, cancellationToken)
            .ConfigureAwait(false);

        if (user is null)
        {
            return Unauthorized(new ApiError("unknown_user", "This user no longer exists."));
        }

        var issued = _jwtProvider.CreateSessionToken(
            user.Id, user.Email, user.DisplayName, user.IsCheckoutAllowed);

        if (!issued.Succeeded)
        {
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new ApiError("token_issue_failed", issued.Error ?? "Could not issue a token."));
        }

        return Ok(new AuthResponse(
            issued.Token!,
            issued.ExpiresAtUtc,
            user.Id,
            user.Email,
            user.DisplayName,
            user.IsCheckoutAllowed));
    }
}