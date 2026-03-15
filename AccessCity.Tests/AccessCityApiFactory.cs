using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using AccessCity.API.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Linq;

namespace AccessCity.Tests
{
    public class AccessCityApiFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            
            builder.ConfigureAppConfiguration((context, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    {"Jwt:Key", "AccessCity_Secret_Key_For_Integration_Tests_2026_Placeholder_Long_Enough"},
                    {"Jwt:Issuer", "AccessCity.API"},
                    {"Jwt:Audience", "AccessCity.App"},
                    {"Jwt:AccessTokenExpirationMinutes", "15"},
                    {"Jwt:RefreshTokenExpirationDays", "7"},
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
    }
}
