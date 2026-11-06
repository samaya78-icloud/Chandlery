# 06. Risk assessment

Template-driven risk assessments that are completed by the vessel (or office), reviewed and signed off, and re-reviewed on a schedule.

## 1. Goals
- A shared library of reusable assessments maintained by shore.
- Assessments completed onboard offline, signed off by the Master/Chief Engineer, immutable once signed.
- Review dates drive tasks automatically; control actions become Worklist tasks.

## 2. Concepts
- **Category** (configurable list): navigational, technical, operations, HSE&V, critical equipment, hotwork, IT, ship security.
- **Template** (shore-owned, versioned): job/activity description, category, work activities, hazards, default controls, default scoring, contingency.
- **Assessment** (the document): reference number, job description, category, original assessment date, last review date, next review date, team, activities, hazards, status, links.
- **Team:** name, rank (people from the user directory or typed names).
- **Work activity:** title; ordered; contains hazards.
- **Hazard:** hazard, effect, risk before controls, existing and additional controls, risk after controls, responsible person, due date, contingency and recovery action.
- **Risk matrix** (configurable per company, default 5×5): consequence 1–5 (insignificant…catastrophic) × probability 1–5 (rare…almost certain) → risk factor and band (Low, Medium, Medium High, High, Extreme). Scored twice: before (RE) and after (RC) controls.

## 3. Types and lifecycle
| Status | Meaning |
|---|---|
| Draft | being written |
| Submitted | sent for review/sign-off |
| Signed (Initial) | reviewed and finalised by Master/Chief Engineer; **locked** |
| Interim | repeat of a work activity: previous assessment re-reviewed and re-signed |
| Annual | re-reviewed after the review period (default 11 months from the initial date) |
| Superseded | replaced by a newer version |
- After signing, changes create a **new version**; the old one stays readable.
- **Repeat activity:** "copy as interim" starts from the last signed version.
- **Sharing:** shore may share an assessment with a vessel/group (appears in their library); shared flag visible.

## 4. Numbering
`RA-{ORIGIN}-{CATEGORY}-{SEQ}` (origin = shore office or vessel code), per spec 01.

## 5. Ownership and sync
| Item | Writer |
|---|---|
| Template library | shore |
| Assessment draft/submit | creator side (vessel or shore) |
| Review/sign-off | the reviewer role named by workflow (Master/CE onboard, superintendent ashore) |
| Locked assessment | nobody (new version only) |
Lane: `Transaction` for assessments, `Master` for templates. Scope: assessments created for or shared with the vessel.

## 6. Integrations
- **Worklist:** each control action with owner and due date creates a task; next review date creates a reminder task.
- **PMS:** critical equipment deferral can require a linked assessment.
- **Attachments:** photos, drawings, permits.
- **PDF export** with signatures and version history.

## 7. Screens
- **Library (shore):** templates by category, versions, publish to fleet/group.
- **Assessment editor:** guided steps (details → team → activities → hazards → scoring → controls → review), scoring shows the band live, validation before submit.
- **Listing:** filter by scope (office/fleet/vessel), category, status, due for review; PDF; sharing.
- **Dashboards:** due for review, high residual risk, unsigned drafts older than N days.

## 8. Data model (summary)
`RaTemplate(+versions)`, `RiskAssessment`, `RaTeamMember`, `RaActivity`, `RaHazard`, `RiskMatrix` (per company), uses `Lists`, `Attachments`, `WorklistLink`.

## 9. Acceptance criteria
- A signed assessment cannot be edited; "new version" copies it and links the old one.
- Scoring uses the company matrix and shows band before and after controls.
- Review due dates create tasks that reach the vessel; control actions appear in the Worklist.
- Templates updated ashore reach vessels as new versions without altering signed assessments.
