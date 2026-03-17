using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using AccessCity.API.Data;
using AccessCity.API.Models.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AccessCity.Tests
{
    public class AccessCityApiFactory : WebApplicationFactory<Program>
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
        };

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            
            builder.ConfigureAppConfiguration((context, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    {"ConnectionStrings:DefaultConnection", "InMemory"}
                });
            });

            builder.ConfigureServices(services =>
            {
                // The API already uses In-Memory if "db" is not reachable or it's hardcoded to In-Memory in Program.cs.
                // We ensure it stays that way for tests by removing any existing SQL registration if it existed.
                var descriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (descriptor != null) services.Remove(descriptor);

                services.AddDbContext<AppDbContext>(options =>
                {
                    options.UseInMemoryDatabase("IntegrationTestDb");
                });
            });
        }

        public async Task<HttpClient> CreateAuthenticatedClientAsync(WebApplicationFactoryClientOptions? options = null)
        {
            var client = options is null ? CreateClient() : CreateClient(options);
            var registerRequest = new RegisterRequest(
                $"integration-{Guid.NewGuid():N}@example.com",
                "P@ssword123!",
                "Integration Test User");

            var response = await client.PostAsJsonAsync("/api/auth/register", registerRequest, JsonOptions);
            response.EnsureSuccessStatusCode();

            var auth = await response.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions)
                ?? throw new InvalidOperationException("Auth response did not contain a token.");

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", auth.Token);

            return client;
        }
    }
}
