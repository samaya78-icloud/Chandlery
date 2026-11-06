# 02. Vessel groups and policy

Shore-side feature, valuable on its own and required by onboard (spec 04) and sync scoping (spec 03).

## 1. Vessel groups
- A **vessel group tree** on the Vessels master page, up to **3 levels**.
- **Level 1 is mandatory**: every vessel belongs to a level-1 group. Levels 2 and 3 are optional refinements (for example Fleet › Vessel type › Trading area).
- A vessel is assigned to **one node** of the tree (its deepest chosen level); it inherits everything above it.
- Group names are unique among siblings. Groups can be renamed and moved; deleting a group requires it to be empty.
- UI: tree on the left (same master-detail pattern as the Catalog page, with search, collapse, counts), vessels grid on the right, drag to move vessels or groups.
- Data: `VesselGroup(Id, CompanyId, ParentId, Name, Level, SortOrder)`, `Vessel.GroupId`.
- Rule: tree depth ≤ 3 enforced on the server; moving a group can never exceed it.

## 2. Policy rows
A policy row states what a **target** (group or vessel) may do for a **subject** (department and/or category), plus module switches.
| Field | Meaning |
|---|---|
| Target | group (applies to all vessels below) or a single vessel |
| Subject | department, category, or both; blank = all |
| `CanRequisition` | vessel may raise requisitions for this subject |
| `CanPurchaseDirect` | vessel may create RFQs and POs for this subject |
| `MasterApprovalLimitUsd` | the Master can approve a direct PO up to this amount for this department/catalog combination (compared in USD); above it the office approves |
| `MonthlyBudgetUsd` | optional cap per vessel per month for this subject |
Master limits are therefore **per department and catalog (category) combination**, maintained by the office and resolved like every other policy cell (vessel > group levels).
| Modules | switches for Worklist, PMS, Risk Assessment, Tank Inspection, Projects |
| Effective dates | optional valid-from / valid-to |

## 3. Resolution
1. Collect rows that match the vessel: its own, then each ancestor group.
2. For a given subject, **the most specific row wins** (vessel > level 3 > level 2 > level 1); a subject-specific row beats a catch-all at the same level.
3. A row can **deny** explicitly (`CanRequisition = false`) and that overrides inherited allows.
4. No matching row means **not allowed**.
5. The result is the vessel's **effective policy**, computed at shore and shipped to the vessel as a signed snapshot, so the vessel enforces it offline.

## 4. Where it is enforced
- **Shore web and vessel app** both call the same policy service.
- Requisition and spot PO pickers show only items whose category the vessel may use (this extends the department-category rule already built).
- Direct RFQ/PO creation is blocked when `CanPurchaseDirect` is false, with a message saying which policy decides ("Group X allows requisitions only for Deck Stores").
- Sync scoping (spec 03) uses the same policy to decide which catalog items and master data a vessel receives.

## 5. Office user scope (optional, recommended)
Office users can be limited to groups: a superintendent sees only vessels in his groups. Same tree, same resolution.

## 6. Screens
- **Vessels page:** group tree + vessel list + vessel detail (group, node id, connectivity, last sync, version).
- **Policy matrix:** rows = departments (or categories), columns = capabilities, scoped to the selected group or vessel, with inherited values shown greyed and overridable.
- **Effective policy view:** pick a vessel and see the final result and which row decided each cell ("why can't this vessel buy this?").
- **History:** every policy change is audited and versioned.

## 7. Acceptance criteria
- A vessel in Fleet A › Tankers inherits Fleet A rules, a Tanker rule overrides it, and a vessel-specific row overrides both.
- Setting `CanPurchaseDirect = false` for Provisions blocks direct RFQ/PO creation for those items on that vessel only.
- Changing a policy produces a new snapshot that reaches the vessel at the next sync and takes effect there without restart.
- A vessel group move re-resolves policy for all vessels beneath it.
