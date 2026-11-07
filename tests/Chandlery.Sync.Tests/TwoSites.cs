using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
using Chandlery.Services.Sync;
using Marine.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Chandlery.Sync.Tests;

/// <summary>
/// An office and a vessel, each a full Chandlery database of its own, joined by the real sync engine.
/// The vessel is a copy of the office database (the "clone the shore database" way of starting a vessel), so both hold
/// the same master data under the same ids. Needs PostgreSQL: set CHANDLERY_TEST_PG (no Database part), default
/// "Host=localhost;Username=postgres".
/// </summary>
public sealed class TwoSites : IAsyncLifetime
{
    private static string Server => Environment.GetEnvironmentVariable("CHANDLERY_TEST_PG") ?? "Host=localhost;Username=postgres";

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];
    private readonly bool _isTemplate;
    public string ShoreDb => $"chandlery_sync_{(_isTemplate ? "tpl_" : "")}{_tag}_shore";
    public string VesselDb => $"chandlery_sync_{(_isTemplate ? "tpl_" : "")}{_tag}_vessel";

    // Building an office and an empty vessel, and sending the baseline, takes about twenty seconds, so it is done once per test
    // run; every test then starts from a copy of the two finished databases.
    private NpgsqlDataSource? _shoreSource, _vesselSource;
    private static readonly SemaphoreSlim TemplateGate = new(1, 1);
    private static TwoSites? _template;

    public TwoSites() { }
    private TwoSites(bool isTemplate) { _isTemplate = isTemplate; }
    private string Conn(string db) => $"{Server};Database={db}";

    public NodeIdentity ShoreNode { get; } = new();
    public NodeIdentity VesselNode { get; } = new();
    public IDbContextFactory<ChandleryDbContext> ShoreFactory { get; private set; } = null!;
    public IDbContextFactory<ChandleryDbContext> VesselFactory { get; private set; } = null!;
    public RequisitionService ShoreRequisitions { get; private set; } = null!;
    public RequisitionService VesselRequisitions { get; private set; } = null!;

    public CrewCrypto Crypto { get; } = new(System.Security.Cryptography.SHA256.HashData("test-crew-key"u8.ToArray()));
    public CrewService ShoreCrew { get; private set; } = null!;
    public CrewService VesselCrew { get; private set; } = null!;
    public ProcurementService ShoreLogin { get; private set; } = null!;
    public ProcurementService VesselLogin { get; private set; } = null!;

    public PostgresSyncStore ShoreSyncStore { get; private set; } = null!;
    public PostgresSyncStore VesselSyncStore { get; private set; } = null!;
    public HubEndpoint Hub { get; private set; } = null!;
    public NodeAgent Agent { get; private set; } = null!;
    public SyncPublisher ShorePublisher { get; private set; } = null!;
    public SyncPublisher VesselPublisher { get; private set; } = null!;

    // What the seeded company offers.
    public Guid CompanyId { get; private set; }
    public Guid VesselId { get; private set; }
    public Guid OtherVesselId { get; private set; }
    public Guid DepartmentId { get; private set; }
    public Guid PortId { get; private set; }
    public Guid[] ItemIds { get; private set; } = [];
    public Actor User { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var template = await TemplateAsync();
        NpgsqlConnection.ClearAllPools();
        await AdminAsync($"CREATE DATABASE \"{ShoreDb}\" TEMPLATE \"{template.ShoreDb}\"");
        await AdminAsync($"CREATE DATABASE \"{VesselDb}\" TEMPLATE \"{template.VesselDb}\"");
        ShoreFactory = Factory(Conn(ShoreDb), ShoreNode);
        VesselFactory = Factory(Conn(VesselDb), VesselNode);

        foreach (var (factory, node) in new[] { (ShoreFactory, ShoreNode), (VesselFactory, VesselNode) })
        {
            await using var db = await factory.CreateDbContextAsync();
            node.Set(await db.NodeInfos.SingleAsync());
            node.SyncEnabled = true;
        }
        await LoadFactsAsync();
        await StartEnginesAsync();
        ShoreRequisitions = new RequisitionService(ShoreFactory, new AttachmentService(ShoreFactory, new NoFiles()));
        VesselRequisitions = new RequisitionService(VesselFactory, new AttachmentService(VesselFactory, new NoFiles()));
    }

    /// <summary>Templates left behind by a test run that was killed before it could clean up.</summary>
    private static async Task DropStaleTemplatesAsync()
    {
        await using var conn = new NpgsqlConnection($"{Server};Database=postgres;Pooling=false");
        await conn.OpenAsync();
        var names = new List<string>();
        await using (var list = new NpgsqlCommand("SELECT datname FROM pg_database WHERE datname LIKE 'chandlery!_sync!_tpl!_%' ESCAPE '!'", conn))
        await using (var reader = await list.ExecuteReaderAsync())
            while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        foreach (var name in names)
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", conn);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private async Task<TwoSites> TemplateAsync()
    {
        await TemplateGate.WaitAsync();
        try
        {
            if (_template is null)
            {
                await DropStaleTemplatesAsync();
                var built = new TwoSites(isTemplate: true);
                await built.BuildAsync();
                await built.CloseAsync();
                _template = built;
                AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                {
                    NpgsqlConnection.ClearAllPools();
                    DropSync(built.VesselDb);
                    DropSync(built.ShoreDb);
                };
            }
            return _template;
        }
        finally { TemplateGate.Release(); }
    }

    /// <summary>The slow part, done once: an office with its seed data, an empty vessel, the baseline sent and settled.</summary>
    private async Task BuildAsync()
    {
        // 1. The office database: migrated and seeded.
        await AdminAsync($"CREATE DATABASE \"{ShoreDb}\"");
        ShoreFactory = Factory(Conn(ShoreDb), ShoreNode);
        await using (var db = await ShoreFactory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
            await DbSeeder.SeedAsync(db);
        }

        // 2. The vessel database starts EMPTY: migrated, nothing else. It fills itself from the office through sync.
        await AdminAsync($"CREATE DATABASE \"{VesselDb}\"");
        VesselFactory = Factory(Conn(VesselDb), VesselNode);
        await using (var db = await VesselFactory.CreateDbContextAsync())
            await db.Database.MigrateAsync();

        // 3. Each installation learns who it is.
        var shoreInfo = new NodeInfo { Id = Guid.CreateVersion7(), Profile = "Shore", Name = "Office", CreatedAt = DateTime.UtcNow };
        var vesselInfo = new NodeInfo { Id = Guid.CreateVersion7(), Profile = "Vessel", Name = "MV Sea Falcon", CreatedAt = DateTime.UtcNow };
        foreach (var (factory, info, node) in new[] { (ShoreFactory, shoreInfo, ShoreNode), (VesselFactory, vesselInfo, VesselNode) })
        {
            await using var db = await factory.CreateDbContextAsync();
            db.NodeInfos.Add(info);
            await db.SaveChangesAsync();
            node.Set(info);
        }

        // 4. The office registers which node runs the Sea Falcon (before sync is switched on, so this is not itself announced).
        await using (var db = await ShoreFactory.CreateDbContextAsync())
        {
            var vessels = await db.Vessels.IgnoreQueryFilters().OrderBy(v => v.ShortCode).ToListAsync();
            var falcon = vessels.Single(v => v.ShortCode == "MVSF");
            falcon.NodeId = vesselInfo.Id;
            VesselId = falcon.Id;
            OtherVesselId = vessels.First(v => v.ShortCode != "MVSF").Id;
            CompanyId = falcon.CompanyId;
            await db.SaveChangesAsync();
        }
        ShoreNode.SyncEnabled = true;
        VesselNode.SyncEnabled = true;

        await LoadFactsAsync();
        await StartEnginesAsync();

        // The empty vessel is filled by the office's baseline.
        await ShorePublisher.BaselineAsync(VesselNode.SyncNodeId!);
        await SettleUntilQuietAsync();
    }

    private async Task LoadFactsAsync()
    {
        await using (var db = await ShoreFactory.CreateDbContextAsync())
        {
            var vessels = await db.Vessels.IgnoreQueryFilters().OrderBy(v => v.ShortCode).ToListAsync();
            var falcon = vessels.Single(v => v.ShortCode == "MVSF");
            VesselId = falcon.Id;
            OtherVesselId = vessels.First(v => v.ShortCode != "MVSF").Id;
            CompanyId = falcon.CompanyId;
        }

        await using (var db = await ShoreFactory.CreateDbContextAsync())
        {
            db.CurrentCompanyId = CompanyId;
            User = new Actor((await db.Users.FirstAsync(u => u.CompanyId == CompanyId)).Id, "Test Buyer");
            PortId = (await db.Ports.FirstAsync()).Id;

            // A department and some items the company's own rules let it order together.
            var items = await db.Items.Where(i => i.CompanyId == CompanyId).OrderBy(i => i.Code).Take(40).ToListAsync();
            foreach (var department in await db.Departments.Where(d => d.CompanyId == CompanyId).ToListAsync())
            {
                var allowed = new List<Guid>();
                foreach (var item in items)
                {
                    try { await ProcurementService.EnsureItemsAllowedAsync(db, CompanyId, department.Id, [item.Id]); allowed.Add(item.Id); }
                    catch (InvalidOperationException) { }
                    if (allowed.Count == 3) break;
                }
                if (allowed.Count >= 2) { DepartmentId = department.Id; ItemIds = allowed.ToArray(); break; }
            }
            Assert.NotEmpty(ItemIds);
        }

    }

    private async Task StartEnginesAsync()
    {
        // 5. Sync engine, in each database's own "sync" schema.
        _shoreSource = NpgsqlDataSource.Create(Conn(ShoreDb));
        _vesselSource = NpgsqlDataSource.Create(Conn(VesselDb));
        ShoreSyncStore = new PostgresSyncStore(_shoreSource, "sync");
        VesselSyncStore = new PostgresSyncStore(_vesselSource, "sync");
        await ShoreSyncStore.EnsureCreatedAsync();
        await VesselSyncStore.EnsureCreatedAsync();

        // Short resend delays, so a message that had to wait (for master data still on its way) is tried again within the test.
        var retry = new RetryPolicy(TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(600));
        Hub = new HubEndpoint(ShoreSyncStore, new ChandlerySyncApplier(ShoreFactory, ShoreNode), TimeProvider.System, retry);
        Agent = new NodeAgent(VesselNode.SyncNodeId!, VesselSyncStore, new ChandlerySyncApplier(VesselFactory, VesselNode),
            new DirectTransport(Hub), TimeProvider.System, retry);

        var clock = new HybridClock(TimeProvider.System);
        ShorePublisher = new SyncPublisher(ShoreFactory, ShoreNode, clock,
            new ServiceCollection().AddSingleton(Hub).BuildServiceProvider(), NullLogger<SyncPublisher>.Instance);
        VesselPublisher = new SyncPublisher(VesselFactory, VesselNode, clock,
            new ServiceCollection().AddSingleton<INodePublisher>(new AgentPublisher(Agent)).BuildServiceProvider(), NullLogger<SyncPublisher>.Instance);

        ShoreRequisitions = new RequisitionService(ShoreFactory, new AttachmentService(ShoreFactory, new NoFiles()));
        VesselRequisitions = new RequisitionService(VesselFactory, new AttachmentService(VesselFactory, new NoFiles()));
        ShoreCrew = new CrewService(ShoreFactory, Crypto);
        VesselCrew = new CrewService(VesselFactory, Crypto);
        ShoreLogin = new ProcurementService(ShoreFactory);
        VesselLogin = new ProcurementService(VesselFactory);
    }

    /// <summary>Let everything pending travel: both publishers and a few exchanges, enough for messages and acknowledgements both ways.</summary>
    public async Task SettleAsync(int rounds = 4)
    {
        for (var i = 0; i < rounds; i++)
        {
            await VesselPublisher.PublishPendingAsync();
            await Agent.ExchangeAsync();
            await ShorePublisher.PublishPendingAsync();
            await Agent.ExchangeAsync();
        }
    }

    /// <summary>Exchange until nothing is left to send either way (the baseline is several hundred messages).</summary>
    public async Task SettleUntilQuietAsync(int maxRounds = 200)
    {
        for (var i = 0; i < maxRounds; i++)
        {
            await VesselPublisher.PublishPendingAsync();
            await ShorePublisher.PublishPendingAsync();
            var result = await Agent.ExchangeAsync();
            var node = VesselNode.SyncNodeId!;
            var backlog = (await ShoreSyncStore.GetBacklogAsync(new StreamKey(node, Direction.Down))).Messages
                + (await VesselSyncStore.GetBacklogAsync(new StreamKey(node, Direction.Up))).Messages;
            if (backlog == 0 && result.Sent + result.Received == 0 && await DirtyAsync(ShoreFactory) == 0 && await DirtyAsync(VesselFactory) == 0) return;
        }
        throw new TimeoutException("Sync did not settle.");
    }

    public NewRequisition NewRequisition(string remarks = "test", Guid? vesselId = null, int lines = 2) => new()
    {
        VesselId = vesselId ?? VesselId,
        DepartmentId = DepartmentId,
        DeliveryPortId = PortId,
        Remarks = remarks,
        Lines = ItemIds.Take(lines).Select((id, i) => new NewRfqLine { ItemId = id, Quantity = 3 + i }).ToList(),
    };

    public async Task<Requisition?> FindAsync(IDbContextFactory<ChandleryDbContext> factory, Guid id)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Requisitions.IgnoreQueryFilters().Include(r => r.Lines).AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
    }

    public async Task<int> VersionAsync(IDbContextFactory<ChandleryDbContext> factory, Guid id)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Requisitions.IgnoreQueryFilters().Where(r => r.Id == id).Select(r => EF.Property<int>(r, "SyncVersion")).FirstAsync();
    }

    public async Task<int> DirtyAsync(IDbContextFactory<ChandleryDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.SyncDirties.CountAsync();
    }

    /// <summary>Let go of every connection, so the databases can be copied.</summary>
    private async Task CloseAsync()
    {
        await _shoreSource!.DisposeAsync();
        await _vesselSource!.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
    }

    private static void DropSync(string db)
    {
        using var conn = new NpgsqlConnection($"{Server};Database=postgres;Pooling=false");
        conn.Open();
        using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{db}\" WITH (FORCE)", conn);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Settle repeatedly until a condition holds (or time runs out): for messages that wait and are resent.</summary>
    public async Task SettleUntilAsync(Func<Task<bool>> condition, int seconds = 120)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            await SettleAsync(1);
            if (await condition()) return;
            await Task.Delay(120);
        }
        throw new TimeoutException("The expected state was not reached.");
    }

    public async Task DisposeAsync()
    {
        if (_shoreSource is not null) await _shoreSource.DisposeAsync();
        if (_vesselSource is not null) await _vesselSource.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await AdminAsync($"DROP DATABASE IF EXISTS \"{VesselDb}\" WITH (FORCE)");
        await AdminAsync($"DROP DATABASE IF EXISTS \"{ShoreDb}\" WITH (FORCE)");
    }

    private static IDbContextFactory<ChandleryDbContext> Factory(string connection, NodeIdentity node)
        => new ServiceCollection()
            .AddSingleton(node)
            .AddDbContextFactory<ChandleryDbContext>(o => o.UseNpgsql(connection))
            .BuildServiceProvider()
            .GetRequiredService<IDbContextFactory<ChandleryDbContext>>();

    private static async Task AdminAsync(string sql)
    {
        await using var conn = new NpgsqlConnection($"{Server};Database=postgres;Pooling=false");
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 180 };
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed class DirectTransport(HubEndpoint hub) : ITransport
    {
        public Task<SyncResponse> ExchangeAsync(SyncRequest request, CancellationToken ct = default)
        {
            // Through the wire format, so what the hub sees is exactly what HTTP would deliver.
            var wire = System.Text.Json.JsonSerializer.Serialize(request);
            return hub.HandleAsync(System.Text.Json.JsonSerializer.Deserialize<SyncRequest>(wire)!, ct);
        }
    }

    private sealed class AgentPublisher(NodeAgent agent) : INodePublisher
    {
        public Task<long> PublishAsync(SyncDocument document, Lane lane = Lane.Transaction) => agent.PublishAsync(document, lane);
    }

    private sealed class NoFiles : IFileStore
    {
        public Task<string> SaveAsync(Guid companyId, Stream content, CancellationToken ct = default) => throw new NotSupportedException();
        public Stream? OpenRead(string key) => null;
        public void Delete(string key) { }
    }
}
