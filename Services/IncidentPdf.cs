using Chandlery.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Chandlery.Services;

/// <summary>An incident report as a PDF: the report, the people, the investigation, the actions and the history, as the person who asked for it may see them.</summary>
public static class IncidentPdf
{
    private const string Ink = "#0f172a";
    private const string Muted = "#475569";
    private const string Line = "#cbd5e1";

    static IncidentPdf()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static string FileName(IncidentDetail d) => new string($"incident-{d.Incident.Code}".Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-').ToArray()) + ".pdf";

    public static byte[] Build(IncidentDetail d, string companyName, RaMatrixView? words = null)
    {
        var i = d.Incident;
        string Level(int level, bool consequence) => level == 0 ? "Nothing" : words is null ? level.ToString() : $"{level} - {(consequence ? words.Consequence : words.Probability)[Math.Clamp(level, 1, RiskMatrix.Levels) - 1]}";
        return Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(32);
            page.DefaultTextStyle(t => t.FontSize(9.5f).FontColor(Ink));
            page.Footer().AlignCenter().Text(t => { t.Span($"{i.Code}   ·   printed {DateTime.UtcNow:dd MMM yyyy HH:mm} UTC   ·   page ").FontColor(Muted).FontSize(8); t.CurrentPageNumber().FontSize(8); t.Span(" of ").FontSize(8).FontColor(Muted); t.TotalPages().FontSize(8); });

            page.Content().Column(col =>
            {
                col.Spacing(10);
                col.Item().BorderBottom(2).BorderColor(Ink).PaddingBottom(6).Row(row =>
                {
                    row.RelativeItem().Column(c =>
                    {
                        c.Item().Text("INCIDENT REPORT").FontSize(16).ExtraBold();
                        c.Item().Text($"{companyName}   ·   {d.VesselName}").FontColor(Muted);
                    });
                    row.ConstantItem(170).AlignRight().Column(c =>
                    {
                        c.Item().AlignRight().Text(i.Code).Bold().FontSize(13);
                        c.Item().AlignRight().Text(IncidentStatusRules.Label(d.Status)).FontColor(Muted);
                        if (i.Band != "") c.Item().AlignRight().Text($"Band: {i.Band}{(i.IsUrgent ? "  (urgent)" : "")}").Bold();
                    });
                });

                col.Item().Row(row =>
                {
                    Meta(row, "KIND", IncidentLook.Kind(i.Kind));
                    Meta(row, "WHEN (SHIP'S TIME)", IncidentLook.When(i.OccurredAt));
                    Meta(row, "WHERE", i.Place == "" ? "-" : i.Place);
                    Meta(row, "SHIP'S STATUS", IncidentLook.ShipStatus(i.ShipStatus));
                });
                col.Item().Row(row =>
                {
                    Meta(row, "REPORTED BY", d.ReporterName);
                    Meta(row, "REPORTED", i.SubmittedAt is { } at ? IncidentLook.When(at) + (d.ReportedLate ? " (late)" : "") : "Not submitted");
                    Meta(row, "EQUIPMENT", i.EquipmentName == "" ? "-" : i.EquipmentName);
                    Meta(row, "RISK ASSESSMENT", i.RiskAssessmentRef == "" ? "None named" : i.RiskAssessmentRef);
                });

                Section(col, "What happened", i.Description);
                Section(col, "Done straight away", i.ImmediateAction);
                col.Item().Text($"Actually: {Level(i.ActualConsequence, true)}.   Could have been: {Level(i.PotentialConsequence, true)}.   Likely again: {Level(i.Likelihood, false)}.").FontColor(Muted).FontSize(8.5f);

                if (i.Persons.Count > 0)
                {
                    col.Item().Text("People").Bold().FontSize(11);
                    col.Item().Table(t =>
                    {
                        t.ColumnsDefinition(c => { c.RelativeColumn(2); c.RelativeColumn(1.2f); c.RelativeColumn(1.2f); c.RelativeColumn(3); });
                        foreach (var h in new[] { "Name", "Rank or company", "Part", "Injury" }) t.Cell().BorderBottom(1).BorderColor(Ink).PaddingVertical(2).Text(h).Bold().FontSize(8.5f);
                        foreach (var p in i.Persons)
                        {
                            t.Cell().BorderBottom(0.5f).BorderColor(Line).PaddingVertical(3).Text(p.Name);
                            t.Cell().BorderBottom(0.5f).BorderColor(Line).PaddingVertical(3).Text(string.Join(", ", new[] { p.Rank, p.Company }.Where(x => x != "")));
                            t.Cell().BorderBottom(0.5f).BorderColor(Line).PaddingVertical(3).Text(IncidentLook.Role(p.Role));
                            t.Cell().BorderBottom(0.5f).BorderColor(Line).PaddingVertical(3).Text(p.Role == IncidentPersonRole.Injured
                                ? string.Join(" · ", new[] { p.InjuryType, p.BodyPart, IncidentLook.Treatment(p.Treatment), p.DaysLost > 0 ? $"{p.DaysLost} days lost" : "", p.RestrictedDays > 0 ? $"{p.RestrictedDays} days light duty" : "" }.Where(x => x != "")) : "");
                        }
                    });
                }

                foreach (var s in i.Supplements.OrderBy(x => x.At)) col.Item().Text(t => { t.Span($"Addition, {IncidentLook.When(s.At)}, {s.ByName}: ").Bold(); t.Span(s.Text); });

                if (d.Investigation is { } inv)
                {
                    col.Item().PaddingTop(4).Text($"Investigation (version {inv.Version}, {(inv.Finished ? "finished" : "in progress")})").Bold().FontSize(11);
                    col.Item().Text($"{inv.ByName}{(inv.InvestigatedBy == "" ? "" : ", " + inv.InvestigatedBy)}, {IncidentLook.When(inv.At)}").FontColor(Muted).FontSize(8.5f);
                    Section(col, "Facts", inv.Facts);
                    Section(col, "Immediate cause", inv.ImmediateCause);
                    Section(col, "Root causes", inv.RootCauses);
                    Section(col, "Why it happened", inv.WhyChain);
                    Section(col, "Contributing factors", inv.ContributingFactors);
                    Section(col, "Risk assessment", IncidentLook.Assessment(inv.AssessmentFinding));
                    Section(col, "Lessons", inv.Lessons);
                }

                if (d.Actions.Count > 0)
                {
                    col.Item().PaddingTop(4).Text("Actions").Bold().FontSize(11);
                    foreach (var a in d.Actions) col.Item().Text($"{a.Code}   {a.Title}   ({a.Status}{(a.TargetDate is { } due ? $", due {due:dd MMM yyyy}" : "")})");
                }

                col.Item().PaddingTop(4).Text("History").Bold().FontSize(11);
                var lines = new List<(DateTime At, string Text)>();
                if (i.SubmittedAt is { } submitted) lines.Add((submitted, $"Reported by {d.ReporterName}"));
                foreach (var e in d.Events) lines.Add((e.At, $"{IncidentLook.Event(e.Kind)}, {e.ByName}{(e.Text == "" ? "" : ": " + e.Text)}"));
                foreach (var v in d.Investigations) lines.Add((v.At, $"Investigation {(v.Finished ? "finished" : "saved")} (version {v.Version}), {v.ByName}"));
                foreach (var l in lines.OrderBy(x => x.At)) col.Item().Text(t => { t.Span($"{IncidentLook.When(l.At)}   ").FontColor(Muted); t.Span(l.Text); });
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
