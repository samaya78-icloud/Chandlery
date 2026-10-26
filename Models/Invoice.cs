namespace Chandlery.Models;

public enum InvoiceKind
{
    /// <summary>Submitted by the supplier against a purchase order and its deliveries.</summary>
    PoInvoice,

    /// <summary>Entered by the company for a supplier, with no purchase order.</summary>
    SpotInvoice
}

public enum InvoiceStatus
{
    /// <summary>Sent in by the supplier; waiting for the company to verify it.</summary>
    Submitted,
    PendingApproval,
    Approved,
    Finalized,
    Rejected
}

public class Invoice
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public string InvoiceNumber { get; set; } = "";
    public InvoiceKind Kind { get; set; }
    public int? PurchaseOrderId { get; set; }
    public int SupplierId { get; set; }

    /// <summary>The number printed on the supplier's own invoice.</summary>
    public string SupplierInvoiceRef { get; set; } = "";
    public DateTime InvoiceDate { get; set; }
    public DateTime? DueDate { get; set; }
    public string Currency { get; set; } = "USD";

    /// <summary>Units of Currency per 1 USD when the invoice was verified or entered (null = USD or no rate was available).</summary>
    public decimal? UsdRate { get; set; }

    /// <summary>Goods or services value: the delivered value for a PO invoice, the sum of the lines for a spot invoice.</summary>
    public decimal InvoiceAmount { get; set; }
    public decimal AdditionalItemsCost { get; set; }
    public decimal AdditionalDeliveryCost { get; set; }

    /// <summary>A discount or credit taken off the total (entered as a positive number).</summary>
    public decimal DiscountOrCredit { get; set; }

    public int? TaxCodeId { get; set; }

    /// <summary>The rate of the tax code when it was chosen, so later changes to the code do not alter old invoices.</summary>
    public decimal TaxRatePct { get; set; }
    public int? BankAccountId { get; set; }

    // Spot invoices only
    public int? DepartmentId { get; set; }
    public int? VesselId { get; set; }
    public string CostCentre { get; set; } = "";

    public string Remarks { get; set; } = "";

    /// <summary>Required when the invoice total differs from the delivered value.</summary>
    public string VarianceReason { get; set; } = "";
    public string RejectionReason { get; set; } = "";
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Submitted;

    public string SubmittedByName { get; set; } = "";
    public DateTime SubmittedAt { get; set; }
    public string VerifiedByName { get; set; } = "";
    public DateTime? VerifiedAt { get; set; }
    public string FinalizedByName { get; set; } = "";
    public DateTime? FinalizedAt { get; set; }

    public List<InvoiceLine> Lines { get; set; } = new();

    /// <summary>The invoice total before tax. This is the amount the approval matrix is compared with.</summary>
    public decimal TotalExclTax => InvoiceAmount + AdditionalItemsCost + AdditionalDeliveryCost - DiscountOrCredit;
    public decimal TaxAmount => Math.Round(TotalExclTax * TaxRatePct / 100m, 2, MidpointRounding.AwayFromZero);
    public decimal TotalInclTax => TotalExclTax + TaxAmount;
}

/// <summary>A free-text line on a spot invoice.</summary>
public class InvoiceLine
{
    /// <summary>The catalog item (spot invoices); null on lines entered as free text before items were required.</summary>
    public int? ItemId { get; set; }
    public string Description { get; set; } = "";
    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal DiscountPct { get; set; }
}

/// <summary>One approver's pending/decided slot on an invoice (the same shape as PoApproval).</summary>
public class InvoiceApproval
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int InvoiceId { get; set; }
    public int Level { get; set; }
    public int ApproverUserId { get; set; }
    public ApprovalDecision Decision { get; set; } = ApprovalDecision.Pending;
    public int? DecidedByUserId { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string Comment { get; set; } = "";
}

/// <summary>A supporting document (the supplier's invoice PDF, a delivery note, and so on). The bytes live in the file store.</summary>
public class InvoiceDocument
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int InvoiceId { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string StoredKey { get; set; } = "";
    public string UploadedByName { get; set; } = "";
    public DateTime UploadedAt { get; set; }
}

/// <summary>Where a supplier wants to be paid. Owned by the supplier and readable by the companies it invoices.</summary>
public class SupplierBankAccount
{
    public int Id { get; set; }
    public int SupplierId { get; set; }
    public string BankName { get; set; } = "";
    public string AccountName { get; set; } = "";
    public string AccountNumber { get; set; } = "";
    public string SwiftCode { get; set; } = "";
    public string Currency { get; set; } = "USD";
}

/// <summary>A tax rate the company applies when verifying an invoice.</summary>
public class TaxCode
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public string Code { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal RatePct { get; set; }
}
