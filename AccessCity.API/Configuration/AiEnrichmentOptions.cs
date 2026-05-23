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
}
