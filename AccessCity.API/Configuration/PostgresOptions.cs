namespace AccessCity.API.Configuration;

public sealed class PostgresOptions
{
    public const string SectionName = "Postgres";

    public string? ConnectionString { get; set; }
    public string? DatabaseUrl { get; set; }
    public bool AutoMigrate { get; set; } = true;
}
