# 15a. Forms: plan (implements spec 15 in stages)

Status: **F1 committed (61d6986); F2 built 2026-11-15 (not yet committed); F3 to F7 not started.** Order and the answers in section 1 were accepted ("use your recommendations"). Builds on patterns already proven: master data copied to every vessel (tank checklists, risk templates, incident causes), a document owned by one side with review as separate append-only events (tank inspections, risk assessments, incidents), state and checks worked out by pure functions shared by the screens, the sync filter and the tests, a module switch, two-database tests, a walkthrough and a mutation check at the end.

## 1. Answers used for the open points (recommended in spec 15; change any and the plan follows)
1. **Offline** means the vessel's installation draws and checks the form from the definition it holds; no call to shore is ever needed to fill a form. A device only needs the vessel's own network.
2. **Existing forms** are converted by the tool and checked by a person, a few at a time (stage F6). Building them by hand in the builder stays possible.
3. **Approval** is one review by the office. A per-form second sign-off is not in the first version.
4. **Scheduling** follows PMS: a per-form choice of calendar (every period from a start date) or from the previous entry; calendar by default.
5. **Numbers** are kept typed so later stages can feed other modules; the first version feeds none.
6. **The expression language** is deliberately small (see section 3) and closed; widening it is a decision, not a drift.
7. **Layout** is the app's, not a copy of the paper (spec 15 section 11).

## 2. The core idea
- **A definition is data.** `FormVersion.Definition` is JSON of a closed vocabulary (sections, fields, grids, rules). `FormDefinitionRules` (pure) checks it: ids unique within their scope, every type known, every reference (a calculation, a show-if, a lookup source) names something that exists, no loops, grids well formed, limits sane. A version that fails cannot be published.
- **A version never changes once published.** An edit makes the next version. An entry names the version it was filled on and is checked against that version forever, so changing a form can never break an entry already filled.
- **Values are data too.** `FormEntry.Values` is JSON keyed by the stable field and cell ids. `FormValueRules` (pure) takes a definition and values and says: the cleaned values (hidden fields dropped, calculations recomputed), the problems (required, type, range, length, date rules), and the **exceptions** (answers a rule flags). The editor, the submit step, the sync applier and the tests all call it, so the vessel and the office cannot disagree.
- **One small expression language, our own.** Field names, numbers, text, `+ - * /`, comparisons, `and or not`, and `sum()`, `count()`, `any()`, `min()`, `max()`, `round()` over a field, a grid column or a row. A parser and a tree evaluator, no `eval`, no reflection, a depth and a length limit, so a form can compute and nothing else.
- **The entry is the vessel's document; the review is the office's events.** Draft, Submitted by the vessel; Started, Accepted, Returned appended by the office. Status is worked out (`FormStatusRules`), the same on both sides. A reviewed entry is locked.
- **Reuse, do not rebuild:** comments are a spec 10 conversation on the entry (`ChatModules.FormEntry`), photos are `FileAttachment`s owned by the entry, action items are Worklist tasks linked to it (link type `form`), numbering is `FRM`, the dashboard and menu counts go through the nav catalog and the dashboard modules like the others.

## 3. What syncs
| Record | Writer | Direction | Kind |
|---|---|---|---|
| `FormType`, `FormVersion` | office | to the vessels the form applies to | master data, Master lane |
| `FormEntry` | the side that fills it (the vessel normally; the office for a vessel or for a shore form, flagged) | both ways for the vessel | document, Transaction lane |
| `FormReview` | office | down | append-only |
| Comments, photos, actions | spec 10, spec 10, spec 05 | both ways | as there |
A vessel holds only the versions that apply to it (by vessel type and named vessels) and its own entries, even after a full baseline: a boundary test proves it. A published version is immutable, so it cannot conflict; an entry has one writer at a time. A change of applicability (a form now applying to a new vessel type) queues the versions to the vessels that now qualify.

## 4. Stages (each ends with tests and your go-ahead to commit)
**F1. Foundation.** Tables and migration (`FormType`, `FormVersion`, `FormEntry`, `FormReview`); the pure rules: `FormDefinitionRules`, the expression parser and evaluator, `FormValueRules`, `FormStatusRules`, `FormSchedule` (what is due and late from periodicity, a start date and the previous entry); the service (types, draft and publish and retire versions, copy a version, start an entry for a vessel and period, save a draft, submit, review, correct); sync wiring and baseline; module switch "Forms"; reference `FRM`; pure and two-database tests.
**F2. The builder.** Form type list (the existing filters: form, department, periodicity, fleet, active), versions under each, the builder (sections, fields, grids, rules), the JSON tab with live validation and the same definition, the preview (drawn by the same renderer the vessel uses), publish.
**F3. Vessel entry.** Due list, start, the fill screen drawn from the definition (field list and grid renderers, show-if and calculations live, prefill, saved as typed), submit, my entries, status; photos on a field or on the entry; comments tab (the conversation panel); the vessel menu and dashboard card.
**F4. Office review and reports.** Submitted list (month, year, form, type, department, keywords, vessel), the entry page with form, comments and action-item tabs, review and return, action items as Worklist tasks (an exception can propose one), print and PDF of an entry, CSV and Excel export of a form's entries (one column per field), the year view, dashboard card, attention items and menu counts.
**F5. Schedule and due.** Due forms generated from periodicity and applicability for each vessel (calendar or from the previous entry), the due and overdue figures on the vessel and the office home pages and the year view, "form needed" reminders through the existing attention strip; voyage and ad hoc forms started by hand.
**F6. The converter.** Upload a legacy HTML form, read its tables, `data-ititle` paths and input classes into a draft definition (section ids, field ids and types, grids from repeating headers), a report of what it could not convert, a side-by-side check, save as a draft version; an import of the old XML values as entries where the paths match. Tested on three real samples: a grid-heavy engine report, a checklist, and a change-of-command form.
**F7. Walkthrough and mutation check.** One end-to-end test across a real office and vessel (publish a form, a vessel fills it offline, submits, the office reviews and returns, the vessel corrects, the office accepts, an exception becomes a Worklist task, the year view and the due list agree, a vessel never holds a form that does not apply to it); then deliberate changes to `FormDefinitionRules`, the expression evaluator and `FormValueRules` against the pure tests, as for the incident engine.

