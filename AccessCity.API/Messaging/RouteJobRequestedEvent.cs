using AccessCity.API.Models;

namespace AccessCity.API.Messaging;

public sealed record RouteJobRequestedEvent(
    string JobId,
    RouteRequest Request,
    DateTime SubmittedAtUtc) : IntegrationEvent, IKeyedIntegrationEvent
{
    public string PartitionKey => JobId;
}
