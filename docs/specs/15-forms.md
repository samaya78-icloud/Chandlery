# 15. Forms: a generic form builder

Periodic and one-off forms (engine performance, tank and hold inspections, test records, checklists, reports) that the office designs once and a vessel fills in, with the whole thing held as JSON. The office builds a form in the app, publishes it to the vessels it applies to, the vessel fills it in on a tablet, and the office reviews it. Plan: [15a](15a-forms-plan.md).

## 1. Goals
- One **form builder**: the office designs a form from sections, typed fields and grids, with no HTML and no developer. Changing a form is a new **version**; entries already filled stay on the version they were filled on.
- A form is **JSON**: the definition is JSON, the values are JSON, and a form is checked against its definition by the same pure rules on both sides (vessel and office), so they cannot disagree.
- **Vessel entry** that works with no link to shore: a due list, a form drawn from its definition, drafts saved as the person goes, submit.
- **Office review**: a submitted list with filters, accept or return with comments, action items that become Worklist tasks, print and PDF, export, and a year view of which vessels have done which form.
- **Schedule**: a form has a periodicity (daily to yearly, voyage, PMS, third party) and an applicability (vessel types or named vessels); the system knows what is due and what is overdue.
- A tool that **converts the existing HTML forms** into draft definitions, so the roughly 130 forms in use are not rebuilt by hand.
- Reuse what exists: conversations (spec 10) for comments, the Worklist (spec 05) for actions, files (spec 10, `FileAttachment`) for photos, the module switch and vessel groups (spec 02), the sync engine (spec 03).

## 2. What the existing system does (the starting point)
Studied read-only in the live forms module, 2026-11-15; nothing was changed there.
- **Form types** (Forms Maintenance): about 275 rows, each a form name, department (Corporate, HSEV, Operations, Technical), periodicity (Daily, Weekly, Fortnight, Monthly, Quarterly, Half-Yearly, Yearly, Voyage, PMS, Periodical, Third Party), the **fleets** it applies to (vessel types such as Aframax, VLCC, LPG, Container, Bulk, PCTC and dual-fuel variants; empty means all), an **active** flag and a **version number**. The same form appears in several versions. The form's content is an uploaded file.
- **The template** is an HTML table layout (a paper form): each table is a section with an id, each input carries a path such as `Section [Label]`, the values are saved as XML against an XSD and bound back into the page by that path. A form such as the crankpin bearing report has about 220 text inputs because its grids are laid out cell by cell.
- **A submitted form**: vessel, form date, posted date, entry date and an id, and a toolbar (save, return to the vessel, delete, approve, print). Three tabs: the form, **comments** (text and attachments), **action items** (an item, who is responsible, create action item or worklist task).
- **The listing**: month, year, type, department, form, keywords and vessels as filters; a submitted list and a year view; columns vessel, form, id, form date, posted date, action items, reviewed by; export.
What is kept: the catalogue, versions, applicability, periodicity, the three tabs, the listing and the year view. What changes: the template and the values (JSON, not HTML plus XML plus XSD), grids declared rather than hand-drawn, typed fields with limits, rules and calculations, versions that cannot change under a filled entry, and the same engine on the vessel as ashore.

