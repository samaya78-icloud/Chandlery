# 01. Platform foundation

Everything else depends on this spec. The product is pre-production, so these changes are cheapest now.

## 1. Goals
- A modular platform where each module plugs into shared services and is sync-ready from day one.
- The same codebase runs as **Shore** or **Vessel** (deployment profile).
- Existing Chandlery (requisitions, RFQs, POs, deliveries, invoices, units, currencies, attachments) migrates onto it without losing function.

## 2. Identity
- **Primary keys are UUIDv7** (time-ordered) for every record that can sync. No auto-increment keys on synced tables.
- Every installation has a permanent **node id** (shore hub id, vessel node id). It never changes when a vessel is renamed.
- Alternative considered: composite key (origin, integer id). Rejected as default because every foreign key, route and EF relationship becomes composite.
- **Clarity about which vessel a record belongs to** (the concern with GUIDs) is met in three ways, so the key itself does not need to carry it:
  1. every record has an indexed `OriginNodeId` and, where relevant, a `VesselId`, shown in every grid and filter;
  2. human-visible **document numbers carry the vessel code** (`REQ-MVSF-2026-0007`, section 3), and users see numbers, not keys;
  3. support tools resolve a GUID to "type, vessel, number" in one lookup (a "who owns this id" search box).
- Human-readable numbers are separate from keys (section 3). Recommendation recorded in the README open questions for confirmation.

## 3. Document numbering
- Format: `{TYPE}-{ORIGIN}-{YEAR}-{SEQ}` where ORIGIN is a short code (`HO` for shore, vessel short code onboard), e.g. `REQ-MVSF-2026-0007`.
- Sequences are per (type, origin, year) and allocated locally, so no collisions and no round trip.
- A number is assigned when the document is created and never reused.

## 4. Change tracking columns (every synced table)
`Id`, `CreatedAt`, `CreatedBy`, `UpdatedAt`, `UpdatedBy`, `RowVersion` (monotonic per record), `OriginNodeId`, `IsDeleted` + `DeletedAt` (soft delete, tombstones sync like any change).
- All timestamps are UTC. A hybrid logical clock is used for ordering across nodes with drifting clocks.
- Local display uses the vessel's time zone; documents that carry a local meaning (ETA, noon report) store the zone explicitly.

## 5. Ownership model
Every record type declares, per lifecycle stage, **who may write**: `Shore`, `Vessel`, `Either-append-only`.
Template for each module:

| Record | Stage | Owner | Notes |
|--------|-------|-------|-------|
| Requisition (vessel-raised) | Draft → Submitted | Vessel | after submit the vessel may only add comments |
| Requisition | Under review → Closed | Shore | status flows back to the vessel |

Rules: a record has exactly one writer at a time; history/comment entries may be added by either side; hand-over is an explicit state change.

## 6. Shared services
- **Users, roles, permissions.** Roles are capability sets. Vessel users are created at shore and sync down (hashed passwords); vessel crew can sign in offline.
- **Vessels and vessel groups** (spec 02). **Departments, categories, units, currencies, tax codes** stay shore-owned master data scoped by policy.
- **Lists (lookup values).** A generic configurable list feature (list, value, parent value, active) used by Worklist, PMS, Risk Assessment and others instead of hard-coded dropdowns.
- **Files and attachments.** Content-addressed (SHA-256), metadata syncs as documents, bytes travel in the file pipeline (spec 03). Images are downscaled onboard by policy.
- **Audit and event history.** Every business change writes an immutable event (who, when, what, from which node).
- **Workflow and approvals.** The existing approval engine (chains, named steps, USD limits) generalised: a workflow definition can be attached to any document type, onboard or ashore.
- **Notifications.** In-app, and email when connected; rules defined by event type.
- **Conversations on every record** (chat threads with mentions, attachments, delivery states and supplier-visible channels, spec 10), **search**, **export (CSV/PDF)**, **numbering** (section 3).

