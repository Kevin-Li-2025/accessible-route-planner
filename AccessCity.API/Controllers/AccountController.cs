using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using AccessCity.API.Common;
using AccessCity.API.Models;
using AccessCity.API.Models.DTOs;
using AccessCity.API.Models.Identity;
using AccessCity.API.Security;
using AccessCity.API.Services;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace AccessCity.API.Controllers;

[Authorize]
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/account")]
public sealed class AccountController : ControllerBase
{
    private const string NotificationSettingsClaimType = "accesscity:notification-settings";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly AccessibilityPreferenceDto DefaultAccessibilityPreferences = new(
        "Manual wheelchair",
        AvoidStairs: true,
        AvoidSteepIncline: true,
        PreferCurbRamps: true,
        PreferSmoothSurface: true,
        MaxDetourToleranceMinutes: 30);
    private static readonly NotificationSettingsDto DefaultNotificationSettings = new(
        HazardAlerts: true,
        RouteWarnings: true,
        ReportUpdates: true,
        WeeklySummary: false);

    private readonly UserManager<AccessCityUser> _userManager;
    private readonly IAccountService _accountService;

    public AccountController(UserManager<AccessCityUser> userManager, IAccountService accountService)
    {
        _userManager = userManager;
        _accountService = accountService;
    }

