using AccessCity.API.Configuration;
using AccessCity.API.Extensions;
using Serilog;

EnvironmentBootstrap.LoadRepoRootDotEnv();

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    var keyPerFilePath = builder.Configuration["Secrets:KeyPerFilePath"]
        ?? Environment.GetEnvironmentVariable("ACCESSCITY_SECRETS_PATH")
        ?? "/mnt/secrets";
    if (Directory.Exists(keyPerFilePath))
    {
        builder.Configuration.AddKeyPerFile(keyPerFilePath, optional: true);
    }

    builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console());

    // Add Services
    builder.Services
        .AddDatabase(builder.Configuration, builder.Environment)
        .AddInfrastructure(builder.Configuration)
        .AddMessaging(builder.Configuration)
            .AddApplicationServices(builder.Configuration)
            .AddSecurity(builder.Configuration, builder.Environment)
            .AddObservability(builder.Configuration)
            .AddWebServices(builder.Configuration, builder.Environment);

    var app = builder.Build();
    var migrateAndExit = builder.Configuration.GetValue<bool>("Postgres:MigrateAndExit")
        || args.Any(arg => string.Equals(arg, "--migrate-and-exit", StringComparison.OrdinalIgnoreCase));
    var routeGraphProfileAndExit = builder.Configuration.GetValue<bool>("Routing:RouteGraphProfileAndExit")
        || args.Any(arg => string.Equals(arg, "--profile-route-graph-and-exit", StringComparison.OrdinalIgnoreCase));
    var routeGraphReleaseBuildAndExit = builder.Configuration.GetValue<bool>("Routing:RouteGraphReleaseBuildAndExit")
        || args.Any(arg => string.Equals(arg, "--build-route-graph-release", StringComparison.OrdinalIgnoreCase));
    var routeGraphReleaseValidateAndExit = builder.Configuration.GetValue<bool>("Routing:RouteGraphReleaseValidateAndExit")
        || args.Any(arg => string.Equals(arg, "--validate-route-graph-release", StringComparison.OrdinalIgnoreCase));
    var routeGraphProfileUsesOsmExtract = builder.Configuration.GetValue<bool>("Routing:RouteGraphProfileUseOsmExtract")
        && !string.IsNullOrWhiteSpace(builder.Configuration["OsmImport:FilePath"]);

    // Configure Pipeline
    app.ConfigurePipeline();

    if (routeGraphProfileAndExit && routeGraphProfileUsesOsmExtract && !migrateAndExit)
    {
        await app.RunRouteGraphProfileCommandAsync();
        Log.Information("Route graph OSM extract profile completed; exiting before database initialization.");
        return;
    }

    // Initialize Database
    await app.InitializeDatabaseAsync();
    if (migrateAndExit)
    {
        Log.Information("Database migration completed; exiting because Postgres:MigrateAndExit is enabled.");
        return;
    }

    if (routeGraphProfileAndExit)
    {
        await app.RunRouteGraphProfileCommandAsync();
        Log.Information("Route graph profile completed; exiting because Routing:RouteGraphProfileAndExit is enabled.");
        return;
    }

    if (routeGraphReleaseBuildAndExit || routeGraphReleaseValidateAndExit)
    {
        await app.RunRouteGraphReleaseCommandAsync(
            buildRelease: routeGraphReleaseBuildAndExit,
            validateRelease: routeGraphReleaseValidateAndExit || routeGraphReleaseBuildAndExit);
        Log.Information("Route graph release command completed; exiting.");
        return;
    }

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    throw; // Re-throw so WebApplicationFactory sees the failure
}
finally
{
    Log.CloseAndFlush();
}

public partial class Program { }
