# 05a. Worklist: plan (implements spec 05 in stages)

Status: **approved 2026-11-07. W1 built 2026-11-07** (data, replay, sync, rules, tests; no screens yet, they are W2); W2 to W5 to do. Builds on the sync engine, vessel groups and policy (module switches), crew logins, and the ownership patterns proven in procurement sync.

## 1. The core idea: history is the only thing that changes
Spec 05 says nobody overwrites anyone else's work and history cannot be edited. The simplest design that guarantees it:

- A **task** is created once and never edited. It holds only identity: code (`SF-WL-2026-0001`), vessel, who raised it and from which side, source, nature, category, department.
- Everything else (title, description, priority, planned and target date, assignee, status, percent complete, deferrals, links, completion, verification) is the result of **timeline entries**. The first entry, "Created", carries the starting values. Later entries are comments, status changes, percent updates, date changes (with a reason), reassignments, priority changes, deferrals, links, "completed", "verified".
- An entry is written once by one side and never changed or deleted by anyone; the data layer refuses it on both vessel and office.
- The task's current state is a **projection**: both sides replay the entries in a fixed order (time, then entry id) with the same pure function, so they always show the same result. The projected columns are kept on the task row for fast lists and are never sent; only tasks and entries travel.
- If the vessel and the office both change the target date while apart, both entries are kept and visible; the later one wins, and the history shows both. No work is lost.

## 2. What syncs
| Document | Created by | Goes to | Notes |
|---|---|---|---|
| WorklistTask (identity only) | vessel or office | the other side, for the task's vessel | immutable after creation |
| WorklistEntry (append-only) | vessel or office | the other side | owned by its author's side forever; never edited |
| Lists (source, nature, category) | office | every vessel of the company | joins master data |

Scope: a vessel receives tasks and entries for its own vessel only. Lane: Transaction (an urgent task goes on the Urgent lane). Offline: everything works with no link.

## 3. Rules
- **Verification** is a flag on the source (class, port state control and so on require it): a vessel completing such a task shows "Completed, awaiting verification" until the office adds a "Verified" entry.
- The office may change priority, target date and assignee of a vessel-raised task; both sides add progress. Both through entries.
- **Defer** needs a reason and a new target date (counted and shown); "Defer to drydock" waits for the Projects module.
- **The module is switched on per company, group or vessel** (existing module switches); off by default, so nothing appears until the office turns it on.

## 4. Stages (each ends with two-database tests and your go-ahead to commit)
**W1. Foundation.** Task, entry and list tables; the projection function with its tests; immutability guard; lists with sensible defaults per company; sync of tasks, entries and lists; reference numbers; module switch gate. No screens beyond a bare list.
**W2. Screens and workflows.** Vessel board (overdue, this week, later, completed; filters by source, nature, department, priority), quick add, task detail with timeline, add progress, change status, defer, complete; office can change priority, target and assignee and verify. Lists admin page.
**W3. Fleet view and reports.** Office fleet worklist (vessel and group filter), overdue by vessel, tasks awaiting verification, new from vessels; reports (overdue, ageing, completion rate by source) with CSV export; overdue tiles on the vessel home and office dashboard; an escalation list of tasks overdue by more than N days (shown in the app, since Chandlery sends no email).
**W4. Links and copies.** Link a task to a requisition or order; "purchase needed" opens a requisition with the parts pre-filled; transfer a task to sister vessels or a group (new tasks linked to the origin).
**W5. Walkthrough.** One end-to-end test: vessel and office both work on tasks while apart, and both sides end with the same history and the same overdue counts.

## 5. Left for later
Attachments and photos on entries (waits for file sync), fleet-level single tasks that list several vessels (copies cover this for now), reminders by email (nothing in Chandlery sends mail), approval of deferrals by policy, "defer to drydock" (needs Projects), quick add from PMS and inspections (those modules do not exist yet).

## 6. Risks
- Clocks differ between vessel and office. Entries are ordered by time, so a skewed vessel clock can make an entry win or lose against an office entry made within the skew. History stays intact either way; the engine's clock-skew warning already flags large skews.
- The projection must stay deterministic: it is pure, covered by property-style tests, and any rule change is versioned with the entry schema.
