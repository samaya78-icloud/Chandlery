using Chandlery.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Chandlery.Services;

/// <summary>A permit to work as a printable page with signature lines, to post at the work site. What is printed is what was recorded, as it stood when printed.</summary>
public static class PermitPdf
{
    private const string Ink = "#0f172a";
    private const string Muted = "#475569";
    private const string Line = "#cbd5e1";

    static PermitPdf()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static string FileName(PermitDetail d) => $"permit-{new string(d.Permit.Code.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-').ToArray())}.pdf";

    public static byte[] Build(PermitDetail d, string companyName)
    {
        var p = d.Permit;
        var (state, _) = PermitLook.State(d.State);
        return Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(32);
            page.DefaultTextStyle(t => t.FontSize(9.5f).FontColor(Ink));
            page.Footer().AlignCenter().Text(t => { t.Span($"{p.Code}   ·   printed {DateTime.UtcNow:dd MMM yyyy HH:mm} UTC   ·   page ").FontColor(Muted).FontSize(8); t.CurrentPageNumber().FontSize(8); t.Span(" of ").FontSize(8).FontColor(Muted); t.TotalPages().FontSize(8); });
            page.Content().Column(col =>
            {
                col.Spacing(10);
                col.Item().BorderBottom(2).BorderColor(Ink).PaddingBottom(6).Row(row =>
                {
                    row.RelativeItem().Column(c =>
                    {
                        c.Item().Text("PERMIT TO WORK").FontSize(16).ExtraBold();
                        c.Item().Text($"{companyName}   ·   {d.VesselName}").FontColor(Muted);
                    });
                    row.ConstantItem(190).AlignRight().Column(c =>
                    {
                        c.Item().AlignRight().Text(p.Code).Bold().FontSize(13);
                        c.Item().AlignRight().Text(state).FontColor(Muted);
                    });
                });
                col.Item().Row(row =>
                {
                    Meta(row, "KIND", $"{p.TypeName} (checklist v{p.ChecklistVersion})");
                    Meta(row, "WHERE", p.Location);
                    Meta(row, "VALID", PermitLook.Window(p, d.Until));
                });
                col.Item().Row(row =>
                {
                    Meta(row, "PERSON IN CHARGE", p.RequesterName);
                    Meta(row, "ISSUED BY", p.IssuerName == "" ? "-" : p.IssuerName);
                    Meta(row, "ATTENDANT", p.AttendantName == "" ? "-" : p.AttendantName);
                });
                Section(col, "The work", p.Description);
                Section(col, "Doing the work", string.Join(", ", p.Persons.Select(x => x.Name)));
                Section(col, "Isolation points and equipment", p.Isolation);
                Section(col, "Conditions", p.Conditions);

                col.Item().PaddingTop(4).Text("Checklist").Bold().FontSize(11);
                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(c => { c.RelativeColumn(6); c.RelativeColumn(1.4f); c.RelativeColumn(3); });
                    t.Header(h => { Head(h.Cell(), "Item"); Head(h.Cell(), "Answer"); Head(h.Cell(), "Note"); });
                    foreach (var a in p.Answers.OrderBy(x => x.Position))
                    {
                        Cell(t.Cell(), $"{(a.Section == "" ? "" : a.Section + ": ")}{a.Text}");
                        Cell(t.Cell(), PermitLook.Answer(a.Answer));
                        Cell(t.Cell(), a.Note);
                    }
                });

                if (p.Readings.Count > 0)
                {
                    col.Item().PaddingTop(4).Text("Gas readings").Bold().FontSize(11);
                    col.Item().Table(t =>
                    {
                        t.ColumnsDefinition(c => { c.RelativeColumn(2); c.RelativeColumn(1); c.RelativeColumn(1); c.RelativeColumn(1); c.RelativeColumn(1); c.RelativeColumn(3); c.RelativeColumn(2); });
                        t.Header(h => { Head(h.Cell(), "Time"); Head(h.Cell(), "O2 %"); Head(h.Cell(), "LEL %"); Head(h.Cell(), "H2S"); Head(h.Cell(), "CO"); Head(h.Cell(), "Instrument"); Head(h.Cell(), "Result"); });
                        foreach (var r in p.Readings.OrderBy(x => x.At))
                        {
                            Cell(t.Cell(), PermitLook.When(r.At)); Cell(t.Cell(), r.Oxygen?.ToString("0.0") ?? "-"); Cell(t.Cell(), r.FlammablePercentLel?.ToString("0.#") ?? "-");
                            Cell(t.Cell(), r.H2sPpm?.ToString("0.#") ?? "-"); Cell(t.Cell(), r.CoPpm?.ToString("0.#") ?? "-"); Cell(t.Cell(), r.Instrument); Cell(t.Cell(), r.WithinLimits ? "In limits" : "OUTSIDE LIMITS");
                        }
                    });
                }

                if (p.Entries.Count > 0)
                {
                    col.Item().PaddingTop(4).Text("Entry log").Bold().FontSize(11);
                    col.Item().Table(t =>
                    {
                        t.ColumnsDefinition(c => { c.RelativeColumn(3); c.RelativeColumn(2); c.RelativeColumn(2); c.RelativeColumn(3); });
                        t.Header(h => { Head(h.Cell(), "Name"); Head(h.Cell(), "In"); Head(h.Cell(), "Out"); Head(h.Cell(), "Attendant"); });
                        foreach (var e in p.Entries.OrderBy(x => x.In)) { Cell(t.Cell(), e.PersonName); Cell(t.Cell(), PermitLook.When(e.In)); Cell(t.Cell(), e.Out is { } o ? PermitLook.When(o) : "inside"); Cell(t.Cell(), e.AttendantName); }
                    });
                }

                col.Item().PaddingTop(4).Text("What happened").Bold().FontSize(11);
                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(c => { c.RelativeColumn(2); c.RelativeColumn(3); c.RelativeColumn(3); c.RelativeColumn(5); });
                    t.Header(h => { Head(h.Cell(), "Time"); Head(h.Cell(), "Event"); Head(h.Cell(), "By"); Head(h.Cell(), "Note"); });
                    foreach (var e in p.Events.OrderBy(x => x.At).ThenBy(x => x.Id)) { Cell(t.Cell(), PermitLook.When(e.At)); Cell(t.Cell(), PermitLook.Event(e.Kind)); Cell(t.Cell(), e.ByName); Cell(t.Cell(), e.Note); }
                });

                col.Item().PaddingTop(14).Row(row =>
                {
                    Sign(row, "Issuer", p.IssuerName);
                    Sign(row, "Person in charge", p.RequesterName);
                    Sign(row, "Master", "");
                });
            });
        })).GeneratePdf();
    }

    private static void Meta(RowDescriptor row, string label, string value) => row.RelativeItem().Column(c =>
    {
        c.Item().Text(label).FontSize(7.5f).FontColor(Muted).SemiBold();
        c.Item().Text(string.IsNullOrWhiteSpace(value) ? "-" : value);
    });

    private static void Section(ColumnDescriptor col, string title, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        col.Item().Column(c =>
        {
            c.Item().Text(title).FontSize(7.5f).FontColor(Muted).SemiBold();
            c.Item().Text(text);
        });
    }

    private static void Head(IContainer cell, string text) => cell.BorderBottom(1).BorderColor(Ink).PaddingVertical(2).Text(text).FontSize(8).SemiBold();

    private static void Cell(IContainer cell, string text) => cell.BorderBottom(0.5f).BorderColor(Line).PaddingVertical(2).PaddingRight(4).Text(text ?? "").FontSize(8.5f);

    private static void Sign(RowDescriptor row, string role, string name) => row.RelativeItem().PaddingRight(12).Column(c =>
    {
        c.Item().Height(34).BorderBottom(1).BorderColor(Ink);
        c.Item().PaddingTop(2).Text($"{role}{(name == "" ? "" : ": " + name)}").FontSize(8).FontColor(Muted);
    });
}
