namespace AccessCity.API.Security;

public static class AccessCityRateLimitPolicies
{
    public const string Auth = "auth";
    public const string RoutingHeavy = "routing-heavy";
    public const string RoutingPoll = "routing-poll";
    public const string HotRead = "hot-read";
    public const string Tile = "tile";
    public const string Write = "write";
    public const string Upload = "upload";
    public const string AiAssist = "ai-assist";
}

public static class AccessCityRequestTimeoutPolicies
{
    public const string ShortRead = "short-read";
    public const string RouteSync = "route-sync";
    public const string RouteAsyncSubmit = "route-async-submit";
    public const string AiAssist = "ai-assist";
    public const string Upload = "upload";
}
