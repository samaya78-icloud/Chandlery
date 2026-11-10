# 09a. Projects: plan (implements spec 09 in stages)

Status: **approved 2026-11-10 ("Approve, start J1"). J1 (foundation: nine tables and an additive migration, status, links, cost and day rules as pure functions, project service, sync wiring with budget and money kept at the office, 12 new tests) built 2026-11-10.** A **first cut** (J1 to J6) gives a working project with its specification, budget, costs from procurement and the vessel's yard-stay reports, ending in a walkthrough test. A **second cut** (J7, J8) adds tiered approvals, variation orders and the fleet view. Yard tenders, the Gantt chart and the documents checklist are left for later. Reuses the patterns proven in the other modules: records written once, state worked out from them, one owner per record, module switch ("Projects", already in the policy list), Worklist and PMS links, `ModuleGate`, `[VesselAllowed]` with the lockdown test.

## 1. The core idea
A project is an **office document that the vessel contributes to**, and costs are **links to procurement documents, never copies**:

- **Project** is owned by the office: vessel, title, type (Drydock, Non-drydock), superintendent, planned and actual dates, yard (text), the UWILD flag, with its **specification items** (group, description, source, status) as children of the one document.
- **Status is worked out, not stored.** The office adds **status events** (submitted, approved, started, completed, closed, each with a name, a time and a note) and the status (Planning, Approved, Ongoing, Complete, Closed) is the latest one, the same way on both sides, as for risk assessments. A closed project is locked in the service and in the data layer for sync.
- **Budget and costs are the office's commercial data and never go to the vessel.** The vessel receives the project, its specification items (without costs), dates and status. **Budget lines, cost links and cost entries are shore-only.** The vessel can still add to the project (see section 2).
- **Costs are links.** A **project link** names a requisition, purchase order or spot invoice by its global id and says "this belongs to project X". Links are append-only (added, removed), written by whichever side made them, so a link made onboard stays valid ashore. A PO made from a linked requisition is linked automatically. Nothing is added to the PO, requisition or invoice documents, so their owner rules and sync stay as they are.
- **Actual cost is worked out from the links**, in US dollars with the rate already stored on each document (the way the dashboard does spend today), and shown in three columns: **committed** (approved POs), **invoiced** (finalized invoices) and **other** (manual entries, such as a yard bill paid outside the system). A PO that has finalized invoices counts at the invoiced amount instead of its own total, so nothing is counted twice.

## 2. What syncs
| Record | Writer | Direction | Kind |
|---|---|---|---|
| Project with its specification items (no costs) | office | down to the vessel the project is for | document, locked once closed |
| Status event | office | down | append-only |
| Budget lines, cost entries | office | **stay at the office** | shore-only document |
| Project link | the side that made it | both ways | append-only; the office reads the costs behind it, the vessel only sees that the link exists |
| Yard-stay note (date, text, progress percent, optional specification item) | vessel (or office) | both ways | append-only |
| Specification item request (a job the ship asks to be added) | vessel | up | append-only; the office accepts it into the specification or declines |

Lanes: `Transaction` for all of it. Scope: projects of the vessel. Reference code on the existing format (`MVSF-PR-2026-0001`, an H after the year when started ashore). **Files (drawings, reports, photos) wait for the file channel**, as in the other modules; notes carry text only until then.

## 3. Rules
- **Create** needs a vessel with the module switched on, a title and a type. **Who manages projects:** a company admin, or an office user flagged "Project manager" on the Team page (as for Risk reviewer); the superintendent in charge is picked from the office users.
- **First cut approval** is a single action by a project manager: it records who approved, the budget total at that moment and the time. **Tiered approval through the approval matrix** by budget total comes in J7, along with variation orders.
- **Budget vs actual:** variance is budget minus cost to date, and cost to date is committed plus invoiced plus other, using the rule in section 1. Contingency is kept per budget line and shown beside the total, not hidden in it.
- **Specification items** have a source (Worklist task, PMS job, class requirement, owner request, other) and a status (Open, Done, Dropped). Adding from the Worklist or PMS keeps the link, and **an item is never added twice** from the same task or job.
- **From the Worklist:** the vessel's open tasks of the nature a company marks as "deferred to drydock" (a Projects setting; the default is a nature whose name says so) can be added in one action. Marking the item Done adds a completed entry to the task (the Worklist rules about verification still apply). From PMS: due jobs of the vessel can be added the same way; recording the completion stays a PMS action (an item Done shows "record the job as done in PMS" until it is), so the job rules are not bypassed.
- **Close** needs every specification item Done or Dropped and a reconciliation note; it locks the project and puts the closing figures in the history.
- **Module off:** the same gate as the other modules; nothing is shown or accepted for a vessel it is off for.

