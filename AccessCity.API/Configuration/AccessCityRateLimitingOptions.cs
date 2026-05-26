namespace AccessCity.API.Configuration;

public sealed class AccessCityRateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    public RateLimitWindowOptions Global { get; set; } = new()
    {
        PermitLimit = 30_000,
        QueueLimit = 0
    };

    public RateLimitWindowOptions Auth { get; set; } = new()
    {
        PermitLimit = 100,
        QueueLimit = 0
    };

    public RateLimitWindowOptions RoutingHeavy { get; set; } = new()
    {
        PermitLimit = 300,
        QueueLimit = 0
    };

    public RateLimitWindowOptions RoutingPoll { get; set; } = new()
    {
        PermitLimit = 900,
        QueueLimit = 0
    };

    public RateLimitWindowOptions HotRead { get; set; } = new()
    {
        PermitLimit = 1_200,
        QueueLimit = 0
    };

    public RateLimitWindowOptions Tile { get; set; } = new()
    {
        PermitLimit = 2_400,
        QueueLimit = 0
    };

    public RateLimitWindowOptions Write { get; set; } = new()
    {
        PermitLimit = 120,
        QueueLimit = 0
    };

    public RateLimitWindowOptions Upload { get; set; } = new()
    {
        PermitLimit = 30,
        QueueLimit = 0
    };

    public RateLimitWindowOptions AiAssist { get; set; } = new()
    {
        PermitLimit = 60,
        QueueLimit = 0
    };
}

public sealed class RateLimitWindowOptions
{
    public int PermitLimit { get; set; } = 100;
    public int QueueLimit { get; set; }
    public int WindowSeconds { get; set; } = 60;
    public int SegmentsPerWindow { get; set; } = 4;
}
