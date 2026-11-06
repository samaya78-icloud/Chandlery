# 03. Sync engine (standalone project)

A generic product, independent of Chandlery, that keeps data **and files** consistent between a shore **Hub** and many vessel **Nodes** over slow, intermittent, metered links. Supports multiple databases.

## 1. Goals
- Reliable two-way sync of structured data and files for any application, with Chandlery as the first client.
- Works over satellite and other weak links using modern transports (no email channel); never blocks the application.
- Multi-database: PostgreSQL, SQL Server, SQLite first; MySQL/MariaDB later.
- Operators can see what is waiting, what is stuck and why, and fix it without a developer.

## 2. Non-goals
- Not a general-purpose message broker. No remote SQL execution. No real-time collaboration.

## 3. Lessons from the two reference systems
| Seen | Borrow | Improve |
|---|---|---|
| SyncIt (own earlier project): trigger-based capture, JSON + password zip, email transport, per-node sequence numbers | sequence numbers, per-table scoping | typed payloads instead of SQL text, retries and gap recovery, conflict rules, files, more transports, non-SQL-Server databases |
| Reference ERP console: Source/Destination schedulers, "pending onboard" blockage view, separate file queue with parked items, days-silent, resend, decoded-message viewer, alert rules, versioned releases with rollback, two-stage first sync | all of these ideas | no SQL-text filters/alerts; automatic isolation of a failing record instead of head-of-line blocking; adaptive transport; bandwidth lanes; fleet overview |

