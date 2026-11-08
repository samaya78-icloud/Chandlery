# 11. Safety: seafarers' hours of work and rest

Status: **draft for review** (2026-11-08). First module of the Safety family. Sources: the ILO/IMO *Guidelines for the development of tables of seafarers' shipboard working arrangements and formats of records of seafarers' hours of work or hours of rest* (Convention No. 180, STCW Code A-VIII/1, with the model formats in appendices 3 and 4), and the NavInsider STCW/MLC rest-hours guide. Rule numbers below come from the ILO/IMO text; anything taken only from the guide is marked **(to verify)**.

## 1. Goals
- Every seafarer's hours of work or rest are recorded onboard, every day, in the model format (appendix 4), and checked against the limits automatically.
- A **table of shipboard working arrangements** (appendix 3) exists per vessel and per position, and can be printed and posted.
- Breaches are never hidden: they are flagged, explained, reviewed by the Master, and visible to the office.
- Records are signed by the seafarer and the Master (or someone the Master authorises), and the seafarer can get a copy.
- The office sees compliance across the fleet (breaches, unsigned months, trends) and can produce records for an inspection.

## 2. The rules the system must apply (all configurable data, not code constants)
Taken from Convention 180 art. 5 and STCW A-VIII/1. A **rule set** holds the numbers, so a flag state or collective agreement can differ.

| Rule | Default |
|---|---|
| Minimum rest in any 24-hour period | 10 h |
| Minimum rest in any 7-day period | 77 h |
| Rest may be split into at most | 2 periods |
| Longest rest period at least | 6 h |
| Interval between consecutive rest periods at most | 14 h |
| STCW reduction: rest in 24 h may fall to | not less than 6 consecutive hours, for no more than 2 days, if at least 70 h rest in each 7-day period. **Off by default**: it applies only where the flag state allows it, so a rule set switches it on. On a reduced day the 14 h limit between rests is waived. |
| Under 18: no night work | 9 consecutive hours including midnight to 05:00 |
| Alternative form of the limit | maximum work 14 h / 24 h and 72 h / 7 days (same thing seen from the other side) |

Exceptions the master may use (art. 7, STCW A-VIII/1.3): emergency, drill, or overriding operational condition, and work needed for the immediate safety of ship, persons or cargo. The record keeps these as an **exception flag with the Master's reason**; the day is still shown as a deviation, and **compensatory rest** is owed afterwards (art. 7.3). On-call call-outs that disturb rest need compensatory rest too (art. 5.4).

## 3. Concepts
- **Rule set:** the numbers in section 2, a name (for example "STCW / MLC default"), optional flag state. Office-owned master data, copied to vessels. A vessel (or a group) uses one rule set.
- **Working-arrangements table (appendix 3):** per vessel, one row per position/rank: scheduled watchkeeping periods and non-watchkeeping duties at sea and in port, comments, and the total daily work or rest worked out from them. Header: ship name, flag, IMO number, date of latest update, the national law or agreement that applies, the Master's signature. Rank names should match the safe-manning document. Office creates it; the Master signs; a new version is a new record (history is kept).
- **Day status of the vessel:** At sea, In port, or Anchored, with times. Recorded by the Master or an officer, once per vessel per day. It chooses which row of the table (sea or port) pre-fills a seafarer's day.
- **Hours entry (the unit of recording):** for one seafarer and one date, the list of **work periods** (start and end, to the quarter hour; everything else is rest). Written as an append-only **revision**: a correction is a new revision with a reason, never an edit, and the latest revision wins when the record is read. Who entered it and when is kept.
- **Month sheet (appendix 4):** seafarer, month and year, position, a watchkeeper yes/no flag, the daily grid, the totals per 24-hour period and per 7-day period, the applicable rules, the comments. It is a view over the entries plus the sign-offs.
- **Sign-off:** an append-only record that the seafarer, then the Master (or authorised officer), agree the record is accurate for a period (a week by default; the guidelines require at least weekly in the simplified procedure, para 22). A sign-off remembers a fingerprint of the entries it covered. **If an entry is revised afterwards the sign-off shows as out of date** and both must sign again.
- **Breach (non-conformity):** worked out, not stored: any date or window where a rule is not met. Each breach needs an explanation entry (reason, and whether it is an emergency/drill/overriding condition or compensatory rest given) and Master acknowledgement.
- **Compensatory rest owed:** hours worked out from breaches marked as exceptions, shown until the following rest covers them (the Master confirms).

