namespace AccessCity.API.Configuration;

public sealed class HotPathWarmupOptions
{
    public const string SectionName = "HotPathWarmup";

    public bool Enabled { get; set; }
    public int InitialDelaySeconds { get; set; } = 2;
    public int IntervalSeconds { get; set; } = 300;
    public int TimeoutSeconds { get; set; } = 20;
    public int MaxPoints { get; set; } = 16;
    public int BucketCorridorRadiusSteps { get; set; }
    public double BucketCorridorStepDegrees { get; set; } = 0.0001;
    public double RiskRadiusMetres { get; set; } = 500;
    public double PoiRadiusMetres { get; set; } = 750;
    public bool WarmReadiness { get; set; } = true;
    public bool WarmRisk { get; set; } = true;
    public bool WarmPoi { get; set; } = true;
    public bool WarmRouteGraph { get; set; }
    public List<HotPathWarmupPointOptions> Points { get; set; } = new();
}

public sealed class HotPathWarmupPointOptions
{
    public string Name { get; set; } = "point";
    public double Lat { get; set; }
    public double Lng { get; set; }
}
