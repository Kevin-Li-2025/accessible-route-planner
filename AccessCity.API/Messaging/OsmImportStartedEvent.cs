namespace AccessCity.API.Messaging;

public record OsmImportStartedEvent(string FilePath, string CityName) : IntegrationEvent;
