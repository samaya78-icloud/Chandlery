# 03a. Sync engine as a standalone product: plan

Status: **draft for approval (2026-11-15).** Nothing is built from this plan yet. It implements spec 03 sections 4, 9, 10, 12 and 17 (phases 2 and 3) and the decision of 2026-11-08 that Marine.Sync is a separate, configurable product. Repository: `C:\Projects\Sync` (solution Marine.Sync, GitHub samaya78-icloud/Sync).

## 1. The decision this plan makes
**The engine comes first, as its own product; Chandlery stays a client of it.** Chandlery's own sync already runs on the current engine (procurement, master data, crew, worklist, PMS, tanks, risk, projects, and all of Safety), so there is no Chandlery-specific sync left that waits on the engine except **files** (vessel photos and statements). Files are therefore the first big stage after the product is decoupled, and they are built as an engine feature that Chandlery then uses.

## 2. What exists today (checked in the code)
| Piece | State |
|---|---|
| Contracts (`SyncDocument`, `Envelope`, lanes, ACK/NACK) | done, application and database neutral: a document is a type, an id, a version and JSON |
| Protocol core (sequence numbers, ACK, resend, quarantine, deferral, backoff, lanes, clock) | done, memory store plus simulator |
| `ISyncStore` | the one interface for protocol state (outbox, inbox, applied versions, quarantine, contacts, reports). **Hub and node both take one**, so they may use different databases already by design |
| Stores | in memory and **PostgreSQL only** (`PostgresSyncStore`) |
| Hub (`AddSyncHub`) | ASP.NET, HTTPS, console, node registry, admin API. **Bound to PostgreSQL**: `SyncHubExtensions` and `NodeRegistry` use Npgsql directly |
| Node (`NodeSyncService`) | a library the application embeds; **no standalone host** |
| `Hub.Host` | stand-alone hub that accepts every document; takes no store choice from configuration |
| Files, offline package, AES-GCM, signed messages, commands, conflict inbox, SQL Server/Oracle/SQLite, database adapter | not built |

## 3. Product shape
Two independent database roles. The plan keeps them apart, because mixing them up is what would stop "Oracle on the vessel, SQL Server in the office":

- **Role A, the engine's state store** (`ISyncStore`, plus the hub's node registry): where the engine keeps its outbox, inbox, versions and quarantine. One implementation per database. The hub and each node choose theirs by configuration. A vessel node on Oracle talking to an office hub on SQL Server needs nothing more than the two stores, because only JSON documents cross the wire.
- **Role B, the application's own database**: where the application keeps its data. Two ways to connect it, both in spec 03 section 4:
  - **Application adapter** (preferred): the application calls the engine from its own save path and implements `IDocumentApplier`. Chandlery does this today. It works with any application database the application itself supports.
  - **Database adapter** (stage E3): for applications that cannot change. The engine installs change capture on the application's tables (triggers, change tracking, or log reading where the database offers it), turns changes into typed documents, and applies received documents with parameterised upserts. This is how the engine would sync, say, an SQL Server application at the office with an Oracle application on the vessel without either application knowing about sync.

**Chandlery's own database.** Chandlery is built on PostgreSQL (spec decision) and its migrations use PostgreSQL features. This plan does **not** make Chandlery itself run on Oracle or SQL Server; that is a separate piece of work (an EF provider per database, migrations per provider, the shared test suite on each) and is listed as an open question. Chandlery gains the new stores for its **engine state** only if the company wants that, and gains everything else (files, hosts, security) in any case.

Packages after the plan (names provisional):
`Marine.Sync.Contracts`, `Marine.Sync.Core`, `Marine.Sync.Stores.Postgres`, `.SqlServer`, `.Oracle`, `.Sqlite`, `Marine.Sync.Hub` (library, no database reference), `Marine.Sync.Node` (library), `Marine.Sync.Transport.Http`, `Marine.Sync.Files`, `Marine.Sync.Adapter.Database` (+ one provider per database), `Marine.Sync.Hub.Host` and `Marine.Sync.Node.Host` (standalone programs driven by configuration), `Marine.Sync.Client` (the small library an application uses).

## 4. Stages
Each stage ends with the engine's own tests (including the fleet simulator) and, where Chandlery is touched, the Chandlery two-database tests, and with your go-ahead to commit. Stages E1 and E2 can proceed in either order once E0 is done.

