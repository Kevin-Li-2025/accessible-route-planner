using AccessCity.API.Configuration;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace AccessCity.Tests;

public class PostgresConnectionStringResolverTests
{
    [Fact]
    public void Resolve_UsesPooledDatabaseUrlByDefault()
    {
        var configuration = BuildConfiguration();

        var resolved = PostgresConnectionStringResolver.Resolve(
            configuration,
            new PostgresOptions { MaxPoolSize = 20 });

        var builder = new NpgsqlConnectionStringBuilder(resolved);
        Assert.Equal("accesscity-postgres-rw-pooler", builder.Host);
        Assert.Equal(20, builder.MaxPoolSize);
    }

    [Fact]
    public void Resolve_UsesDirectDatabaseUrlWhenRequested()
    {
        var configuration = BuildConfiguration();

        var resolved = PostgresConnectionStringResolver.Resolve(
            configuration,
            new PostgresOptions { UseDirectDatabaseUrl = true, MaxPoolSize = 5 });

        var builder = new NpgsqlConnectionStringBuilder(resolved);
        Assert.Equal("accesscity-postgres-rw", builder.Host);
        Assert.Equal(5, builder.MaxPoolSize);
    }

    private static IConfiguration BuildConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DATABASE_URL"] = "postgresql://accesscity:secret@accesscity-postgres-rw-pooler:5432/accesscitydb?sslmode=disable",
                ["DIRECT_DATABASE_URL"] = "postgresql://accesscity:secret@accesscity-postgres-rw:5432/accesscitydb?sslmode=require"
            })
            .Build();
}
