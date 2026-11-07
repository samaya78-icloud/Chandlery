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
    public string ShoreDb => $"chandlery_sync_{_tag}_shore";
    public string VesselDb => $"chandlery_sync_{_tag}_vessel";
    private string Conn(string db) => $"{Server};Database={db}";

    public NodeIdentity ShoreNode { get; } = new();
    public NodeIdentity VesselNode { get; } = new();
    public IDbContextFactory<ChandleryDbContext> ShoreFactory { get; private set; } = null!;
    public IDbContextFactory<ChandleryDbContext> VesselFactory { get; private set; } = null!;
    public RequisitionService ShoreRequisitions { get; private set; } = null!;
    public RequisitionService VesselRequisitions { get; private set; } = null!;

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
        // 1. The office database: migrated and seeded.
        await AdminAsync($"CREATE DATABASE \"{ShoreDb}\"");
        ShoreFactory = Factory(Conn(ShoreDb), ShoreNode);
        await using (var db = await ShoreFactory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
            await DbSeeder.SeedAsync(db);
        }

        // 2. The vessel database starts as a copy of it.
        NpgsqlConnection.ClearAllPools();
        await AdminAsync($"CREATE DATABASE \"{VesselDb}\" TEMPLATE \"{ShoreDb}\"");
        VesselFactory = Factory(Conn(VesselDb), VesselNode);

        // 3. Each installation learns who it is.
        var shoreInfo = new NodeInfo { Id = Guid.CreateVersion7(), Profile = "Shore", Name = "Office", CreatedAt = DateTime.UtcNow };
        var vesselInfo = new NodeInfo { Id = Guid.CreateVersion7(), Profile = "Vessel", Name = "MV Sea Falcon", CreatedAt = DateTime.UtcNow };
        foreach (var (factory, info, node) in new[] { (ShoreFactory, shoreInfo, ShoreNode), (VesselFactory, vesselInfo, VesselNode) })
        {
            await using var db = await factory.CreateDbContextAsync();
            db.NodeInfos.Add(info);
            await db.SaveChangesAsync();
            node.Set(info);
            node.SyncEnabled = true;
        }

        // 4. What the office knows: which node runs the Sea Falcon. (Master data sync is a later step; both sides are told here.)
        foreach (var factory in new[] { ShoreFactory, VesselFactory })
        {
            await using var db = await factory.CreateDbContextAsync();
            db.ApplyingSync = true;
            var vessels = await db.Vessels.IgnoreQueryFilters().OrderBy(v => v.ShortCode).ToListAsync();
            var falcon = vessels.Single(v => v.ShortCode == "MVSF");
            falcon.NodeId = vesselInfo.Id;
            VesselId = falcon.Id;
            OtherVesselId = vessels.First(v => v.ShortCode != "MVSF").Id;
            CompanyId = falcon.CompanyId;
            await db.SaveChangesAsync();
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

        // 5. Sync engine, in each database's own "sync" schema.
        ShoreSyncStore = new PostgresSyncStore(NpgsqlDataSource.Create(Conn(ShoreDb)), "sync");
        VesselSyncStore = new PostgresSyncStore(NpgsqlDataSource.Create(Conn(VesselDb)), "sync");
        await ShoreSyncStore.EnsureCreatedAsync();
        await VesselSyncStore.EnsureCreatedAsync();

        Hub = new HubEndpoint(ShoreSyncStore, new ChandlerySyncApplier(ShoreFactory, ShoreNode), TimeProvider.System);
        Agent = new NodeAgent(vesselInfo.Id.ToString("N"), VesselSyncStore, new ChandlerySyncApplier(VesselFactory, VesselNode),
            new DirectTransport(Hub), TimeProvider.System);

        var clock = new HybridClock(TimeProvider.System);
        ShorePublisher = new SyncPublisher(ShoreFactory, ShoreNode, clock,
            new ServiceCollection().AddSingleton(Hub).BuildServiceProvider(), NullLogger<SyncPublisher>.Instance);
        VesselPublisher = new SyncPublisher(VesselFactory, VesselNode, clock,
            new ServiceCollection().AddSingleton<INodePublisher>(new AgentPublisher(Agent)).BuildServiceProvider(), NullLogger<SyncPublisher>.Instance);

        ShoreRequisitions = new RequisitionService(ShoreFactory, new AttachmentService(ShoreFactory, new NoFiles()));
        VesselRequisitions = new RequisitionService(VesselFactory, new AttachmentService(VesselFactory, new NoFiles()));
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

    public async Task DisposeAsync()
    {
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
        await using var cmd = new NpgsqlCommand(sql, conn);
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
