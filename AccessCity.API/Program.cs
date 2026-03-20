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
        .AddApplicationServices()
        .AddSecurity(builder.Configuration, builder.Environment)
        .AddObservability(builder.Configuration)
        .AddWebServices();

    var app = builder.Build();

    // Configure Pipeline
    app.ConfigurePipeline();

    // Initialize Database
    await app.InitializeDatabaseAsync();

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
