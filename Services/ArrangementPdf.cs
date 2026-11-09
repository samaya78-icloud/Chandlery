using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Chandlery.Services;

/// <summary>Renders the table of shipboard working arrangements (appendix 3) as a PDF to post onboard. Mirrors the on-screen table.</summary>
public static class ArrangementPdf
{
    private const string Ink = "#0f172a";
    private const string Muted = "#475569";
    private const string Line = "#cbd5e1";

    static ArrangementPdf()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static string Spans(string text) => string.IsNullOrWhiteSpace(text) ? "-" : string.Join("  ", RestSpans.Normalize(RestSpans.Parse(text)).Select(s => RestSpans.Format([s])));

    public static byte[] Build(ArrangementDocument doc)
    {
        var table = doc.Table;
        return Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4.Landscape());
            page.Margin(28);
            page.DefaultTextStyle(t => t.FontSize(8.5f).FontColor(Ink));

            page.Content().Column(col =>
            {
                col.Spacing(8);
                col.Item().BorderBottom(2).BorderColor(Ink).PaddingBottom(6).Row(row =>
                {
                    row.RelativeItem().Column(c =>
                    {
                        c.Item().Text("TABLE OF SHIPBOARD WORKING ARRANGEMENTS").FontSize(15).ExtraBold();
                        c.Item().Text("Hours of work and rest, ILO Maritime Labour Convention and STCW. Appendix 3 of the guidelines.").FontColor(Muted);
                    });
                    row.ConstantItem(150).AlignRight().Column(c =>
                    {
                        c.Item().AlignRight().Text($"Version {table.VersionNo}").Bold().FontSize(12);
                        c.Item().AlignRight().Text($"Latest update {table.CreatedAt:dd MMM yyyy}").FontColor(Muted);
                        c.Item().AlignRight().Text($"In force from {table.EffectiveFrom:dd MMM yyyy}").FontColor(Muted);
                    });
                });

                col.Item().Row(row =>
                {
                    Meta(row, "NAME OF SHIP", doc.VesselName);
                    Meta(row, "IMO NUMBER", doc.ImoNumber ?? "-");
                    Meta(row, "FLAG STATE", string.IsNullOrWhiteSpace(doc.FlagState) ? "-" : doc.FlagState);
                    Meta(row, "OPERATOR", doc.CompanyName);
                    Meta(row, "LAW OR AGREEMENT", string.IsNullOrWhiteSpace(table.LawReference) ? "-" : table.LawReference);
                });
                if (!string.IsNullOrWhiteSpace(table.LimitsText))
                    col.Item().Text(t => { t.Span("Limits: ").Bold(); t.Span(table.LimitsText); });

                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(c =>
                    {
                        c.RelativeColumn(2.2f);
                        c.RelativeColumn(2.4f); c.RelativeColumn(2.4f); c.ConstantColumn(52);
                        c.RelativeColumn(2.4f); c.RelativeColumn(2.4f); c.ConstantColumn(52);
                        c.RelativeColumn(2.4f);
                    });
                    t.Header(h =>
                    {
                        Head(h.Cell().RowSpan(2), "Position");
                        Head(h.Cell().ColumnSpan(3), "At sea");
                        Head(h.Cell().ColumnSpan(3), "In port");
                        Head(h.Cell().RowSpan(2), "Comments");
                        foreach (var label in new[] { "Watchkeeping", "Other duties", "Work / rest", "Watchkeeping", "Other duties", "Work / rest" }) Head(h.Cell(), label);
                    });
                    foreach (var r in doc.Rows)
                    {
                        Body(t.Cell(), r.Row.Position, bold: true);
                        Body(t.Cell(), Spans(r.Row.SeaWatchkeeping));
                        Body(t.Cell(), Spans(r.Row.SeaOther));
                        Body(t.Cell(), $"{RestRules.Hm(r.Sea.WorkMinutes)} / {RestRules.Hm(r.Sea.RestMinutes)}", problem: !r.Sea.Ok);
                        Body(t.Cell(), Spans(r.Row.PortWatchkeeping));
                        Body(t.Cell(), Spans(r.Row.PortOther));
                        Body(t.Cell(), $"{RestRules.Hm(r.Port.WorkMinutes)} / {RestRules.Hm(r.Port.RestMinutes)}", problem: !r.Port.Ok);
                        Body(t.Cell(), r.Row.Comments);
                    }
                });

                if (!string.IsNullOrWhiteSpace(table.OtherRequirements))
                    col.Item().Text(t => { t.Span("Other requirements: ").Bold(); t.Span(table.OtherRequirements); });

                col.Item().PaddingTop(10).Row(row =>
                {
                    row.RelativeItem().Column(c =>
                    {
                        c.Item().Text("MASTER'S SIGNATURE").FontSize(7.5f).Bold().FontColor(Muted);
                        if (doc.Signature is { } s)
                        {
                            c.Item().Text(s.SignedByName).Bold();
                            c.Item().Text($"Signed onboard {s.SignedAt:dd MMM yyyy HH:mm} UTC").FontColor(Muted);
                        }
                        else c.Item().PaddingTop(14).BorderBottom(1).BorderColor(Ink).Text("Not signed yet").FontColor(Muted);
                    });
                    row.RelativeItem().AlignRight().Text($"Prepared by {table.CreatedByName}, {table.CreatedAt:dd MMM yyyy}").FontColor(Muted);
                });
            });
        })).GeneratePdf();
    }

    private static void Meta(RowDescriptor row, string label, string value) => row.RelativeItem().Column(c =>
    {
        c.Item().Text(label).FontSize(7).Bold().FontColor(Muted);
        c.Item().Text(value).Bold();
    });

    private static void Head(IContainer cell, string text)
        => cell.Background("#e2e8f0").Border(0.5f).BorderColor(Line).Padding(3).AlignCenter().Text(text).Bold().FontSize(8);

    private static void Body(IContainer cell, string text, bool bold = false, bool problem = false)
    {
        var t = cell.Border(0.5f).BorderColor(Line).Padding(3).Text(text);
        if (bold) t.Bold();
        if (problem) t.Bold().FontColor("#b91c1c");
    }
}
