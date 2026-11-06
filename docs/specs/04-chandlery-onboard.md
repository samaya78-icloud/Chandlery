# 04. Chandlery onboard

The vessel-side edition of procurement: requisitions, RFQs, POs and deliveries performed on board, offline-first, within the permissions the office grants (spec 02).

## 1. Scope
- **In:** requisitions, direct RFQs, quote entry, comparison, POs, deliveries, attachments, onboard approvals within limits, sync status, port-call view.
- **Out for now:** onboard invoices (shore handles invoicing), supplier bank details, other vessels' data, stock/ROB ledger (later with PMS spares).

## 2. Permissions model
- **Requisition:** any department/catalog category the vessel's effective policy allows (`CanRequisition`).
- **Direct purchasing (RFQ/PO):** only subjects with `CanPurchaseDirect`; the Master approves up to `MasterApprovalLimitUsd` for that department/catalog, within `MonthlyBudgetUsd`; above that the office approves.
- Items outside direct-purchase scope can only be requisitioned; the office then runs RFQ/PO ashore (today's flow).
- Breakdown/emergency override: a flagged purchase above limit is allowed with mandatory justification, notifies shore immediately (`Urgent` lane), and is reviewed afterwards.

## 3. Ownership and lifecycle
| Document | Stage | Writer |
|---|---|---|
| Requisition | Draft, Submitted | Vessel |
| Requisition | Office review, RFQ created ashore, Ordered, Closed | Shore |
| Direct RFQ (vessel-raised) | Draft, Open for quotes, quote entry, Evaluation | Vessel |
| Direct RFQ | Posted to supplier portal (relay) | Shore (on request) |
| Quote (portal) | Submitted by supplier | Supplier via shore |
| Quote (entered onboard from email/phone) | Entered | Vessel |
| Purchase order (direct) | Created, Master approval within limit | Vessel |
| Purchase order | Shore review, supplier dispatch, cancellation | Shore |
| Delivery order | Recorded, discrepancies | Vessel |
| Comments, attachments | any stage | Either (append-only) |
Status always flows back to the vessel, shown as a timeline: *Saved on ship → Sent to office → Received → Approved → Ordered → Delivered*.

## 4. Supplier interaction
Suppliers use the shore portal. Two supported paths for a vessel RFQ:
1. **Portal path:** the RFQ syncs to shore, shore posts it to chosen suppliers, quotes sync back to the vessel for evaluation.
2. **Manual path:** the vessel enters quotes it received by email, phone or at the quay ("quote received from agent"), with the file attached.
**PO dispatch (decided):** the **vessel sends the PO to the supplier with the office in copy**. Two ways, usable together:
1. **Through the system:** the PO is delivered to the supplier's portal account and the supplier is notified; the office sees it as copied. This needs a connection (or queues until there is one).
2. **By email:** the vessel can **generate an email to the supplier with the office in CC and the PO PDF attached**, opened in the vessel's own mail client or sent via the vessel's configured mail account; the generated message and send time are recorded on the PO.
Dispatch status is tracked on the PO (Not sent / Queued / Sent via system / Sent by email / Acknowledged by supplier). Shore can resend or relay if the vessel's mail is unavailable.

## 5. Vessel experience
- **Home:** open requisitions, items awaiting quotes, deliveries expected at the next port, approvals pending the Master, sync status.
- **Sync status chip (always visible):** "Last synced 2h ago · 14 changes waiting · 3 files queued", Sync now, link quality, offline banner.
- **Fast requisitions:** catalog restricted to allowed categories, favourites, "last ordered", templates (monthly provisions, engine consumables), photos attached from a tablet, units per line.
- **Port-call view:** upcoming ports and ETAs with the orders due at each; vessel confirms the delivery port/ETA that shore uses for delivery planning.
- **Delivery recording:** partial receipts, photos of the delivery note, discrepancy flags, supplier comments. Stock-on-board later.
- Plain-language states and a visible trail for every document so crew trust that the office has it.

## 6. Data on the vessel
- Its own documents; allowed catalog subset (by policy); departments, units, currencies and rates (daily), approved supplier list (names and contacts only), approval limits, users.
- **Never on board:** supplier bank details, other vessels' prices, other companies' data.
- Exchange rates arrive as `Master` lane updates; USD snapshots are stored on each document as today.

## 7. Numbering and identity
Per spec 01: vessel-prefixed numbers (`REQ-MVSF-2026-0007`), UUIDv7 keys.

## 8. Approvals onboard
Uses the approval engine. **The Master approves within a limit that the office maintains per department/catalog combination** (see spec 02, `MasterApprovalLimitUsd`). Limit checks compare the PO total in USD.
- **At or below the Master's limit:** the Master approves onboard, the PO is dispatched, and shore sees it for information.
- **Above the limit:** the PO is handed over to the office (stage hand-over) and waits for office approval, shown to the crew as "Awaiting office approval". The office decides using the normal approval matrix.
- An optional earlier step (e.g. Chief Engineer) can precede the Master via the vessel approval chain.
- Limits sync down as part of the policy snapshot and apply offline.

## 9. Offline behaviour
Everything above works with no link. Features needing shore are marked and queue their request: portal posting, PO relay, template updates, new catalog items.

## 10. Changes to existing Chandlery code
- Profile switch (`Vessel`) with a vessel-scoped data filter.
- Policy service used by pickers, RFQ/PO creation and approvals.
- Numbering service, UUID keys, change tracking columns (spec 01).
- Sync adapter manifest for Requisition, Rfq, Quote, PurchaseOrder, Delivery, Attachment, master data.
- A vessel UI shell (simplified navigation, tablet-friendly).

## 11. Acceptance criteria
- A vessel raises a requisition offline; after reconnect shore sees it and shore status updates reach the vessel.
- A vessel cannot create a PO for a subject without `CanPurchaseDirect`, and sees which policy blocked it.
- A direct PO within limit is approved by the Master onboard, reaches shore, and is flagged for review.
- No supplier bank data exists in the vessel database.
- Vessel and shore create documents simultaneously with no number or key collisions.
