# 07a. PMS: plan (implements spec 07 in stages)

Status: **approved 2026-11-08. P1 (foundation) and P2 (vessel screens) and P3 (office: library, apply library, fleet dashboard, reports with CSV, tiles) and P4 (deferral requests and decisions with a Master limit, plan change requests, job editing by the owner, template updates with a diff, approvals queue) built 2026-11-08; P5 (spares lists and "order the spares" requisition prefill, defect to Worklist, clone a plan to sister vessels or a group, drift report) built 2026-11-08; P6 (import wizard for a vessel's existing plan: Excel or CSV, column guessing, preview, library matching, read-only history, proven on 3,000 jobs) built 2026-11-08; P7 (end-to-end walkthrough test) built 2026-11-08. The PMS module is complete.** Builds on the sync engine, vessel groups and policy (module switch `Pms`), master data sync, and the patterns proven by Worklist (records that are history, state worked out by replaying them) and procurement (one owner per record).

## 1. The core idea
Spec 07 has two kinds of data: things that are **defined** (the library, the machinery, the planned jobs) and things that **happen** (running-hours readings, job completions, deferrals). The design keeps them apart, exactly as Worklist keeps a task apart from its timeline:

- **Defined things** are ordinary synced documents with one owner (the side that created them). Shore owns the library and the plan it generates; a vessel owns the jobs and machinery it adds itself. Changing someone else's definition goes through a change request (stage P4), never an overwrite.
- **Things that happen** are append-only records: a reading, a completion, a deferral request and its decision. Nobody edits or deletes them (the data layer refuses it, and the sync path ignores a resend that differs, as Worklist now does). A wrong reading is corrected by a **new reading marked as a correction**; the latest correction for that meter and date wins.
- A planned job's **schedule is a projection**: last done date and hours, average daily running hours, next due date and overdue status are worked out by one pure function from the job definition, the readings and the completions. Vessel and office run the same function on the same records, so they agree. The projected columns are kept on the job for fast lists and are never sent.
- Because "overdue" depends on today's date, it is computed when read, from the stored next-due date; the hours-based due date is recomputed whenever a reading or completion arrives.

## 2. What syncs
| Record | Owner (writer) | Direction | Kind |
|---|---|---|---|
| Machinery class, job template (with version) | shore | to every vessel | master data (office to vessel, like the Worklist lists) |
| Machinery item (tree: location, system, item, maker, model, serial, critical, hour meter) | the side that created it | both ways, scoped to the vessel | document |
| Planned job (template link or vessel-own, interval, anchor, grace, overrides) | the side that created it | both ways | document |
| Running-hours reading | its author's side | both ways | append-only |
| Job completion | its author's side | both ways | append-only |
| Deferral request and decision, plan change request | vessel requests, shore decides (P4) | both ways | append-only |

Lanes: `Transaction` for readings and completions, `Urgent` for a deferral request on a critical job, `Master` for the library. Plan size up to about 5,000 jobs per vessel, so a vessel's plan is sent in batches and the vessel starts from a baseline like master data does.

## 3. Scheduling rules (spec section 3, made precise)
- **Next due = the earlier of** the calendar date (`last done + calendar interval`, or the fixed date for a fixed-calendar job) and the projected hours date (`last done hours + hours interval`, reached at the average daily hours of the last 30 days).
- **Anchor:** *from completion* (floating) or *fixed calendar* (the next due moves by whole intervals from the original date, so annual items stay annual).
- **Grace:** a job is **due** from its next-due date and **overdue** once the grace window has passed. Default grace: 10% of the interval, at least 1 day, overridable per template (in days). Overdue flags on the board and in the fleet figures.
- **Stale hours:** no reading for 7 days (default, per policy later) flags an hours-driven job as "hours out of date"; the hours date is then shown as an estimate.
- **Back-dated completion:** last done is the completion with the latest done date (then the latest recorded). Entering an older one never moves the schedule backwards.
- **Without enough readings** (fewer than two in the window) the hours date cannot be projected: the job shows calendar due only and "hours unknown".
- Dates are whole UTC days, as in the rest of the platform.

## 4. Stages (each ends with two-database tests and your go-ahead to commit)
**P1. Foundation.** Tables and migration; the scheduling function with exhaustive tests (both intervals, whichever first, floating and fixed anchors, grace, stale hours, corrections, back-dated completions); append-only guard; sync of library (master data), machinery, planned jobs, readings and completions; module switch gate; vessel baseline for a plan. No screens beyond what tests need.
**P2. Vessel screens.** PMS board (overdue, this week, next 30 days, by department), job page with checklist and history, complete a job (hours, who did it, time spent, comments), fast daily running-hours entry, machinery tree, add a vessel-own job or item, nav gated by the module switch.
**P3. Office screens and library.** Library management (classes, templates, versions), apply the library to a vessel (map machinery to classes, jobs are generated), fleet compliance dashboard (percent on time, overdue, critical overdue), reports with CSV (jobs done, to be done, overdue, running-hours summary), tiles on both home pages.
**P4. Deferrals and plan changes.** Deferral request (reason, new date), decision by shore or by an onboard role within a policy limit, using the existing approval patterns; an approved deferral moves the due date on both sides, an unapproved one leaves the job overdue; plan change requests for intervals and machinery; template updates arrive on the vessel as a **proposed change with the differences shown**, nothing changes silently.
**P5. Links.** A defect found on a job raises a Worklist task linked to the job; spares needed opens a requisition pre-filled from the machinery's spares list (header only, as with "purchase needed": the item list comes from the spares, which this stage adds as a simple per-machinery list); clone a plan from a sister vessel or group with a drift report.
**P6. Import wizard.** Excel or CSV: map columns (location, system, job code, title, interval, last done date and hours), dry-run preview, suggested template matches, unmatched kept as vessel-own jobs, imported history read-only. Proven on a 3,000-job file.
**P7. Walkthrough.** One end-to-end test: vessel and office both work while apart (readings, completions, a correction, a deferral decision), and both sides end with the same history, the same next-due dates and the same overdue counts.

Spec phase 1 is P1 to P3, phase 2 is P4, phase 3 is P6. Stock-on-board (spec phase 4) and classification surveys and certificates stay out.

## 5. Proposed answers to the three open decisions (approved as proposed)
1. **Second sign-off:** off by default. A template can mark a job "needs verification"; the vessel's completion then shows "awaiting verification" until a designated rank (the Master or Chief Engineer, set in policy) or the office verifies, the same way Worklist verification works.
2. **Who edits intervals:** the office, through the plan. A vessel can change the interval only on jobs it created itself; for a library job it sends a plan change request (P4).
3. **Default grace:** 10% of the interval with a one-day minimum, overridable per template.

## 6. Left for later
Classification surveys and certificates, stock-on-board, risk-assessment links on critical deferrals (waits for the Risk Assessment module), tank inspection jobs (spec 08), projects and drydock deferral (spec 09), photos and attachments on completions (waits for file sync), chat on every job (spec 10).

## 7. Risks
- **Plan size.** 5,000 jobs and their history per vessel: lists and the board must be paged and the schedule projection must be cheap (one pass per machinery item). P1 includes a timing test at that size.
- **Projection must stay deterministic.** It is pure and covered by property-style tests (order of arrival must not change the result); any rule change is versioned.
- **Hours accuracy.** The projected hours date is only as good as the readings. The board shows when a date is an estimate and when readings are stale, rather than hiding it.
- **Clocks.** As with Worklist, readings and completions are ordered by their own dates first; the recorded time only breaks ties.
- **Template versioning.** Vessels may be on an older template version while apart; planned jobs keep the version they were made from until a proposed change is accepted.
