using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Chandlery.Services;

/// <summary>Renders the formal purchase order as a PDF file. Mirrors the on-screen document (PoDocumentView).</summary>
public static class PoPdf
{
    private const string Ink = "#0f172a";
    private const string Muted = "#475569";
    private const string Faint = "#64748b";
    private const string Line = "#e2e8f0";

    static PoPdf()
    {
        // QuestPDF Community licence: free for organisations under USD 1M yearly revenue; a paid licence is needed above that.
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static byte[] Build(PoDocument doc)
    {
        var po = doc.Po;
        var approved = po.Status == Models.PoStatus.Approved;
        var showPrices = !po.IsDummy;

        return Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(36);
            page.DefaultTextStyle(t => t.FontSize(9).FontColor(Ink));

            page.Content().Column(col =>
            {
                col.Spacing(10);

                // Header
                col.Item().BorderBottom(2).BorderColor(Ink).PaddingBottom(8).Row(row =>
                {
                    row.RelativeItem().Column(c =>
                    {
                        c.Item().Text(doc.Company.Name).FontSize(17).ExtraBold();
                        if (!string.IsNullOrWhiteSpace(doc.Company.Address))
                            foreach (var line in doc.Company.Address.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                                c.Item().Text(line).FontColor(Muted);
                        if (!string.IsNullOrWhiteSpace(doc.Company.ContactEmail))
                            c.Item().Text(doc.Company.ContactEmail).FontColor(Muted);
                        if (!string.IsNullOrWhiteSpace(doc.Company.RegistrationNumber))
                            c.Item().Text($"Reg. no. {doc.Company.RegistrationNumber}").FontColor(Muted);
                    });
                    row.ConstantItem(170).AlignRight().Column(c =>
                    {
                        c.Item().AlignRight().Text("PURCHASE ORDER").FontSize(7.5f).Bold().FontColor(Faint).LetterSpacing(0.15f);
                        c.Item().AlignRight().Text(po.PoNumber).FontSize(22).ExtraBold();
                        var stampColor = approved ? "#047857" : "#b45309";
                        var stamp = approved ? "APPROVED" : po.Status == Models.PoStatus.Rejected ? "REJECTED, NOT VALID" : "DRAFT, AWAITING APPROVAL";
                        c.Item().PaddingTop(4).AlignRight().Border(1).BorderColor(stampColor).PaddingHorizontal(8).PaddingVertical(1.5f)
                            .Text(stamp).FontSize(7.5f).Bold().FontColor(stampColor);
                    });
                });

                // Meta strip
                col.Item().BorderBottom(1).BorderColor(Line).PaddingBottom(8).Row(row =>
                {
                    Meta(row, "ISSUE DATE", po.CreatedDate.ToString("dd MMM yyyy"));
                    Meta(row, "CURRENCY", po.Currency);
                    Meta(row, "ORDER BASIS", po.IsSpot ? "Direct order" : $"Quotation, RFQ {doc.RfqNumber}");
                    Meta(row, "YOUR REFERENCE", string.IsNullOrWhiteSpace(po.SupplierRef) ? "-" : po.SupplierRef);
                });

                // Parties
                col.Item().Row(row =>
                {
                    row.RelativeItem().Background("#f8fafc").Border(1).BorderColor(Line).Padding(8).Column(c =>
                    {
                        c.Item().Text("SUPPLIER").FontSize(7.5f).Bold().FontColor(Faint);
                        c.Item().Text(doc.Supplier.Name).Bold();
                        if (!string.IsNullOrWhiteSpace(doc.Supplier.ContactEmail))
                            c.Item().Text(doc.Supplier.ContactEmail).FontColor(Muted);
                    });
                    row.ConstantItem(12);
                    row.RelativeItem().Background("#f8fafc").Border(1).BorderColor(Line).Padding(8).Column(c =>
                    {
                        c.Item().Text("SUPPLIED FOR").FontSize(7.5f).Bold().FontColor(Faint);
                        c.Item().Text((string.IsNullOrWhiteSpace(doc.Vessel) ? "-" : doc.Vessel) + (string.IsNullOrWhiteSpace(doc.Imo) ? "" : $" (IMO {doc.Imo})")).Bold();
                        if (doc.Port is not null) c.Item().Text($"Delivery port: {doc.Port}").FontColor(Muted);
                        if (doc.Department is not null) c.Item().Text($"Department: {doc.Department}").FontColor(Muted);
                        if (doc.RequisitionNumber is not null) c.Item().Text($"Requisition: {doc.RequisitionNumber}").FontColor(Muted);
                    });
                });

                if (po.IsDummy)
                    col.Item().Background("#fffbeb").Border(1).BorderColor("#fde68a").Padding(6)
                        .Text("This is a record-only order. It records goods supplied under an arrangement made outside this system, so no prices are shown.")
                        .FontColor("#92400e");

                // Lines
                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.ConstantColumn(22);
                        c.ConstantColumn(58);
                        c.RelativeColumn();
                        c.ConstantColumn(40);
                        c.ConstantColumn(34);
                        if (showPrices)
                        {
                            c.ConstantColumn(58);
                            c.ConstantColumn(34);
                            c.ConstantColumn(64);
                        }
                    });

                    table.Header(h =>
                    {
                        h.Cell().Element(Head).AlignCenter().Text("#");
                        h.Cell().Element(Head).Text("CODE");
                        h.Cell().Element(Head).Text("DESCRIPTION");
                        h.Cell().Element(Head).AlignRight().Text("QTY");
                        h.Cell().Element(Head).Text("UOM");
                        if (showPrices)
                        {
                            h.Cell().Element(Head).AlignRight().Text("UNIT PRICE");
                            h.Cell().Element(Head).AlignRight().Text("DISC.");
                            h.Cell().Element(Head).AlignRight().Text("AMOUNT");
                        }
                    });

                    foreach (var l in doc.Lines)
                    {
                        table.Cell().Element(Body).AlignCenter().Text(l.Number.ToString()).FontColor(Faint);
                        table.Cell().Element(Body).Text(l.Code);
                        table.Cell().Element(Body).Text(l.Name);
                        table.Cell().Element(Body).AlignRight().Text(Models.Money.Qty(l.Quantity));
                        table.Cell().Element(Body).Text(l.Uom);
                        if (showPrices)
                        {
                            table.Cell().Element(Body).AlignRight().Text(l.UnitPrice.ToString("N2"));
                            table.Cell().Element(Body).AlignRight().Text(l.DiscountPct > 0 ? l.DiscountPct.ToString("0.##") + "%" : "");
                            table.Cell().Element(Body).AlignRight().Text(l.Amount.ToString("N2"));
                        }
                    }
                });

                // Totals
                if (showPrices)
                {
                    col.Item().AlignRight().Width(200).Column(c =>
                    {
                        TotalRow(c, "Subtotal", Models.Money.Format(po.Subtotal, po.Currency));
                        if (po.DiscountPct > 0)
                            TotalRow(c, $"Discount {po.DiscountPct:0.##}%", "-" + Models.Money.Format(Models.Money.HeaderDiscount(po.Subtotal, po.DiscountPct), po.Currency));
                        if (po.AdditionalCharges > 0)
                            TotalRow(c, string.IsNullOrWhiteSpace(po.AdditionalChargeDescription) ? "Additional charges" : po.AdditionalChargeDescription,
                                Models.Money.Format(po.AdditionalCharges, po.Currency));
                        c.Item().PaddingTop(3).BorderTop(2).BorderColor(Ink).PaddingTop(4).Row(r =>
                        {
                            r.RelativeItem().Text("Total").FontSize(11).ExtraBold();
                            r.AutoItem().Text(Models.Money.Format(po.Total, po.Currency)).FontSize(11).ExtraBold();
                        });
                    });
                }

                // Remarks
                if (!string.IsNullOrWhiteSpace(po.Remarks))
                    col.Item().ShowEntire().Column(c =>
                    {
                        c.Item().Text("INSTRUCTIONS AND REMARKS").FontSize(7.5f).Bold().FontColor(Faint);
                        c.Item().Text(po.Remarks);
                    });

                // Terms
                col.Item().ShowEntire().Column(c =>
                {
                    c.Spacing(2);
                    c.Item().Text("TERMS OF PURCHASE").FontSize(7.5f).Bold().FontColor(Faint);
                    var terms = Terms(po.PoNumber);
                    for (var i = 0; i < terms.Length; i++)
                        c.Item().Text(t =>
                        {
                            t.DefaultTextStyle(s => s.FontSize(8).FontColor("#334155"));
                            t.Span($"{i + 1}. ");
                            t.Span(terms[i]);
                        });
                });

                // Approval and acknowledgement
                col.Item().ShowEntire().BorderTop(1).BorderColor("#cbd5e1").PaddingTop(8).Row(row =>
                {
                    row.RelativeItem().Column(c =>
                    {
                        c.Item().Text("APPROVED ELECTRONICALLY").FontSize(7.5f).Bold().FontColor(Faint);
                        if (doc.Approvers.Count == 0)
                            c.Item().Text(approved ? "No approval was required for this order." : "Not yet approved.").FontColor(Muted);
                        foreach (var a in doc.Approvers)
                            c.Item().Text(t =>
                            {
                                t.Span(a.Name).Bold();
                                t.Span($"  {a.LevelName}" + (a.At.HasValue ? $" · {a.At.Value:dd MMM yyyy HH:mm} UTC" : "")).FontColor(Muted);
                            });
                    });
                    row.ConstantItem(12);
                    row.RelativeItem().Column(c =>
                    {
                        c.Item().Text("SUPPLIER ACKNOWLEDGEMENT").FontSize(7.5f).Bold().FontColor(Faint);
                        c.Item().Text($"Please confirm receipt by replying to {(string.IsNullOrWhiteSpace(doc.Company.ContactEmail) ? "the buyer" : doc.Company.ContactEmail)}, quoting {po.PoNumber}.").FontColor(Muted);
                    });
                });
            });

            page.Footer().BorderTop(1).BorderColor(Line).PaddingTop(4).Row(row =>
            {
                row.RelativeItem().Text($"{doc.Company.Name} · {po.PoNumber} · generated by Chandlery from the approved record").FontSize(7.5f).FontColor("#94a3b8");
                row.AutoItem().Text(t =>
                {
                    t.DefaultTextStyle(s => s.FontSize(7.5f).FontColor("#94a3b8"));
                    t.Span("Page ");
                    t.CurrentPageNumber();
                    t.Span(" of ");
                    t.TotalPages();
                });
            });
        })).GeneratePdf();
    }

    /// <summary>Generic purchase terms (placeholder wording, to be replaced by terms your legal team has approved).</summary>
    public static string[] Terms(string poNumber) => new[]
    {
        $"Quote {poNumber} on every delivery note, invoice and message about this order.",
        "Prices are firm and in the currency shown. No extra charges are accepted unless they are listed on this order.",
        "Goods must match the description, quantity and specification above. They are checked on receipt, and anything that does not conform may be rejected and returned at the supplier's cost.",
        "Deliver to the port shown and tell us at once if any item will be late or is unavailable. Part deliveries need our prior agreement and each needs its own delivery note.",
        "Invoices must reference this order, match the quantities actually delivered and the prices above, and be raised in the order currency.",
        "The supplier must comply with all applicable laws, including those on bribery, sanctions, health, safety and the environment, and with the rules that apply to supplying vessels.",
        "We may cancel this order in whole or in part if these terms are not met.",
    };

    private static void Meta(RowDescriptor row, string label, string value) =>
        row.RelativeItem().Column(c =>
        {
            c.Item().Text(label).FontSize(7).Bold().FontColor(Faint);
            c.Item().Text(value);
        });

    private static void TotalRow(ColumnDescriptor c, string label, string value) =>
        c.Item().PaddingVertical(1.5f).Row(r =>
        {
            r.RelativeItem().Text(label).FontColor("#334155");
            r.AutoItem().Text(value).FontColor("#334155");
        });

    private static IContainer Head(IContainer c) =>
        c.Background("#f1f5f9").BorderBottom(1.2f).BorderColor(Ink).PaddingVertical(4).PaddingHorizontal(3)
            .DefaultTextStyle(t => t.FontSize(7).Bold().FontColor(Muted));

    private static IContainer Body(IContainer c) =>
        c.BorderBottom(0.6f).BorderColor(Line).PaddingVertical(3).PaddingHorizontal(3);
}
