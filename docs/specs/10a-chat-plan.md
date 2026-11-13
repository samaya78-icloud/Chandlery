# 10a. Collaboration and chat: plan (implements spec 10 in stages)

Status: **approved 2026-11-13 ("Approve, start C1"). C1 built 2026-11-13:** `Models/Chat.cs`, pure `Services/ChatRules.cs`, `Services/ChatService.cs`, sync wiring (`Aggregates`, `SyncPublisher` vessel, lane and baseline, `VesselRules.Writable`), module switch `Conversations`, migration `Conversations` (two tables); 29 pure and 10 two-database tests pass. Mentions are kept as tokens in a column of the message (no child table). Written on branch `chat` (from `main` at 9d35654) while the other session works on Safety (`origin/safety`: drills and meetings built, permit to work started) and Projects. Builds on the patterns proven in Worklist, Incidents and Projects: append-only records owned by the side that wrote them, state worked out by pure functions, a module switch, two-database tests. Nothing is committed until you approve this plan and each stage's go-ahead.

## 1. What the code gives us today (checked 2026-11-13)
- **Append-only, author-owned records already sync cleanly**: `WorklistEntry`, `ProjectNote`, `IncidentEvent`. A chat message is the same kind of record, so chat needs no new sync idea.
- **Worklist already has a conversation of its own** (`WorklistEntryType.Comment` in the task timeline) and Projects has `ProjectNote`. Both stay as they are in the first version (section 6).
- **There is no general attachment.** `Attachment` belongs to requisitions only (`RequisitionId`). This is the same gap 11b section 3 names for incidents and permits.
- **Users**: `AppUser` has company roles, supplier roles and crew sign-in. Crew and office users are company users; suppliers have their own portal pages.
- **Scope**: a record reaches a vessel only when `SyncPublisher.VesselOfAsync` names that vessel; returning no vessel keeps a record ashore. That is the filter that keeps office-only messages off the vessel.
- **Module switch**: `PolicyModules` is a list of names switched per vessel group. Chat is a core feature (spec 10), so it gets a switch only to turn it off.

## 2. The core idea
- **One record type does the work: `ChatMessage`**, written once and never changed. There is no mutable thread row: a thread is simply all messages with the same record key `(ModuleType, RecordId)`, and a topic thread adds a `Topic` text.
- **Edits, deletes, resolve and reopen are new messages** of a different `Kind` that point at the message or thread they act on. The thread view replays them in a fixed order, so a vessel and the office arrive at the same thread however they worked while apart (the same method as the Worklist timeline).
- **The author's side owns each message** (`OwnerStage`), as for `WorklistEntry`. Nobody edits anybody else's message.
- **Who may see a message is decided by one pure function** (`ChatRules.Visible`) used by the screens, by the sync filter and by the tests, so the three cannot disagree.

## 3. Data (stage C1)
`ChatMessage`: `Id`, `CompanyId`, `VesselId` (null for a record with no vessel; such messages never leave the office), `ModuleType` (text: `Requisition`, `Rfq`, `PurchaseOrder`, `Delivery`, `Worklist`, `Pms job`, `Tank inspection`, `Risk assessment`, `Project`, `Incident`, others added by the modules), `RecordId`, `Topic` (empty for the main thread), `Kind` (`Message`, `System`, `Edit`, `Delete`, `Redact`, `Resolve`, `Reopen`, `NeedsReply`), `TargetId` (the message an edit, delete or marker acts on), `Channel` (`Internal`, `Shared`, `OfficeOnly`), `Body`, `ReplyToId`, `AuthorUserId`, `AuthorName` (kept as text beside the link, as in rest hours), `AuthorSide`, `At`, `OwnerStage`.
`ChatMention`: `MessageId`, `UserId` or role name, for the inbox. `ChatRead`: `UserId`, `ThreadKey`, `LastReadAt`, **local to each installation and never sent** (read state is a personal convenience; see open question 2).
The vessel is copied onto the message when it is written, from the record it belongs to (a service call, not a join), so a message can be scoped without reading the other module's tables.

## 4. Ownership and what syncs
| Record | Writer | Direction | Kind | Lane |
|---|---|---|---|---|
| Message, edit, delete, resolve, reopen | the side of the author | both ways for the vessel's records | append-only | Transaction; **Urgent** when it mentions the office (spec 10 section 4) |
| System message (status change, approval, delivery) | the side where the event happened | both ways | append-only | Transaction |
| Redaction by an administrator | office | down | append-only | Transaction |
| Office-only messages | office | **nowhere** | stay ashore | none |
| Shared (supplier) messages | office or supplier, via the portal | office only, never the vessel unless the record is the vessel's own RFQ or PO | append-only | Transaction |
`ChatRules.VesselOf` returns no vessel for an office-only message, which is how it never travels; the baseline query for a new vessel filters the same way. A vessel holds only the threads of its own records, and an Internal message on a record that is shared with a supplier is never visible on the supplier's side (tests, section 9).

