using AccessCity.API.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AccessCity.API.Data;

public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        EnvironmentBootstrap.LoadRepoRootDotEnv();

        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = PostgresConnectionStringResolver.Resolve(configuration);
        var historySchema = PostgresConnectionStringResolver.GetPrimarySearchPath(connectionString);

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.UseNetTopologySuite();
            if (!string.IsNullOrWhiteSpace(historySchema))
            {
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", historySchema);
            }
        });

        return new AppDbContext(optionsBuilder.Options);
    }
}
