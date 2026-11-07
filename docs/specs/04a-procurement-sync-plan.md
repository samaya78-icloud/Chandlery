# 04a. Procurement sync: plan (implements spec 04 in stages)

Status: **approved 2026-11-07. P1 built 2026-11-07** (orders down, deliveries up, Orders pages onboard); P2 to P6 to do. Decisions taken: full spec 04 scope; the vessel sees prices on its own POs only; attachments and photos wait for file sync in the engine.

## 1. Where we are
Requisitions, master data (including crew) sync, and a vessel is locked down to requisitions, a read-only catalogue and crew logins. RFQs, quotes, POs and deliveries exist only at the office.

## 2. Design

### Documents that travel
| Document | Children | Created by | Owner | Goes to |
|---|---|---|---|---|
| Supplier, CompanySupplier (master) | none | office | office | every vessel of the company: name and contact only, never bank accounts |
| Rfq | lines, invited suppliers | office or vessel | creator, for life (see below) | office RFQs: a summary to the vessel they are for (no quotes); vessel RFQs: up to the office |
| Quote | lines | supplier via portal (office), or entered onboard | where it was entered | quotes of a vessel RFQ go down to that vessel; onboard-entered quotes go up |
| PurchaseOrder | lines | office or vessel | creator, until handed over | the vessel it is for, with prices; vessel-raised POs go up |
| DeliveryOrder | lines | whoever receives (usually the vessel) | creator, for life | both ways |

Never on a vessel: supplier bank details, other vessels' prices or documents, quotes of office-run RFQs, approval matrix people.

### Ownership
One writer per document at a time. A document is owned by the side that created it; the owner changes only through an **explicit hand-over** (a vessel PO above the Master's limit is handed to the office; the office can also take over a vessel RFQ or PO). Each side refuses edits to documents it does not own (same guard as requisitions). Derived or office-only values are **not** part of the synced document: `PurchaseOrder.DeliveryStatus` is recomputed on each side from the deliveries it holds; `DeliveryOrder.InvoiceId` (the invoice lock) and PO approval rows stay at the office.

### Generic document codec
Requisition has a hand-written document class. For the new types a generic **aggregate codec** reads the EF model: the root's stored properties plus its child collections (owned or by foreign key), with the same canonical JSON rules as master data. The manifest says which columns are local-only. Requisition keeps its own class.

### Policy and approval onboard
- Creating a vessel RFQ or PO requires `CanPurchaseDirect` for the department and category (the existing policy guard); the vessel is told which policy blocked it.
- **Master** is a flag on a crew member, set by the office (like crew admin).
- Direct PO total (USD, using the rates the vessel holds) at or below `MasterApprovalLimitUsd` and within `MonthlyBudgetUsd`: the Master approves onboard, the PO stays vessel-owned and shows at the office as "for review". Above: it is handed to the office, shown to the crew as "Awaiting office approval", and decided through the normal approval matrix.
- **Emergency override:** above the limit, with mandatory justification, the Master may approve; the PO is flagged, sent on the Urgent lane, and sits in an office review list until reviewed.

### Dispatch
`DispatchStatus` on a vessel PO: not sent, queued, sent via system, sent by email, acknowledged. Via system: when the approved PO reaches the office the supplier portal shows it at once. By email: the vessel generates an `.eml` message (supplier to, office cc, PO PDF attached) that opens in its own mail client; the time and recipient are recorded. Nothing is ever sent by Chandlery itself.

## 3. Stages (each ends with tests on two real databases and your go-ahead to commit)
**P1. Down to the vessel, and deliveries up.**
Aggregate codec and ownership columns (migration); Supplier and CompanySupplier join the master manifest; POs and deliveries for a vessel sync down; deliveries recorded onboard sync up; `DeliveryStatus` recomputed on each side; vessel pages: Orders (list and detail, with prices and a status timeline) and a delivery recording form; requisition detail shows its orders and where each stands.
**P2. RFQs and quotes onboard.**
Vessel direct RFQ (policy-checked, suppliers from the received list), RFQ up to the office where invited suppliers see it in the portal; portal quotes come down; manual quote entry onboard (an agent's email or phone quote) goes up; comparison and award onboard; office takes-over action.
**P3. Direct POs and Master approval.**
PO from an award or a spot PO onboard; Master flag on crew; limit and monthly-budget checks in USD; hand-over above the limit; emergency override with justification; office "for review" and "handed over" lists.
**P4. PO dispatch.**
Dispatch status, via-system dispatch, `.eml` generation with the PDF, supplier acknowledgement, office resend.
**P5. Vessel home.**
Dashboard of open requisitions, items awaiting quotes, deliveries due at the next port, Master approvals, a sync status chip (last sync, changes waiting, Sync now); port-call view; replaces today's zero-tile dashboard.
**P6. Live demo** on two apps as before: office-run order flowing to the vessel and delivered onboard; a vessel-run RFQ, quote, Master-approved PO and delivery, all visible at the office.

## 4. Not in this plan
Onboard invoices, supplier bank details, stock and ROB ledger, attachments and photos (waits for file sync), an earlier approval step before the Master, PO cancellation by the office (needs a Cancelled status; can follow), conflict inbox for the rare simultaneous edit.

## 5. Risks
- It is the biggest piece so far: P1 alone touches the codec, four tables, the publisher and applier, and new vessel pages.
- Master limit and monthly budget become enforced for the first time; policy rows that were only stored now bite. Companies with no policy rows stay unrestricted (today's rule).
- Quotes and prices are commercially sensitive; scope tests must prove a vessel never receives another vessel's or an office-run RFQ's quotes.
