# 07. Planned maintenance system (PMS)

For all vessels. A standard job library owned by shore, applied to each vessel's machinery, plus the option to import a vessel's own plan. Jobs are driven by **running hours, calendar, or both (whichever comes first)**. Classification surveys and certificates are **out of scope for now**.

## 1. Goals
- A complete maintenance plan per vessel that works fully offline onboard.
- Shore sees compliance, overdue work and approves deferrals.
- Deferrals, defects and spare needs connect to Worklist and purchasing.

## 2. Concepts
**Shore-owned library (versioned)**
- **Machinery class** hierarchy (e.g. main engine › cylinder unit), with typical makers/models.
- **Job template:** code, title, instructions/checklist, type (Check, Overhaul, Test, Service, Inspection), interval (calendar n days/months and/or running hours), critical flag, required rank/department, estimated duration, required spares, optional risk-assessment link.

**Vessel plan**
- **Machinery item:** location › system › subsystem › item, maker, model, serial, critical flag, class mapping, optional hour meter.
- **Planned job:** instance of a template or a vessel-own job; interval overrides (change-controlled), last done date and hours, computed next due.

**Append-only vessel records**
- **Running-hours reading** (meter, hours, date, source, correction flag).
- **Job completion** (done date, hours at done, performed by, verified by, comments, photos, spares used, time spent).
- **Deferral request** (job, reason, requested new due, status, approver comment).
- **Plan change request** (add/modify job or machinery, reason, status).

## 3. Scheduling rules
- **Due = whichever comes first:** calendar date (`last done + calendar interval`) or the date the hours interval is projected to be reached (`last done hours + interval ÷ average daily hours over the last 30 days`).
- **Anchor option per job:** *from completion* (floating) or *fixed calendar* (e.g. annual items).
- **Grace window** per job (default 10% or a set number of days); beyond it the job is **overdue**.
- **Stale hours:** no reading for N days → job flagged "hours out of date"; corrections are new readings, never edits.
- Calculations run on the vessel, offline, and on shore for fleet views.

## 4. Workflows
- **Execute:** crew open a due job, follow the checklist, record hours, parts, photos, complete. Optional second sign-off (policy).
- **Overdue and deferral:** the vessel requests deferral with reason and new date; the superintendent approves within a limit, escalates above it; critical equipment can require a risk assessment. Uses the approval engine.
- **Defect found:** one tap creates a Worklist task linked to the job.
- **Spares needed:** raise a requisition from the job with parts pre-filled from the machinery spares list.
- **Plan change request:** vessel proposes, office approves into the plan or template.
- **Template update:** the office edits a template; vessels receive a **proposed change with diff**, nothing changes silently.

## 5. Library, import and sister vessels
- **Apply library:** map the vessel's machinery to classes → jobs generated.
- **Clone from sister vessel** or group, with a "drift" report (jobs not matching).
- **Import own plan** (Excel/CSV wizard): map columns (location, system, job code, title, interval, last done date/hours), dry-run preview, suggested template matches, unmatched kept as vessel-own jobs, imported history read-only.

## 6. Ownership and sync
| Record | Writer |
|---|---|
| Library, approved plan, approved changes | shore |
| Planned job instances, vessel-own jobs | vessel (within policy); intervals via change request |
| Readings, completions, comments | vessel, append-only |
| Deferral decision | shore (or authorised onboard role within a policy limit) |
Lanes: `Transaction` for completions/readings, `Urgent` for critical overdue and deferral requests, `Master` for library. Plan size up to ~5,000 jobs per vessel.

## 7. Screens
- **Vessel:** board (Overdue / This week / Next 30 days) by department; job detail with checklist and history; fast daily running-hours entry; machinery tree; spares per machinery.
- **Office:** fleet compliance dashboard (% on time, overdue, critical overdue), overdue approval queue, deferral history, sister-vessel drift, library management, change-request queue.
- **Reports:** jobs done, jobs to be done, overdue reports submitted/not submitted by vessel, running-hours summary.

## 8. Integrations
Worklist (defects, deferrals), purchasing (spares), risk assessment (critical deferrals), tank inspection (inspection jobs open an inspection, spec 08), projects/drydock (jobs deferred to yard), attachments.

## 9. Policy controls (spec 02)
Which job types a vessel may self-defer and by how many days; whether intervals may be edited directly; whether completions need verification.

## 10. Phases
1. Library, machinery, planned jobs, readings, completions, scheduling, dashboards.
2. Deferral approval and plan change requests.
3. Import wizard.
4. Spares and stock-on-board integration.
5. Later: classification surveys and certificates.

## 11. Open decisions
Second sign-off rule, who edits intervals, default grace window (see README).

## 12. Acceptance criteria
- A job with both hours and calendar intervals becomes due at whichever occurs first, offline.
- Correcting a wrong reading adds a corrected reading and recomputes due dates without losing history.
- A deferral approved ashore reaches the vessel and shifts the due date; unapproved deferral keeps the job overdue.
- An imported plan of 3,000 jobs completes with a preview, mapping to the library where possible.