## 4. Stages (each ends with two-database tests and your go-ahead to commit)
**J1. Foundation.** Models and an additive migration; pure functions with tests (status from the events, cost to date from the links, variance, planned against actual days, the no-double-counting rule); project, specification items, status events, notes, links, budget lines and cost entries as documents; `DocType.Project`; the shore-only kind (budget and costs never published to a vessel) and a test that proves it; module switch and lockdown wiring; the applier refuses changes to a closed project; sync wiring, vessel baseline, reference codes, tests.
**J2. Cost tagging and actuals.** A "Project" picker on requisitions, spot POs and spot invoices (shown only where the module is on for the vessel), the automatic link on a PO raised from a linked requisition, manual cost entries, and the actual-cost rollup that follows approvals and finalizing (a rejected PO drops out).
**J3. Office screens.** Project list (filters for vessel, superintendent, status, year; columns planned, actual, variance, days), create and edit form, detail page (overview, specification, budget against actual with the documents behind each number, history), menu group, finder pages, a Projects card on the dashboard.
**J4. Specification from the Worklist and PMS.** The one-action import of deferred-to-drydock tasks and due PMS jobs, the links back, and closing a task from an item marked Done.
**J5. The vessel's side.** The vessel's project list and detail (specification, dates, status, no costs), yard-stay notes, specification item requests and the office's accept or decline, tablet-friendly, notes arriving in order.
**J6. Walkthrough.** One end-to-end test: the office plans a project and adds items from the Worklist and PMS, the vessel adds notes and asks for another item while the office raises and approves a tagged PO, a spot invoice is finalized, a closed project is locked everywhere, and both sides end with the same specification, notes and status while the vessel never receives a budget or cost figure.

*Second cut, decided after J6:* **J7. Approvals and variation orders** (tiers from the approval matrix by budget total, a variation as an extra item and amount approved through the same chain). **J8. Fleet view** (drydock timeline across vessels, workload by month, spend).

## 5. Proposed answers to the open decisions (change any you disagree with)
1. **One vessel per project** in this plan, matching the sync scope used elsewhere. A drydock for several vessels is several projects (a shared group name can come later).
2. **Costs are links, and budgets stay at the office.** It protects commercial data and leaves the PO, requisition and invoice documents untouched.
3. **Currency:** budget and reporting in US dollars; documents converted with the rate stored on them, as the dashboard does.
4. **Yard** is a text field now; a yard master and tenders come with the tender work (left for later).
5. **Approval in the first cut** is one named action; the matrix tiers arrive in J7.
6. **Documents checklist and attachments** wait for the file channel; the "missing documents" report goes with them.
7. **PMS completion stays in PMS**, with the project reminding you, so job rules are never bypassed from a project.

## 6. Left for later
Yard tenders (invitations, quotes per item, comparison, awarding), the Gantt timeline, the documents checklist and missing-documents report, photos and drawings (file channel), a yard master, projects for several vessels, lessons learned, chat on every project (spec 10), and risk-assessment links (spec 06).

## 7. Risks
- **Double counting.** A PO and its invoice describe the same money. The rule in section 1 (a PO with finalized invoices counts at the invoiced amount) is a pure function with its own tests, and the detail page shows the document behind every number so a total can always be checked.
- **Confidentiality.** Budget and cost data must never reach a vessel. J1 builds the shore-only kind first and tests it before any screen depends on it; if the publisher cannot hold such a kind, it is extended in J1.
- **Links to documents that change.** A linked PO may be rejected or a linked invoice rejected after the fact; costs are worked out from the current state of each document, never stored on the link.
- **Currency.** A document with no stored rate is counted at its own amount, as the dashboard does today, and the detail page says so plainly.
- **Closed means closed.** Enforced in the service and in the data layer for sync, with a test that tries every edit path, as for the other modules.
- **Duplicate imports.** Each specification item remembers the task or job it came from, so importing twice adds nothing.

## 8. Decisions made while building J1
- **Status events** are Approved, Started, Completed, Closed and Withdrawn (an approval taken back, only before work starts). "Submitted" comes with the matrix tiers in J7. The steps must follow in order, and a project can only move forward from where it is.
- **Actual dates are the dates on the Started and Completed events**, which the office enters (the day the ship reached the yard may be earlier than the day it was recorded). The project itself holds only the planned dates.
- **Money never leaves the office.** The project, its items, status events, notes and links are registered for sync; budget lines, item estimates, cost entries and the approved totals are ordinary tables that are not registered, so nothing is ever published for them and a vessel installation refuses to write them. A vessel starts from an empty database, so none can arrive by copying either. Two tests prove it: a vessel holds none, and cannot create one.
- **What was approved is kept.** Approving writes the budget total (and contingency) at that moment beside the event, so withdrawing, changing the budget and approving again leaves both figures on record.
- **Specification items are marked Done or Dropped by the office** (saving the specification); the vessel reports progress in notes. Removing an item removes its estimate.
- **Who may do what** is not decided in the service yet: the "Project manager" permission arrives with the screens in J3, as the Risk reviewer flag did in R4. Onboard, the service refuses everything except notes and links.
- **Links** can name a requisition, an order or an invoice. The vessel holds its own requisitions and orders but not invoices (they are office business), so only the office can link an invoice. A document belongs to the project of its latest link; linking it to another project moves it, and saying the same thing twice changes nothing.
- **Closed means closed** for everything: items, budget, cost entries, notes and links are refused, on both sides, and a changed copy of a closed project arriving by sync is ignored.