## 7. Module contract
A module registers with the platform and must declare:
1. Its **sync documents**: aggregate root plus children, key, version, size class.
2. **Ownership** per stage (section 5).
3. **Scope rule**: which vessels receive which records (by group, vessel, department, catalog).
4. **Sync lane** (priority, spec 03): `Urgent`, `Transaction`, `Master`, `Bulk`.
5. **Schema version** and migration scripts; compatibility with the previous version.
6. **Permissions** (capabilities) and navigation entries.
7. **Offline behaviour**: what works with no connection and what needs shore.
A module may be switched on or off per company and per vessel group (spec 02).
8. Its **dependencies**: `requires` (hard, kept to the platform core only) and `integratesWith` (soft). A soft integration must degrade gracefully when the other module is not licensed (e.g. PMS without Worklist keeps a local "defect note" and hides "create task").

## 8. Modularity and licensing (every module sellable on its own)
- **Platform core** (always installed): users and roles, vessels and groups, lists, files/attachments, audit, notifications, numbering, workflow, policy, sync client. It is the only hard dependency of any module.
- **Modules** (separately licensed): Chandlery procurement, Worklist, Risk Assessment, PMS, Tank Inspection, Projects, and later others. The **sync engine** is a separate product (spec 03 section 19) that the core uses through its client library.
- **Entitlements:** a signed licence per customer lists modules, number of vessels, number of users, and validity. It is checked at startup and daily, works offline (vessels), and a missing or expired module disables only that module with a clear message; data is never deleted.
- **Standalone install:** a customer can buy only PMS, or only Worklist; navigation, dashboards and cross-links adapt to what is licensed.
- **Cross-module links** use only ids and a small public interface (never direct table access), so modules can be built, versioned and shipped independently.
- **Packaging:** each module is an assembly set with its own migrations, seed data, permissions and navigation, discovered by the host; per-module version numbers with a compatibility matrix against the core.
- **Metering and billing hooks:** vessel count and active users are measured by the core and reported to the hub for invoicing.
- **Demo mode:** any module can run in a trial licence with a time limit.

## 9. Deployment profiles
| | Shore | Vessel |
|---|---|---|
| Runtime | .NET, Blazor Server, PostgreSQL | same build, `Profile=Vessel`, local PostgreSQL |
| Users | office, suppliers (portal) | crew only, on the ship network via browser |
| Data | all vessels | one vessel only, plus scoped master data |
| Sync | Hub | Node agent |
| Updates | publishes signed packages | applies packages from sync, with rollback |
Packaging for vessels: container or installer with a health check; app packages arrive through sync.

## 10. Security
- Per-node key pair; all sync payloads signed and encrypted (spec 03).
- Vessel database encrypted at rest; no supplier bank details, no other vessel's prices on board.
- Remote lock and wipe commands (signed, audited).
- Passwords hashed; lockout; password reset via shore for vessels.

## 11. Migration of today's Chandlery
1. Add UUIDv7 key columns and change-tracking columns; backfill; switch foreign keys; drop integer keys (dev data only, so a scripted rebuild is acceptable).
2. Replace count-based numbering with section 3 numbering; keep old numbers as aliases.
3. Declare sync documents and ownership for: Requisition, Rfq (+quotes), PurchaseOrder (+approvals, deliveries), Invoice, Attachment, master data.
4. Add `Profile` configuration and the vessel-scoped query filter.
5. Regression: the full requisition → RFQ → PO → delivery → invoice flow still passes on shore.

## 12. Non-functional requirements
- Vessel app works offline indefinitely; sync catch-up after 30 days offline completes without manual steps.
- A vessel PMS plan of 5,000 jobs loads lists under 2 seconds on a modest server.
- No data loss on power cut (transactional writes, outbox persisted before acknowledging).
- Audit history retained at least 5 years.

## 13. Acceptance criteria
- Two nodes create records offline with the same type and no key or number collision.
- Every module can show its ownership table and the platform rejects a write from the non-owning side.
- Deleting a record on one side removes it on the other after sync.
