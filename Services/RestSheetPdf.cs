using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Chandlery.Services;

/// <summary>The names on the head of a printed record: the company, the vessel and the rules the hours were checked against.</summary>
public sealed record RestHeader(string Company, string Vessel, string? Imo, string FlagState, string RuleSet);

/// <summary>Everything the printed record of hours of work or rest (appendix 4) shows for one seafarer and one month.</summary>
public sealed record RestSheetDocument(RestHeader Head, RestHoursService.RestMonth Month, RestMonthReview Review);

/// <summary>Renders the record of hours of work or rest (appendix 4) as a PDF: the seafarer's copy, and the one the office keeps. Mirrors the month sheet on screen.</summary>
public static class RestSheetPdf
{
    private const string Ink = "#0f172a";
    private const string Muted = "#475569";
    private const string Line = "#cbd5e1";

    static RestSheetPdf()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static byte[] Build(RestSheetDocument doc)
    {
        var month = doc.Month;
        var limits = month.Limits;
        return Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4.Landscape());
            page.Margin(24);
            page.DefaultTextStyle(t => t.FontSize(7).FontColor(Ink));

            page.Content().Column(col =>
            {
                col.Spacing(6);
                col.Item().BorderBottom(2).BorderColor(Ink).PaddingBottom(4).Row(row =>
                {
                    row.RelativeItem().Text("RECORD OF HOURS OF WORK OR REST").FontSize(14).ExtraBold();
                    row.ConstantItem(220).AlignRight().Text($"{month.First:MMMM yyyy}").FontSize(12).Bold();
                });
                col.Item().Row(row =>
                {
                    Meta(row, "NAME OF SEAFARER", month.CrewName);
                    Meta(row, "POSITION / RANK", month.Rank);
                    Meta(row, "NAME OF SHIP", doc.Head.Vessel);
                    Meta(row, "IMO NUMBER", doc.Head.Imo ?? "-");
                    Meta(row, "FLAG STATE", string.IsNullOrWhiteSpace(doc.Head.FlagState) ? "-" : doc.Head.FlagState);
                    Meta(row, "RULES APPLIED", doc.Head.RuleSet);
                });

                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(c =>
                    {
                        c.ConstantColumn(30);
                        for (var h = 0; h < 24; h++) c.ConstantColumn(14);
                        c.ConstantColumn(30); c.ConstantColumn(40); c.ConstantColumn(40);
                        c.RelativeColumn();
                    });
                    t.Header(h =>
                    {
                        Head(h.Cell(), "Date");
                        for (var hour = 0; hour < 24; hour++) Head(h.Cell(), hour.ToString("00"));
                        Head(h.Cell(), "Rest");
                        Head(h.Cell(), "24 h");
                        Head(h.Cell(), "7 d");
                        Head(h.Cell(), "Result");
                    });
                    foreach (var d in month.Days)
                    {
                        var work = d.Spans is null ? [] : RestSpans.Parse(d.Spans);
                        Body(t.Cell(), d.Date.ToString("ddd d"), d.Date.DayOfWeek == DayOfWeek.Sunday);
                        for (var hour = 0; hour < 24; hour++)
                            t.Cell().Border(0.25f).BorderColor(Line).Background(Shade(d.Spans is null ? -1 : WorkIn(work, hour))).Height(11).Text("");
                        Body(t.Cell(), d.Spans is null ? "" : RestRules.Hm(d.Result.RestMinutes));
                        Body(t.Cell(), d.Result.Worst24 is { } a ? RestRules.Hm(a) : "");
                        Body(t.Cell(), d.Result.Worst7 is { } b ? RestRules.Hm(b) : "");
                        var result = d.Result.State == RestDayState.NotRecorded ? "Not recorded" : RestLook.Describe(d.Result.State).Label;
                        t.Cell().Border(0.25f).BorderColor(Line).PaddingHorizontal(2).Text(text =>
                        {
                            text.Span(result).FontColor(d.Result.State == RestDayState.Breach ? "#b91c1c" : Ink).FontSize(6.5f);
                            if (d.Revisions > 1) text.Span($" (corrected {d.Revisions - 1}x)").FontColor(Muted).FontSize(6);
                        });
                    }
                });
                col.Item().Text("Dark: work. Light: rest. Grey: not recorded. Each date shows the rest in the calendar day, the least rest found in any 24 hours ending that day, and in any 7 days ending that day.").FontColor(Muted).FontSize(6.5f);

