namespace AccessCity.API.Configuration;

public sealed class OsmImportOptions
{
    public const string SectionName = "OsmImport";

    public string? FilePath { get; set; }
    public bool ImportOnStartup { get; set; }
    public bool ReplaceExisting { get; set; } = true;
}
