# 13. Safety: drills, training and safety meetings

Status: **draft for review** (2026-11-12). Third module of the Safety family. Basis: SOLAS chapter III regulation 19 (emergency training and drills), SOLAS XI-1/7 (enclosed space entry and rescue drills), ISPS Code part A section 13 (security drills), MARPOL and SOPEP/SMPEP requirements for pollution drills, ISM Code sections 8.3 and 6.5, and the safety committee practice of the company's safety management system. Intervals are **data, not code**: the numbers below are common defaults and each is marked **(to verify)** against the flag state, class and the company's SMS before they are relied on.

## 1. Goals
- The vessel knows what drills are due and when, without a spreadsheet, and the office sees which vessels are behind.
- A drill is recorded in minutes onboard: what, when, who took part, what was found, who signed.
- Every crew member's own participation is tracked, because the rules are about each person (every person in a fire and an abandon-ship drill every month), not only about the ship.
- Findings from drills and meetings become Worklist tasks and, where equipment failed, PMS jobs, so they are not left in the minutes.
- Safety committee and toolbox meetings are minuted the same way, with actions that are followed up at the next meeting.

## 2. Concepts
- **Drill type** (office master data, configurable per company; vessel types and groups may override): name, category (emergency, security, pollution, medical, training), **interval** (days or "every calendar month"), a **warning period** before it is due, whether it must involve **every crew member** or only named roles, standard scenario text, a checklist of what to test (alarms, muster, lifeboat engine start, fire pump, breathing apparatus, communications...), and the references it satisfies. Defaults **(to verify)**: fire (monthly), abandon ship (monthly), man overboard, enclosed space entry and rescue (every two months), steering gear emergency, blackout, oil spill (SOPEP), security drill (every three months), medical emergency, collision or grounding, cargo or fuel spill, helicopter if relevant.
- **Drill:** one record: vessel, type, date and time it started and ended, ship status (at sea, port, anchor, shipyard), place, scenario, who conducted it (Master or officer), participants, equipment tested, **findings** (what went wrong or could be better), **deficiencies** (equipment or procedure that failed), remarks, signed by the conductor and the Master. Participants carry present, absent with reason (on watch, ashore, on leave, off sick, joined after), or observer.
- **Participation:** worked out, never typed: for each crew member and each drill type, the last drill they took part in.
- **Crew change rule (SOLAS III/19.3.2 **(to verify)**):** when more than a quarter of the crew has not taken part in the drills of the ship in the previous month, the drills are held within 24 hours of leaving port. Worked out from the crew list and participation and shown as a warning when the ship sails.
- **Meeting type** (office-configurable): Safety committee, Toolbox talk, Pre-arrival or pre-departure briefing, Management review onboard, Other; each with a **standard agenda** (previous actions, incidents and near-misses since last time, drills held, unsafe conditions, suggestions, health and welfare, any other business) and an interval (for example monthly) **(to verify: the safety committee interval is set by the company and flag state)**.
- **Meeting:** date and time, place, chair, secretary, attendees (present or absent with reason), agenda items each with a note and an **action** (owner, due date), suggestions raised by crew, signed by the chair and the Master. The record keeps the previous meeting's open actions in front of the room.
- **Training record (light):** a seafarer's familiarisation, onboard training and short courses given onboard: date, topic, trainer, duration, attendees. Certificates and course validity (STCW, medical) are **not** here (see section 9).