## 4. How compliance is worked out (pure rules, tested)
- Checks use **rolling windows**, not just calendar days: the rule says "any 24-hour period" and "any 7-day period". With a 15-minute grid the worst window always starts on a grid boundary, so every boundary is tested.
- Rest periods are the gaps between work periods across midnight (a night watch is one period, not two days).
- The 6-hour and 14-hour tests look at the actual rest periods in the window; the 2-day reduction is tested by counting the days that used it in the 7-day period.
- The result per date is one of: OK, OK by reduction, Breach, Excepted (breach with a Master-confirmed emergency/drill), with the numbers that decided it, so the screen can say *why* ("rest in the 24 h ending 14:00 was 8.5 h; minimum 10").
- The same function runs on both sides, so a vessel and the office always show the same result.

## 5. Roles
- **Seafarer:** enters and signs their own hours (on a tablet or shared terminal onboard, signing in with the crew code).
- **Officer in charge / HoD:** may enter hours for crew on their behalf (flagged as entered by someone else, and the seafarer still has to agree when signing).
- **Master (and persons the Master authorises):** reviews breaches, endorses sheets, signs the working-arrangements table, declares exceptions.
- **Office:** edits rule sets and the working-arrangements tables, reads everything for the fleet, produces reports. The office does not edit a vessel's entries.

## 6. Ownership and sync
| Record | Writer | Direction | Kind |
|---|---|---|---|
| Rule set | office | to vessels | master data |
| Working-arrangements table (version, rows) | office; Master signs onboard | down; signature up | document, versions append-only |
| Vessel day status | vessel | up | append-only |
| Hours entry revision | vessel | up | append-only |
| Sign-off | vessel | up | append-only |
| Breach explanation, Master acknowledgement | vessel | up | append-only |

Lanes: Transaction for entries and sign-offs; **Urgent** for a breach explanation flagged as emergency; Master for rule sets and tables. A vessel only ever holds its own crew's hours. After a seafarer signs off the vessel keeps the sheets for the retention period and the office keeps its copy (see open decisions).

## 7. Links to other modules
- **Worklist:** a Master-acknowledged breach can raise a Worklist task (source "Rest hours non-conformity") so the office follows up, matching the six-step routine in the guide (mark, explain, tell the Master, notify the company, file, review the schedule).
- **Crew:** the seafarer, rank and Master flag come from `CrewMember`; date of birth drives the under-18 night rule.
- **PMS and Projects (later):** planned maintenance load against rest capacity, so the schedule can be checked before a heavy week. Not in the first version.
- **Risk assessment (spec 06, next Safety module):** a repeated breach pattern is a trigger for an assessment.

## 8. Screens
Vessel: *My hours* (day grid, tap or drag to mark work, prefilled from the table and the vessel's day status, live totals and warnings), *Month sheet*, *Sign records* (seafarer and Master), *Breaches* (explain and acknowledge), *Working arrangements* (view and Master signature), *Vessel day status*.
Office: *Fleet compliance* (vessel × month: breaches, unsigned, out-of-date sign-offs), *Vessel detail*, *Seafarer detail and trend*, *Rule sets*, *Working-arrangements editor*, *Reports* (CSV) and the **printable appendix 3 table and appendix 4 record** (HTML print and PDF, as the PO document does).

## 9. Out of scope for now
Biometric or automatic time capture, integration with a gangway or engine log, fatigue risk scoring, planning/optimisation of watch schedules, inspector endorsement workflow for the competent authority, translating the forms into the ship's working language (the English form first; labels are kept in one place so a second language can be added).

## 10. Open questions (owner decision)
1. **Rolling or calendar window?** Recommended: rolling (the rule says "any" period). Calendar-day checking is easier to read but can miss breaches that span midnight.
2. **Sign interval:** daily, weekly (the guideline's minimum for the simplified procedure) or monthly? Recommended: weekly by default, configurable per company. The NavInsider guide says daily signing is needed under MLC **(to verify against the flag state rules)**.
3. **Who may enter hours:** seafarers themselves only, or officers on their behalf too? Recommended: both, flagged.
4. **Retention:** how long vessel and office keep records after sign-off. The guide says 3 years onboard **(to verify)**; make it a per-company setting.
5. **Time resolution:** 15 minutes (recommended) or 30 minutes or 1 hour?
6. **Vessel day status:** entered by hand each day, or is there any existing port-call data to use? Recommended: by hand, with a default of "same as yesterday".
7. **Existing data:** do you want an import from the system vessels use now (for example ISF Watchkeeper) so history is not lost?
8. **Which other Safety modules** besides rest hours, and in what order (risk assessment is already specified as spec 06)?
