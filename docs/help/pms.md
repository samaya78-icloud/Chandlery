---
module: pms
title: Maintenance (PMS) and certificates
questions:
- How do I record running hours?
- How is the next due date of a job worked out?
- How do I defer an overdue job?
- How do I add a certificate, and what happens when I renew it?
- How do I import our existing maintenance plan?
---
Planned maintenance for each vessel: machinery, jobs driven by running hours or calendar (whichever comes first), deferrals, and the vessel's statutory and class certificates and surveys.

## Getting started
Start here if the vessel has no maintenance plan yet:
1. Open [Machinery](/pms/machinery) and build the tree (location, system, item) with makers and serial numbers. Importing a plan in step 2 can create this for you.
2. Bring in the jobs: [Import a plan](/pms/import) from the vessel's own Excel or CSV file, or use [Apply the library](/pms/apply) to generate jobs from the office's standard templates.
3. Enter the current running hours on [Running hours](/pms/hours), so hour-based jobs get a due date.
4. Open the [Plan](/pms) to see what is due, overdue or deferred. Open a job to work it.
5. Add the vessel's certificates and surveys under [Certificates](/pms/certificates).
The office keeps the standard job templates in the [Maintenance library](/company/pms-library) (admin).

## Pages
- [Plan](/pms): the vessel's jobs with due, overdue and deferred states. Open a job to complete it, defer it or add a defect.
- [Fleet overview](/pms/fleet): office roll-up of compliance and overdue work.
- [Running hours](/pms/hours): enter hour-meter readings. A mistake is corrected with a new reading, not an edit.
- [Machinery](/pms/machinery): the machinery tree (location, system, item) with makers and serials.
- [Import a plan](/pms/import) and [Apply the library](/pms/apply): bring in a vessel's own plan from Excel or CSV, or generate jobs from the shared library.
- [Approvals](/pms/approvals): deferrals and plan changes waiting for the office.
- [Template updates](/pms/template-updates), [Sister vessels](/pms/sister-vessels), [Reports](/pms/reports).
- [Maintenance library](/company/pms-library) (admin): the standard job templates owned by the office.

## Scheduling
A job is due on the earlier of its calendar date and the date its hours interval is projected to be reached (using the last 30 days of average running hours). A grace window follows the due date; after it the job is overdue. With no recent hour reading the job is flagged "hours out of date".

## Completing and deferring
Open a job, follow the checklist, enter hours, parts and photos, and complete it. To defer, give a reason and a new date; the superintendent approves within their limit. A critical job may need a signed risk assessment first (a company setting). A defect found on a job creates a Worklist task.

## Linking a job to the Worklist
A job and a Worklist task are linked from the job, not the other way round:
1. Open the job from the [Plan](/pms).
2. In the "Parts and defects" box, choose "Report a defect", add a title and what was found, and choose "Raise on the worklist".
3. This creates a Worklist task linked to the job (urgent if the job is critical). The job lists its "Worklist tasks for this job", and the task shows the job under "Linked" with a link back.
A deferral that needs follow-up also appears on the [Worklist](/worklist). To order spares for a job, use "Order the spares" in the same box.

## Certificates and surveys
[Certificates](/pms/certificates) lists every statutory and class certificate and survey with the time left. The office creates and owns a certificate; a vessel adds to its history: endorsements, renewals, extensions, surveys done, notes and scans (10 MB each). A renewal or extension never edits the certificate: the latest dated event decides the due date. Colours warn at 90, 60 and 30 days, and overdue shows in red. The office sees [all vessels](/pms/certificates/fleet). Alerts are in the app only (dashboard, menu badge, attention strip); there is no email.
