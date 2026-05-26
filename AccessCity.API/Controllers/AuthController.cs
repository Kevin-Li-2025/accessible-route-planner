using Asp.Versioning;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using AccessCity.API.Models.Identity;
using AccessCity.API.Security;
using AccessCity.API.Services.Security;
using AccessCity.API.Common;
using AccessCity.API.Models.DTOs;
using Microsoft.AspNetCore.WebUtilities;

namespace AccessCity.API.Controllers;

/// <summary>
/// Authentication and Identity management: Registration, Login, Token Rotation, and Password Recovery.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/[controller]")]
[EnableRateLimiting(AccessCityRateLimitPolicies.Auth)]
public class AuthController : ControllerBase
{
    private readonly UserManager<AccessCityUser> _userManager;
    private readonly ITokenService _tokenService;
    private readonly IRefreshTokenRevocationService _refreshTokenRevocation;
    private readonly IConfiguration _configuration;

    public AuthController(
        UserManager<AccessCityUser> userManager,
        ITokenService tokenService,
        IRefreshTokenRevocationService refreshTokenRevocation,
        IConfiguration configuration)
    {
        _userManager = userManager;
        _tokenService = tokenService;
        _refreshTokenRevocation = refreshTokenRevocation;
        _configuration = configuration;
    }

