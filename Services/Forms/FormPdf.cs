using System.Text.Json.Nodes;
using Chandlery.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Chandlery.Services;

/// <summary>A form entry as a PDF: what was asked and answered, flagged answers marked, then the review. Drawn from the version the entry was filled on.</summary>
public static class FormPdf
{
    private const string Ink = "#0f172a";
    private const string Muted = "#475569";
    private const string Line = "#cbd5e1";
    private const string Warn = "#b45309";

    static FormPdf() { QuestPDF.Settings.License = LicenseType.Community; }

    public static string FileName(FormEntry e) => $"form-{new string(e.Code.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-').ToArray())}.pdf";

    public static byte[] Build(FormEntryDetail detail, string vessel, string company, IReadOnlyList<WorklistTask> actions)
    {
        var e = detail.Entry;
        var values = JsonNode.Parse(e.Values) as JsonObject ?? [];
        var check = detail.Definition is null ? null : FormValueRules.Check(detail.Definition, values, DateOnly.FromDateTime(DateTime.UtcNow), false);
        var flagged = check?.Exceptions.Select(x => x.FieldId).ToHashSet() ?? [];

        return Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(32);
            page.DefaultTextStyle(t => t.FontSize(9.5f).FontColor(Ink));
            page.Footer().AlignCenter().Text(t => { t.Span($"{e.Code}   ·   printed {DateTime.UtcNow:dd MMM yyyy HH:mm} UTC   ·   page ").FontColor(Muted).FontSize(8); t.CurrentPageNumber().FontSize(8); t.Span(" of ").FontSize(8).FontColor(Muted); t.TotalPages().FontSize(8); });
            page.Content().Column(col =>
            {
                col.Spacing(9);
                col.Item().BorderBottom(2).BorderColor(Ink).PaddingBottom(6).Row(row =>
                {
                    row.RelativeItem().Column(c =>
                    {
                        c.Item().Text($"{e.FormCode}  {e.FormTitle}".Trim()).FontSize(15).ExtraBold();
                        c.Item().Text($"{company}   ·   {vessel}").FontColor(Muted);
                    });
                    row.ConstantItem(170).AlignRight().Column(c =>
                    {
                        c.Item().AlignRight().Text(e.Code).Bold().FontSize(13);
                        c.Item().AlignRight().Text($"{detail.Status} · version {e.VersionNumber}").FontColor(Muted);
                    });
                });
                col.Item().Row(row =>
                {
                    Meta(row, "ABOUT THE DATE", e.FormDate.ToString("dd MMM yyyy"));
                    Meta(row, "FILLED IN BY", e.CreatedByName == "" ? "-" : e.CreatedByName + (e.FilledAshore ? " (ashore)" : ""));
                    Meta(row, "SUBMITTED", e.SubmittedAt is { } at ? at.ToString("dd MMM yyyy HH:mm") + " UTC" : "-");
                    Meta(row, "EXCEPTIONS", e.ExceptionCount.ToString());
                });

                if (detail.Definition is null) { col.Item().Text("The definition of this version cannot be read."); return; }
                foreach (var section in detail.Definition.Sections)
                {
                    if (check!.IsSectionHidden(section.Id)) continue;
                    col.Item().PaddingTop(3).Text(string.IsNullOrWhiteSpace(section.Title) ? section.Id : section.Title).Bold().FontSize(11);
                    if (section.Layout == "fields")
                    {
                        col.Item().Table(t =>
                        {
                            t.ColumnsDefinition(c => { c.RelativeColumn(2); c.RelativeColumn(3); });
                            foreach (var f in (section.Fields ?? []).Where(f => !check.IsHidden(f.Id)))
                            {
                                var text = FormExport.Text(values[f.Id], f.Type);
                                var unit = text != "" && f.Unit is { Length: > 0 } ? " " + f.Unit : "";
                                var bad = flagged.Contains(f.Id);
                                t.Cell().BorderBottom(0.5f).BorderColor(Line).PaddingVertical(3).PaddingRight(6).Text(f.Label).FontColor(Muted);
                                t.Cell().BorderBottom(0.5f).BorderColor(Line).PaddingVertical(3).Text(x => { x.Span((text == "" ? "-" : text) + unit).FontColor(bad ? Warn : Ink); if (bad) x.Span("   exception").FontColor(Warn).Bold().FontSize(8); });
                            }
                        });
                    }
                    else Grid(col, section, values);
                }

                if (check!.Exceptions.Count > 0)
                {
                    col.Item().PaddingTop(4).Text("Exceptions").Bold().FontSize(11);
                    foreach (var x in check.Exceptions) col.Item().Text("•  " + x.Message).FontColor(Warn);
                }
                if (detail.Reviews.Count > 0)
                {
                    col.Item().PaddingTop(4).Text("Review").Bold().FontSize(11);
                    foreach (var r in detail.Reviews)
                        col.Item().Text(t => { t.Span($"{r.At:dd MMM yyyy HH:mm}   ").FontColor(Muted); t.Span($"{r.ReviewerName}: {r.Decision.ToString().ToLowerInvariant()} (submission {r.ForSubmission})" + (r.Comment == "" ? "" : " - " + r.Comment)); });
                }
                if (actions.Count > 0)
                {
                    col.Item().PaddingTop(4).Text("Actions").Bold().FontSize(11);
                    foreach (var a in actions) col.Item().Text($"{a.Code}   {a.Title}   ({a.Status}{(a.TargetDate is { } due ? $", due {due:dd MMM yyyy}" : "")})");
                }
            });
        })).GeneratePdf();
    }

    private static void Grid(ColumnDescriptor col, FormSection section, JsonObject values)
    {
        var cells = FormDef.Cells(section).ToList();
        var rows = section.Rows?.Mode == "fixed"
            ? (section.Rows.Items ?? []).Select(r => (r.Id, r.Label)).ToList()
            : (values[section.Id] as JsonObject)?.Select(p => (p.Key, p.Key)).ToList() ?? [];
        if (rows.Count == 0) { col.Item().Text("-").FontColor(Muted); return; }
        string Answer(string rowId, string cellId, FormColumn c) => FormExport.Text(((values[section.Id] as JsonObject)?[rowId] as JsonObject)?[cellId], c.Type);

        if (cells.Count <= 8)
        {
            col.Item().Table(t =>
            {
                t.ColumnsDefinition(c => { c.RelativeColumn(2); foreach (var _ in cells) c.RelativeColumn(1); });
                t.Cell().BorderBottom(1).BorderColor(Ink).PaddingVertical(2).Text("");
                foreach (var (_, c) in cells) t.Cell().BorderBottom(1).BorderColor(Ink).PaddingVertical(2).Text(c.Label).Bold().FontSize(8);
                foreach (var (rowId, label) in rows)
                {
                    t.Cell().BorderBottom(0.5f).BorderColor(Line).PaddingVertical(2).Text(label).Bold().FontSize(8.5f);
                    foreach (var (cellId, c) in cells) t.Cell().BorderBottom(0.5f).BorderColor(Line).PaddingVertical(2).Text(Answer(rowId, cellId, c) is { Length: > 0 } a ? a : "-").FontSize(8.5f);
                }
            });
            return;
        }
        // too wide for a page: one block per row, "heading: answer" runs
        foreach (var (rowId, label) in rows)
            col.Item().BorderBottom(0.5f).BorderColor(Line).PaddingVertical(3).Text(t =>
            {
                t.Span(label + "   ").Bold();
                t.Span(string.Join("   ·   ", cells.Select(c => (c.Id, c.Column, A: Answer(rowId, c.Id, c.Column))).Where(x => x.A != "").Select(x => $"{x.Column.Label} {x.A}"))).FontSize(8.5f);
            });
    }

    private static void Meta(RowDescriptor row, string label, string value) => row.RelativeItem().Column(c =>
    {
        c.Item().Text(label).FontSize(7).FontColor(Muted).Bold();
        c.Item().Text(value).Bold();
    });
}
