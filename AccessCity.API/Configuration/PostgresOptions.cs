namespace AccessCity.API.Configuration;

public sealed class PostgresOptions
{
    public const string SectionName = "Postgres";

    public string? ConnectionString { get; set; }
    public string? DatabaseUrl { get; set; }
    public bool AutoMigrate { get; set; } = true;
    public bool AutoSchemaMaintenance { get; set; } = true;
    public int CommandTimeoutSeconds { get; set; } = 30;
    public bool UseStartupSessionParameters { get; set; }
    public int? StatementTimeoutMs { get; set; }
    public int? IdleInTransactionSessionTimeoutMs { get; set; }
    public int? MaxPoolSize { get; set; }
    public int? MinPoolSize { get; set; }
    public int DbContextPoolSize { get; set; } = 128;
}
