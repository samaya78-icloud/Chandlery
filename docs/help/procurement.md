---
module: procurement
title: Procurement
questions:
- How do I raise a requisition?
- What happens after a requisition is submitted?
- How do I compare supplier quotes on an RFQ?
- How do I record a delivery or an invoice?
- Why can't the vessel buy this directly?
---
Procurement takes a need from requisition to purchase order, delivery and invoice. It works on the vessel and in the office.

## Getting started
Office, once: set up [Suppliers](/company/suppliers), the [Catalog](/company/items) and [Departments](/company/departments), then the [Approval Matrix](/company/approval-matrix) and the [Vessel policy](/company/vessel-policy) for what a vessel may buy directly.
Then, for the first purchase:
1. Raise a [New requisition](/company/requisitions/new) for the vessel, pick items from the catalog, and submit it.
2. The office reviews it and creates an [RFQ](/company/rfqs), or raises a [spot PO](/company/purchase-orders/new-spot) for a one-off buy.
3. Compare the quotes, award, and approve the PO. The vessel then records the delivery and the supplier is invoiced.

## Pages
- [Requisitions](/company/requisitions): create with [New requisition](/company/requisitions/new). Vessels also use [Orders](/vessel/orders) and [Port calls](/vessel/port-calls).
- [RFQs](/company/rfqs): requests for quotation sent to suppliers; quotes come back through the supplier portal or are typed in from email or phone. Vessels see [RFQs](/vessel/rfqs).
- [Purchase Orders](/company/purchase-orders): created from an RFQ or as a spot order ([New spot PO](/company/purchase-orders/new-spot)). A PO has a printable document.
- [Invoices](/company/invoices): matched to a PO, or a spot invoice ([New spot invoice](/company/invoices/new-spot)). Office only.
- [Onboard orders](/company/onboard-orders): orders raised by vessels directly, for the office to follow.
- [Catalog](/company/items) and [Suppliers](/company/suppliers) hold the items and suppliers you pick from.

## How it flows
1. Requisition: draft, then submit. The office reviews it.
2. The office creates an RFQ, suppliers quote, the office compares quotes and awards.
3. A PO is raised and goes through approval by amount ([Approval Matrix](/company/approval-matrix)), then is sent to the supplier.
4. The vessel records the delivery (partial receipts and discrepancies allowed).
5. The supplier invoices; the office matches it to the PO and approves it.
Status always flows back as a timeline: saved on ship, sent to office, received, approved, ordered, delivered.

## Direct purchasing on a vessel
What a vessel may requisition or buy directly, and the Master's approval limit, come from the policy for that vessel's group, department and category ([Vessel policy](/company/vessel-policy)). Above the limit, or outside the allowed categories, the office approves or buys.

## Good to know
Amounts are compared in USD using [Exchange Rates](/company/exchange-rates). Every document has a conversation thread for questions.

## Linking procurement to other records
- From a PMS job, "Order the spares" opens a requisition with the spares kept for that machinery already filled in (marked urgent for a critical job).
- From a Worklist task, use "Link" to attach a requisition or order to the task.
- A project's actual cost comes from the purchase orders and invoices tagged to the project.
