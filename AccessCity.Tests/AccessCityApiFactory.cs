using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AccessCity.API.Configuration;
using AccessCity.API.Data;
using AccessCity.API.Models.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace AccessCity.Tests;

public class AccessCityApiFactory : WebApplicationFactory<Program>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private readonly SemaphoreSlim _importLock = new(1, 1);
    private bool _osmImported;

    public AccessCityApiFactory()
    {
        EnvironmentBootstrap.LoadRepoRootDotEnv();

        var configuration = new ConfigurationBuilder()
            .SetBasePath(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../AccessCity.API")))
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        ConnectionString = PostgresConnectionStringResolver.Resolve(configuration);
        OsmFixturePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Fixtures", "test-network.osm"));

        ResetDatabaseState();
    }

    public string ConnectionString { get; }
    public string OsmFixturePath { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DATABASE_URL"] = string.Empty,
                ["Postgres:ConnectionString"] = ConnectionString,
                ["Postgres:AutoMigrate"] = "true",
                ["OsmImport:FilePath"] = OsmFixturePath,
                ["OsmImport:ImportOnStartup"] = "false",
                ["OsmImport:ReplaceExisting"] = "true"
            });
        });
    }

    public async Task<HttpClient> CreateAuthenticatedClientAsync(
        WebApplicationFactoryClientOptions? options = null,
        Action<HttpClient>? configureClient = null)
    {
        var client = options is null ? CreateClient() : CreateClient(options);
        configureClient?.Invoke(client);
        var registerRequest = new RegisterRequest(
            $"integration-{Guid.NewGuid():N}@example.com",
            "P@ssword123!",
            "Integration Test User");

        var response = await client.PostAsJsonAsync("/api/v1/auth/register", registerRequest, JsonOptions);
        response.EnsureSuccessStatusCode();

        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions)
            ?? throw new InvalidOperationException("Auth response did not contain a token.");

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", auth.Token);

        return client;
    }

    public async Task ImportOsmAsync(HttpClient client)
    {
        await _importLock.WaitAsync();
        try
        {
            if (_osmImported)
            {
                return;
            }

            var response = await client.PostAsync("/api/v1/admin/osm/import", content: null);
            response.EnsureSuccessStatusCode();
            _osmImported = true;
        }
        finally
        {
            _importLock.Release();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _importLock.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ResetDatabaseState()
    {
        using var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            DO $$
            DECLARE
                current_table text;
            BEGIN
                IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'AspNetUsers') THEN
                    FOREACH current_table IN ARRAY ARRAY[
                        'AspNetUserTokens',
                        'AspNetUserRoles',
                        'AspNetUserLogins',
                        'AspNetUserClaims',
                        'AspNetRoleClaims',
                        'AspNetRoles',
                        'refresh_token',
                        'refresh_tokens',
                        'hazard_report',
                        'hazard_reports',
                        'infrastructure_assets',
                        'route_edges',
                        'route_nodes',
                        'feed_ingestion_runs',
                        'AspNetUsers'
                    ]
                    LOOP
                        IF EXISTS (
                            SELECT 1
                            FROM information_schema.tables
                            WHERE table_schema = 'public'
                              AND table_name = current_table
                        ) THEN
                            EXECUTE format('TRUNCATE TABLE public.%I RESTART IDENTITY CASCADE', current_table);
                        END IF;
                    END LOOP;
                END IF;
            END $$;
            """;
        command.ExecuteNonQuery();
    }
}
