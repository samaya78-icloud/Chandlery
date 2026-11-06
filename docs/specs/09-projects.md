# 09. Projects (drydock and technical)

Planned yard and technical projects with budget versus actual cost, schedule, documents and approvals. Cost actuals come straight from procurement.

## 1. Goals
- One place to plan, run and review projects: drydocks, repairs, retrofits and other technical projects.
- Real-time budget vs actual because POs and invoices already know their vessel, department and project.
- A specification list assembled from Worklist items deferred to drydock and PMS jobs.

## 2. Concepts
- **Project:** id, type (Drydock, Non-drydock), vessel(s), title, status (Planning, Approved, Ongoing, Complete, Closed), superintendent in charge, planned and actual dates, yard awarded, budget, approvals.
- **Specification item (job):** group, description, source (worklist task, PMS job, class requirement, owner request), estimated cost, quoted cost, status, linked tasks.
- **Budget lines:** category, planned amount, currency (USD for reporting), contingency.
- **Cost actuals:** POs, invoices and credit entries tagged to the project; manual cost entries allowed with attachments.
- **Yard tender:** invitations to yards, quotes per spec item, comparison (reuses the RFQ/evaluation engine).
- **Documents checklist:** required documents with status and "missing documents" report.
- **Timeline:** planned vs actual days, phases (arrival, docking, undocking, sea trial), Gantt-style view.
- **Flags:** underwater inspection in lieu of drydock (UWILD) where applicable.

## 3. Lifecycle and approvals
Draft → submitted for approval → approved (budget and yard) → ongoing → complete → closed after cost reconciliation. Approvals use the approval engine with amount tiers.

## 4. Ownership and sync
Projects are mostly **shore-owned**. The vessel contributes: spec item requests, progress notes, photos, daily reports during a yard stay, all append-only. Lane: `Transaction`; scope: projects of the vessel.

## 5. Workflows
- Create a project, import candidate items from the vessel's deferred-to-drydock worklist and due PMS jobs.
- Tender to yards, evaluate, award; approved quote becomes the spec/budget baseline.
- During the stay: daily progress, variation orders (approval), cost tracking.
- Close: reconcile actuals vs budget, lessons learned, update PMS (jobs done at yard) and Worklist (items closed).

## 6. Screens
- **Project list:** filter by fleet, vessel, superintendent, yard, year, status; columns planned/actual/variance cost, days, approval state; views for 12-month workload, documents, pending approvals, management review.
- **Project detail:** overview, spec items, budget vs actual, tender, documents, timeline, variations, history.
- **Fleet view:** drydock timeline across vessels, workload per month, spend.
- Non-drydock list with project-wise and vessel-wise listing, ongoing/complete.

## 7. Integrations
Requisitions and POs can link to a project; invoices inherit it; Worklist items and PMS jobs link to spec items; attachments for drawings and reports.

## 8. Acceptance criteria
- Actual cost updates automatically when a PO or invoice tagged to the project is approved.
- Deferred-to-drydock Worklist items can be added to a project's specification in one action and close when the item is done.
- Planned vs actual cost and days are correct on the list and detail screens.
- A vessel's yard-stay reports sync to shore in order.
