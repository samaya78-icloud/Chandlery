# 12. Safety: incidents, near-misses and observations

Status: **draft for review** (2026-11-12). Second module of the Safety family. Basis: ISM Code section 9 (reports and analysis of non-conformities, accidents and hazardous occurrences) and section 10 (corrective action). Anything that depends on a flag state, a class society or the company's own safety management system is marked **(to verify)** or left to configuration.

## 1. Goals
- Anyone onboard can report an incident, a near-miss, an unsafe act or condition, or an observation in minutes, offline, from a tablet.
- Every report reaches the Master, then the office, and is investigated to a root cause in proportion to how serious it was or could have been.
- Every corrective or preventive action is a Worklist task with an owner and a due date, and the report is not closed until they are done.
- The office sees the fleet: what is open, what is overdue, what repeats, and whether near-miss reporting is healthy (a vessel that reports nothing is a warning, not a good sign).
- A serious report reaches the office at once (urgent lane) and personal data about injured people stays with those who need it.

## 2. Concepts
- **Report type** (configurable list, defaults): Near miss, Unsafe act, Unsafe condition, Observation (good practice or suggestion), Injury or illness, Hazardous occurrence, Pollution, Damage to equipment or property, Security, Non-conformity.
- **Report:** one document: reference number, vessel, type, date and time it happened, place onboard, ship status (at sea, port, anchor, shipyard), weather and sea where relevant, a description, immediate action taken, persons involved, equipment involved, the person who reported it, and two scores.
- **Actual and potential severity:** both on the company's risk matrix scale (spec 06, consequence 1 to 5). A near-miss has no actual harm and a high potential; that pairing is what makes the data useful. The band (Low to Extreme) comes from the same matrix, so incidents and risk assessments speak one language.
- **Persons involved:** a crew member (link, with name and rank kept beside the link as in rest hours) or someone else (name, company, role); the part they played (injured, witness, involved); for the injured: injury type, body part, treatment (none, first aid, medical treatment, hospital, evacuation), days lost, restricted work. This is personal data and sensitive (section 5).
- **Review (Master):** the Master's comment, a check that the facts are complete, a confirmation of the severity, and the decision "send to office".
- **Investigation:** facts and timeline, immediate cause, **root causes** (a company list of categories, for example procedure, training, fatigue, equipment, communication, supervision, management, design; a short why-chain is free text), contributing factors, whether the risk assessment for this job was adequate, lessons learned, who investigated (ship, office, or both) and the date finished.
- **Action:** corrective (fix this) or preventive (stop it happening elsewhere); description, owner, due date. Each is a Worklist task linked to the report. A preventive action may be addressed to a vessel group or the whole fleet (it then becomes one task per vessel).
- **Notification record:** whether and when the flag state, class, P&I club, charterer or port state were told, and the reference they gave. Recorded only; nothing is sent from here **(to verify: which authorities, and their time limits, per flag state)**.
- **Attachments:** photographs, statements, sketches. Uses the attachment service when it exists; until then a list of file names and notes, so nothing here waits for it.

## 3. Lifecycle (state is worked out from appended events, as in rest hours and tank inspection)
| Status | Meaning |
|---|---|
| Draft | being written by the reporter; visible only to them and the Master |
| Reported | submitted; waiting for the Master |
| Reviewed | the Master has reviewed it and sent it to the office |
| Investigating | an investigation is open (the office, the ship, or both) |
| Actions open | the investigation is done and actions remain |
| Closed | investigation finished, every action done, closed by the office with a closing note |
- A report can be **reopened** with a reason (a new event; the history stays).
- A near-miss or an observation with no investigation needed can be closed directly by the office after the Master's review, with a note saying why no investigation was needed.
- A report is never deleted. A report entered by mistake is **withdrawn** with a reason; it stays visible in the history and out of the statistics.
- After "Reviewed" the reporter's text is locked; additions are dated **supplements** (appended, signed with the writer's name).

