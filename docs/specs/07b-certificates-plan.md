# 07b. Certificates and surveys (part of PMS)

Built 2026-11-16. Covers ISM 10.3: every statutory or class certificate and every survey that comes round on a cycle is on record, shows how long is left, warns in time, keeps what was done to it, and keeps the scans.

## Decisions
- **Alerts are in-app only.** Dashboard card (Maintenance), attention strip, menu badge and colours at 90, 60 and 30 days. The platform has no mail or scheduler, and permits and drills work the same way. Email and escalation are later.
- **Scans are record files** (10 MB, 10 per certificate), as on incidents and permits: the bytes stay on the device that took them and the other side sees the record, marked "held on the vessel".
- **The office owns the certificate, either side adds to its history.** A vessel cannot create or change a certificate; it records endorsements, renewals, extensions, surveys done, notes and scans.
- Gated by the PMS module switch; no new module.

## Model
| Record | Owner | Notes |
|---|---|---|
| `ShipCertificate` | office | kind (statutory, class, survey), title, number, issued by, issued on, due date, survey window (days before and after), cycle in months, optional machinery item, active flag. Code `CRT`. |
| `CertificateEvent` | either side, append-only | type (issued, endorsed, extended, survey done, note, document), the day it happened, an optional new due date, reference, note. Files are children of the event. |
| `CertificateFile` | with its event | a `RecordFile`. A scan is added as a "document" event. |

The date that counts is the latest event that carries a date (latest day first, then latest recording), otherwise the certificate's own: a renewal or extension never edits the certificate.

## Rules (`CertificateRules`, pure)
- Last day: the date itself for a certificate; the end of the window (date plus days after) for a survey.
- States by days to the last day: more than 90 valid; 61 to 90 due in 90 days; 31 to 60 due in 60 days; 0 to 30 due in 30 days; past it overdue. A survey whose window has opened shows "window open" while more than 90 days remain.
- An issue or renewal needs the new date; an extension needs the date and the authority's reference; a survey done needs the next date; a note needs text. No event may be dated in the future.

## Screens
`/pms/certificates` (list with filters, add, standard set, CSV; vessel-allowed), `/pms/certificates/{id}` (details, record what happened, history, scans; vessel-allowed), `/pms/certificates/fleet` (office roll-up per vessel). The Maintenance dashboard card gains "Certificates due in 90 days" and "Certificates overdue"; the menu entry carries a badge of overdue plus due in 30 days.

## Later
Email or push alerts and escalation to the superintendent; a survey pack that bundles the files; continuous-survey credit; finding certificates from Ctrl+K; a conversation thread on each certificate.
