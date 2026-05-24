using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using AccessCity.API.Common;
using AccessCity.API.Models;
using AccessCity.API.Services;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AccessCity.API.Controllers;

[Authorize]
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/admin/accessibility-verifications")]
public sealed class AdminAccessibilityController : ControllerBase
{
    private readonly IAccessibilityVerificationService _verifications;

    public AdminAccessibilityController(IAccessibilityVerificationService verifications)
    {
        _verifications = verifications;
    }

    /// <summary>
    /// Applies a reviewed field verification to the infrastructure accessibility profile.
    /// This updates POI/facility metadata only; route graph edge costs remain deterministic.
    /// </summary>
    [HttpPost("{submissionId:guid}/apply")]
    [ProducesResponseType(typeof(AccessibilityVerificationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AccessibilityVerificationResponse>> Apply(
        Guid submissionId,
        [FromBody] AccessibilityVerificationReviewRequest? request,
        CancellationToken cancellationToken)
    {
        var response = await _verifications.ApplyAsync(
            submissionId,
            ResolveUserId(),
            request?.Notes,
            cancellationToken);

        return response is null ? NotFound(new ApiError("Verification submission not found.")) : Ok(response);
    }

    /// <summary>
    /// Rejects a pending field verification without changing the accessibility profile.
    /// </summary>
    [HttpPost("{submissionId:guid}/reject")]
    [ProducesResponseType(typeof(AccessibilityVerificationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AccessibilityVerificationResponse>> Reject(
        Guid submissionId,
        [FromBody] AccessibilityVerificationReviewRequest? request,
        CancellationToken cancellationToken)
    {
        var response = await _verifications.RejectAsync(
            submissionId,
            ResolveUserId(),
            request?.Notes,
            cancellationToken);

        return response is null ? NotFound(new ApiError("Verification submission not found.")) : Ok(response);
    }

    private string ResolveUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue(JwtRegisteredClaimNames.NameId)
            ?? User.Identity?.Name
            ?? "unknown";
    }
}
