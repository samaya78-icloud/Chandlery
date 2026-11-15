# 13a. Drills and safety meetings: plan (implements spec 13 in stages)

Status: **started 2026-11-12 ("Start spec 13"; the spec's open questions were left to the recommended answers below). C1 built 2026-11-12, uncommitted:** `Models/Drills.cs` (drill types, per-vessel overrides, drill with participants and notes, office comment), `Services/DrillRules.cs` (pure: due dates in days and calendar months, status with the warning, a person's own date with the joiner's 14-day grace, quarter-of-crew rule, possible duplicates, time checks), `Services/DrillService.cs` (types and overrides, record, change while unsigned, the Master's signature, notes after signing, deficiency to Worklist, office comment, the schedule), `DrillDefaults` (eleven common types, each "to verify"), the new authority ConductDrills, sync wiring including the child-to-document mapping in change tracking, module "Drills and Meetings", references DR and MT, migration `Drills` (six tables). 26 pure and 9 two-database tests pass. C1 pushed 4398fa3. C2 (vessel screens) built, live-checked and pushed 639fb25: schedule with the people due or late and "My drills", record and change a drill, the drill page with the Master's signature, notes, actions and the office's comment, history; the quarter rule applies only where a type everyone attends exists. C3 (meetings) built, live-checked and pushed 0b9992b: `Models/Meetings.cs`, `Services/MeetingService.cs` (types, record, change while unsigned, sign, notes, office comment, actions as Worklist tasks, open actions carried to the next meeting of the same kind, the schedule), five default meeting types, pages `/meetings`, `/meetings/new`, `/meetings/{id}`, migration `Meetings` (six tables). 5 pure and 7 two-database meeting tests pass.** Builds on the patterns proven in rest hours and incidents: a document the vessel writes, state worked out by pure functions, office master data, a module switch, two-database tests, and (a lesson from incidents) every child table of a document mapped to its parent in change tracking so edits after the first sync travel.

## 1. Answers used for the open questions (change any and the plan follows)
1. **Drill types and intervals:** the common set in spec 13 as the company's starting list (fire and abandon ship monthly for every crew member; enclosed space entry and rescue every two months; man overboard, steering gear, blackout, oil spill, security every three months; medical, collision or grounding, cargo or fuel spill every six months). Every default carries "to verify against the flag state, class and the SMS". The office edits the list and may switch a type off or change its interval for one vessel, with a reason.
2. **Per person or per ship:** both. A type marked "every crew member" tracks each person's own last participation; others track the ship.
3. **Who conducts:** the Master, and crew the office has given the new authority **ConductDrills** (the crew authority list from rest hours, step A1). The Master signs.
4. **Safety committee:** monthly by default, configurable per meeting type; minutes reach the office by sync as they are written.
5. **Rest hours link:** the offer to add a drill's duration to the participants' hours of work is built last (C5), as a button the conductor presses, never silently; until then the drill page tells the Master how long it ran.
6. **Existing records:** no import; a vessel with no history shows "Never held" until its first drill is recorded.

## 2. The core idea
- **A drill is a document the vessel writes** (`Drill` with participants and notes). It is editable by its conductor or the Master until the Master signs; after that only dated notes are added.
- **The schedule is worked out, never typed:** for each type on each vessel, the last drill gives the due date (`DrillRules`); for the types that every crew member must take part in, each person's own last participation gives theirs. A new joiner gets a grace period, an excused absence does not reset the clock.
- **Findings become action:** a deficiency is a Worklist task linked to the drill (link type `drill`), and the office may comment on any drill (a separate appended record).
- **Types and overrides are the office's master data**, copied to each vessel.

## 3. What syncs
| Record | Writer | Direction | Kind |
|---|---|---|---|
| Drill type | office | to every vessel | master data |
| Per-vessel override of a type | office | to that vessel only | master data |
| Drill with participants and notes | vessel | up | document |
| Office comment on a drill | office | down | append-only |
| Meeting type | office | to every vessel | master data (C3) |
| Meeting with attendees and agenda items | vessel | up | document (C3) |
Lane: Transaction. A vessel holds only its own drills and crew.

## 4. Stages (each ends with two-database tests and your go-ahead to commit)
**C1. Drills foundation.** Models, migration; `DrillRules` (due date for days and for calendar months, status with the warning period, individual due dates with the new-joiner grace period, the quarter-of-crew rule, possible duplicates); `DrillService` (types, per-vessel overrides, record, change while unsigned, Master's signature, notes after signing, deficiency to Worklist, office comment, the schedule, reading); defaults; sync wiring; module "Drills and Meetings"; references `DR` and `MT`; the new authority ConductDrills; tests.
**C2. Vessel screens.** The schedule (each type with last held, next due and status, people overdue underneath, the quarter-of-crew warning), record a drill (type, scenario prefilled, tick participants by name with "everyone present", findings, deficiencies, equipment), history, a crew member's own record, the Master's signature, menu and lockdown list.
**C3. Meetings.** Meeting types and standard agendas (office), the meeting record (attendees, agenda items with notes and actions, suggestions, signatures), the previous meeting's open actions in front of the room and carried over, overdue meetings; their own migration.
**C4. Office.** Fleet drill status (vessel by type, overdue first), vessel detail, crew participation, types and overrides editor, meetings overview, drill and meeting PDFs, CSV exports, comments on a drill. **Done:** fleet table with overdue count in the menu (`/drills/fleet`), crew participation (`/drills/crew`), types and per-vessel rules editor (`/drills/types`), drill/meeting PDFs, drill log PDF, CSVs; comments were already on the drill and meeting pages.
**C5. Links and dashboard.** The dashboard card and attention items, a failed equipment test starting a PMS job, a drill finding starting an incident report (and back), the rest-hours offer, menu counts. **Done (C5a):** home-page card with attention items (vessel and fleet), "Report an incident about this" from a drill with findings (form prefilled). **C5b done:** equipment picked from the vessel maintenance plan, on the drill form and on an action (the Worklist task carries the equipment and a link; PMS has no one-off job, so a task is the corrective job), and the rest-hours button (the Master or a crew admin adds the drill to the hours of those whose recorded day shows rest while it ran; same recording as the hours screen, reason kept, never silent). Not built: an incident report asking for a drill.
**C6. Walkthrough and mutation check.** The module as one story across a real office and vessel database (including a crew change and the quarter rule), the vessel holding only its own records after a baseline, then deliberate changes to `DrillRules` against the pure tests.

## 5. Risks
- **Intervals differ by flag state and company.** They are data with "to verify" on every default, never constants in code.
- **Counting people right.** The per-person rule (joiners, leavers, excused absences, a drill held while someone was ashore) is the part inspectors ask about, so it gets the heaviest tests and the mutation check.
- **Two sides writing.** Settled by the split above, and by mapping child tables to their document in change tracking.
