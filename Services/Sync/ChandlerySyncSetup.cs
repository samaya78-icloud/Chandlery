using Chandlery.Models;
using Marine.Sync;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Chandlery.Services.Sync;

/// <summary>
/// Switches sync on from configuration.
///   Sync:Mode = Off (default) | Hub (the office) | Node (a vessel)
/// Hub: SyncHub:AdminKey (and optionally SyncHub:Schema, SyncHub:ConnectionString; the database is Chandlery's own by default).
/// Node: SyncNode:HubUrl, SyncNode:Token, SyncNode:NodeId (this installation's node id, 32 hex digits, as registered at the hub).
/// Sync's own tables live in schema "sync" of the same database.
/// </summary>
public static class ChandlerySyncSetup
{
    public static string Mode(IConfiguration config) => config["Sync:Mode"] ?? "Off";

    public static IServiceCollection AddChandlerySync(this IServiceCollection services, IConfiguration config, NodeIdentity identity)
    {
        var mode = Mode(config);
        if (mode == "Off") return services;
        if (mode is not ("Hub" or "Node")) throw new InvalidOperationException($"Sync:Mode must be Off, Hub or Node (was '{mode}').");

        identity.SyncEnabled = true;
        services.AddSingleton(identity);
        services.AddSingleton(new HybridClock(TimeProvider.System));
        services.AddSingleton<ChandlerySyncApplier>();
        services.AddSingleton<SyncPublisher>();
        services.AddHostedService(sp => sp.GetRequiredService<SyncPublisher>());

        var connection = config.GetConnectionString("Chandlery")!;
        if (mode == "Hub")
        {
            var overlay = new ConfigurationBuilder().AddConfiguration(config)
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SyncHub:ConnectionString"] = config["SyncHub:ConnectionString"] ?? connection,
                }).Build();
            services.AddSyncHub(overlay, sp => sp.GetRequiredService<ChandlerySyncApplier>());
            return services;
        }

        var options = new NodeSyncOptions
        {
            NodeId = config["SyncNode:NodeId"] ?? throw new InvalidOperationException("SyncNode:NodeId is required."),
            HubUrl = new Uri(config["SyncNode:HubUrl"] ?? throw new InvalidOperationException("SyncNode:HubUrl is required.")),
            Token = config["SyncNode:Token"] ?? throw new InvalidOperationException("SyncNode:Token is required."),
            PollInterval = TimeSpan.FromSeconds(config.GetValue("SyncNode:PollSeconds", 30)),
            Batch = new NodeOptions(AppVersion: typeof(ChandlerySyncSetup).Assembly.GetName().Version?.ToString(),
                LinkProfile: config["SyncNode:LinkProfile"]),
        };
        var schema = config["SyncNode:Schema"] ?? "sync";
        services.AddSingleton(_ => NpgsqlDataSource.Create(connection));
        services.AddSingleton(sp => new PostgresSyncStore(sp.GetRequiredService<NpgsqlDataSource>(), schema));
        services.AddHostedService<NodeSyncStoreInitializer>();
        services.AddSyncNode(options,
            sp => sp.GetRequiredService<PostgresSyncStore>(),
            sp => sp.GetRequiredService<ChandlerySyncApplier>(),
            o => HttpTransport.Create(o.HubUrl, o.Token));
        services.AddSingleton(options);
        return services;
    }

    /// <summary>Creates the vessel's sync tables and refuses to run when the configured node id is not this installation's.</summary>
    private sealed class NodeSyncStoreInitializer(PostgresSyncStore store, NodeSyncOptions options, NodeIdentity identity) : IHostedService
    {
        public async Task StartAsync(CancellationToken ct)
        {
            if (identity.SyncNodeId is { } actual && !string.Equals(actual, options.NodeId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"SyncNode:NodeId is '{options.NodeId}' but this installation is node '{actual}'. Documents would be sent under the wrong identity.");
            await store.EnsureCreatedAsync();
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
