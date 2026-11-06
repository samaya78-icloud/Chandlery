# 10. Collaboration and chat

A conversation mechanism built into the platform core. **Every module record has its own conversation thread** where all parties concerned can talk, with the history kept on the record. It replaces ad-hoc email and phone follow-ups and is available to every module (Chandlery requisitions/RFQs/POs/deliveries, Worklist tasks, risk assessments, PMS jobs and deferrals, tank inspections, projects).

## 1. Goals
- Discuss a record in the context of that record: no hunting through mail.
- Vessel, office and (where appropriate) external parties see one consistent thread, even with intermittent links.
- Conversations are part of the audit trail and travel with the record.
- Works as a standalone feature in any single licensed module (core service).

## 2. Concepts
- **Thread:** exactly one main thread per record (`Record = {moduleType, recordId}`), created on first message. Extra **topic threads** per record are optional (e.g. "Quote clarification", "Delivery damage").
- **Message:** text, author, time, node of origin, optional attachments, mentions, reply-to, reactions, edited/deleted markers.
- **Participants:** derived from the record (creator, assignee, approvers, vessel crew with access, office owner, supplier contact where the record is shared) plus anyone added or @mentioned.
- **Visibility channels** (per message, chosen by sender within permission):
  - **Internal** (company users only, vessel and office).
  - **Shared with supplier/external** where the record is visible to them (e.g. RFQ, PO, delivery): supplier users see only these messages.
  - **Office only** (hidden from the vessel), for sensitive evaluation notes.
- **System messages:** status changes, approvals, deferrals, deliveries and similar events appear in the thread as non-editable system entries, so the thread reads as the story of the record.

## 3. Behaviour
- **Compose** with mentions (`@name`, `@role`, `@vessel crew`), replies, attachments (photos, PDFs) using the shared files pipeline, and quick phrases/templates.
- **Delivery states shown per message:** *Saved on ship → Sent → Received by office → Read*. Read receipts per participant are optional by policy.
- **Edit/delete:** an author may edit within a short window (e.g. 10 minutes) with an "edited" marker and kept history; deletion is a soft-delete that leaves "message removed" and is audited. Administrators can redact with reason.
- **Unread and attention:** unread counts per record and in a global inbox; mentions raise notifications; "needs reply" and "resolved" markers on a message; thread can be marked **resolved** and reopened.
- **Notifications:** in-app first; push/email digests for connected users on rules (mention, reply, assigned, unread older than N hours). Email here is only a **notification** channel, never a transport for the data.
- **Search** across messages, with filters by vessel, module, record, author, date.
- **Moderation and retention:** retention follows the record; legal hold; export a thread to PDF with the record.

## 4. Offline and sync
- Messages are **append-only documents** (author owns each message), so chat never conflicts. Edits and deletes are new events on the message.
- Lane: **`Urgent` for mentions of the office, `Transaction` otherwise**; attachments use the file pipeline (`Bulk`).
- Ordering uses the hybrid logical clock; the UI shows messages in send order and marks those not yet synced.
- A vessel chatting offline sees its own messages immediately; shore messages arrive at the next sync. A thread shows "last synced" so crew know how fresh it is.
- **Scope:** a vessel receives only threads of records it owns or may see; supplier-visible messages are published through the supplier portal path only.

## 5. Permissions
- Reading and writing follow the record's permissions and the visibility channel.
- Suppliers can only see and post shared-channel messages on records shared with them.
- Policy (spec 02) can disable chat or external channels per vessel group or module.
- Office-only and internal messages are never sent to a vessel or supplier that must not see them (filtered at the hub before transmission).

## 6. User experience
- A **conversation panel** on every record detail page (right side or tab), with channel selector, unread badge, mentions, attachments, and the system story.
- A **global inbox** ("Conversations"): unread, mentions, awaiting my reply, by vessel, by module.
- Compact **tablet-friendly** composer onboard; photos from the camera attach in one tap.
- Presence is not required (links are intermittent); "typing" and live presence are out of scope, with live push when the link is good.

## 7. Data model (summary)
`Thread(Id, ModuleType, RecordId, Topic, ResolvedAt)`, `Message(Id, ThreadId, AuthorId, NodeId, Channel, Body, ReplyToId, CreatedAt, EditedAt, DeletedAt)`, `MessageAttachment`, `MessageMention`, `MessageRead(MessageId, UserId, ReadAt)`. Module records register as "conversation-capable" through the module contract (spec 01).

## 8. Integration points
- **Chandlery:** requisition, RFQ (with suppliers on the shared channel), quote clarifications, PO, delivery discrepancy, invoice query.
- **Worklist:** replaces the free-text progress notes with conversation plus structured timeline entries.
- **PMS, Risk Assessment, Tank Inspection, Projects:** deferral discussion, review comments, exception follow-up, variation orders.
- **Approvals:** approver questions and answers stay on the record.

## 9. Acceptance criteria
- A vessel user and an office user exchange messages on a PO; after reconnect both see the same ordered thread with correct delivery states.
- A supplier sees only the shared-channel messages on an RFQ and cannot see internal ones, even after a sync.
- Mentions reach the right user and are marked read after viewing; unread counts match on vessel and shore.
- A deleted message shows as removed with audit history; edited messages show their original on request for administrators.
- Chat works in a standalone Worklist-only or PMS-only installation (core feature).
