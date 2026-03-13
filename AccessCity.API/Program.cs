using AccessCity.API.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Serialisation: GeoJSON support via NetTopologySuite ──
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        var factory = new NetTopologySuite.IO.Converters.GeoJsonConverterFactory();
        options.JsonSerializerOptions.Converters.Add(factory);
        options.JsonSerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals;
    });

// ── Core domain services ──
builder.Services.AddSingleton<RiskScoringService>();
builder.Services.AddSingleton<RoutingService>();

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
app.MapControllers();

app.Run();
