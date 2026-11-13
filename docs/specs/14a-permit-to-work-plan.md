# 14a. Permit to work: plan (implements spec 14 in stages)

Status: **started 2026-11-13 ("Start spec 14"; the spec's open questions were left to the recommended answers below, as for spec 13).** Builds on the patterns proven in drills: a document the vessel writes with child tables mapped to it in change tracking, state worked out by pure functions, office master data, a module switch, two-database tests.

## 1. Answers used for the open questions (change any and the plan follows)
1. **Types and checklists:** the seven defaults of spec 14, each with a starting checklist marked "to verify against the company's forms". The office edits them; a change makes a new checklist **version**, so permits already issued keep the version they used. No import of the company's own forms until you share them.
2. **Who may issue:** a person with the new crew authority **IssuePermits**, and the Master. A type may also set the rank needed to request (any crew, an officer, the Master). One person may not be both requester and issuer unless the type allows it (off by default, "to verify against the SMS").
3. **Gas limits and repeat interval:** per type, copied onto the permit when it is requested (so a later change to the type never rewrites it). Defaults (all "to verify"): oxygen 20.9 to 22.0 percent, flammable gas not above 1 percent of the lower explosive limit, hydrogen sulphide not above 1 ppm, carbon monoxide not above 25 ppm, repeat every 30 minutes for an entry and 60 for hot work.
4. **Attendant and entry log:** required where the type says so; on for enclosed space entry by default.
5. **Master's approval:** on for enclosed space entry and hot work outside the workshop by default; the type decides.
6. **Conflict pairs:** office master data; defaults are hot work against enclosed space entry and hot work against cold work in a hazardous area, same vessel at the same time, a warning the issuer acknowledges.
7. **Retention:** a permit is never deleted; how long it is kept is left to the company's retention rule (no purge is built).
8. **Tank link:** a warning only, never a block (stage P5).

## 2. The core idea
- **A permit is a document the vessel writes** (`Permit`) with its checklist answers, gas readings, entry log, persons and an append-only **event** list. The state (Requested, Issued, Active, Suspended, Completed, Cancelled, Withdrawn, Expired) is worked out from the events and the clock by `PermitRules`, never typed.
- **The terms are frozen on the permit** (`PermitTerms`: gas limits, validity, which signatures are needed), so the rules read the permit alone and a later edit of the type cannot loosen a permit already requested.
- **Validity is a window plus conditions:** inside the window, state Active, and the last gas reading (where required) within limits and within its repeat interval. Otherwise "not valid".
- **Types, checklists and conflict pairs are the office's master data**, copied to each vessel; the office never issues or signs onboard.

## 3. What syncs
| Record | Writer | Direction | Kind |
|---|---|---|---|
| Permit type, checklist items, conflict pairs | office | to every vessel | master data |
| Permit with answers, persons, gas readings, entries, events | vessel | up | document |
| Office comment on a permit | office | down | append-only |
Lane: Transaction. A vessel holds only its own permits.

## 4. Stages (each ends with two-database tests and your go-ahead to commit)
**P1. Foundation.** **Built 2026-11-13, uncommitted:** `Models/Permits.cs` (type, checklist items, conflict pairs, permit with answers, persons, readings, entries, events, office comment, `PermitTerms`), `Services/PermitRules.cs`, sync wiring (master data, aggregates, change tracking `PermitOf`, vessel-writable list), module "Permit to Work", reference `PTW`, authority IssuePermits, migration `PermitToWork` (ten tables); 27 pure tests pass. Models and migration; `PermitRules` (pure: state and validity from the events and the clock, gas check and next reading due, issue check listing what is missing, persons inside, close check, role separation); sync wiring; module "Permit to Work"; reference `PTW`; authority IssuePermits; tests for the rules.
**Status 2026-11-13: P1 to P6 built.** P2 service and tests (7), P3 vessel screens, P4 office views and exports, P5 links (home card, Master task, tank warning, PMS job panel, incident prefill, hours of work for issuers), P6 walkthrough (2 tests pass) and mutation check (44 changes: 39 caught at first, 5 survivors closed with 3 new rule tests, 1 pure fleet test and removal of one line of dead code; all 44 caught). Not built: a PMS "needs permit type" setting (needs a column), drill and project links, a purge for retention. The full suite has not been run.
**P2. Service.** Types, checklist versions and conflict pairs (office); request, answer, gas reading, issue, Master approval, start, entry and exit, suspend and resume, extend with a fresh reading, hand over, complete with two signatures, cancel, withdraw; expiry raising a Worklist task (link type `permit`); office comment; defaults.
**P3. Vessel screens.** Permit board, new permit, the permit page as a guided sequence, entry log, history, printable permit.
**P4. Office.** Fleet permit activity, permit types and checklists editor, conflict pairs, audit view, PDF and CSV.
**P5. Links.** Risk assessment, PMS job, tank inspection warning, incident from an out-of-limits reading, home-page card and menu counts.
**P6. Walkthrough and mutation check.** One story across a real office and vessel database, the vessel holding only its own permits, deliberate changes to `PermitRules` against the pure tests.

## 5. Risks
- **Gas limits and sign-off levels differ by company.** They are data marked "to verify", never constants in the rules.
- **A permit nobody closed** is what an auditor looks for: Expired stays on the board and raises a task until closed.
- **Someone left inside a space:** closing is refused while the entry log shows anyone inside.