                col.Item().Text(t =>
                {
                    t.Span("Limits: ").Bold();
                    t.Span($"at least {RestRules.Hm(limits.MinRest24)} of rest in any 24 hours and {RestRules.Hm(limits.MinRest7)} in any 7 days; rest in no more than {limits.MaxPeriods} periods, one of at least {RestRules.Hm(limits.MinLongest)}; no more than {RestRules.Hm(limits.MaxInterval)} between rest periods.");
                });

                if (doc.Review.Breaches.Count > 0)
                {
                    col.Item().Text("BREACHES AND WHAT WAS SAID ABOUT THEM").FontSize(7.5f).Bold().FontColor(Muted);
                    foreach (var b in doc.Review.Breaches)
                        col.Item().Text(t =>
                        {
                            t.Span($"{b.Finding.Date:d MMM}: ").Bold();
                            t.Span($"{b.Finding.Message} ");
                            t.Span(StatusWords(b)).FontColor(b.Resolved ? Ink : "#b91c1c");
                            if (b.Explanation is { } e) t.Span($" Explanation ({e.ByName}): {e.Text}");
                            if (b.Decision is { } dec && !string.IsNullOrWhiteSpace(dec.Text)) t.Span($" Master ({dec.ByName}): {dec.Text}");
                        });
                }

                col.Item().PaddingTop(4).Text("SIGNATURES").FontSize(7.5f).Bold().FontColor(Muted);
                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(c => { c.ConstantColumn(90); c.RelativeColumn(); c.RelativeColumn(); });
                    t.Header(h => { Head(h.Cell(), "Period"); Head(h.Cell(), "Seafarer"); Head(h.Cell(), "Master"); });
                    foreach (var p in doc.Review.Periods)
                    {
                        Body(t.Cell(), $"{p.From:d MMM} - {p.To:d MMM}");
                        Body(t.Cell(), Sign(p.Seafarer));
                        Body(t.Cell(), Sign(p.Master));
                    }
                });
            });
        })).GeneratePdf();
    }

    private static string StatusWords(BreachView b) => b.Status switch
    {
        BreachStatus.Unexplained => "Not yet explained.",
        BreachStatus.Explained => "Explained, waiting for the Master.",
        BreachStatus.Stale => "Days changed since the Master dealt with it.",
        BreachStatus.Acknowledged => "Acknowledged by the Master.",
        _ => $"Exception declared by the Master ({b.Decision?.ExceptionKind}).{(b.Compensatory is null ? " Compensatory rest owed." : " Compensatory rest given.")}",
    };

    private static string Sign(SignView? s) => s is null ? "Not signed" : s.Current ? $"{s.Sign.SignedByName}, {s.Sign.SignedAt:d MMM yyyy HH:mm} UTC" : $"Out of date: {s.Sign.SignedByName}, {s.Sign.SignedAt:d MMM yyyy}";

    private static int WorkIn(List<WorkSpan> spans, int hour)
    {
        int from = hour * 60, to = from + 60, total = 0;
        foreach (var s in spans) total += Math.Max(0, Math.Min(s.End, to) - Math.Max(s.Start, from));
        return total;
    }

    private static string Shade(int workMinutes) => workMinutes switch
    {
        < 0 => "#cbd5e1",
        0 => "#ffffff",
        >= 60 => "#1e293b",
        _ => "#94a3b8",
    };

    private static void Meta(RowDescriptor row, string label, string value) => row.RelativeItem().Column(c =>
    {
        c.Item().Text(label).FontSize(6).Bold().FontColor(Muted);
        c.Item().Text(value).Bold().FontSize(8);
    });

    private static void Head(IContainer cell, string text)
        => cell.Background("#e2e8f0").Border(0.25f).BorderColor(Line).PaddingVertical(1).AlignCenter().Text(text).Bold().FontSize(6.5f);

    private static void Body(IContainer cell, string text, bool shaded = false)
        => cell.Border(0.25f).BorderColor(Line).Background(shaded ? "#f1f5f9" : "#ffffff").PaddingHorizontal(2).Text(text).FontSize(6.5f);
}