## 4. Rules (pure functions, tested like the rest-hours engine)
- **Severity and band:** from the company matrix; the higher of actual and potential decides how the report is treated.
- **Urgent lane:** an Injury with days lost or evacuation, a Pollution report, or any report whose band is High or Extreme goes to the office on the urgent lane when it is submitted, even if the Master has not yet reviewed it. Thresholds are in the company's settings.
- **Investigation due date** by band (defaults: Extreme 7 days, High 14, Medium High 30, Medium 60, Low none; configurable). A report past its date is **overdue** and shown on the dashboards and the Worklist.
- **Late reporting:** if a report is entered more than N hours after the occurrence (default 24) it is flagged "reported late"; the flag is a fact, not a penalty.
- **Closing check:** a report cannot be closed while an action task is open, an investigation required by its band is missing, or a Master review is missing. The check lists what is missing.
- **Repeat detection:** reports with the same type, place and equipment or root cause within a window (default 12 months) are shown together on the investigation page ("this has happened before"), and a third repeat raises a suggestion to review the risk assessment (section 6).
- **Statistics** (pure, from the reports and the exposure hours): counts by type, severity and month; **lost-time injury frequency** (lost-time injuries x 1,000,000 / hours worked) and total recordable case frequency; near-misses per injury; mean days from report to close; open and overdue counts. Hours worked come from Rest Hours when it is on for the vessel (24 hours minus the recorded rest, summed over the crew) and otherwise from a number the vessel enters per month; the page says which one it used. Formulas and the definitions of "lost time" and "recordable" are stated on the page and configurable **(to verify against the company's reporting standard)**.

## 5. Roles and privacy
- **Reporter:** any crew member (or office user) writes a report and sees their own.
- **Master:** sees every report of the vessel, reviews, sends, may add supplements.
- **Safety officer or DPA (office role):** sees every report of the fleet, investigates, sets root causes, closes. A company can name more than one.
- **Everyone else:** sees a report only when it is shared (a toggle on the report, "share the lesson"), and then without the persons involved: names and injury details are left out, which is the form used for lessons learned and the fleet alert.
- A vessel holds only its own reports; an investigation made ashore reaches the vessel as a read-only summary plus its actions.
- Injury and medical detail are visible to the Master and the safety roles only. A report keeps the person's name as text, so deleting a crew record never damages the history.

## 6. Ownership and sync
| Item | Writer |
|---|---|
| Report type list, root-cause list, severity thresholds, settings | office (master data, copied to vessels) |
| Report, supplements, Master review | the vessel (the office may enter a report for a vessel, flagged) |
| Investigation, notification record, closing | the office (the vessel may add its own findings as a separate entry) |
| Action tasks | the Worklist rules |
Lanes: Transaction normally; **Urgent** under the rules in section 4. A report is a document (report, persons, supplements, events). Events are appended, never edited. Reference numbers follow spec 01: `INC-{ORIGIN}-{SEQ}`, so a report named on the phone onboard is found ashore.

## 7. Links to other modules
- **Worklist:** every action is a task (link type `incident`); the report page shows the tasks and their progress; overdue investigations raise a task for the safety officer.
- **Risk assessment (06):** a report can name the assessment that covered the job (or say there was none); a repeated pattern, or an investigation that finds the assessment inadequate, offers to start a new version of it with the incident attached as the reason.
- **PMS (07):** equipment involved is picked from the vessel's machinery; "equipment damage" and a corrective action can start a PMS job with the report attached.
- **Rest hours (11):** an investigation page shows the hours worked and rested by the persons involved in the 72 hours before the occurrence, so fatigue is considered every time. Visible only to the Master and safety roles.
- **Drills and safety meetings (13):** a drill finding can become a report; a report can ask for a drill; actions from safety meetings and reports meet in the same Worklist.
- **Permit to work (14):** a permit breach or a near-miss during a permit starts a report prefilled with the permit.
- **Tank inspection (08):** a bad rating that is also an incident is linked, not duplicated.
- **Projects (09):** a corrective action may be a project job; cost is recorded there, not here.

## 8. Screens
Vessel: *Report an incident* (a short form first: what, when, where, who, what was done; severity added after), *My reports*, *Reports of the vessel* (Master), *Review* (the Master), the report page with its timeline and actions, *Safety statistics for the vessel* (month and year).
Office: *Fleet reports* (filter by vessel, group, type, band, status, overdue), the report page with the investigation panel and repeats, *Investigation queue*, *Fleet statistics and trend* (frequency rates, near-miss ratio, open and overdue by vessel, vessels with no reports in N months), *Lessons learned* (shared reports without persons), *Settings* (lists, thresholds, roles), a printable and PDF report (with signatures and the investigation) and CSV exports. Tiles on both home pages through the dashboard module list.

## 9. Out of scope for now
Statutory forms of any particular flag state or port state (the data is kept so a form can be produced later), automatic messages to authorities or insurers, the vessel's medical log and any medical records beyond the injury summary, claims handling, a camera or body-worn capture, root-cause analysis methods beyond the list and the why-chain (for example fault trees), analysis of free text by machine.

## 10. Open questions (owner decision)
1. **Types and severity:** are the default types and the use of the risk matrix (actual and potential) right, or does the company already have its own classes (for example the company's severity levels A to D)?
2. **Who investigates what:** a single rule by band (the defaults above), or does the safety officer assign each time? Recommended: the rule by band proposes, the office confirms.
3. **Reporting culture:** may a report be anonymous to the office (the Master sees the name, the office sees "crew member")? Recommended: yes, as a per-company setting, because it raises the number of near-misses reported.
4. **Statutory notifications:** which authorities must be told for the flags you manage, and how soon? Needed to set the defaults of the notification record.
5. **Rates:** which definitions of lost-time injury, recordable case and exposure hours does the company report under (OCIMF, P&I, its own)?
6. **Existing data:** is there a history of incidents to import (a spreadsheet or the system used now), so trends start with a past?
7. **Chat on a report (spec 10):** is the discussion thread needed from the first version, or after spec 10 is built?

## 11. Acceptance criteria
- A report written offline onboard reaches the office with every person, supplement and event, and the office's investigation and actions reach the vessel; both sides end with the same status.
- A High or Extreme report, or an injury with days lost, uses the urgent lane; a Low near-miss uses the normal one.
- A report cannot be closed with an open action, a missing investigation (where the band needs one) or no Master review, and the page says which.
- A shared lesson never shows a name or injury detail to a user without the safety role.
- The rates match a hand calculation on a prepared month, and say whether the hours came from Rest Hours or from a typed number.
- A vessel never holds another vessel's reports, even after a full baseline.