## 5. Parallel work (rules from 10a and 11a still apply)
- This work goes on its own branch `forms` from `main`; `main` is merged into it before each stage; the other sessions' branches are not touched.
- **The shared files are the risk.** Registering a new synced record is one-line additions in `Aggregates.cs`, `SyncPublisher.cs` (`VesselOfAsync`, the baseline), `Platform.cs`, `VesselGroup.cs` (`PolicyModules`), `ChandleryDbContext.cs`, `Program.cs`, the nav catalog and the dashboard constructors; each goes in its own line or block.
- **The migration of a stage is generated last, from `main`'s snapshot,** after `main` is merged; if another migration lands in between, ours is regenerated after it, never edited by hand.
- Tests are run per stage on only the new test classes and the ones the stage touches; the whole suite is run once at the end, as agreed.

## 6. Risks and how they are tested
- **A definition that can hang or crash the vessel.** Loops in show-if and calculations, huge grids, deep expressions: the definition rules reject loops and cap grid size, expression depth and length; a test feeds each.
- **A change under a filled entry.** Versions are immutable; a test publishes a new version and re-checks an old entry against its own version.
- **Different answers on the two sides.** One pure function; a two-database test checks the same entry on both sides after sync, including a calculation.
- **A form on a vessel it does not apply to.** The boundary test above, after a full baseline and after applicability changes.
- **Grid size on a tablet.** A grid is rendered by section on a small screen with sticky headers; checked in the browser at tablet width on the largest converted form.
- **Size on the link.** An entry is values only (a few KB even for a large grid); photos go on the Bulk lane through `FileAttachment`.
- **Conversion fidelity.** The converter is a first draft, never a publish: nothing converted is live until a person has checked and published it.

## 7. Needed from you
1. Approve the stages (F1 to F7), or change the order (for example the converter earlier, to start on real forms sooner).
2. Answers, or "use your recommendation", for section 1.
3. **Three sample forms for F6**, with a note of which is the hardest one. Only the crankpin bearing report (PM05, grid-heavy) was opened while studying; the cargo gear report (MT07) and the change-of-command form (VT01) are suggestions from the catalogue and have not been looked at yet.

## 8. F1 as built (2026-11-15)
- **Code:** `Models/Forms.cs` (four tables), `Services/Forms/` (`FormExpression`, `FormDefinition`, `FormDefinitionRules`, `FormValueRules` with `Initial` for prefill, `FormRules` holding `FormStatusRules`, `FormApplicability`, `FormSchedule`, and `FormService`), migration `FormsFoundation`, module switch "Forms", reference `FRM`. Sync: `FormType` and `FormVersion` are master data (a draft, an office-only form and a form that does not apply never go down), `FormEntry` and `FormReview` are documents (an office-only entry stays ashore).
- **Tests:** pure `FormRulesTests` (62) and two-database `FormSyncTests` (9, including the whole fill, submit, return, correct, accept walkthrough and the boundary checks); the nearby chat, nav and master-data suites still pass.
- **Applicability uses vessel groups, not vessel types.** The vessel has no type field, and the group tree (Fleet, Tankers, Far East) already plays that part, so a form applies to groups (and everything beneath them) and named vessels.
- **Known limit:** when a form stops applying to a vessel (a group is removed from the list), the vessel keeps the copy it already holds; it simply receives no new versions. Dropping it is a small addition for F2 (the type update can send a tombstone to the vessels that lost it).
- **Left for later stages on purpose:** conversation lines on submit and review (F4, with `ChatModules.FormEntry`), Worklist tasks for exceptions (F4), photo files on a field (F3), the screens (F2 to F4).

## 9. F2 as built (2026-11-15)
- **Pages (company admin, only where Forms is on):** /forms/types (list with search, department, periodicity and in-use filters; create and edit a form with its schedule and where it applies; versions with edit, view, copy, retire, discard) and /forms/builder/{version} (design, JSON and preview tabs; checks beside it as you type; save draft; publish; a published version opens read-only with Copy to a new draft). Menu entry under Admin.
- **Shared pieces for F3:** FormRenderer and FormControl draw a form from its definition and keep the answers as typed, using FormValueRules for what shows, what is calculated and what is wrong; the vessel's fill screen reuses them. FormCheck now says which fields and sections are hidden. FormDef.Tidy and ToPrettyJson support the builder.
- **Checked in the app:** created a form, designed it from JSON, saw the same on the design tab, previewed it (calculation, required message, show-if), published it. Not checked: tablet width, dark/light themes beyond the default, and the grid designer by hand (only through JSON).
- **Still to do from the F2 list:** nothing in scope; dropping a copy from vessels that lost a form stays noted for later.
