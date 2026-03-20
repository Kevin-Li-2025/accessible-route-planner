# ── Stage 1: Build ──────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy csproj first for layer-cached restore
COPY AccessCity.API.csproj ./
RUN dotnet restore

# Copy everything else and publish
COPY . ./
RUN dotnet publish -c Release -o /app/publish --no-restore

# ── Stage 2: Runtime ────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Create a non-root user for security
RUN adduser --disabled-password --gecos "" appuser

COPY --from=build /app/publish .
COPY --from=build /src/london.osm ./london.osm
COPY --from=build /src/birmingham.osm ./birmingham.osm

# Expose the HTTP port
EXPOSE 8080

# Run as non-root
USER appuser

ENTRYPOINT ["dotnet", "AccessCity.API.dll"]