## 3. Rules (pure functions, tested)
- **Due date** for each drill type on each vessel: the last drill of that type plus the interval, or the end of the calendar month for "monthly". Status: Not due, Due soon (inside the warning period), Due, Overdue, Never held. A vessel's status for a drill type is the worst of its due dates, and a **fleet status** is the same rolled up.
- **Individual due date:** for types that need every crew member, each crew member's own next date from their last participation; a person who joined the ship carries a due date from the day they joined (so a new joiner is not "never held" on day one, and is flagged after the grace period, default 14 days). Excused absence (leave, off sick, on another vessel) does not reset the clock but is shown.
- **Quarter-of-crew rule:** a pure check over the crew list and participation.
- **A drill held on the same day counts once;** two records of one type within an hour are shown as a possible duplicate.
- **Overdue meetings** by type interval.
- **Open actions:** an action that is past its due date at the next meeting is carried forward and marked "carried over from" with the meeting reference.
- **Statistics:** drills held against drills due by month and vessel; the percentage of crew who met the monthly rule; deficiencies found and closed; time taken (a long drill duration is shown because it also eats the crew's rest).

## 4. Roles
- **Master:** schedules, records or approves every drill and meeting, signs.
- **Officers (chief officer, chief engineer, safety officer onboard):** may record a drill or meeting they conduct; the Master signs after. Needs the crew authority record (rest-hours follow-up, plan 11b) so a company can say who may conduct and who may sign.
- **Any crew member:** sees the schedule, their own participation and the minutes; may add a suggestion before a meeting.
- **Office (safety officer, superintendent):** sees every vessel's schedule and records, edits drill types and meeting types, comments on a record and may raise a task or a report.

## 5. Ownership and sync
| Item | Writer |
|---|---|
| Drill types, meeting types, agendas, intervals | office (master data, copied to vessels) |
| Per-vessel overrides (a type not applicable, a different interval with a reason) | office |
| Drill, participants, findings, signatures | the vessel (append-only; a correction is a new entry with a reason) |
| Meeting, minutes, actions, signatures | the vessel |
| Office comment on a record | the office (separate appended entry) |
Lane: Transaction. A vessel holds its own records and the crew it carries. Reference numbers per spec 01: `DR-{ORIGIN}-{SEQ}` and `MT-{ORIGIN}-{SEQ}`.

## 6. Links to other modules
- **Worklist:** each drill deficiency and each meeting action becomes a task (link types `drill` and `meeting`); open tasks appear on the next drill or meeting record.
- **PMS (07):** equipment tested is picked from the vessel's machinery; a failed item starts a PMS job with the drill attached; a successful test can optionally close a "test emergency equipment" PMS job.
- **Incidents (12):** a drill finding can start a report; a report can ask for a drill; the incident statistics show drills held in the same month.
- **Rest hours (11):** a drill held while crew are resting disturbs their rest. When a drill is saved the screen offers to add its duration as work for participants whose hours show rest at that time, using the same recording rules as the hours screen (a correction with a reason, which the Master's lock respects). Never done silently.
- **Crew:** participants, joining dates and the quarter-of-crew rule come from the crew list.
- **Risk assessment (06):** a drill scenario may name the assessment that covers the same emergency; a failed drill can ask for a review.
- **Permit to work (14):** enclosed space rescue drills and the permit checklist share the same equipment list.
- **Calendar and dashboards:** due and overdue counts through the dashboard module list; a month calendar of drills and meetings.

## 7. Screens
Vessel: *Drill schedule* (each type with last held, next due and status, people overdue underneath), *Record a drill* (type, scenario prefilled, tick participants by name with "everyone present", findings, equipment tested), *Drill history*, *Meetings* (new meeting from the standard agenda with last meeting's open actions in front, minutes, actions), *My training and drills* (a seafarer's own record).
Office: *Fleet drill status* (vessel by type, colour by status, overdue first), vessel detail, *Crew participation* (who has not taken part, by vessel), *Meetings overview*, *Drill and meeting types* (with per-vessel overrides), printable and PDF drill report and meeting minutes with signatures, CSV exports.

## 8. Reports
Drill log for inspection (all drills in a period, with participants), monthly participation sheet per crew member, overdue by vessel, meeting minutes, actions carried over, findings by type.

## 9. Out of scope for now
Crew certificates, endorsements and medical validity (a separate Crew Compliance module), course booking and training budgets, e-learning and tests, attendance by card or biometrics, the muster list and station bill itself (a separate small document that this module can read once it exists), a simulator or video record, rescue boat launching logs beyond a drill record.

## 10. Open questions (owner decision)
1. **Which drill types and intervals** do your vessels follow? The defaults above are the common ones; the company's SMS and each flag state decide.
2. **Per person or per ship:** is the per-person participation rule needed from the first version? Recommended: yes, because it is what an inspector asks for.
3. **Who conducts:** may any officer record a drill, or only the Master and the safety officer? (This depends on the crew authority decision in 11b.)
4. **Safety committee:** monthly by default, or by the flag state? Are minutes sent to the office every time?
5. **Rest hours link:** is the offer to add a drill's duration to hours of work wanted, or should the drill only be flagged to the Master?
6. **Existing records:** is there a drill history to import so the schedule starts correct instead of "never held"?

## 11. Acceptance criteria
- A drill recorded offline appears ashore with its participants, findings and signatures and the schedule on both sides shows the same status.
- A new joiner appears in the schedule with a grace period and is flagged only after it; an excused absence does not reset a person's due date.
- The quarter-of-crew warning follows a prepared crew change.
- A finding becomes a Worklist task; a failed equipment test can start a PMS job; closing them shows on the drill record.
- A meeting shows the previous meeting's open actions and carries unfinished ones forward with their origin.
- A vessel never holds another vessel's drills or minutes, even after a full baseline.
