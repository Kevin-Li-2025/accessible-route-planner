namespace AccessCity.API.Configuration;

public sealed class AccessCityRequestProtectionOptions
{
    public const string SectionName = "RequestProtection";

    public int DefaultTimeoutSeconds { get; set; } = 15;
    public int ShortReadTimeoutSeconds { get; set; } = 5;
    public int RouteSyncTimeoutSeconds { get; set; } = 6;
    public int RouteAsyncSubmitTimeoutSeconds { get; set; } = 3;
    public int AiAssistTimeoutSeconds { get; set; } = 10;
    public int UploadTimeoutSeconds { get; set; } = 15;
    public long MaxRequestBodyBytes { get; set; } = 10 * 1024 * 1024;
    public int RequestHeadersTimeoutSeconds { get; set; } = 10;
    public int KeepAliveTimeoutSeconds { get; set; } = 65;
    public long? MaxConcurrentConnections { get; set; }
}
