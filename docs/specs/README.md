# Marine platform: specification set

Status: **draft for review** (2026-11-06). Written from the design discussions with the product owner, informed by studying the reference ERP (structure and workflow only; nothing is copied).

## Vision
One platform used by ship managers and their vessels. The **office (shore)** and each **vessel (onboard)** run the same product, work offline-first, and stay consistent through a **generic sync engine** that is its own standalone project. Procurement (Chandlery) is the first module; Worklist, Risk Assessment, PMS, Tank Inspection and Projects follow on the same platform.

## Documents
| # | Spec | What it covers |
|---|------|----------------|
| 01 | [Platform foundation](01-platform-foundation.md) | Identity, numbering, ownership rules, shared services, module contract, deployment profiles, migration of today's Chandlery |
| 02 | [Vessel groups and policy](02-vessel-groups-and-policy.md) | 3-level vessel groups, department/catalog permissions, onboard direct-purchase rules, scopes |
| 03 | [Sync engine](03-sync-engine.md) | Standalone generic data and file sync, multi-database, satellite-aware transports, console |
| 04 | [Chandlery onboard](04-chandlery-onboard.md) | Vessel-side requisitions, RFQs, POs, deliveries, and how they sync |
| 05 | [Worklist](05-worklist.md) | Recorded tasks with progress history |
| 06 | [Risk assessment](06-risk-assessment.md) | Templates, assessments, review cycle, risk matrix |
| 07 | [Planned maintenance (PMS)](07-pms.md) | Library, machinery, running hours and calendar scheduling, deferrals, import |
| 08 | [Tank inspection](08-tank-inspection.md) | Tank master and plan, inspections, ratings, trends, exceptions, review |
| 09 | [Projects](09-projects.md) | Drydock and technical projects with budget vs actual |
| 10 | [Collaboration and chat](10-collaboration-chat.md) | A conversation thread on every record in every module, across vessel, office and suppliers |

## Principles (apply to every module)
1. **Offline-first.** Every screen reads and writes the local store; sync is a background process. A vessel must work for weeks without connectivity.
2. **One writer per record per stage.** Each record type declares who owns it at each stage; this removes almost all conflicts.
3. **History is append-only.** Progress, readings, completions, approvals and status changes are new entries, never edits.
4. **Documents are the unit of sync** (a requisition with its lines, a PO with approvals), not table rows.
5. **Links by global id**, so a link made onboard stays valid ashore.
6. **Scope is policy.** The same vessel-group policy decides what a vessel may do and what data it receives.
7. **Our own design.** The reference system informs the concepts only.

## Build order
1. Foundation (01): global ids, numbering, change tracking, ownership declarations, and the conversation core (10).
2. Vessel groups and policy (02): shore-side only, useful on its own.
3. Sync engine v1 (03): own repository, with a simulator.
4. Worklist (05): first module on the platform, exercises two-way writes.
5. Chandlery onboard (04).
6. PMS (07), then Tank Inspection (08), Risk Assessment (06), Projects (09).

Tank Inspection follows PMS because inspections are driven by PMS jobs. Classification, surveys and certificates are deliberately **out of scope for now** (later phase of PMS).

## Decisions taken
- PMS for all vessels, with a standard job library and an option to import a vessel's own plan.
- Jobs can be running-hours driven, calendar driven, or both (whichever first).
- Vessel groups: three levels, level 1 mandatory, levels 2 and 3 optional.
- Sync is a separate, generic product supporting multiple databases and both data and files.
- Onboard database: PostgreSQL (same as shore), because Chandlery uses PostgreSQL features.
- **No email-based sync.** The sync engine uses modern transports only (HTTPS/HTTP-2 with resumable chunks and adaptive behaviour on weak links) plus an offline **package** option (USB or file transfer) for first loads. Email remains only a business feature (sending POs), never a sync channel.
- **PO dispatch:** the vessel sends the PO to the supplier with the office in copy, through the system (supplier portal notification) and optionally as a generated email with the PO attached.
- **Onboard approvals:** the Master approves within an **office-maintained limit per department/catalog combination**; above the limit the office approves.
- **Every module is sellable on its own**, and the **sync engine is also sold separately** (see 01 section 8 and 03 section 19).
- Sync is a separate product and repository; Chandlery and the other modules are clients of it.

## Open questions (owner decision needed)
1. Identity scheme: **GUID keys with vessel-prefixed document numbers and a visible origin column** is recommended (see 01 section 2); product owner to confirm.
2. Commercial packaging: which modules sell separately first, pricing metering (per vessel, per user, per module), and whether customers can mix modules from different vendors' data (see 01 section 8).
3. Sync engine as a product: hosted hub for customers, or customer-hosted, or both?
4. Minimum vessel connectivity to support with HTTPS-only sync (smallest link, daily budget)?
5. PMS: second sign-off on completions (always / critical only / never), who may edit intervals, default grace window.
6. Tank inspection: which vessel types must be supported first (product, chemical, gas, bulk, container)?
7. Direct purchase: should Master limits apply per PO only, or also per month per department?

## Glossary
**Shore/Office** the head-office installation. **Vessel/Node** an onboard installation. **Hub** the shore side of sync. **Document** a record plus the child records that belong to it (a sync unit). **Stage** a lifecycle state in which one side owns the record. **Policy** a group- or vessel-level permission row.
