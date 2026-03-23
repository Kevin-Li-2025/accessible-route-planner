using Asp.Versioning;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using AccessCity.API.Models.Identity;
using AccessCity.API.Services.Security;
using AccessCity.API.Data;
using AccessCity.API.Common;
using AccessCity.API.Models.DTOs;

namespace AccessCity.API.Controllers;

/// <summary>
/// Authentication and Identity management: Registration, Login, Token Rotation, and Password Recovery.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/[controller]")]
[EnableRateLimiting("auth")]
public class AuthController : ControllerBase
{
    private readonly UserManager<AccessCityUser> _userManager;
    private readonly ITokenService _tokenService;
    private readonly AppDbContext _context;

    public AuthController(
        UserManager<AccessCityUser> userManager,
        ITokenService tokenService,
        AppDbContext context)
    {
        _userManager = userManager;
        _tokenService = tokenService;
        _context = context;
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
        user.RefreshTokens.Add(refreshToken);
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

        user.RefreshTokens.Add(refreshToken);
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

        var user = await _userManager.Users
            .Include(u => u.RefreshTokens)
            .SingleOrDefaultAsync(u => u.RefreshTokens.Any(t => t.Token == token));

        if (user == null) return Unauthorized(new ApiError("Invalid token."));

        var refreshToken = user.RefreshTokens.Single(x => x.Token == token);
        if (!refreshToken.IsActive) return Unauthorized(new ApiError("Invalid token."));

        var newRefreshToken = _tokenService.GenerateRefreshToken(GetIpAddress());
        refreshToken.Revoked = DateTime.UtcNow;
        refreshToken.RevokedByIp = GetIpAddress();
        refreshToken.ReplacedByToken = newRefreshToken.Token;
        refreshToken.ReasonRevoked = "Token rotated";

        user.RefreshTokens.Add(newRefreshToken);
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
    public async Task<IActionResult> RevokeToken([FromQuery] string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return BadRequest(new ApiError("Token is required."));

        var refreshToken = await _context.RefreshTokens
            .FirstOrDefaultAsync(t => t.Token == token);

        if (refreshToken == null) return NotFound(new ApiError("Token not found."));
        if (!refreshToken.IsActive) return BadRequest(new ApiError("Token is already inactive."));

        refreshToken.Revoked = DateTime.UtcNow;
        refreshToken.RevokedByIp = GetIpAddress();
        refreshToken.ReasonRevoked = "Revoked by user";

        await _context.SaveChangesAsync();
        return Ok(new { message = "Token revoked" });
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
}
