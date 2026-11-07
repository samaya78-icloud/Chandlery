# Sync setup (requisitions, first slice)

Chandlery uses the separate **Marine.Sync** engine (repo `C:\Projects\Sync`). Packages come from a local feed:

```bash
cd ../Sync && dotnet pack Marine.Sync.slnx -c Release -o packages     # then build Chandlery; nuget.config points at ..\Sync\packages
```

Sync is **off by default** (`Sync:Mode` = `Off`). When it is off nothing changes in behaviour.

## Office (hub)
```json
"Sync":    { "Mode": "Hub" },
"SyncHub": { "AdminKey": "<long random secret>" }
```
The office app then serves `/v1/sync` (vessels connect here), `/health`, `/console` (fleet console) and `/admin/*` (needs the admin key).
Sync's own tables live in schema `sync` of the same database.

## Starting a vessel
A vessel starts from an **empty, migrated database**. No clone is needed: it fills itself with the master data it is entitled to.

1. In the office console, **Add vessel**: pick a node id (32 hex digits, no dashes) and copy the token it shows.
2. In the office, set that node id on the vessel record (`Vessel.NodeId`).
3. Create the vessel's database and start the installation on it with:
```json
"Platform": { "Profile": "Vessel" },
"Sync":     { "Mode": "Node" },
"SyncNode": { "HubUrl": "https://office.example.com", "NodeId": "<32 hex digits>", "Token": "<token>", "PollSeconds": 30, "LinkProfile": "satellite" }
```
   The installation takes `SyncNode:NodeId` as its own identity on first start, creates the sync tables, and does not seed demo data.
4. In the office console, press **Send all master data** for that vessel (queues a baseline). The vessel receives its company, itself and its
   group chain, the catalogue, policies and rates, in dependency order; a record that arrives before its parent waits for it.

The app refuses to start if `SyncNode:NodeId` is not this installation's own node id.

## What syncs today
**Requisitions with their lines.** Rules (spec 04): a requisition raised onboard is owned by the vessel until it is first sent; the office then owns it
(approved quantities, status, cancel) and tells the vessel. Requisitions raised ashore are office-owned and go to the vessel they are for. Edits made onboard after
the office took over are ignored. A requisition that refers to master data that has not arrived waits for it (and is parked, visible and replayable, if it never does).

**Master data, office to vessel only.** Company, ports, currencies and exchange rates, vessel groups, vessels, departments, categories,
department/category links, items, units and conversions, vessel policies and module switches (the list is `Services/Sync/MasterData.cs`;
adding a type is one line). The office owns it all: a vessel installation refuses to save a change to it. A vessel receives only:
its own company record, its own vessel, the groups above it, and company-wide catalogue data; never other vessels, their policies or office staff.
Removals travel too, as do records leaving a vessel's scope (a vessel moved to another group forgets the old branch's policies and gets the new one's).

**Crew.** The office keeps crew records (`/company/crew`: code, name, rank, nationality, ID, vessel, crew admin flag). A vessel receives only the crew
who serve on it, and removes a person when they are signed off or moved. Crew sign in onboard with their **crew code** and a password (the sign-in
box takes an email or a crew code; crew codes are refused at the office). The office sets a temporary password; the person must choose their own at
first sign-in. A designated **crew admin** (set at the office) gets `/crew` onboard: reset passwords, enable or disable logins, reveal an ID number
(audited), for the crew of their own vessel only. A login change made onboard travels to the office; when both sides change the same login, the latest
change wins. ID numbers are AES-256-GCM encrypted: the database and the sync queue only ever hold ciphertext; every installation needs the same key,
`Crew:IdKey` (32 random bytes, base64; Development uses a fixed key when none is set). Screens show the last four characters only.
Reveal audit entries stay on the installation where the reveal happened.

## Not yet
Suppliers (they wait for the RFQ stage), RFQs, POs, deliveries, attachments, crew CSV import, onboard UI restrictions beyond the data rules (stage 4).
A vessel that lost its database needs its sync state reset together with it (sync skips versions it has already applied).
