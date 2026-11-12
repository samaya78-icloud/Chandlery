# 14. Safety: permit to work

Status: **draft for review** (2026-11-12). Fourth module of the Safety family, and the most exacting. Basis: ISM Code 7 (shipboard operations) and the company's SMS, IMO Assembly resolution A.1050(27) (revised recommendations for entering enclosed spaces aboard ships), SOLAS XI-1/7, the ICS *Tanker Safety Guide* and the MCA *Code of Safe Working Practices for Merchant Seafarers* (permit to work chapter, with the model permit forms). All gas limits, validity times and sign-off levels are **configuration with defaults marked (to verify)** against the company's SMS; this system records and enforces what the company decides, and does not decide what is safe.

## 1. Goals
- A dangerous job does not start without a permit that was checked, issued and signed by the right people, and the permit cannot quietly stay open after the job is finished.
- Everyone onboard can see which permits are active right now, where, and for how long.
- The checklist, gas readings, the log of people entering and leaving a space, suspension, extension and close-out are recorded as they happen, offline, and form a complete record for audit.
- The permit forms are the company's own checklists, maintained by the office, not fixed in code.
- The office sees permit activity across the fleet and can audit a vessel's permits.

## 2. Concepts
- **Permit type** (office master data, versioned): name and code; the **checklist** (sections of items, each yes, no or not applicable, with mandatory items and items that require a note); **maximum validity** (hours); **who may request, issue, authorise and close** (by role: crew member, officer, chief officer or chief engineer, Master); whether a **gas test** is needed and its limits; whether an **attendant** (stand-by person) is required; whether a **risk assessment** must be linked; whether the **Master's approval** is required in addition to the issuer; the prefix on its reference number; the form used when it is printed. Default types **(to verify)**: Hot work, Enclosed space entry, Working aloft, Working overside, Electrical isolation and live electrical work, Pressure testing and opening of pressurised systems, Cold work in a hazardous area. Types can be added.
- **Permit:** the document: reference number, type and the checklist **version** it used, vessel, **location** (free text, plus a pick from the vessel's tanks for an enclosed space and from its machinery for a job), a description of the work, the **person in charge** (requester), the persons doing it, **start and end of validity**, the linked risk assessment, the linked PMS job or Worklist task, the attendant, the equipment and isolation points, and the conditions written on it.
- **Checklist answers:** each item answered at the time, by whom, with a note; the checklist is part of the permit and is not changed afterwards.
- **Gas reading:** time, the instrument and its calibration date, location of the reading, oxygen, flammable gas (percent of the lower explosive limit), toxic gases (for example hydrogen sulphide, carbon monoxide, in parts per million) and the person who took it; compared with the limits of the type **(to verify: defaults would be oxygen 20.9 percent for entry, flammable gas not above 1 percent of the lower explosive limit, toxic gas below the company's limit)**. A reading outside the limits is recorded and stops the permit from being issued or continued.
- **Entry log (enclosed space):** the name, time in, time out of each person and who was the attendant, so the space can never be left with someone inside unnoticed.
- **Events (appended, never edited):** Requested, Checklist completed, Gas tested, Issued, Master approved, Started, Person entered, Person left, Suspended, Resumed, Extended (a new validity period, needing new readings), Completed (work done, area made safe, nobody in the space), Cancelled, Expired, Withdrawn.

## 3. Lifecycle (state worked out from the events)
Requested → Checklist done → Issued (and Master approved where the type needs it) → Active → (Suspended ⇄ Active) → Completed, or Cancelled, or **Expired**.
- A permit is **valid only inside its window** and only while the last gas reading, where required, is within the limits and within its repeat interval. Outside that it shows as "not valid" (red) on every screen.
- When the window ends and the permit is not completed it becomes **Expired, not closed**: it stays on the board and raises a Worklist task for the issuer until someone closes it with the status of the work (finished, left unsafe, handed to the next shift). A permit left expired is the case an auditor looks for.
- **Extension** creates a new validity period on the same permit only with a fresh gas test and the issuer's signature; the permit shows its periods.
- **Handover** between shifts is a recorded event (outgoing and incoming person in charge, with the state of the work).
- **Completed** needs two signatures: the person in charge ("work finished, area safe, everyone out, equipment restored") and the issuer ("inspected and closed"). It is then locked.
- **Simultaneous operations:** the board shows overlapping active permits by location; a conflicting pair (for example hot work and a fuel transfer, or hot work next to an open enclosed space) displays a warning that must be acknowledged by the issuer, using configurable conflict pairs per company.
- A permit is never deleted; a permit raised in error is **withdrawn** with a reason.

## 4. Rules (pure functions, tested like the rest-hours engine)
- **Validity:** from the events and the clock, is the permit valid at this moment, and until when.
- **Gas check:** each reading against the limits of the type; the next reading due (for example every 30 minutes for an entry, or on every change of conditions) and whether it is overdue.
- **Issue check:** a permit can be issued only when every mandatory checklist item is answered, the required roles are signed, any required risk assessment is linked and signed, any required gas test is within limits and recent, and the issuer is authorised for the type. The check lists exactly what is missing.
- **Separation of roles:** the issuer is not the person in charge; for an enclosed space the attendant is not one of the persons inside; the Master approves where the type needs it. **(to verify against the SMS: whether one person may be both requester and issuer on a small vessel)**
- **Entry log:** persons currently inside is the entries minus the exits; closing is refused while anyone is inside.
- **Authority:** who may issue a type comes from the crew authority record (rank and an explicit "may issue" authority); an unauthorised person cannot issue, only request.

## 5. Roles
- **Requester (person in charge):** any crew member for non-critical types; an officer for the rest (configured per type). Fills the checklist, receives the permit, closes it.
- **Issuer:** an authorised officer (chief officer for deck work, chief engineer for the engine room; configured). Checks, takes or reviews the gas reading, signs.
- **Master:** approves the types that need it; sees all permits; may suspend or cancel any permit.
- **Attendant:** named on the permit; records entries and exits.
- **Office:** sees permit activity, the audit lists and the master data; edits permit types and checklists; does not issue or sign onboard permits.

## 6. Ownership and sync
| Item | Writer |
|---|---|
| Permit types, checklist templates (versions), limits, conflict pairs, authorities by type | office (master data, copied to vessels) |
| Permit, checklist answers, gas readings, entry log, events | the vessel (append-only; offline-first) |
| Audit comment on a permit | the office (separate appended entry) |
Lane: Transaction; the vessel's active permits are also summarised for the office as ordinary records, so no special lane is needed. A vessel holds only its own permits. Reference numbers per spec 01: `PTW-{ORIGIN}-{TYPE}-{SEQ}`. A new checklist version never changes a permit already issued on an older one.

## 7. Links to other modules
- **Risk assessment (06):** a type can require a linked signed assessment (hot work and enclosed space usually do); the permit page opens it; the assessment's category "hot work" is the natural match.
- **PMS (07):** a job can carry "needs permit type X"; starting that job offers a permit prefilled from the job and the machinery, and the job's completion shows the permit.
- **Tank inspection (08):** an inspection of a tank that is an enclosed space needs an active enclosed-space permit for that tank; the tank inspection page shows its status and a missing permit is a warning on starting, not a silent gap. Tank and permit share the tank record.
- **Worklist (05):** an expired permit, a failed gas reading and a permit awaiting the Master each raise a task (link type `permit`).
- **Incidents (12):** a gas reading outside limits during entry, an unplanned entry, or a closed-out unsafe state starts a report prefilled with the permit.
- **Drills (13):** enclosed space entry and rescue drills use the same equipment list; a drill checks the rescue plan the permit refers to.
- **Rest hours (11):** a permit's duration is not hours of work by itself; the job's persons are shown so a long job on tired crew is visible to the issuer (read-only, Master and issuers only).
- **Projects (09):** shipyard work packages can attach permits to a job.

## 8. Screens
Vessel: *Permit board* (active, valid-until, expired not closed, overlapping), *New permit* (choose type, location, work, persons, assessment), the permit page as a **guided sequence** (checklist → gas test → issue → start → entry log and readings → suspend or extend → close), *Entry log* (tablet-sized buttons for in and out, big and few), *Permit history*, a printable permit with signatures, posted at the work site.
Office: *Fleet permit activity* (active and overdue by vessel, count by type and month, expired not closed, gas exceedances), vessel detail, *Permit types and checklists* (editor with versions and a preview), *Conflict pairs and authorities*, the audit view (permits in a period with their events), PDF and CSV exports.

## 9. Out of scope for now
Electronic gas detector integration (readings are typed), electronic lock-out tags and key safes, tank-cleaning and cargo operations plans, the vessel's full tanker permit set under OCIMF (the types can be extended to them), hot work in a shipyard under the yard's own permit system (linked by reference only), a camera or location proof at the work site, scoring of the quality of checklist answers.

## 10. Open questions (owner decision)
1. **Which permit types and checklists** does the company use? Can you share the company's forms so the templates start from the real ones, not from a generic set?
2. **Who may issue** each type, and may one person be requester and issuer on a small vessel? (This decides the crew authority model in plan 11b.)
3. **Gas limits and repeat intervals** the SMS sets for entry and hot work, and the instruments used.
4. **Enclosed space entry:** is an entry log with an attendant required for every entry, or only where the type says so?
5. **Master's approval:** for which types is it needed in addition to the issuer's signature?
6. **Simultaneous operations:** which pairs conflict on your vessels? (The defaults are a starting list for you to correct.)
7. **Retention:** how long are permits kept, onboard and ashore, by the company and flag state **(to verify)**?
8. **Tank link:** should starting a tank inspection without a valid enclosed-space permit be a warning only (recommended) or a block?

## 11. Acceptance criteria
- A permit cannot be issued while a mandatory item is blank, a required signature is missing, a required gas reading is out of limits or old, or the issuer is not authorised; the screen lists what is missing.
- Gas readings outside limits stop the permit, are kept in the record and raise a task; the next reading is shown as due.
- The entry log shows who is inside right now, and a permit with anyone inside cannot be closed.
- A permit past its window and not closed shows as expired on both the vessel and the office and raises a task; extending it requires a new reading and a signature.
- A closed permit cannot be changed; a corrected checklist version does not touch permits issued before it.
- A permit written offline reaches the office with every event, and both sides end with the same status; a vessel never holds another vessel's permits.
