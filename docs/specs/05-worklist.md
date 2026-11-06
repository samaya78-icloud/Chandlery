# 05. Worklist

A recorded list of tasks the vessel must do, each with a full progress history until completion. First module built on the platform because it exercises two-way writes and append-only history.

## 1. Goals
- One place for everything a vessel owes: audit and inspection findings, class and PSC items, defects, recommendations, PMS deferrals, internal tasks.
- Both office and vessel can create and progress tasks; nobody overwrites anyone else's work.
- Overdue work is visible per vessel and across the fleet.

## 2. Concepts
- **Task** (the document): code, title, description, source, nature, categories, priority, status, planned date, target (end) date, assignee/department, links.
- **Source ("raised by")** a list: audit, class, charterer inspection, port state control, incident, safety officer, PMS deferment, management, vessel. Configurable list.
- **Nature:** Planned, Emergency, Deferred, Survey, Recommendation, Condition. Configurable.
- **Categories:** primary, secondary and minor levels from a hierarchical configurable list.
- **Departments:** office department and vessel department.
- **Priority:** Normal, Urgent (extendable).
- **Status:** Open, In progress, Waiting (parts/yard/external), Completed, Cancelled. Status changes are events.
- **Timeline entries (append-only):** comment, status change, percent complete, date change (with reason), reassignment, attachment, link added, deferral.
- **Links:** PO/requisition (spares needed), PMS job, risk assessment, tank inspection exception, project/drydock specification item, other vessel (transfer).

## 3. Ownership and sync
| Item | Writer |
|---|---|
| Task created by vessel | vessel creates; shore may edit priority, target date, assignee (with timeline entries) |
| Task created by office | shore creates; vessel adds progress; vessel marks "completed" for review |
| Closure | shore confirms completion where the task type requires verification |
| Timeline entries | either side, append-only |
Lane: `Transaction`. Scope: tasks of the vessel (and fleet-level tasks that list the vessel).

## 4. Workflows
- **Create:** quick add from anywhere (PMS defect, inspection exception, risk action) pre-fills source and link.
- **Progress:** updates with percent and notes; attachments (photos, reports).
- **Defer:** a deferral requires reason, new target date and optionally approval (policy); "Defer to drydock" links the task to a drydock project (spec 09).
- **Transfer to other vessels:** copy a task to sister vessels or a group (new tasks linked to the origin).
- **Purchase needed:** a task can raise a requisition with parts pre-filled; delivery updates the task's "waiting for parts" state.
- **Complete and verify:** vessel completes with evidence; office verifies if required.
- **Reminders and escalation:** upcoming and overdue notifications by role; overdue beyond N days escalates to the superintendent.

## 5. Screens
- **Vessel:** my open tasks board (today, this week, overdue), filters by source, nature, department, priority; task detail with timeline.
- **Office:** fleet worklist (vessel/group filter), overdue by fleet, tasks waiting for verification, new from vessels.
- **Configuration:** lists for source, nature, categories, priorities (admin).
- **Reports:** overdue worklist by fleet, ageing, completion rate, by source (audit vs class vs PSC); export CSV/PDF.

## 6. Data model (summary)
`WorklistTask`, `WorklistTimelineEntry` (type, text, payload, author, node), `WorklistLink` (type, target id), uses shared `Lists` and `Attachments`.

## 7. Acceptance criteria
- Vessel adds progress while offline and the office sees entries in order after sync; office edits of the target date and vessel progress entries never overwrite each other.
- A deferred-to-drydock task appears as a drydock specification item.
- Overdue counts match between vessel and shore after sync.
- Task history cannot be edited or deleted.
