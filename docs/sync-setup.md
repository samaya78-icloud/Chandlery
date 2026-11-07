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
1. In the office, open **Vessels** and note the vessel. (A vessel's node id is stored on the vessel record, `Vessel.NodeId`.)
2. **Clone the office database** (`pg_dump` and restore, or `CREATE DATABASE vessel TEMPLATE office` while nobody is connected). This gives the vessel the same master data under the same ids.
3. Start the vessel installation on the copy with `Platform:Profile = Vessel`. It creates its own node id on first start (see `NodeInfos`).
4. Set that node id on the vessel record in both databases (`Vessels.NodeId`) and register it at the hub: console, **Add vessel**, use the node id as written by `NodeInfo.Id` in 32 hex digits without dashes. Copy the token.
5. Configure the vessel:
```json
"Sync":     { "Mode": "Node" },
"SyncNode": { "HubUrl": "https://office.example.com", "NodeId": "<32 hex digits>", "Token": "<token>", "PollSeconds": 30, "LinkProfile": "satellite" }
```
The app refuses to start if `SyncNode:NodeId` is not this installation's own node id.

## What syncs today
Requisitions with their lines. Rules (spec 04): a requisition raised onboard is owned by the vessel until it is first sent; the office then owns it
(approved quantities, status, cancel) and tells the vessel. Requisitions raised ashore are office-owned and go to the vessel they are for. Edits made onboard after
the office took over are ignored. A requisition that refers to master data the receiver does not have is parked and shows in the console for replay.

## Not yet
Master-data sync (new items, vessels, users must still be copied by hand), RFQs, POs, deliveries, attachments, onboard UI restrictions beyond requisition edits.