    /// <summary>
    /// Lists OAuth providers and whether this deployment has credentials configured.
    /// </summary>
    [HttpGet("oauth/providers")]
    [ProducesResponseType(typeof(IEnumerable<OAuthProviderResponse>), StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<OAuthProviderResponse>> GetOAuthProviders()
    {
        return Ok(GetKnownOAuthProviders().Select(provider => new OAuthProviderResponse(
            provider.Provider,
            provider.DisplayName,
            IsOAuthProviderConfigured(provider.Provider))));
    }

    /// <summary>
    /// Builds the upstream OAuth authorization URL for a configured provider.
    /// The mobile/web client still owns callback handling and code exchange.
    /// </summary>
    [HttpGet("oauth/{provider}/authorize")]
    [ProducesResponseType(typeof(OAuthAuthorizeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status501NotImplemented)]
    public ActionResult<OAuthAuthorizeResponse> CreateOAuthAuthorizeUrl(
        string provider,
        [FromQuery] string redirectUri,
        [FromQuery] string? state)
    {
        var knownProvider = ResolveOAuthProvider(provider);
        if (knownProvider is null)
        {
            return BadRequest(new ApiError("Unsupported OAuth provider."));
        }

        if (!IsSafeRedirectUri(redirectUri))
        {
            return BadRequest(new ApiError("redirectUri must be an absolute http(s) or app-scheme URI."));
        }

        var clientId = _configuration[$"Authentication:OAuth:{knownProvider.Provider}:ClientId"];
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return StatusCode(
                StatusCodes.Status501NotImplemented,
                new ApiError($"{knownProvider.DisplayName} OAuth is not configured for this deployment."));
        }

        var authorizationEndpoint = _configuration[$"Authentication:OAuth:{knownProvider.Provider}:AuthorizationEndpoint"]
            ?? knownProvider.AuthorizationEndpoint;
        var scope = _configuration[$"Authentication:OAuth:{knownProvider.Provider}:Scopes"]
            ?? knownProvider.DefaultScopes;

        var parameters = new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = scope,
            ["state"] = string.IsNullOrWhiteSpace(state) ? Guid.NewGuid().ToString("N") : state
        };

        if (string.Equals(knownProvider.Provider, "Apple", StringComparison.OrdinalIgnoreCase))
        {
            parameters["response_mode"] = "form_post";
        }

        var url = QueryHelpers.AddQueryString(authorizationEndpoint, parameters);
        return Ok(new OAuthAuthorizeResponse(knownProvider.Provider.ToLowerInvariant(), url));
    }

    /// <summary>
    /// Placeholder exchange endpoint. It is intentionally explicit instead of fake-login behavior.
    /// Add provider token validation before enabling production OAuth sessions.
    /// </summary>
    [HttpPost("oauth/{provider}/exchange")]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status501NotImplemented)]
    public IActionResult ExchangeOAuthCode(string provider, [FromBody] OAuthCodeExchangeRequest _)
    {
        var knownProvider = ResolveOAuthProvider(provider);
        if (knownProvider is null)
        {
            return BadRequest(new ApiError("Unsupported OAuth provider."));
        }

        return StatusCode(
            StatusCodes.Status501NotImplemented,
            new ApiError("OAuth code exchange is not enabled. Configure provider token validation before issuing AccessCity sessions."));
    }

    /// <summary>
    /// Registers a new user account.
    /// </summary>
    [HttpPost("register")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AuthResponse>> Register([FromBody] RegisterRequest request)
    {
        var email = request.Email.Trim().ToLower();
        if (email.Length > 256) return BadRequest(new ApiError("Email is too long."));

        if (await _userManager.Users.AnyAsync(u => u.Email == email))
        {
            return BadRequest(new ApiError("Email is already in use."));
        }

        var user = new AccessCityUser
        {
            UserName = email,
            Email = email,
            FullName = request.FullName?.Trim()
        };

        var result = await _userManager.CreateAsync(user, request.Password);

        if (!result.Succeeded)
        {
            return BadRequest(new ApiError("Registration failed.", Detail: string.Join(", ", result.Errors.Select(e => e.Description))));
        }

        var refreshToken = _tokenService.GenerateRefreshToken(GetIpAddress());
        user.RefreshTokens.Add(refreshToken.Entity);
        await _userManager.UpdateAsync(user);

        return Ok(new AuthResponse(
            _tokenService.CreateToken(user),
            refreshToken.Token,
            user.Email ?? string.Empty,
            user.FullName ?? string.Empty
        ));
    }

    /// <summary>
    /// Authenticates a user and returns a JWT access token and a refresh token.
    /// </summary>
    [HttpPost("login")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthResponse>> Login([FromBody] LoginRequest request)
    {
        var email = request.Email.Trim().ToLower();
        var user = await _userManager.Users
            .Include(u => u.RefreshTokens)
            .SingleOrDefaultAsync(x => x.Email == email);

        if (user == null) return Unauthorized(new ApiError("Invalid credentials."));

        var result = await _userManager.CheckPasswordAsync(user, request.Password);
        if (!result) return Unauthorized(new ApiError("Invalid credentials."));

        var refreshToken = _tokenService.GenerateRefreshToken(GetIpAddress());

        // Revoke existing active tokens (Single Session pattern)
        foreach (var t in user.RefreshTokens.Where(x => x.IsActive))
        {
            t.Revoked = DateTime.UtcNow;
            t.RevokedByIp = GetIpAddress();
            t.ReasonRevoked = "Replaced by new login";
        }

        user.RefreshTokens.Add(refreshToken.Entity);
        await _userManager.UpdateAsync(user);

        return Ok(new AuthResponse(
            _tokenService.CreateToken(user),
            refreshToken.Token,
            user.Email ?? string.Empty,
            user.FullName ?? string.Empty
        ));
    }

    /// <summary>
    /// Rotates a refresh token to obtain a new JWT access token and refresh token.
    /// </summary>
    [HttpPost("refresh-token")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthResponse>> RefreshToken([FromQuery] string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return BadRequest(new ApiError("Token is required."));

        var tokenHash = _tokenService.HashRefreshToken(token);
        var user = await _userManager.Users
            .Include(u => u.RefreshTokens)
            .SingleOrDefaultAsync(u => u.RefreshTokens.Any(t => t.Token == tokenHash || t.Token == token));

        if (user == null) return Unauthorized(new ApiError("Invalid token."));

        var refreshToken = user.RefreshTokens.Single(x => x.Token == tokenHash || x.Token == token);
        if (!refreshToken.IsActive) return Unauthorized(new ApiError("Invalid token."));

        var newRefreshToken = _tokenService.GenerateRefreshToken(GetIpAddress());
        refreshToken.Revoked = DateTime.UtcNow;
        refreshToken.RevokedByIp = GetIpAddress();
        refreshToken.ReplacedByToken = newRefreshToken.Entity.Token;
        refreshToken.ReasonRevoked = "Token rotated";

        user.RefreshTokens.Add(newRefreshToken.Entity);
        await _userManager.UpdateAsync(user);

        return Ok(new AuthResponse(
            _tokenService.CreateToken(user),
            newRefreshToken.Token,
            user.Email ?? string.Empty,
            user.FullName ?? string.Empty
        ));
    }

    /// <summary>
    /// Revokes a refresh token, rendering it and any descendants unusable.
    /// </summary>
    [HttpPost("revoke-token")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeToken(
        [FromQuery] string token,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
            return BadRequest(new ApiError("Token is required."));

        var status = await _refreshTokenRevocation.RevokeAsync(token, GetIpAddress(), cancellationToken);
        return status switch
        {
            RefreshTokenRevokeStatus.Revoked => Ok(new { message = "Token revoked" }),
            RefreshTokenRevokeStatus.NotFound => NotFound(new ApiError("Token not found.")),
            RefreshTokenRevokeStatus.AlreadyInactive => BadRequest(new ApiError("Token is already inactive.")),
            _ => StatusCode(StatusCodes.Status500InternalServerError, new ApiError("Token revocation failed."))
        };
    }

    /// <summary>
    /// Initiates the password recovery flow by generating a reset token.
    /// In Development, the token is printed to the console.
    /// </summary>
    [HttpPost("forgot-password")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request)
    {
        var user = await _userManager.FindByEmailAsync(request.Email.Trim().ToLower());

        if (user == null)
        {
            return Ok(new { message = "If your email is registered, you will receive a reset token." });
        }

        var token = await _userManager.GeneratePasswordResetTokenAsync(user);

        if (string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase))
        {
            // Security: Only log tokens in development for easier testing without a real SMTP server.
            System.Diagnostics.Debug.WriteLine($"RESET TOKEN for {request.Email}: {token}");
        }

        return Ok(new { message = "If your email is registered, you will receive a reset token." });
    }

    /// <summary>
    /// Completes the password recovery flow using a valid reset token.
    /// </summary>
    [HttpPost("reset-password")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request)
    {
        var email = request.Email.Trim().ToLower();
        var user = await _userManager.Users
            .Include(u => u.RefreshTokens)
            .SingleOrDefaultAsync(x => x.Email == email);

        if (user == null) return BadRequest(new ApiError("Invalid request."));

        var result = await _userManager.ResetPasswordAsync(user, request.Token, request.NewPassword);

        if (!result.Succeeded)
        {
            return BadRequest(new ApiError("Password reset failed.", Detail: string.Join(", ", result.Errors.Select(e => e.Description))));
        }

        // Revoke all existing tokens on password reset for security.
        foreach (var t in user.RefreshTokens.Where(x => x.IsActive))
        {
            t.Revoked = DateTime.UtcNow;
            t.RevokedByIp = GetIpAddress();
            t.ReasonRevoked = "Password reset";
        }

        await _userManager.UpdateAsync(user);
        return Ok(new { message = "Password has been reset successfully." });
    }

    private string GetIpAddress()
    {
        if (Request.Headers.ContainsKey("X-Forwarded-For"))
            return Request.Headers["X-Forwarded-For"]!;

        return HttpContext.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "unknown";
    }

    private bool IsOAuthProviderConfigured(string provider) =>
        !string.IsNullOrWhiteSpace(_configuration[$"Authentication:OAuth:{provider}:ClientId"]);

    private static OAuthProviderDefinition? ResolveOAuthProvider(string provider) =>
        GetKnownOAuthProviders().FirstOrDefault(
            item => string.Equals(item.Provider, provider, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<OAuthProviderDefinition> GetKnownOAuthProviders() =>
        new[]
        {
            new OAuthProviderDefinition("Google", "Google", "https://accounts.google.com/o/oauth2/v2/auth", "openid email profile"),
            new OAuthProviderDefinition("Apple", "Apple", "https://appleid.apple.com/auth/authorize", "name email"),
            new OAuthProviderDefinition("Facebook", "Facebook", "https://www.facebook.com/v19.0/dialog/oauth", "email public_profile")
        };

    private static bool IsSafeRedirectUri(string redirectUri)
    {
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.Equals(uri.Scheme, "javascript", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, "data", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record OAuthProviderDefinition(
        string Provider,
        string DisplayName,
        string AuthorizationEndpoint,
        string DefaultScopes);
}
