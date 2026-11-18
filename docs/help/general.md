---
module: general
title: Chandlery in general
questions:
- How do I find a document or page quickly?
- Which modules are switched on for my vessels, and who changes that?
- How do I approve something waiting for me?
- What is the difference between the office and the vessel?
---
Chandlery is a fleet-management workspace. The office (shore) and every vessel run the same product. A vessel works offline and syncs with the office in the background, so a change made on board reaches the office when the link is up.

## Getting started
If Chandlery is new to the company, the office sets it up in this order:
1. [Company Profile](/company/profile): company details and approval step names.
2. [Team](/company/team): add your people and give each a role.
3. [Vessels](/company/vessels), [Departments](/company/departments), [Suppliers](/company/suppliers) and the [Catalog](/company/items): the master data everything else picks from.
4. [Vessel policy](/company/vessel-policy): switch on the modules each vessel or group uses, and what a vessel may buy directly.
5. [Approval Matrix](/company/approval-matrix): who approves what, by amount.
6. Then set up the module you want first: each module's own help has a "Getting started" part.
A vessel starts from the Dashboard: it receives the office's master data by sync, so it needs no set-up of its own.

## Getting around
- Press Ctrl+K (or click the search box in the top bar) to jump to any page, or to find a document by number or name.
- The logo in the top bar opens the main menu. Under each page title a strip shows the neighbouring pages of the same part of the menu, and a breadcrumb shows where you are.
- The [Dashboard](/) has one card per module with its counts and an "Add new" shortcut.
- [My Approvals](/company/approvals) lists everything waiting for your decision.

## Modules and policy
Each module (Procurement, Worklist, Maintenance, Tank inspection, Risk assessment, Projects, Hours of rest, Incident reports, Drills and meetings, Permit to work, Forms, Conversations) is switched on per vessel or per vessel group. A page that is missing from your menu usually means the module is off for that vessel. A Company Admin changes this on [Vessel policy](/company/vessel-policy).

## Master data and setup (office)
There is no separate Admin menu: a company admin finds each setup page under the module it belongs to, for example the Risk library under Risk assessment, Form types under Forms, and the Approval Matrix under Work. Finance and company pages are under Master data.
- [Vessels](/company/vessels), [Departments](/company/departments), [Ports](/company/ports), [Units of Measure](/company/uoms), [Suppliers](/company/suppliers), [Catalog](/company/items), [Crew](/company/crew).
- [Approval Matrix](/company/approval-matrix): who must approve what, by amount.
- [Company Profile](/company/profile): company details, approval step names, the help assistant switch and module settings.
- [Team](/company/team): people and their roles. [Tax Codes](/company/tax-codes), [Currencies](/company/currencies), [Exchange Rates](/company/exchange-rates).

## Roles
Company Admin manages settings and people. Company User works in the modules. Suppliers and yards use their own portal, not the office screens.

## On a vessel
The sync chip in the top bar shows when the vessel last synced and how many changes are waiting. Records the vessel does not own (for example certificates) can be added to but not changed.

## Linking records across modules
Records link from the place where you work, and the link shows on both sides:
- PMS job and Worklist: open the job and use "Report a defect" to raise a Worklist task linked to it.
- PMS job and a requisition: open the job and use "Order the spares" to start a requisition with the machinery's spares filled in.
- Worklist task and a requisition or order: open the task and use its "Link" action.
- Tank inspection and a PMS job: choose the job while filling in the inspection. A tank exception can become a Worklist task.
- Risk assessment, incident, drill, meeting, permit and form actions become Worklist tasks with an owner and due date.
- Project and Worklist, PMS, procurement: a project's specification takes deferred Worklist tasks and due PMS jobs, and its actual cost comes from purchase orders and invoices tagged to the project.
- Any record: use its conversation thread for questions instead of email.
