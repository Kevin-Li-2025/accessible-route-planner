using AccessCity.API.Models;
using AccessCity.API.Models.DTOs;
using AccessCity.API.Validators;
using NetTopologySuite.Geometries;
using Xunit;

namespace AccessCity.Tests;

/// <summary>
/// Direct unit tests for FluentValidation rules (no HTTP). Covers routing, risk-score query, and hazard report DTOs.
/// </summary>
public class ValidatorUnitTests
{
    private readonly RouteRequestValidator _routeValidator = new();
    private readonly RiskScoreRequestValidator _riskValidator = new();
    private readonly CreateHazardRequestValidator _hazardValidator = new();

    // --- RouteRequestValidator ---

    [Fact]
    public void RouteRequest_ValidStandardRequest_Passes()
    {
        var req = new RouteRequest
        {
            Start = new Coordinate(-1.89, 52.48),
            End = new Coordinate(-1.90, 52.49),
            Profile = "standard",
            SafetyWeight = 0.5,
        };
        var r = _routeValidator.Validate(req);
        Assert.True(r.IsValid, r.ToString());
    }

    [Theory]
    [InlineData("manual-wheelchair")]
    [InlineData("power-wheelchair")]
    [InlineData("stroller")]
    public void RouteRequest_AllSupportedProfiles_Pass(string profile)
    {
        var req = new RouteRequest
        {
            Start = new Coordinate(0, 0),
            End = new Coordinate(0.01, 0.01),
            Profile = profile,
            SafetyWeight = 1,
        };
        var r = _routeValidator.Validate(req);
        Assert.True(r.IsValid, r.ToString());
    }

    [Fact]
    public void RouteRequest_InvalidProfile_Fails()
    {
        var req = new RouteRequest
        {
            Start = new Coordinate(0, 0),
            End = new Coordinate(1, 1),
            Profile = "invalid-profile",
            SafetyWeight = 0.5,
        };
        var r = _routeValidator.Validate(req);
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.PropertyName == nameof(RouteRequest.Profile));
    }

    [Fact]
    public void RouteRequest_SafetyWeightAboveOne_Fails()
    {
        var req = new RouteRequest
        {
            Start = new Coordinate(0, 0),
            End = new Coordinate(1, 1),
            Profile = "standard",
            SafetyWeight = 1.01,
        };
        var r = _routeValidator.Validate(req);
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.PropertyName == nameof(RouteRequest.SafetyWeight));
    }

    [Fact]
    public void RouteRequest_StartLatitudeOutOfRange_Fails()
    {
        var req = new RouteRequest
        {
            Start = new Coordinate(0, 91),
            End = new Coordinate(0, 0),
            Profile = "standard",
            SafetyWeight = 0,
        };
        var r = _routeValidator.Validate(req);
        Assert.False(r.IsValid);
    }

    [Fact]
    public void RouteRequest_EndLongitudeOutOfRange_Fails()
    {
        var req = new RouteRequest
        {
            Start = new Coordinate(0, 0),
            End = new Coordinate(181, 0),
            Profile = "standard",
            SafetyWeight = 0,
        };
        var r = _routeValidator.Validate(req);
        Assert.False(r.IsValid);
    }

    // --- RiskScoreRequestValidator ---

    [Fact]
    public void RiskScore_DefaultRadius_Passes()
    {
        var dto = new RiskScoreRequestDto { Lat = 52.48, Lng = -1.89, Radius = 500 };
        var r = _riskValidator.Validate(dto);
        Assert.True(r.IsValid, r.ToString());
    }

    [Fact]
    public void RiskScore_RadiusZero_Fails()
    {
        var dto = new RiskScoreRequestDto { Lat = 0, Lng = 0, Radius = 0 };
        var r = _riskValidator.Validate(dto);
        Assert.False(r.IsValid);
    }

    [Fact]
    public void RiskScore_RadiusAboveMax_Fails()
    {
        var dto = new RiskScoreRequestDto { Lat = 0, Lng = 0, Radius = 5001 };
        var r = _riskValidator.Validate(dto);
        Assert.False(r.IsValid);
    }

    [Fact]
    public void RiskScore_LatOutOfRange_Fails()
    {
        var dto = new RiskScoreRequestDto { Lat = 91, Lng = 0, Radius = 100 };
        var r = _riskValidator.Validate(dto);
        Assert.False(r.IsValid);
    }

    // --- CreateHazardRequestValidator ---

    [Fact]
    public void CreateHazard_Valid_Passes()
    {
        var req = new CreateHazardRequest
        {
            Location = new Coordinate(-1.89, 52.48),
            Type = "broken_pavement",
            Description = "Trip hazard near crossing.",
            PhotoUrl = null,
        };
        var r = _hazardValidator.Validate(req);
        Assert.True(r.IsValid, r.ToString());
    }

    [Fact]
    public void CreateHazard_EmptyType_Fails()
    {
        var req = new CreateHazardRequest
        {
            Location = new Coordinate(0, 0),
            Type = "",
            Description = "x",
        };
        var r = _hazardValidator.Validate(req);
        Assert.False(r.IsValid);
    }

    [Fact]
    public void CreateHazard_TypeTooLong_Fails()
    {
        var req = new CreateHazardRequest
        {
            Location = new Coordinate(0, 0),
            Type = new string('x', 51),
            Description = "ok",
        };
        var r = _hazardValidator.Validate(req);
        Assert.False(r.IsValid);
    }

    [Fact]
    public void CreateHazard_DescriptionTooLong_Fails()
    {
        var req = new CreateHazardRequest
        {
            Location = new Coordinate(0, 0),
            Type = "t",
            Description = new string('a', 501),
        };
        var r = _hazardValidator.Validate(req);
        Assert.False(r.IsValid);
    }

    [Fact]
    public void CreateHazard_PhotoUrlTooLong_Fails()
    {
        var req = new CreateHazardRequest
        {
            Location = new Coordinate(0, 0),
            Type = "t",
            Description = "d",
            PhotoUrl = "https://x.com/" + new string('p', 2100),
        };
        var r = _hazardValidator.Validate(req);
        Assert.False(r.IsValid);
    }
}