**E0. Decouple and package (small).** Remove the Npgsql reference from the Hub library: `NodeRegistry` becomes `INodeRegistry` behind the store contract (one more interface implemented by every store). `Hub.Host` chooses its store and registry from configuration (`Store:Provider`, `Store:ConnectionString`, `Schema`). The node host settings mirror it. A short `docs/` set in the Sync repo (what it is, how to run a hub and a node, configuration reference). Version 0.4. Exit: hub and node start from configuration on PostgreSQL exactly as today; Chandlery's tests unchanged.

**E1. More stores and the mixed-database proof.** `Marine.Sync.Stores.SqlServer`, `.Oracle`, `.Sqlite`, each passing the **same shared store contract tests** the Postgres store passes now (restart, crash between apply and record, fleet simulation). Then a topology test: hub on SQL Server, one node on Oracle, one on SQLite, one on PostgreSQL, all exchanging documents through the simulator with faults. Exit: that test passes, and `Store:Provider` in configuration is the only difference between nodes. Cost note: tests need a database of each kind (containers or developer editions); Oracle needs the free XE edition and its managed driver; the licence terms of each driver are checked before anything ships (spec 03 section 16 rule).

**E2. Files pipeline (what Chandlery needs first).** Content-addressed blobs (SHA-256) beside documents: a document carries a file reference (hash, size, name), the bytes travel on the `Bulk` lane in resumable, integrity-checked chunks, never twice (deduplication), with optional image downscaling and a scan hook, per-folder direction rules, and parked files visible to operators. A file store abstraction on each side (disk first; blob storage later). Constrained links send only `Urgent` and `Transaction`. Exit: file resume suite (cut the link in every chunk position, restart both sides) passes. **Chandlery then:** the `IncidentFile` and `PermitFile` records already carry the SHA-256 and the side that holds the bytes, so the change is small: send the bytes, flip the holder, and the office screen stops saying "held on the vessel". Requisition attachments and the Projects yard files can use the same pipeline.