## 4. Architecture
```
Application (Chandlery, PMS, ...)           Application
        │ adapter                                  │ adapter
   [Node agent] ── transports ──────────────── [Hub]
   local store      (HTTPS | offline package)   hub store
        │                                          │
   files pipeline  ═══════════════════════   files pipeline
                              Admin console (fleet)
```
- **Node agent**: service on the vessel; owns the outbox/inbox, scheduling, link policy.
- **Hub**: receives from all nodes, routes to nodes, keeps per-node state.
- **Admin console**: fleet overview, per-node detail, conflicts, commands.
- **Adapters**: how an application exposes sync documents (section 5). Two kinds: **application adapter** (library called from the app's save path; preferred) and **database adapter** (triggers or CDC for apps that cannot change).
- **Database providers**: `ISyncDatabaseProvider` with schema introspection, change capture installation, row read, parameterised upsert, type mapping, quoting. One implementation per database.
- **Transports**: `ITransport`. The product ships **HTTPS/HTTP-2** (resumable chunks, adaptive to weak links, optional WebSocket for push when the link is good) and an **offline package** transport (signed files moved by USB or any file transfer) for first loads and fully disconnected ships. **There is no email transport**: email is not a sync channel.

## 5. The sync unit: documents
- A **sync document** = aggregate root + children (e.g. Requisition + lines, PO + lines + approvals + deliveries). Defined once per application in a manifest: entity set, key, version field, owner per stage, scope rule, lane.
- A change is a **document version**: `{docType, docId, version, origin, hlc, ownerStage, payload, schemaVersion}`; payload is canonical typed JSON (int64, decimal-as-string, UTC datetime, uuid, bytes, text) so different databases interoperate.
- Applying a document is one transaction, so parents and children can never arrive half-applied.
- Row-level mode (database adapter) exists for legacy tables; it groups changes by transaction and primary key.

## 6. Protocol
- Per (node, direction) a monotonically increasing **sequence number**.
- Messages are **idempotent**: applying twice has the same effect.
- Receiver sends **cumulative ACK + selective NACK**; gaps trigger an automatic resend request. Sender keeps messages in the outbox until acknowledged.
- Messages are **batched and compressed** (zstd), size-capped per transport.
- A message that fails to apply is **quarantined** with the error; later messages for other documents continue. Dependent documents wait only for their parent.
- Retries use exponential backoff; operators can pause, resume, replay a range or skip with an audit note.

## 7. Scoping (what a vessel receives)
- Declared as a **structured rule** (not SQL): `docType where scope in (vessel, vesselGroup, department, category, ...)` evaluated against the application's policy snapshot (spec 02).
- When scope grows (new category assigned), the hub **backfills**; when it shrinks, it sends **evictions** (tombstones) so the vessel drops data it should no longer hold.

## 8. Conflict handling
Per document type: `OwnerWins` (default, via ownership), `LastWriterWins` (HLC), `FieldMerge` (per-field versions), `AppendOnly` (no conflicts), `Manual` (goes to the conflict inbox). Chandlery and the maritime modules mostly use ownership and append-only.

## 9. Initial and bulk sync
- **Snapshot** of a vessel's scope exported as a signed package, deliverable over the network or physically (USB/courier); then the node catches up from a **watermark**.
- Two-stage first load: master data first, then transactions.
- "Re-baseline vessel" rebuilds a node from the hub.

## 10. Files
- Content-addressed blobs (SHA-256); the DB syncs only metadata.
- Resumable chunked transfer, integrity verification, deduplication (the same file is never sent twice), optional image downscaling, virus-scan hook.
- Rules per folder/type and direction, e.g. photos vessel → shore on `Bulk`, forms shore → vessel on `Master`.
- Failed files after retries are **parked** and visible to operators.

## 11. Bandwidth and priority
- **Lanes:** `Urgent` (approvals, POs), `Transaction`, `Master`, `Bulk` (files, backups). Higher lanes preempt.
- **Link profiles:** HTTPS good link, HTTPS metered (daily budget MB, off-peak windows), HTTPS constrained (very small chunks, only `Urgent` and `Transaction` lanes, aggressive compression and delta), offline (queue and package).
- Resumable chunking makes constrained links workable without store-and-forward email.
- Adaptive: the node chooses transport by measured availability; operators can force a profile per vessel.

## 12. Security
- Mutual authentication per node (client certificate or signed tokens), registration approved at the hub.
- Payloads encrypted (AES-GCM) with per-node keys; messages signed; replay protection via sequence numbers.
- No secrets in config files (OS secret store or environment); credential-expiry monitoring with standby credentials.
- **Commands** replace remote SQL: a catalogue of signed, audited actions (apply app package, collect logs, change link profile, lock/wipe, restart).

## 13. Schema evolution
- Every message carries a schema version. Nodes advertise supported versions; the hub holds back messages a node cannot yet understand and delivers the application package first.
- Compatible with the previous version (N-1).

## 14. Admin console
- **Fleet overview:** one row per vessel: last contact, days silent, backlog both ways (messages, MB), lane breakdown, link profile, version, health.
- **Node detail:** outbox/inbox, quarantined items with decoded content, parked files, recent runs, logs.
- **Actions:** pause/resume, resend range, replay, force link profile, re-baseline, push package, run command.
- **Alerts:** no contact for N hours, backlog above threshold, quarantine growing, credential expiry, version drift; delivered to email/webhook.
- **Data-arrival rules** (like the reference "alert manager"): when a document of type X arrives with condition Y, notify Z. Conditions use the structured rule format.
- **Metrics** via OpenTelemetry.

## 15. Testing
A **simulator** runs many fake vessels with scripted network faults (latency, loss, drops, clock skew, power cuts). Required suites: gap recovery, duplicate delivery, partial batch failure, scope growth/shrink, long offline catch-up, schema skew, file resume.

## 16. Build or adopt
Time-boxed spike (2–3 days) to evaluate **Dotmim.Sync** and **SymmetricDS** as the data-sync core. Decision rule: adopt only if they support document-level units, our transports and lanes, and acceptable licence terms. Otherwise build; the differentiators (satellite-aware transport, lanes, files, console) are ours either way. Check current licence and maintenance status before deciding.

## 17. Phases
1. Core protocol, outbox/inbox, application adapter, PostgreSQL provider, HTTPS transport, minimal console, simulator.
2. Files pipeline, lanes, scoping with backfill/evict, conflicts inbox.
3. Offline package transport, SQL Server and SQLite providers, commands and package updates, constrained-link profile tuning.
4. Alerts, metrics, database adapter (triggers) for legacy apps, MySQL.

## 19. Sold as a separate product
- Own repository, versioning and release train; Chandlery and every other module consume it through a small client library and a manifest.
- **Multi-tenant hub** (one hub serves many customers, each isolated by tenant, keys and storage) with an option for **customer-hosted** hubs.
- Licensing by number of nodes (vessels) and optional features (files, advanced conflict rules, console alerts); licence keys validated offline.
- SDK and documentation for third-party applications, sample adapters (.NET first), a console that works without any Chandlery module installed.
- Public API stability: protocol versioning with N-1 support, so customers can upgrade hub and nodes at different times.
- Support tooling: diagnostic bundle export, health endpoint, remote command catalogue.

## 18. Acceptance criteria (phase 1)
- 20 simulated vessels sync with random 30% packet loss and daily disconnects; no lost, duplicated or half-applied documents.
- A missing message is re-requested and applied without operator action.
- A poison document is quarantined and visible; others continue.
- Console shows each vessel's backlog and last contact correctly.