## 3. Concepts
- **Form type:** a code and title (for example `PM05 - Crankpin Bearings`), department, periodicity, applicability, active flag. Its versions are listed under it.
- **Form version (the definition):** a JSON document. Draft until published; published versions never change; a later edit is a new version; a version can be retired (no new entries, old ones still readable). One published version per form type is current for a given vessel.
- **Applicability:** vessel types and/or named vessels (empty means every vessel with the module on). The vessel type is read from the vessel.
- **Entry:** one filled form: form type and the version it was filled on, vessel, form date (the period or voyage it is about), the values (JSON), the status, who filled it. Entries are never edited after review.
- **Section:** a titled part of the form. A section is either a **field list** (labels and fields in order) or a **grid**.
- **Field types:** text, long text, number (unit, decimals, minimum, maximum), date, time, date and time, choice (one of a list), multi choice, yes/no/not applicable, rating (a list such as Good/Fair/Bad), tick, photo, signature (the person's name and time, confirmed), a lookup (a machinery item, a tank, a crew member, a port), and read-only **calculated** fields.
- **Grid:** columns (with column groups, such as bearing 1 to 7 each with fore and aft) and rows that are fixed labels, rows the person adds, or rows taken from a list (the cylinders of the engine, the tanks of the vessel). Cells are typed like fields. A grid is declared once; it is never drawn cell by cell.
- **Rules:** required, a default, **show if** (a field or section appears only when another answer says so), a **validation** (number range, length, a date not in the future), a **calculation** (a total or a difference from other fields) and a **flag** (an answer that is an exception, so the review and action items can point at it).
- **Prefill:** a field can take its first value from a source: the vessel (name, type, flag), the machinery list (engine type), the previous entry of the same form (the "last report" date and readings), or today.
- **Review:** the office's response: start, accept, or return with a comment. A returned entry goes back to the vessel for correction and is submitted again.
- **Action item:** a finding on an entry that becomes a Worklist task linked to it (link type `form`).

## 4. The format
A definition (shortened; the field and grid vocabulary is closed and checked by `FormDefinitionRules`):
```json
{
  "schema": 1,
  "title": "Crankpin Bearings",
  "sections": [
    { "id": "header", "layout": "fields", "fields": [
      { "id": "engineType", "label": "Engine type", "type": "text", "prefill": "vessel.mainEngine", "required": true },
      { "id": "place", "label": "Place", "type": "text" },
      { "id": "date", "label": "Date", "type": "date", "default": "today", "validate": { "notFuture": true } },
      { "id": "draftFwd", "label": "Draft fwd", "type": "number", "unit": "m", "decimals": 1 },
      { "id": "draftAft", "label": "Draft aft", "type": "number", "unit": "m", "decimals": 1 },
      { "id": "trim", "label": "Trim", "type": "number", "unit": "m", "calc": "draftAft - draftFwd" }
    ]},
    { "id": "measured", "title": "Actual measured values (mm)", "layout": "grid",
      "columns": [ { "id": "b", "label": "Bearing", "repeat": 7, "children": [
        { "id": "f", "label": "F", "type": "number", "decimals": 2 },
        { "id": "a", "label": "A", "type": "number", "decimals": 2 } ] } ],
      "rows": { "mode": "fixed", "items": [ { "id": "orig", "label": "Original clearances" }, { "id": "meas", "label": "Measured" } ] } },
    { "id": "remarks", "title": "Remarks", "layout": "fields", "showIf": "measured.any",
      "fields": [ { "id": "chiefEngineer", "label": "Chief Engineer", "type": "longtext" } ] }
  ]
}
```
A set of values (what a vessel sends):
```json
{ "form": "PM05", "version": 3, "values": {
    "engineType": "7G60M-CGIE-HPSCR", "place": "USA DRIFTING", "date": "2026-11-04", "draftFwd": 6.5, "draftAft": 9,
    "measured": { "orig": { "b1.f": 0.55, "b1.a": 0.55 }, "meas": { "b1.f": 0.55 } } } }
```
- Field and cell ids are stable keys the author chooses; renaming a label never changes an id, so values keep their meaning across versions.
- Numbers are kept as text in a document that travels (so no store or language rounds them), as the other modules do; dates are ISO.
- **Rules are data, not code.** Calculations and show-if use a small closed expression language (field names, numbers, + - * /, comparison, `and`/`or`/`not`, `sum()`, `any()`, `count()`); it is parsed by our own evaluator and never run as code, so a form cannot do anything but compute.
- `FormValueRules` checks values against the version they name: required, types, limits, show-if (a hidden field is neither required nor kept), calculations recomputed, exceptions found. The same function runs in the editor, on submit, on arrival at the other side and in the tests.
- A JSON schema can be generated from a definition for outside tools; the definition, not the schema, is what we store.

## 5. Lifecycle
Draft, Submitted, In review, Reviewed (accepted) or Returned for correction; Reviewed is locked, a correction is a new entry for the same period that names the one it corrects. Definitions: Draft, Published, Retired. The office can also fill a form for a vessel (flagged as ashore) and a form can be owned by the office only (a third-party or shore form).

## 6. Ownership and sync
| Record | Writer | Direction | Kind |
|---|---|---|---|
| Form type, version (definition) | office | to the vessels it applies to | master data (Master lane) |
| Entry draft and submission | the side that fills it (the vessel normally) | both ways for the vessel | document |
| Review (start, accept, return) | office | down | append-only event |
| Comments | author's side | both ways | spec 10 conversation on the entry |
| Photos | uploader's side | both ways | `FileAttachment` (Bulk lane) |
| Action items | either side | both ways | Worklist tasks linked to the entry |
A vessel holds only the definitions that apply to it and its own entries. A published version is immutable, so it can never conflict; entries have one writer at a time (the filling side, then the office for review).

## 7. Screens
- **Form types (office):** list with the existing filters (form, department, periodicity, fleet, active), versions under each, create, copy a version, retire.
- **Builder (office):** a sections panel, a field panel (add, order, type, label, id, required, default, show-if, validate, calculate, prefill), a grid designer (columns, groups, repeat, row mode), a **JSON tab** for the same definition with live validation errors, a **preview** that renders exactly what the vessel will see, and publish (which checks the definition and bumps the version).
- **Vessel:** due forms (what is due, overdue, due soon), start an entry, the fill screen (tablet-friendly: big targets, one section at a time on a small screen, the keyboard that matches the field type, saved as typed), submit, my entries and their status.
- **Office:** the submitted list (month, year, form, type, department, keywords, vessel; columns as in the existing list), the entry with its three tabs (form, comments, action items), review, return, print and PDF, CSV/Excel export of the entries of a form (one row per entry, one column per field), the **year view** (vessels by month or by form: due, done, late), a dashboard card and menu counts.
- **Converter (office, once):** upload a legacy HTML form, see the draft definition beside the original, fix, save as a draft version.

## 8. Data model (summary)
`FormType`, `FormVersion` (definition JSON, status, number), `FormEntry` (values JSON, version, vessel, period, status, owner stage, corrects), `FormReview` (append-only), plus Worklist link type `form`, conversation module `FormEntry`, `FileAttachment` owner `FormEntry`. Module switch "Forms". Reference `FRM`.

## 9. Moving from the HTML forms
The converter reads the old markup: each `table` with an id becomes a section; each input with a `data-ititle` path becomes a field whose id comes from the path and whose label comes from the nearest header cell; classes decide the type (`cDatePicker` is a date, the numeric box class is a number, a `select` is a choice, a `textarea` is long text); runs of cells with a repeating header become a grid with a `repeat`. It reports what it could not convert. A person checks each draft. Existing XML values can be imported as entries through the same field ids where the paths match. Forms are converted a few at a time; both systems can run side by side until the last one moves.

## 10. Acceptance criteria
- A definition that breaks a rule (duplicate id, unknown type, a calculation naming a missing field, a show-if loop) cannot be published, and the editor says which and where.
- A published version is never changed; an entry stays valid against the version it was filled on after newer versions exist.
- The same values pass or fail identically on the vessel, in the office and in the pure tests; a hidden field is neither required nor stored.
- A vessel with no link can open a form, fill it, save drafts and submit; the entry reaches the office intact once the link is back, and the office's review reaches the vessel.
- A vessel holds only the definitions that apply to it, even after a full baseline.
- An exception flagged in a form can become a linked Worklist task; the entry shows it.
- The year view and the due list agree: what the office sees as late is what the vessel sees as overdue.
- Converting the three sample legacy forms (a grid-heavy engine report, a checklist, a change-of-command form) gives definitions that render and accept the old values.

## 11. Out of scope for the first version
A pixel copy of the paper layout (a form is laid out by the app, not by merged cells); electronic signatures with identity proof (a signature field records name and time); routing a form through several approvers (a form can be flagged for a second sign-off later); charts of numeric fields over time (the export serves until then); filling a form from a phone.

## 12. Open points
1. **Offline.** The vessel's own installation draws the form from the definition it holds, so it needs no link; a person's device only needs the vessel's network. Confirm that is the offline you mean.
2. **Converting the existing forms.** By the tool with a person checking each one (recommended), or rebuilt one by one.
3. **Approval.** One review by the office (recommended); a second sign-off for chosen forms later.
4. **Scheduling.** Whether a periodic form is due by calendar (every month, from a start date) or by the previous entry (a month after the last), as PMS offers both; the recommendation is to follow PMS: a per-form choice, calendar by default.
5. **Who owns a form's numbers.** Whether numeric answers should feed other modules (running hours into PMS, tank readings into the tank module): left for a later stage, with `FormValueRules` already knowing which fields are numbers.
