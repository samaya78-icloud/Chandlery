# 06a. Risk assessment: plan (implements spec 06 in stages)

Status: **approved 2026-11-09. R1 in progress.** Reuses the patterns proven in Worklist, PMS and Tank Inspection: records written once, state worked out from them, one owner per record, module switch ("Risk Assessment"), office master data for templates, Worklist tasks, `ModuleGate`, `[VesselAllowed]` with the lockdown test.

## 1. The core idea
An assessment is a **document written by one side, signed once, then frozen**:

- **Library items are office master data** (categories, risk matrix, templates with their activities and hazards), copied to every vessel read-only, like Tank checklists.
- An **assessment** is created from a template (or blank) by the vessel or the office. It is owned by the side that made it until it is submitted. Its activities, hazards and team are children of the one document and travel with it. Each hazard keeps a **snapshot** of the template text and the matrix scores, so a later template or matrix change never alters an existing assessment.
- **Sign-off is separate and append-only.** The reviewer (Master or Chief Engineer onboard, a superintendent ashore) adds **sign-off records** (started, signed, returned with comments), each naming the submission it answers. The assessment's status (Draft, Submitted, Signed, Returned) is **worked out** from the submission count and the latest sign-off, the same way on both sides, exactly as `TankStatus.Of` does.
- **Signed means locked.** The service refuses edits, and the data layer refuses a changed copy arriving by sync. Changing a signed assessment is **New version**: a copy that links back to the old one, which becomes Superseded (worked out from the existence of a newer version, not stored).
- **Review dates are worked out, not stored:** next review = last signed date + the review period (default 11 months, set per company). A review is a new version of kind **Annual**; "repeat of a job" is a new version of kind **Interim**. Both start from the last signed version.
- **Scoring** uses the company's matrix (default 5x5: consequence 1-5 by probability 1-5, factor = product, band by thresholds). Bands and thresholds are data, not code. Every hazard is scored twice: before controls and after controls. The editor shows the band live.

## 2. What syncs
| Record | Writer | Direction | Kind |
|---|---|---|---|
| Category, risk matrix and bands, template with activities and hazards | office | to every vessel | master data |
| Assessment with team, activities, hazards | the side that made it | up from a vessel; down when shared by the office | document, locked once signed |
| Sign-off record | the reviewing side | both ways | append-only |
| Share record (assessment shared with a vessel or group) | office | down | append-only |

Lanes: `Transaction` for assessments and sign-offs, `Master` for the library. Scope: assessments made for, by or shared with the vessel. Reference code `RA-{origin}-{category}-{seq}` using the existing reference-code service, so a new version keeps the number and adds a version suffix (`RA-SF-HOT-0007 v2`).

## 3. Rules
- **Submit** needs: job description, category, at least one team member, at least one activity, every hazard scored before and after with the after-score not above the before-score unless a reason is given, and every hazard whose residual band is High or Extreme must name a control, a responsible person and a due date.
- **Sign-off** is by a user with the reviewer role. A person cannot sign an assessment they alone wrote when a second eligible reviewer exists on that side (kept as a company setting, off by default, because small vessels may have one reviewer).
- **Worklist:** each additional control with an owner and due date raises one task, linked to the assessment, once; resubmitting or a new version never raises the same control twice (each hazard remembers its task, as Tank results do). The next-review date raises one reminder task a set number of days ahead (default 30), cancelled when a newer version is signed.
- **Sharing:** the office shares a signed assessment with a vessel or group; it then appears in the vessel library read-only with a "shared" flag. Un-sharing stops new copies, never removes a vessel's own copy.
- **Template updates:** publishing a new template version tells vessels "newer template available" on assessments built from it; it never rewrites them.
- **Module off:** same gate as other modules, nothing is shown or accepted for a vessel the module is off for.

## 4. Stages (each ends with two-database tests and your go-ahead to commit)
**R1. Foundation.** Models and an additive migration; pure functions with tests (risk factor and band from the matrix, status from submission count and latest sign-off, next review date, the version chain, hazard validation); library tables as master data; assessment, team, activity, hazard and sign-off as synced documents; `DocType.RiskAssessment`; module switch and lockdown wiring; the applier ignores a changed copy of a signed assessment; default categories (the eight in the spec) and the default 5x5 matrix created for a company on request and for new companies; baseline for vessels; sync tests.
**R2. Library (office).** Categories and matrix admin, template list and editor (activities, hazards, default controls and scores), versions, publish to fleet or group, nav group.
**R3. Assessment editor.** Start from a template or blank, guided steps (details, team, activities, hazards, scoring with live band, controls, review), draft save, validation before submit, tablet-friendly, assessment list with filters (scope, category, status, due for review).
**R4. Sign-off and lifecycle.** Review queue, start / sign (with confirm, then locked) / return with comments, New version, Interim and Annual copies, Superseded display and version history, share with vessel or group.
**R5. Worklist, reminders and dashboards.** Control-action tasks and review reminders, due-for-review, high-residual-risk and old-unsigned-draft tiles and menu counts, PMS link on a critical equipment deferral (shown as a link to pick an assessment; enforcing it is a company setting), PDF export of a signed assessment with signatures and version history.
**R6. Walkthrough.** One end-to-end test: the vessel writes and submits, the office edits the library meanwhile, the office returns one and signs another, a signed one gets a new version and the old is Superseded, a control action reaches the Worklist once, and both sides end with the same assessments, statuses and review dates.

## 5. Proposed answers to the open decisions (change any you disagree with)
1. **Reviewer role:** a "Risk reviewer" permission a company admin grants to named users, the same way the Tank review is limited to the office, rather than hard-coding Master and Chief Engineer ranks.
2. **Review period:** 11 months by default, a company setting; Interim has no due date of its own.
3. **Matrix:** one matrix per company in this plan (default 5x5), with editable labels and band thresholds. Different matrices per category are left for later.
4. **Attachments, permits and photos:** wait for the file channel (as Tank Inspection did); until then a hazard carries a text note.
5. **PDF export:** built last (R5), from a print-styled page, so it needs no new library.
6. **Team members:** can be users or typed names, as the spec says; typed names are plain text with a rank.

## 6. Left for later
Attachments and permits, per-category matrices, templates by ship type, PMS enforcement beyond a company setting, chat on every assessment (spec 10), project links (spec 09).

## 7. Risks
- **Document size.** An assessment with many activities and hazards is one document; a cap of 25 activities and 20 hazards per activity keeps it small, and the editor must stay fast on a tablet.
- **Locked means locked.** Enforced in the service and in the data layer for sync, with a test that tries every edit path, as for Tank Inspection.
- **Snapshots versus matrix changes.** Hazards store their scores and band at the time, so changing the matrix never changes history; the editor shows the stored band, with a plain note when the current matrix would give another.
- **Duplicate tasks.** One task per control action and one reminder per assessment chain, each remembered on its record.
- **Version chain across sides.** A new version made on one side while the other still shows the old one: both versions are valid documents; Superseded is worked out from the newest signed version in the chain, so both sides agree once synced.
