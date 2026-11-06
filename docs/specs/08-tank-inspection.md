# 08. Tank inspection

Structured inspections of a vessel's tanks (coating, structure, fittings), done onboard, reviewed ashore, with ratings tracked over time and exceptions followed up.

## 1. Goals
- A per-vessel **tank master** and a visual **tank plan**.
- Inspections recorded onboard as checklists with ratings and photos.
- Office review queue, exception summary and long-term trends.
- Inspections are triggered by and linked to PMS jobs; exceptions become Worklist tasks.

## 2. Concepts
- **Tank:** name, type (Cargo, Ballast, Slops, Fresh Water, Void space/Cofferdam, other), position on the tank plan, structural data.
- **Structural data per tank:** steel grade (deck, bottom, side, long bulkhead), original coating, coated area, cathodic protection, protected area.
- **Tank plan:** a schematic of the vessel (profile/plan view) with each tank as a shape, coloured by type and optionally by latest rating. Editable layout per vessel; templates per ship type; clonable to sister vessels.
- **Checklist template** by tank type and ship type, made of **criteria groups**:
  - *Variable data:* condition of coating, cathodic condition (0–25 / 26–50 / 51–75 / 76–100%).
  - *Internal fittings:* ladders and railings, cargo and ballast pipes, stripping lines, draft gauge, hydraulic clamps, heating coils, reach rods, tank-cleaning machines, suction valves, couplings.
  - *Structure:* buckling, side shell, bottom plate, bellmouth, deckhead, longitudinals, bulkheads, crossbeams, stringers, web frames, girders.
  - *Reports:* thickness loss, pitting, cracks.
  - *Type-specific:* e.g. insulation, cold spots, anti-float/pitching/rolling devices, dome space for gas carriers.
- **Rating scale:** Good, Fair, Bad, NI (not inspected), NA; Yes/No/NA checks; percent buckets for cathodic condition. Scale values are configurable lists.
- **Inspection (the document):** vessel, tank, date, place (at sea / port name), work done (e.g. internal, six-monthly), linked PMS job, general comments, criteria results, photos, inspector, status.
- **Exception:** any criterion rated Fair or Bad (thresholds configurable).

## 3. Lifecycle
Draft → Submitted → In review → Reviewed (accepted) or Returned for correction. Reviewed inspections are locked.

## 4. Ownership and sync
| Record | Writer |
|---|---|
| Tank master and plan, checklist templates | shore (vessel may propose changes) |
| Inspection draft and submission | vessel |
| Review outcome, reviewer comments | shore |
| Photos and comments | append-only |
Lane: `Transaction` for inspections, `Bulk` for photos, `Master` for templates.

## 5. Workflows
- **Planning:** a PMS inspection job (e.g. 6-monthly ballast tank inspection) opens the checklist for the right tank; completing the inspection completes the job.
- **Fieldwork on a tablet:** tick through criteria, rate, attach photos, offline.
- **Exceptions:** each Fair/Bad creates a draft Worklist task with the criterion, tank and photo; the reviewer confirms or dismisses.
- **Review (shore):** queue grouped by vessel; keywords (for example rust, anode, crack, pitting) highlighted in comments; accept, return with comments, or raise actions.
- **Alerts:** a Bad rating notifies the superintendent via the Urgent lane.

## 6. Screens
- **Tank master (shore/vessel view):** list with type and structural data, add/edit tank, place on plan, **copy tank list to vessels**.
- **Tank plan view:** coloured schematic; click a tank for history and start an inspection.
- **Listing:** inspections with tank, place, date, work done, linked job, exception marker.
- **Trends:** matrix of criteria × inspection dates for a tank (colour by rating), plus charts of coating condition over time.
- **Exception summary:** choose vessels, rating (Fair/Bad), tank type chips; drill to the inspection.
- **Review list:** pending/reviewed, grouped by vessel.

## 7. Data model (summary)
`Tank`, `TankPlanLayout`, `TankInspectionTemplate(+criteria)`, `TankInspection`, `TankInspectionResult`, `TankInspectionPhoto` (via Attachments), `TankInspectionExceptionLink`.

## 8. Acceptance criteria
- Completing a linked PMS tank-inspection job requires a submitted inspection for that tank.
- Trends show the same history on vessel and shore after sync.
- A Fair or Bad rating creates a linked Worklist draft and appears in the exception summary.
- A reviewed inspection cannot be edited; a correction is a new inspection.
- A tank list can be copied from one vessel to sister vessels in one action.

## 9. Open decisions
Which vessel types are supported first; the exact criteria sets per type; photo requirements for Bad ratings.
