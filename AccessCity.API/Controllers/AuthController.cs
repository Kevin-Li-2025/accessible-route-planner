using Asp.Versioning;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using AccessCity.API.Models.Identity;
using AccessCity.API.Services.Security;
using AccessCity.API.Data;

namespace AccessCity.API.Controllers
{
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

        [HttpPost("register")]
        public async Task<ActionResult<AuthResponse>> Register(RegisterRequest request)
        {
            if (await _userManager.Users.AnyAsync(u => u.Email == request.Email))
            {
                return BadRequest("Email is already in use.");
            }

            var user = new AccessCityUser
            {
                UserName = request.Email,
                Email = request.Email,
                FullName = request.FullName
            };

            var result = await _userManager.CreateAsync(user, request.Password);

            if (!result.Succeeded)
            {
                return BadRequest(result.Errors);
            }

            var refreshToken = _tokenService.GenerateRefreshToken(GetIpAddress());
            user.RefreshTokens.Add(refreshToken);
            await _userManager.UpdateAsync(user);

            return new AuthResponse(
                _tokenService.CreateToken(user),
                refreshToken.Token,
                user.Email ?? string.Empty,
                user.FullName ?? string.Empty
            );
        }

        [HttpPost("login")]
        public async Task<ActionResult<AuthResponse>> Login(LoginRequest request)
        {
            var user = await _userManager.Users
                .Include(u => u.RefreshTokens)
                .SingleOrDefaultAsync(x => x.Email == request.Email);

            if (user == null) return Unauthorized("Invalid credentials.");

            var result = await _userManager.CheckPasswordAsync(user, request.Password);

            if (!result) return Unauthorized("Invalid credentials.");

            var refreshToken = _tokenService.GenerateRefreshToken(GetIpAddress());
            
            foreach (var t in user.RefreshTokens.Where(x => x.IsActive))
            {
                t.Revoked = DateTime.UtcNow;
                t.RevokedByIp = GetIpAddress();
                t.ReasonRevoked = "Replaced by new login";
            }

            user.RefreshTokens.Add(refreshToken);
            await _userManager.UpdateAsync(user);

            return new AuthResponse(
                _tokenService.CreateToken(user),
                refreshToken.Token,
                user.Email ?? string.Empty,
                user.FullName ?? string.Empty
            );
        }

        [HttpPost("refresh-token")]
        public async Task<ActionResult<AuthResponse>> RefreshToken([FromQuery] string token)
        {
            var user = await _userManager.Users
                .Include(u => u.RefreshTokens)
                .SingleOrDefaultAsync(u => u.RefreshTokens.Any(t => t.Token == token));

            if (user == null) return Unauthorized("Invalid token.");

            var refreshToken = user.RefreshTokens.Single(x => x.Token == token);

            if (!refreshToken.IsActive) return Unauthorized("Invalid token.");

            var newRefreshToken = _tokenService.GenerateRefreshToken(GetIpAddress());
            refreshToken.Revoked = DateTime.UtcNow;
            refreshToken.RevokedByIp = GetIpAddress();
            refreshToken.ReplacedByToken = newRefreshToken.Token;
            refreshToken.ReasonRevoked = "Token rotated";

            user.RefreshTokens.Add(newRefreshToken);
            await _userManager.UpdateAsync(user);

            return new AuthResponse(
                _tokenService.CreateToken(user),
                newRefreshToken.Token,
                user.Email ?? string.Empty,
                user.FullName ?? string.Empty
            );
        }

        [HttpPost("revoke-token")]
        public async Task<IActionResult> RevokeToken([FromQuery] string token)
        {
            var user = await _userManager.Users
                .Include(u => u.RefreshTokens)
                .SingleOrDefaultAsync(u => u.RefreshTokens.Any(t => t.Token == token));

            if (user == null) return NotFound("Token not found.");

            var refreshToken = user.RefreshTokens.Single(x => x.Token == token);

            if (!refreshToken.IsActive) return BadRequest("Token is already inactive.");

            refreshToken.Revoked = DateTime.UtcNow;
            refreshToken.RevokedByIp = GetIpAddress();
            refreshToken.ReasonRevoked = "Revoked by user";

            await _userManager.UpdateAsync(user);

            return Ok(new { message = "Token revoked" });
        }

        [HttpPost("forgot-password")]
        public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request)
        {
            var user = await _userManager.FindByEmailAsync(request.Email);
            
            if (user == null) 
            {
                return Ok(new { message = "If your email is registered, you will receive a reset token." });
            }

            var token = await _userManager.GeneratePasswordResetTokenAsync(user);

            if (string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"RESET TOKEN for {request.Email}: {token}");
            }

            return Ok(new { message = "If your email is registered, you will receive a reset token." });
        }

        [HttpPost("reset-password")]
        public async Task<IActionResult> ResetPassword(ResetPasswordRequest request)
        {
            var user = await _userManager.Users
                .Include(u => u.RefreshTokens)
                .SingleOrDefaultAsync(x => x.Email == request.Email);

            if (user == null) return BadRequest("Invalid request.");

            var result = await _userManager.ResetPasswordAsync(user, request.Token, request.NewPassword);

            if (!result.Succeeded)
            {
                return BadRequest(result.Errors);
            }

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
            else
                return HttpContext.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "unknown";
        }
    }
}
