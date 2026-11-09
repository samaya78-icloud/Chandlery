# 08a. Tank inspection: plan (implements spec 08 in stages)

Status: **approved 2026-11-08. T1 (foundation: data, scales and status rules, tanks, checklists as master data, inspections and reviews, sync, tests) built and pushed 2026-11-08; T2 (tank list and forms, tank plan coloured by type or latest inspection, copy to sister vessels, menu group) built and pushed 2026-11-08; T3 (inspections onboard: start form, tablet checklist screen, drafts and submit, worklist tasks for exceptions, PMS tank link, inspection list) built and pushed 2026-11-08; T4 (office: review queue with Bad rating first, review panel with highlighted keywords, start / accept (confirm, then locked) / return, urgent alert and menu count, checklist and criteria editor, configurable keywords) built and pushed 2026-11-08; T5 (per-tank trend matrix and coating chart, exception summary with vessel, rating and tank-type filters, tiles and menu counts) built and pushed 2026-11-09; T6 (walkthrough test) in progress.** Builds on the patterns proven in Worklist and PMS (records written once, state worked out from them, one owner per record, module switch, master data for templates) and links to both of them.

## 1. The core idea
An inspection is a **vessel document that moves through review**, not a record that two sides edit:

- **Tank** is an ordinary synced document with one owner (the side that made it): name, type, position on the tank plan and the structural data. The office keeps the vessel's tank master; a vessel may add tanks it needs.
- **Checklist templates and their criteria** are the office's master data, copied to every vessel (like the Worklist lists and the PMS library). A criterion says what is looked at, in which group, on which scale, and which values count as an exception.
- An **inspection** is made and filled in onboard (tank, date, place, work done, the ticks and ratings, comments) and is owned by the vessel. Each criterion result keeps a **snapshot** of the criterion's name, group and scale, so changing a template later never changes a past inspection.
- **Review is separate and append-only.** The office never edits an inspection. It adds **review records** (started, accepted, returned with comments), each naming the submission it answers. The inspection's status (Draft, Submitted, In review, Returned, Reviewed) is **worked out** from its submission count and its latest review, the same way on both sides.
- **Reviewed inspections are locked.** A returned inspection goes back to the vessel for correction and is resubmitted (the submission count goes up, and the status is Submitted again). A correction to a reviewed inspection is a new inspection, as the spec says.
- **Exceptions are worked out, not stored:** any result whose value is in its criterion's exception list. Submitting raises a Worklist task for each new exception (a Bad rating urgent), linked to the inspection, so the reviewer can confirm it or dismiss it (cancel) there.

## 2. What syncs
| Record | Writer | Direction | Kind |
|---|---|---|---|
| Checklist template, criterion | office | to every vessel | master data |
| Tank (with structural data and plan position) | the side that made it | both ways, for the vessel | document |
| Inspection with its results | vessel | up | document, locked once reviewed |
| Review record | office | down | append-only |

Lanes: `Transaction` for inspections and reviews, `Master` for templates, `Urgent` for a submitted inspection that contains a Bad rating. **Photos wait for the file channel** (the same limit as attachments elsewhere); until then a result carries a text note, and the structure is ready for photos to be added.

## 3. Rules
- **Scales:** Rating (Good, Fair, Bad, NI, NA), Yes/No/NA, and the cathodic percent buckets (0–25, 26–50, 51–75, 76–100, NA). The values are lists in the data, not code.
- **Exception values** are set per criterion. Defaults: Fair and Bad on rating criteria, none on Yes/No and percent criteria (a template author can add, for example, "0–25").
- **Submitting** needs every criterion answered (NI and NA are answers) and a place and date. A returned inspection can be edited by the vessel until it is resubmitted.
- **PMS link:** a PMS job of type Inspection can name a tank. Starting an inspection from the job links the two; **submitting the inspection completes the job**, and completing such a job any other way is refused ("complete it by inspecting the tank").
- **Review keywords** (rust, anode, crack, pitting, corrosion, leak, buckling) are highlighted in comments for the reviewer; the list is configurable.
- A **tank list can be copied** from a vessel to sister vessels in one action, as plans are in PMS (additive, safe to repeat).

## 4. Stages (each ends with two-database tests and your go-ahead to commit)
**T1. Foundation.** Tables and migration; scales, exception rule and status calculation as pure functions with tests; templates and criteria as master data; tanks, inspections, results and reviews as synced documents; module switch gate; standard checklist templates per tank type from the spec's criteria lists (created for a company on request and for new companies); sync wiring, vessel baseline, tests.
**T2. Tank master and tank plan.** Tank list with type and structural data, add and edit, the schematic plan (each tank a shape placed on a grid, coloured by type or by latest rating, click for history), copy tanks to sister vessels, nav.
**T3. Inspections onboard.** Start an inspection (tank and template), the checklist screen designed for a tablet (large rating buttons, comments, place, work done), save as draft, submit, Worklist tasks for exceptions, the PMS link (tank on an inspection job, complete on submit), the inspection list.
**T4. Review and templates (office).** Review queue by vessel with keyword highlights, start review, accept, return with comments, the lock on reviewed inspections, the urgent alert for a Bad rating; template and criteria editor.
**T5. Trends and exceptions.** Per tank: the criteria-by-date matrix coloured by rating and a coating-condition chart over time; the exception summary (choose vessels, rating, tank type chips, drill to the inspection); tiles and menu counts.
**T6. Walkthrough.** One end-to-end test: the vessel inspects and submits while the office works on templates, the office returns one inspection and accepts another, and both sides end with the same history, statuses, exceptions and trends.

## 5. Proposed answers to the open decisions (change any you disagree with)
1. **Vessel types first:** a generic set of templates by **tank type** (Cargo, Ballast, Slops, Fresh water, Void space or cofferdam, Other) using the criteria groups in the spec. The ship type is kept as an optional field on a template, so type-specific sets (gas carriers and so on) can be added later without changing the design.
2. **Criteria sets:** the starter templates carry the lists given in spec 08 (variable data, internal fittings, structure, reports, type-specific). The office edits them freely; inspections keep their snapshot.
3. **Photos for a Bad rating:** not enforced until the file channel exists; the checklist asks for a descriptive comment on a Bad rating instead.

## 6. Left for later
Photos and drawings (wait for file sync), per-ship-type template sets, trend charts beyond coating condition, a tablet-specific offline package, chat on every inspection (spec 10), and risk-assessment links (spec 06).

## 7. Risks
- **Checklist size.** An inspection has about 40 to 70 results; they travel with their inspection as one document, which stays small enough, and the checklist screen must stay quick on a tablet.
- **Locked means locked.** The "reviewed inspections cannot be edited" rule is enforced in the service and checked by a test that tries to edit through every path. The data layer refuses a change to a reviewed inspection arriving by sync as well.
- **Template changes.** Past inspections keep their snapshot, so trends compare criteria by their stored name and group. Renaming a criterion starts a new row in the trend matrix (shown plainly), rather than rewriting history.
- **Duplicate tasks.** Resubmitting a returned inspection must not raise the same exception twice; each result remembers the task it raised.
