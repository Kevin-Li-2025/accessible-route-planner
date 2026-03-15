using AccessCity.API.Services;
using AccessCity.API.Data;
using AccessCity.API.Models.Identity;
using AccessCity.API.Services.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// ── Serialisation: GeoJSON support via NetTopologySuite ──
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        var factory = new NetTopologySuite.IO.Converters.GeoJsonConverterFactory();
        options.JsonSerializerOptions.Converters.Add(factory);
        options.JsonSerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals;
    });

// ── Database: In-Memory (No setup required) ──
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseInMemoryDatabase("AccessCityMemoryDb"));

// ── Identity Setup with Argon2 Hashing ──
builder.Services.AddIdentityCore<AccessCityUser>(options =>
{
    options.Password.RequireDigit = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireNonAlphanumeric = true;
    options.Password.RequiredLength = 8;
    options.User.RequireUniqueEmail = true;
})
.AddEntityFrameworkStores<AppDbContext>()
.AddDefaultTokenProviders();

builder.Services.AddScoped<IPasswordHasher<AccessCityUser>, Argon2PasswordHasher<AccessCityUser>>();

// ── Authentication & JWT ──
var jwtKey = builder.Configuration["Jwt:Key"] ?? "AccessCity_Secret_Key_For_Dev_2026_Placeholder";
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"],
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };
    });

builder.Services.AddAuthorization();

// ── Core domain services ──
builder.Services.AddSingleton<RiskScoringService>();
builder.Services.AddSingleton<RoutingService>();
builder.Services.AddScoped<ITokenService, TokenService>();

// ── Rate Limiting (Security Best Practice) ──
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("auth", opt =>
    {
        opt.PermitLimit = 5;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = 0;
    });
});

// ── External Data Clients (Risk & Safe Haven integrations) ──
builder.Services.AddHttpClient<AccessCity.API.Services.External.IOpenStreetMapClient, AccessCity.API.Services.External.OverpassApiClient>();
builder.Services.AddHttpClient<AccessCity.API.Services.External.IUkPoliceDataClient, AccessCity.API.Services.External.UkPoliceDataClient>();
builder.Services.AddHttpClient<AccessCity.API.Services.External.ISafeHavenPlacesClient, AccessCity.API.Services.External.GooglePlacesClient>();
builder.Services.AddHttpClient<AccessCity.API.Services.External.ILiveHazardClient, AccessCity.API.Services.External.OpenWeatherClient>();

// ── OpenAPI / Swagger ──
builder.Services.AddOpenApi();

// ── CORS (allow the React Native and React dashboard clients) ──
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();

app.Run();

public partial class Program { }