    [HttpGet("profile")]
    [EnableRateLimiting(AccessCityRateLimitPolicies.HotRead)]
    [RequestTimeout(AccessCityRequestTimeoutPolicies.ShortRead)]
    [ProducesResponseType(typeof(AccountProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AccountProfileResponse>> GetProfile(CancellationToken cancellationToken)
    {
        var user = await ResolveUserAsync(cancellationToken);
        if (user is null) return Unauthorized(new ApiError("User session is invalid."));

        return Ok(await ToProfileResponseAsync(user, cancellationToken));
    }

    [HttpPut("profile")]
    [EnableRateLimiting(AccessCityRateLimitPolicies.Write)]
    [ProducesResponseType(typeof(AccountProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AccountProfileResponse>> UpdateProfile(
        [FromBody] UpdateAccountProfileRequest request,
        CancellationToken cancellationToken)
    {
        var user = await ResolveUserAsync(cancellationToken);
        if (user is null) return Unauthorized(new ApiError("User session is invalid."));

        if (request.FullName is not null)
        {
            var fullName = request.FullName.Trim();
            if (fullName.Length is < 1 or > 150)
            {
                return BadRequest(new ApiError("Full name must be between 1 and 150 characters."));
            }

            user.FullName = fullName;
        }

        if (request.AccessibilityPreferences is not null)
        {
            user.PreferredRoutes = ToPreferenceTokens(NormalizePreferences(request.AccessibilityPreferences));
        }

        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            return BadRequest(new ApiError(
                "Profile update failed.",
                Detail: string.Join(", ", result.Errors.Select(e => e.Description))));
        }

        return Ok(await ToProfileResponseAsync(user, cancellationToken));
    }

    [HttpGet("notifications")]
    [EnableRateLimiting(AccessCityRateLimitPolicies.HotRead)]
    [RequestTimeout(AccessCityRequestTimeoutPolicies.ShortRead)]
    [ProducesResponseType(typeof(NotificationSettingsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<NotificationSettingsDto>> GetNotificationSettings(CancellationToken cancellationToken)
    {
        var user = await ResolveUserAsync(cancellationToken);
        if (user is null) return Unauthorized(new ApiError("User session is invalid."));

        return Ok(await LoadNotificationSettingsAsync(user));
    }

    [HttpPut("notifications")]
    [EnableRateLimiting(AccessCityRateLimitPolicies.Write)]
    [ProducesResponseType(typeof(NotificationSettingsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<NotificationSettingsDto>> UpdateNotificationSettings(
        [FromBody] NotificationSettingsDto request,
        CancellationToken cancellationToken)
    {
        var user = await ResolveUserAsync(cancellationToken);
        if (user is null) return Unauthorized(new ApiError("User session is invalid."));

        var currentClaims = await _userManager.GetClaimsAsync(user);
        foreach (var claim in currentClaims.Where(c => c.Type == NotificationSettingsClaimType))
        {
            var removeResult = await _userManager.RemoveClaimAsync(user, claim);
            if (!removeResult.Succeeded)
            {
                return BadRequest(new ApiError("Notification settings update failed."));
            }
        }

        var addResult = await _userManager.AddClaimAsync(
            user,
            new Claim(NotificationSettingsClaimType, JsonSerializer.Serialize(request, JsonOptions)));

        if (!addResult.Succeeded)
        {
            return BadRequest(new ApiError("Notification settings update failed."));
        }

        return Ok(request);
    }

    [HttpPost("support/contact")]
    [EnableRateLimiting(AccessCityRateLimitPolicies.Write)]
    [ProducesResponseType(typeof(SupportContactResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SupportContactResponse>> SubmitSupportContact(
        [FromBody] SupportContactRequest request,
        CancellationToken cancellationToken)
    {
        var user = await ResolveUserAsync(cancellationToken);
        if (user is null) return Unauthorized(new ApiError("User session is invalid."));

        var subject = request.Subject.Trim();
        var message = request.Message.Trim();
        if (subject.Length is < 3 or > 160)
        {
            return BadRequest(new ApiError("Subject must be between 3 and 160 characters."));
        }

        if (message.Length is < 10 or > 4000)
        {
            return BadRequest(new ApiError("Message must be between 10 and 4000 characters."));
        }

        var submission = await _accountService.CreateSupportContactAsync(
            user.Id,
            user.Email ?? string.Empty,
            user.FullName ?? string.Empty,
            NormalizeCategory(request.Category),
            subject,
            message,
            cancellationToken);

        return Created(
            $"/api/v1/account/support/contact/{submission.Id}",
            submission);
    }

    private async Task<AccessCityUser?> ResolveUserAsync(CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(JwtRegisteredClaimNames.NameId)
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return null;

        return await _userManager.Users
            .SingleOrDefaultAsync(user => user.Id == userId, cancellationToken);
    }

    private async Task<NotificationSettingsDto> LoadNotificationSettingsAsync(AccessCityUser user)
    {
        var claim = (await _userManager.GetClaimsAsync(user))
            .LastOrDefault(c => c.Type == NotificationSettingsClaimType);
        if (claim is null || string.IsNullOrWhiteSpace(claim.Value))
        {
            return DefaultNotificationSettings;
        }

        try
        {
            return JsonSerializer.Deserialize<NotificationSettingsDto>(claim.Value, JsonOptions)
                ?? DefaultNotificationSettings;
        }
        catch (JsonException)
        {
            return DefaultNotificationSettings;
        }
    }

    private async Task<AccountProfileResponse> ToProfileResponseAsync(
        AccessCityUser user,
        CancellationToken cancellationToken)
    {
        var stats = await _accountService.GetProfileStatsAsync(user.Id, cancellationToken);

        return new AccountProfileResponse(
            user.Email ?? string.Empty,
            user.FullName ?? string.Empty,
            FromPreferenceTokens(user.PreferredRoutes),
            stats);
    }

    private static AccessibilityPreferenceDto FromPreferenceTokens(IEnumerable<string>? tokens)
    {
        var set = new HashSet<string>(tokens ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        if (set.Count == 0)
        {
            return DefaultAccessibilityPreferences;
        }

        var mobility = set.FirstOrDefault(token => token.StartsWith("mobility:", StringComparison.OrdinalIgnoreCase))
            ?.Split(':', 2)[1]
            ?? DefaultAccessibilityPreferences.MobilityDevice;
        var detour = set.FirstOrDefault(token => token.StartsWith("max-detour-minutes:", StringComparison.OrdinalIgnoreCase))
            ?.Split(':', 2)[1];

        return NormalizePreferences(new AccessibilityPreferenceDto(
            mobility,
            set.Contains("avoid-stairs"),
            set.Contains("avoid-steep-incline"),
            set.Contains("prefer-curb-ramps"),
            set.Contains("prefer-smooth-surface"),
            int.TryParse(detour, out var minutes) ? minutes : DefaultAccessibilityPreferences.MaxDetourToleranceMinutes));
    }

    private static List<string> ToPreferenceTokens(AccessibilityPreferenceDto preferences)
    {
        var tokens = new List<string> { $"mobility:{preferences.MobilityDevice}", $"max-detour-minutes:{preferences.MaxDetourToleranceMinutes}" };
        if (preferences.AvoidStairs) tokens.Add("avoid-stairs");
        if (preferences.AvoidSteepIncline) tokens.Add("avoid-steep-incline");
        if (preferences.PreferCurbRamps) tokens.Add("prefer-curb-ramps");
        if (preferences.PreferSmoothSurface) tokens.Add("prefer-smooth-surface");
        return tokens;
    }

    private static AccessibilityPreferenceDto NormalizePreferences(AccessibilityPreferenceDto preferences)
    {
        var mobility = preferences.MobilityDevice.Trim();
        var allowedMobility = new[] { "Manual wheelchair", "Power wheelchair", "Stroller", "Walking" };
        if (!allowedMobility.Contains(mobility, StringComparer.OrdinalIgnoreCase))
        {
            mobility = DefaultAccessibilityPreferences.MobilityDevice;
        }
        else
        {
            mobility = allowedMobility.First(value => string.Equals(value, mobility, StringComparison.OrdinalIgnoreCase));
        }

        return preferences with
        {
            MobilityDevice = mobility,
            MaxDetourToleranceMinutes = Math.Clamp(preferences.MaxDetourToleranceMinutes, 5, 60)
        };
    }

    private static string NormalizeCategory(string? category)
    {
        var normalized = string.IsNullOrWhiteSpace(category) ? "general" : category.Trim().ToLowerInvariant();
        if (normalized.Length > 80) normalized = normalized[..80];
        return normalized;
    }
}
