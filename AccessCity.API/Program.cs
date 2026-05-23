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

    // Configure Pipeline
    app.ConfigurePipeline();

    // Initialize Database
    await app.InitializeDatabaseAsync();
    if (migrateAndExit)
    {
        Log.Information("Database migration completed; exiting because Postgres:MigrateAndExit is enabled.");
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
