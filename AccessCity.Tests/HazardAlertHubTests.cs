using AccessCity.API.Controllers;
using AccessCity.API.Hubs;
using AccessCity.API.Models;
using AccessCity.API.Models.DTOs;
using AccessCity.API.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Moq;
using NetTopologySuite.Geometries;

namespace AccessCity.Tests;

public sealed class HazardAlertHubTests
{
    [Fact]
    public async Task JoinRouteGroup_and_LeaveRouteGroup_use_connection_group()
    {
        var context = new Mock<HubCallerContext>();
        context.SetupGet(c => c.ConnectionId).Returns("conn-1");

        var groups = new Mock<IGroupManager>();
        groups
            .Setup(g => g.AddToGroupAsync("conn-1", "rt-1", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        groups
            .Setup(g => g.RemoveFromGroupAsync("conn-1", "rt-1", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var hub = new HazardAlertHub
        {
            Context = context.Object,
            Groups = groups.Object
        };

        await hub.JoinRouteGroup("rt-1");
        await hub.LeaveRouteGroup("rt-1");

        groups.Verify(g => g.AddToGroupAsync("conn-1", "rt-1", It.IsAny<CancellationToken>()), Times.Once);
        groups.Verify(g => g.RemoveFromGroupAsync("conn-1", "rt-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReportHazard_broadcasts_hazard_reported_alert()
    {
        var reportedAt = DateTime.UtcNow;
        var report = new HazardReport
        {
            Id = Guid.NewGuid(),
            Location = new Point(-1.8912, 52.481) { SRID = 4326 },
            Type = "hub_signal_test",
            Description = "SignalR broadcast check",
            PhotoUrl = "",
            ReportedAt = reportedAt,
            Status = HazardStatus.Reported
        };

        var hazards = new Mock<IHazardReportService>();
        hazards
            .Setup(h => h.CreateAsync(It.IsAny<CreateHazardRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(report);

        var clientProxy = new Mock<IClientProxy>();
        clientProxy
            .Setup(p => p.SendCoreAsync(
                "HazardReported",
                It.Is<object[]>(args => IsExpectedAlert(args, report, reportedAt)),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.All).Returns(clientProxy.Object);

        var hubContext = new Mock<IHubContext<HazardAlertHub>>();
        hubContext.Setup(c => c.Clients).Returns(clients.Object);

        var controller = new HazardsController(hazards.Object, hubContext.Object);

        var result = await controller.ReportHazard(
            new CreateHazardRequest
            {
                Location = new Coordinate(-1.8912, 52.481),
                Type = report.Type,
                Description = report.Description,
                PhotoUrl = report.PhotoUrl
            },
            CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(result.Result);
        clientProxy.VerifyAll();
    }

    private static bool IsExpectedAlert(object[] args, HazardReport report, DateTime reportedAt)
    {
        if (args.Length != 1 || args[0] is not RouteAlert alert)
        {
            return false;
        }

        return alert.Type == report.Type
               && alert.Description == report.Description
               && alert.Latitude == report.Location.Y
               && alert.Longitude == report.Location.X
               && alert.Timestamp == reportedAt;
    }
}
