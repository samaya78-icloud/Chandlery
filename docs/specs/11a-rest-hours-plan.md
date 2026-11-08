# 11a. Rest hours: plan (implements spec 11 in stages)

Status: **approved 2026-11-08 ("Approve, start S1", recommended answers used). S1 built 2026-11-08, uncommitted; S2 to S6 to do.** S1 as built: `Models/RestHours.cs` (rule set, entry revisions, sign-offs, breach notes, vessel day status, working-arrangements version), `Services/RestRules.cs` (the pure engine, 19 tests), `Services/RestService.cs` (recording, day status, rule sets, checking, the table), sync wiring (rule sets are master data; the rest are append-only documents; the table is office-owned and goes down), migration `RestHours`, module name "Rest Hours", 11 two-database tests. No new DocType (nothing here needs a reference code). Decisions made while building: the shorter rest (A-VIII/1.4) is **off by default** and a rule set switches it on; a rest period belongs to the day it starts; the test databases take a prefix from `CHANDLERY_TEST_PREFIX` so two checkouts can test at once. Builds on the patterns already proven in Worklist, PMS and Tank Inspection: records written once or appended, state worked out from them by pure functions, one owner per record, module switch, master data for rule sets, two-database tests.

## 1. Working in parallel with Tank Inspection
This work lives on branch `safety`, in the git worktree `C:\Projects\Chandlery-safety` (branched from `main` at fa5ecd5, before Tank T2). Rules for keeping the two sessions apart:
- **Own database and port:** the worktree's `appsettings.Development.json` points at `chandlery_safety`; run it with `ASPNETCORE_URLS=http://localhost:5200`. It never touches `chandlery_dev`.
- **The migration is created last and merged last-in-line:** whichever branch merges second deletes its migration, takes `ChandleryDbContextModelSnapshot.cs` from `main` and runs `dotnet ef migrations add` again. During development the Safety migration stays uncommitted.
- **Shared files** both branches touch with small additions: `ChandleryDbContext.cs`, `Models/Platform.cs` (DocType), `Models/VesselGroup.cs` (module name), `Services/DocumentNumbers.cs`, `Program.cs`, `Services/Sync/Aggregates.cs`, `SyncPublisher.cs`, `ChandlerySyncApplier.cs`, `MasterData.cs`, `NavMenu.razor`, the vessel lockdown allow-list. Additions go in their own block so a merge is a plain keep-both.
- **Tests:** run them in one session at a time when possible (the full suite is slow and disk-bound).

## 2. The core idea
Hours are **appended, never edited**. The record for a seafarer's day is whatever the latest revision says, and a pure function turns the revisions into periods, totals and breaches, the same way on the vessel and ashore. Sign-offs are appended records that remember what they covered, so a later correction shows plainly that the signature no longer matches.

## 3. Stages (each ends with two-database tests and your go-ahead to commit)
**S1. Foundation.** Models and migration (rule set, working-arrangements table version and rows, vessel day status, hours entry revision, sign-off, breach explanation); the **rules engine as pure functions** (rolling 24 h and 7 d windows, rest periods across midnight, split rule, 14 h interval, the 2-day reduction, under-18 night rule) with a large table of test cases taken from the guideline examples; module switch "Rest Hours"; DocType; sync wiring and baseline; default rule set per company.
**S2. Vessel entry.** *My hours* day grid for a tablet (quarter-hour taps, drag to mark, prefill from the table and the vessel's day status, live totals and warnings), vessel day status, entry on behalf of crew (flagged), correction with reason, month sheet view.
**S3. Sign-off and breaches.** Seafarer and Master sign-off with the out-of-date fingerprint check, breach list with explanations, emergency/drill exception with Master reason, compensatory rest owed, Worklist task on an acknowledged breach, lock rules, nav and tiles on the vessel home.
**S4. Working arrangements (appendix 3).** Office editor per vessel and position, derived daily totals checked against the rule set, versions, Master signature onboard, printable table (HTML and PDF).
**S5. Office compliance.** Fleet view (vessel × month), vessel and seafarer detail with trend, unsigned and out-of-date lists, CSV reports, printable and PDF appendix 4 record for a seafarer and month, tiles on the office home.
**S6. Walkthrough.** One end-to-end test: office sets the rule set and table; a vessel records hours including a deliberate breach and an emergency; signatures, a correction that invalidates one, the Worklist task, and both sides ending with the same totals, breaches and signatures; the vessel holds only its own crew's data.

## 4. Risks
- **Getting the windows right.** The engine is the product. It gets the heaviest tests (including windows across midnight, month boundaries and the reduction rule), checked against the worked examples in the guideline text, and a mutation check on each rule.
- **Tablet usability.** A day entered by tapping 96 quarter-hours must be fast; prefill and "same as yesterday" are part of S2, not extras.
- **Personal data.** Hours records are personal data. The vessel only holds its own crew; reports for the office are read-only; the seafarer's copy is a PDF they can download.
- **Retention and rule differences by flag state.** Numbers and retention live in data and are not assumed; items marked "to verify" in spec 11 are checked before they are built in.

## 5. Needed from you before S1
Answers (or "use your recommendation") for the open questions in spec 11 section 10, mainly 1 (rolling window), 2 (sign interval), 3 (who enters) and 5 (resolution).
