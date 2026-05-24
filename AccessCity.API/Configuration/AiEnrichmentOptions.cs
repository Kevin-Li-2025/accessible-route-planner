namespace AccessCity.API.Configuration;

public sealed class AiEnrichmentOptions
{
    public const string SectionName = "AiEnrichment";

    public bool Enabled { get; set; } = true;
    public string Provider { get; set; } = "local-rules";
    public int DuplicateRadiusMetres { get; set; } = 25;
    public double MinimumCandidateConfidence { get; set; } = 0.35;
    public bool AllowRouteDecisionInfluence { get; set; }
    public int MaxExplanationWarnings { get; set; } = 5;
    public string OpenAiApiKey { get; set; } = string.Empty;
    public string OpenAiEndpoint { get; set; } = "https://api.openai.com/v1/responses";
    public string OpenAiModel { get; set; } = "gpt-5-mini";
    public int OpenAiTimeoutSeconds { get; set; } = 8;
    public int OpenAiMaxOutputTokens { get; set; } = 900;
    public string NebiusApiKey { get; set; } = string.Empty;
    public string NebiusBaseUrl { get; set; } = "https://api.tokenfactory.nebius.com/v1";
    public string NebiusModel { get; set; } = "openai/gpt-oss-120b-fast";
    public string NebiusHighQualityModel { get; set; } = "Qwen/Qwen3.5-397B-A17B-fast";
    public int NebiusTimeoutSeconds { get; set; } = 8;
    public int NebiusMaxTokens { get; set; } = 700;
    public bool NebiusEnableImageInputs { get; set; }
    public string VisionModelEndpoint { get; set; } = string.Empty;
    public int VisionModelTimeoutSeconds { get; set; } = 5;
    public double VisionModelMinimumConfidence { get; set; } = 0.55;
    public int MaxAccessibilityObservationChars { get; set; } = 2_000;
    public int MaxAccessibilityPhotos { get; set; } = 4;
}