**E3. Database adapter.** A provider per database for change capture and apply: SQL Server (change tracking or triggers), Oracle (triggers; log reading as an option), PostgreSQL (triggers), SQLite (triggers). A mapping file turns tables and key columns into documents (a parent with child tables, as Chandlery's documents are) and says who owns a record at each stage. Typed payloads only; no SQL text on the wire. Exit: a sample application on SQL Server (office) and Oracle (vessel) syncs both ways with no sync code in it, passing the simulator's fault suites.

**E4. Standalone hosts and operations.** `Hub.Host` and `Node.Host` as services (Windows service and container), configuration-driven, secrets from the operating system store or environment, health endpoints, log and metric output. The node host can run beside an application that talks to it through `Marine.Sync.Client` over a local port, so applications in other languages can use the engine. Exit: install, upgrade and rollback of a node by script on a clean machine.

**E5. Security and links.** AES-GCM payload encryption with per-node keys, signed messages, replay protection (extends the existing sequence numbers), credential expiry with a standby credential, a catalogue of signed commands in place of remote SQL (collect logs, change link profile, restart, apply package), the constrained-link profile tuned on the simulator, and the **offline package transport** (signed files moved by USB for first loads and fully disconnected ships). Exit: tamper, replay and wrong-key tests; a vessel first-loaded from a package then continues over HTTPS.

**E6. Console, alerts and the conflict inbox.** Finish the fleet console (parked files, link profile control, version drift), alerts by email or webhook for silence, backlog, quarantine growth and credential expiry, data-arrival rules in the structured format, and a **conflict inbox** for the few records where two sides can write (today the ownership rules avoid conflicts; the inbox is the safety net and the way to review them). Exit: simulator suites for gap recovery, duplicate delivery, partial batches, scope growth and shrink, long offline catch-up, schema skew.

### E0 design (worked out 2026-11-15; approved "Approve, start E0"; no code written yet)
- **Core gets the store-neutral pieces:** `INodeRegistry` (EnsureCreated, Create, RotateToken, Authenticate, SetStatus, List), `NodeStatus` and `NodeRecord` (moved from Hub, same namespace), a shared `NodeTokens` (token format `msn_...`, SHA-256 hash, node id validation) so every store issues identical tokens, and the provider contract: `StoreSettings(Provider, ConnectionString, Schema)`, `ISyncStoreProvider { Name; Create(StoreSettings) }` returning a `SyncStorage` (the `ISyncStore`, the `INodeRegistry` and an `EnsureCreatedAsync`).
- **Postgres package** keeps `PostgresSyncStore`, receives `PostgresNodeRegistry` (the current `NodeRegistry`, with the unique-violation handling), `PostgresStoreProvider` (name `postgres`) and `AddSyncPostgres(this IServiceCollection)`. It needs `Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.14 (the Node package already uses Hosting.Abstractions 10.0.14, so it is in the local cache).
- **Hub library** drops its project reference to Postgres and `using Npgsql`. `AddSyncHub` resolves the provider named by `SyncHub:Provider` (default `postgres`, so existing settings keep working) from the registered providers, builds the storage once, registers `ISyncStore` and `INodeRegistry`, and `HubInitializer` calls `EnsureCreatedAsync`. The endpoints take `INodeRegistry` instead of `NodeRegistry`. Options `ConnectionString` and `Schema` stay where they are.
- **Hub.Host** references the Postgres package and calls `AddSyncPostgres()` before `AddSyncHub`; the provider, connection string and schema come from configuration.
- **Node side:** `AddSyncNode` already takes an `ISyncStore` factory, so any store works. Add an overload that builds the store from `SyncNode:Provider`, `ConnectionString` and `Schema` through the same provider list.
- **Chandlery adopts it by hand:** it already references `Marine.Sync.Postgres`; at the bump to 0.4.0 it adds one `AddSyncPostgres()` call in `ChandlerySyncSetup` (hub path) and nothing else. Its pinned 0.3.0 keeps working until then.
- **Tests to change in the Sync repo** (found by search): `TestHub.cs` and `ConsoleTests.cs` use `NodeRegistry` and `PostgresSyncStore` by name, `StoreContractTests.cs`, `PostgresTests.cs` and the Demo tool construct `PostgresSyncStore` directly (those stay valid); `TestHub` and the Demo tool need the registry type renamed and `AddSyncPostgres()` added.
- **Docs and version:** `Directory.Build.props` 0.3.0 to 0.4.0; README gains a configuration reference (hub and node keys); pack to `C:\Projects\Sync\packages` (delete `~/.nuget/packages/marine.sync.*` after repacking the same version).
- **Exit test:** hub and node start from configuration on PostgreSQL as today; the engine's own tests pass; the Chandlery build with 0.4.0 compiles and its two-site sync tests pass (run once, at the end).

## 5. Order and size
E0, then E2 (files, because Chandlery waits on it) and E1 (stores) in either order, then E3, E4, E5, E6. E0 is days, E1 and E2 are each a substantial stage, E3 is the largest. Chandlery feature work does not wait on any stage except the file hand-over in E2.

## 6. Risks
- **Oracle and SQL Server drivers and licences.** Check before building each store; the engine must stay sellable (spec 01 section 8).
- **Dialect differences** (identity and sequence handling, `MERGE` against upsert, date and time precision, string and large-object limits) are the main store risk; the shared contract tests exist to catch them, and every store runs the same suite.
- **Test cost.** Each new database makes the engine's test run slower. The shared tests run on every store in the engine's own pipeline, not in Chandlery's suite.
- **A database adapter reads someone else's schema.** The mapping file must be validated against the real schema before any capture is installed, and removal must leave the application's tables as they were.
- **Chandlery contention.** Library versions are consumed from the local feed (`..\Sync\packages`). Each stage that changes a public type bumps the version and Chandlery adopts it on its own schedule; the other laptop's work (chat) is not affected unless it uses the engine's public types.

## 7. Open questions
1. Should **Chandlery itself** be made database-portable (SQL Server, Oracle), or stay PostgreSQL and use the engine's new stores only for engine state? The first is a separate, large plan.
2. Which databases are needed in the first release of the product: SQL Server and SQLite with Oracle after, or Oracle at the same time as SQL Server? (Oracle needs the most care.)
3. Hosted hub, customer-hosted hub, or both (spec 03 section 19)? It decides how much multi-tenant isolation E4 needs.
4. Smallest link to support, as a daily budget (spec README open question 3). It sets the constrained-link profile in E5.
5. Is the standalone **client port** for non-.NET applications (E4) in scope for the first release?