## 5. Stages (each ends with two-database tests and your go-ahead to commit)
**C1. Foundation.** `Models/Chat.cs`; pure `Services/ChatRules.cs` (visibility by channel and side, edit window of 10 minutes, thread state from the replayed messages: edited, removed, resolved, needs reply, unread count, mention parsing); `Services/ChatService.cs` (post, reply, edit, delete, resolve, reopen, redact by an administrator with a reason, list a thread for a viewer, unread counts, mark read, post a system message for modules to call); sync wiring (`Aggregates`, `SyncPublisher` vessel and lane, baseline, `VesselRules.Writable`, change tracking so a message maps to its vessel); module switch `Conversations`; migration; tests. No screens yet.
**C2. The conversation panel, on one record.** A reusable `ConversationPanel` (record key, vessel, channels the viewer may use) with messages in send order, "not yet synced" marker, replies, edit and delete within the window, resolve and reopen, unread badge, and a compact tablet composer. First home: **the Worklist task page** (`WorklistTaskPage.razor`), because the Worklist is the first module and exercises two-way writes. It sits beside the existing timeline, which is unchanged.
**C3. Mentions and the inbox.** `@name` and `@role` in the composer; the inbox page `/conversations` (unread, mentions, awaiting my reply, by vessel and module); nav item through `NavCatalog`; a dashboard card and attention items through `DashboardModules`; in-app notifications only.
**C4. Other records, one module at a time.** The panel on Purchase order, Requisition, RFQ, Delivery, PMS job and deferral, Tank inspection, Risk assessment, Project, and Incident, each a small addition at the bottom of that module's page, and the system messages for each module's main events through `PostSystemAsync`. One module per commit, in the order the other session is least likely to be editing (decided when each is reached; Projects and Safety last).
**C5. Attachments.** A general file record shared by chat, incidents and permits (agreed with the other session first, 11b section 3), using the existing `FileStore`; photos from the tablet camera in one tap; images downscaled onboard. Until C5 messages are text only.
**C6. Supplier channel.** The `Shared` channel on RFQ and PO threads, in the supplier portal only: a supplier sees only Shared messages on records shared with them, never Internal or OfficeOnly, and posts only Shared. Needs the portal pages and a supplier-side test that tries to read the other channels.
**C7. Search, export and policy.** Search across messages with filters (extends `SearchService`); thread printed to PDF with the record; `Conversations` switch honoured per vessel group (including turning the Shared channel off); retention follows the record.
**C8. Walkthrough and mutation check.** One end-to-end test across a real office and vessel database (a PO thread: messages both ways, an offline gap, reconnect, same ordered thread on both sides; mentions; edit and delete; a supplier sees only Shared); then deliberate changes to `ChatRules` against the pure tests, as for the Incidents and rest-hours engines.
C1 to C3 give a usable chat on one module; C4 onward widens it.

## 6. Decisions used for the open points (recommended; change any and the plan follows)
1. **No change to Worklist notes or Project notes in the first version.** The Worklist's `Comment` entries and `ProjectNote` stay; the panel sits beside them. Replacing the notes (spec 10 section 8) is a later, separate step once chat is proven, because it rewrites timelines both sides already hold.
2. **Read receipts are not synced in the first version.** "Saved on ship, Sent" come from whether the message has left the outbox; "Received by office" is shown when the sync acknowledges it; per-person "Read" is a policy option left for later (it adds a record per message per reader).
3. **No email.** Notifications are in-app only (spec 10 section 3 allows digests later; email is never a transport).
4. **No typing indicators or presence** (out of scope in spec 10).
5. **Quick phrases** come after C3, as a small company list (the platform lists feature), not in C1.

## 7. Parallel work with the other session (rules from 11a and 11b still apply)
- This work stays on branch `chat`; `main` is merged into `chat` before each stage; the other session's branches are not touched.
- **The shared files are the risk.** Registering a new synced record means one-line additions in `Aggregates.cs` (the type list, `IsAppendOnly`, `StageOf`, `SetStage`), `SyncPublisher.cs` (`VesselOfAsync`, the baseline), `Platform.cs` (`VesselRules.Writable`), `VesselGroup.cs` (`PolicyModules`) and `ChandleryDbContext.cs`. Each addition goes in its **own line or block**, never inside another module's line, so a merge with `safety` or Projects work is a textual union, not a rewrite.
- **The migration is generated last in each stage, from `main`'s snapshot**, and `main` is merged first; if the other session's migration has landed in between, ours is regenerated after it, never edited by hand.
- **C4 touches other modules' pages.** Each is a one-block addition at the foot of the page and goes in only after `git fetch` shows the other session has not changed that page.
- The full test suite is run once per stage in the quiet hours of the other session; the two-database tests use their own database prefix (`chat_sync`).

## 8. Needed from you
1. Approve the stages (C1 to C8), or change the order. In particular: is the **Worklist task page** the right first home, or would you rather start on the purchase order?
2. Answers, or "use your recommendation", for the decisions in section 6.
3. **Attachments (C5):** agree with the other session that a general file record is built once for chat, incidents and permits, and who builds it. Until then chat is text only.
4. **Suppliers (C6):** should suppliers be able to post in the first release, or only read the Shared messages?

## 9. Risks and how they are tested
- **A message seen by the wrong party.** Office-only messages must never reach a vessel, and Internal messages never a supplier, even after a full baseline of a new vessel. A boundary test writes messages in every channel on one record, runs a full baseline to a vessel, and checks the vessel holds none of the office-only ones; a supplier test does the same for Internal and OfficeOnly.
- **Out-of-order arrival.** An edit or a delete can reach a node before the message it acts on. The thread view ignores an action whose target is not yet held and applies it when the target arrives (a test sends them in the wrong order).
- **Names and personal data.** Author names are kept as text beside the link; a message is never rewritten when a user is renamed. Redaction removes the body from the view but keeps the audit record and the reason.
- **Growth.** A busy PO thread is read by record key, newest page first, with an index on `(ModuleType, RecordId, At)`; a vessel's baseline sends only its own records' threads.
- **Touching the other session's pages (C4).** Handled by the one-block rule in section 7; if a page has changed on their side, that page waits.
