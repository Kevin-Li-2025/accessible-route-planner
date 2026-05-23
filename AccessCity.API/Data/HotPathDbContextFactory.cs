using AccessCity.API.Configuration;
using AccessCity.API.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace AccessCity.API.Data;

public interface IHotPathDbContextFactory
{
    HotPathDbContextLease CreateDbContext();
    bool UsesReadOnlyConnection { get; }
}

public sealed class HotPathDbContextFactory : IHotPathDbContextFactory
{
    private readonly AppDbContext _scopedDbContext;
    private readonly DbContextOptions<AppDbContext>? _readOnlyOptions;

    public HotPathDbContextFactory(
        AppDbContext scopedDbContext,
        IConfiguration configuration,
        IOptions<PostgresOptions> options)
    {
        _scopedDbContext = scopedDbContext;
        var postgresOptions = options.Value;
        if (!postgresOptions.UseReadOnlyForHotPaths)
        {
            return;
        }

        var connectionString = PostgresConnectionStringResolver.ResolveReadOnly(configuration, postgresOptions);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var schema = PostgresConnectionStringResolver.GetPrimarySearchPath(connectionString);
        var builder = new DbContextOptionsBuilder<AppDbContext>()
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        builder.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        builder.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.UseNetTopologySuite();
            npgsql.MapEnum<DatabaseHazardStatus>("hazard_status");
            npgsql.CommandTimeout(Math.Max(1, postgresOptions.CommandTimeoutSeconds));
            if (!string.IsNullOrWhiteSpace(schema))
            {
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", schema);
            }
        });

        _readOnlyOptions = builder.Options;
    }

    public bool UsesReadOnlyConnection => _readOnlyOptions is not null;

    public HotPathDbContextLease CreateDbContext()
    {
        if (_readOnlyOptions is null)
        {
            return HotPathDbContextLease.Borrowed(_scopedDbContext);
        }

        return HotPathDbContextLease.Owned(new AppDbContext(_readOnlyOptions));
    }
}

public sealed class HotPathDbContextLease : IAsyncDisposable
{
    private readonly bool _ownsContext;

    private HotPathDbContextLease(AppDbContext context, bool ownsContext)
    {
        Context = context;
        _ownsContext = ownsContext;
    }

    public AppDbContext Context { get; }

    public static HotPathDbContextLease Borrowed(AppDbContext context) => new(context, ownsContext: false);

    public static HotPathDbContextLease Owned(AppDbContext context) => new(context, ownsContext: true);

    public ValueTask DisposeAsync()
    {
        return _ownsContext ? Context.DisposeAsync() : ValueTask.CompletedTask;
    }
}
