using Chandlery.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Chandlery.Services;

/// <summary>Drill records, the vessel's drill log and meeting minutes as PDFs, for inspectors and the office: what was recorded, signed as it was.</summary>
public static class DrillPdf
{
    private const string Ink = "#0f172a";
    private const string Muted = "#475569";
    private const string Line = "#cbd5e1";

    static DrillPdf()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    private static string Safe(string text) => new string(text.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-').ToArray());

    public static string FileName(DrillDetail d) => $"drill-{Safe(d.Drill.Code)}.pdf";

    public static string FileName(MeetingDetail d) => $"meeting-{Safe(d.Meeting.Code)}.pdf";

    public static string LogFileName(string vessel, DateTime from, DateTime to) => $"drill-log-{Safe(vessel)}-{from:yyyyMMdd}-{to:yyyyMMdd}.pdf";

    private static void Frame(PageDescriptor page, string footer)
    {
        page.Size(PageSizes.A4);
        page.Margin(32);
        page.DefaultTextStyle(t => t.FontSize(9.5f).FontColor(Ink));
        page.Footer().AlignCenter().Text(t => { t.Span($"{footer}   ·   printed {DateTime.UtcNow:dd MMM yyyy HH:mm} UTC   ·   page ").FontColor(Muted).FontSize(8); t.CurrentPageNumber().FontSize(8); t.Span(" of ").FontSize(8).FontColor(Muted); t.TotalPages().FontSize(8); });
    }

    public static byte[] Build(DrillDetail d, string companyName)
    {
        var x = d.Drill;
        return Document.Create(container => container.Page(page =>
        {
            Frame(page, x.Code);
            page.Content().Column(col =>
            {
                col.Spacing(10);
                col.Item().BorderBottom(2).BorderColor(Ink).PaddingBottom(6).Row(row =>
                {
                    row.RelativeItem().Column(c =>
                    {
                        c.Item().Text("DRILL RECORD").FontSize(16).ExtraBold();
                        c.Item().Text($"{companyName}   ·   {d.VesselName}").FontColor(Muted);
                    });
                    row.ConstantItem(170).AlignRight().Column(c =>
                    {
                        c.Item().AlignRight().Text(x.Code).Bold().FontSize(13);
                        c.Item().AlignRight().Text(x.IsSigned ? $"Signed by {x.SignedByName}" : "Not signed by the Master").FontColor(Muted);
                    });
                });
                col.Item().Row(row =>
                {
                    Meta(row, "DRILL", x.TypeName);
                    Meta(row, "STARTED (SHIP'S TIME)", DrillLook.When(x.StartedAt));
                    Meta(row, "DURATION", d.Duration is null ? "-" : DrillLook.Duration(d.Duration));
                    Meta(row, "WHERE", x.Place == "" ? "-" : x.Place);
                });
                col.Item().Row(row =>
                {
                    Meta(row, "CONDUCTED BY", x.ConductedByName == "" ? "-" : x.ConductedByName);
                    Meta(row, "SHIP'S STATUS", IncidentLook.ShipStatus(x.ShipStatus));
                    Meta(row, "EQUIPMENT", x.EquipmentTested == "" ? "-" : x.EquipmentTested);
                    Meta(row, "SIGNED", x.SignedAt is { } at ? DrillLook.When(at) : "-");
                });
                Section(col, "Scenario", x.Scenario);
                Section(col, "Findings", x.Findings);
                Section(col, "Deficiencies", x.Deficiencies);
                Section(col, "Remarks", x.Remarks);

                col.Item().PaddingTop(4).Text("Who was there").Bold().FontSize(11);
                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(c => { c.RelativeColumn(3); c.RelativeColumn(2); c.RelativeColumn(2); c.RelativeColumn(2); });
                    foreach (var h in new[] { "Name", "Rank", "Attendance", "Reason" }) t.Cell().BorderBottom(1).BorderColor(Ink).PaddingVertical(2).Text(h).Bold().FontSize(8.5f);
                    foreach (var p in x.Participants.OrderBy(p => p.Attendance).ThenBy(p => p.Name))
                    {
                        t.Cell().BorderBottom(0.5f).BorderColor(Line).PaddingVertical(3).Text(p.Name);
                        t.Cell().BorderBottom(0.5f).BorderColor(Line).PaddingVertical(3).Text(p.Rank);
                        t.Cell().BorderBottom(0.5f).BorderColor(Line).PaddingVertical(3).Text(DrillLook.Attendance(p.Attendance));
                        t.Cell().BorderBottom(0.5f).BorderColor(Line).PaddingVertical(3).Text(DrillLook.Absence(p.Absence));
                    }
                });

                if (d.Actions.Count > 0)
                {
                    col.Item().PaddingTop(4).Text("Actions").Bold().FontSize(11);
                    foreach (var a in d.Actions) col.Item().Text($"{a.Code}   {a.Title}   ({a.Status}{(a.TargetDate is { } due ? $", due {due:dd MMM yyyy}" : "")})");
                }

                var notes = x.Notes.OrderBy(n => n.At).Select(n => (n.At, $"Note, {n.ByName}: {n.Text}")).Concat(d.Comments.Select(c => (c.At, $"Office comment, {c.ByName}: {c.Text}"))).OrderBy(l => l.Item1).ToList();
                if (notes.Count > 0)
                {
                    col.Item().PaddingTop(4).Text("Notes and comments").Bold().FontSize(11);
                    foreach (var n in notes) col.Item().Text(t => { t.Span($"{DrillLook.When(n.Item1)}   ").FontColor(Muted); t.Span(n.Item2); });
                }
            });
        })).GeneratePdf();
    }

    /// <summary>The vessel's drill log for a period: one line per drill, newest last, as an inspector reads it.</summary>
    public static byte[] BuildLog(IReadOnlyList<DrillRow> rows, string vesselName, string companyName, DateTime from, DateTime to)
    {
        var ordered = rows.OrderBy(r => r.Drill.StartedAt).ToList();
        return Document.Create(container => container.Page(page =>
        {
            Frame(page, $"{vesselName} drill log");
            page.Content().Column(col =>
            {
                col.Spacing(8);
                col.Item().BorderBottom(2).BorderColor(Ink).PaddingBottom(6).Column(c =>
                {
                    c.Item().Text("DRILL LOG").FontSize(16).ExtraBold();
                    c.Item().Text($"{companyName}   ·   {vesselName}   ·   {from:d MMM yyyy} to {to:d MMM yyyy}").FontColor(Muted);
                });
                if (ordered.Count == 0) { col.Item().Text("No drills were recorded in this period."); return; }
                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(c => { c.ConstantColumn(80); c.RelativeColumn(2.2f); c.RelativeColumn(1.4f); c.ConstantColumn(40); c.RelativeColumn(3); c.RelativeColumn(1.4f); });
                    foreach (var h in new[] { "Date", "Drill", "Conducted by", "Crew", "Findings and deficiencies", "Signed" }) t.Cell().BorderBottom(1).BorderColor(Ink).PaddingVertical(2).Text(h).Bold().FontSize(8.5f);
                    foreach (var r in ordered)
                    {
                        var found = string.Join(" · ", new[] { r.Drill.Findings, r.Drill.Deficiencies }.Where(s => s != ""));
                        t.Cell().BorderBottom(0.5f).BorderColor(Line).PaddingVertical(3).Text($"{r.Drill.StartedAt:dd MMM yyyy}").FontSize(8.5f);
                        t.Cell().BorderBottom(0.5f).BorderColor(Line).PaddingVertical(3).Text($"{r.Drill.TypeName}\n{r.Drill.Code}").FontSize(8.5f);
                        t.Cell().BorderBottom(0.5f).BorderColor(Line).PaddingVertical(3).Text(r.Drill.ConductedByName).FontSize(8.5f);
                        t.Cell().BorderBottom(0.5f).BorderColor(Line).PaddingVertical(3).Text(r.Present.ToString()).FontSize(8.5f);
                        t.Cell().BorderBottom(0.5f).BorderColor(Line).PaddingVertical(3).Text(found == "" ? "-" : found).FontSize(8.5f);
                        t.Cell().BorderBottom(0.5f).BorderColor(Line).PaddingVertical(3).Text(r.Drill.IsSigned ? $"{r.Drill.SignedByName}\n{r.Drill.SignedAt:dd MMM yyyy}" : "Not signed").FontSize(8.5f);
                    }
                });
            });
        })).GeneratePdf();
    }

    public static byte[] Build(MeetingDetail d, string companyName)
    {
        var m = d.Meeting;
        return Document.Create(container => container.Page(page =>
        {
            Frame(page, m.Code);
            page.Content().Column(col =>
            {
                col.Spacing(10);
                col.Item().BorderBottom(2).BorderColor(Ink).PaddingBottom(6).Row(row =>
                {
                    row.RelativeItem().Column(c =>
                    {
                        c.Item().Text("MINUTES OF MEETING").FontSize(16).ExtraBold();
                        c.Item().Text($"{companyName}   ·   {d.VesselName}").FontColor(Muted);
                    });
                    row.ConstantItem(170).AlignRight().Column(c =>
                    {
                        c.Item().AlignRight().Text(m.Code).Bold().FontSize(13);
                        c.Item().AlignRight().Text(m.IsSigned ? $"Signed by {m.SignedByName}" : "Not signed by the Master").FontColor(Muted);
                    });
                });
                col.Item().Row(row =>
                {
                    Meta(row, "MEETING", m.TypeName);
                    Meta(row, "HELD (SHIP'S TIME)", DrillLook.When(m.HeldAt));
                    Meta(row, "WHERE", m.Place == "" ? "-" : m.Place);
                });
                col.Item().Row(row =>
                {
                    Meta(row, "CHAIR", m.ChairName == "" ? "-" : m.ChairName);
                    Meta(row, "SECRETARY", m.SecretaryName == "" ? "-" : m.SecretaryName);
                    Meta(row, "SIGNED", m.SignedAt is { } at ? DrillLook.When(at) : "-");
                });
                Section(col, "Outcome", m.Summary);

                col.Item().PaddingTop(4).Text("Agenda and what was said").Bold().FontSize(11);
                foreach (var item in m.Items.OrderBy(i => i.Position))
                    col.Item().Column(c =>
                    {
                        c.Item().Text($"{item.Position}. {item.Title}{(item.Kind == MeetingItemKind.Suggestion ? $"  (suggestion{(item.RaisedBy == "" ? "" : " from " + item.RaisedBy)})" : "")}").Bold();
                        if (item.Notes != "") c.Item().Text(item.Notes);
                    });

                col.Item().PaddingTop(4).Text("Who was there").Bold().FontSize(11);
                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(c => { c.RelativeColumn(3); c.RelativeColumn(2); c.RelativeColumn(2); c.RelativeColumn(2); });
                    foreach (var h in new[] { "Name", "Rank", "Attendance", "Reason" }) t.Cell().BorderBottom(1).BorderColor(Ink).PaddingVertical(2).Text(h).Bold().FontSize(8.5f);
                    foreach (var a in m.Attendees.OrderBy(a => a.Attendance).ThenBy(a => a.Name))
                    {
                        t.Cell().BorderBottom(0.5f).BorderColor(Line).PaddingVertical(3).Text(a.Name);
                        t.Cell().BorderBottom(0.5f).BorderColor(Line).PaddingVertical(3).Text(a.Rank);
                        t.Cell().BorderBottom(0.5f).BorderColor(Line).PaddingVertical(3).Text(DrillLook.Attendance(a.Attendance));
                        t.Cell().BorderBottom(0.5f).BorderColor(Line).PaddingVertical(3).Text(DrillLook.Absence(a.Absence));
                    }
                });

                if (d.Carried.Count > 0)
                {
                    col.Item().PaddingTop(4).Text("Open from earlier meetings").Bold().FontSize(11);
                    foreach (var c in d.Carried) col.Item().Text($"{c.Task.Code}   {c.Task.Title}   (from {c.FromCode}, {c.FromHeldAt:dd MMM yyyy})");
                }
                if (d.Actions.Count > 0)
                {
                    col.Item().PaddingTop(4).Text("Actions from this meeting").Bold().FontSize(11);
                    foreach (var a in d.Actions) col.Item().Text($"{a.Code}   {a.Title}   ({a.Status}{(a.TargetDate is { } due ? $", due {due:dd MMM yyyy}" : "")})");
                }

                var notes = m.Notes.OrderBy(n => n.At).Select(n => (n.At, $"Note, {n.ByName}: {n.Text}")).Concat(d.Comments.Select(c => (c.At, $"Office comment, {c.ByName}: {c.Text}"))).OrderBy(l => l.Item1).ToList();
                if (notes.Count > 0)
                {
                    col.Item().PaddingTop(4).Text("Notes and comments").Bold().FontSize(11);
                    foreach (var n in notes) col.Item().Text(t => { t.Span($"{DrillLook.When(n.Item1)}   ").FontColor(Muted); t.Span(n.Item2); });
                }
            });
        })).GeneratePdf();
    }

    private static void Meta(RowDescriptor row, string label, string value) => row.RelativeItem().Column(c =>
    {
        c.Item().Text(label).FontSize(7).FontColor(Muted).Bold();
        c.Item().Text(value).Bold();
    });

    private static void Section(ColumnDescriptor col, string title, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        col.Item().Column(c =>
        {
            c.Item().Text(title).FontSize(8).FontColor(Muted).Bold();
            c.Item().Text(text);
        });
    }
}
