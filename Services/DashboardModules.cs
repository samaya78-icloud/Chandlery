using System.Security.Claims;
using Chandlery.Models;

namespace Chandlery.Services;

/// <summary>One number on a module card. Tone is success, accent, info, warning or danger; the attention fields feed the strip at the top of the dashboard.</summary>
public record ModuleStat(string Label, string Value, string Href, string Tone = "accent", string? Hint = null, int AttentionCount = 0, string? AttentionText = null)
{
    /// <summary>0 = nothing to look at, 1 = something waiting, 2 = something wrong.</summary>
    public int Severity => AttentionCount > 0 && AttentionText is not null
        ? (Tone == "danger" ? 2 : 1)
        : Tone switch { "danger" => 2, "warning" => 1, _ => 0 };
}

/// <summary>One module's card: its name, where it lives, and its handful of numbers.</summary>
public record ModuleSummary(string Key, string Title, string Href, List<ModuleStat> Stats)
{
    public int Severity => Stats.Count == 0 ? 0 : Stats.Max(s => s.Severity);
}

/// <summary>An entry in the attention strip: a count and what it counts, linking to where it is dealt with.</summary>
public record AttentionItem(int Count, string Text, string Href, bool Urgent);

/// <summary>Gathers the per-module numbers for the home page, so the cards and the attention strip read from the same place.</summary>
public class DashboardModuleService(
    WorklistService worklist, PmsReportService pms, TankTrendService tanks, RiskAssessmentService risk,
    RestFleetService restFleet, RestReviewService restReview, ProjectService projects, IncidentService incidents)
{
    private static string Hot(int n, string tone, string cold = "success") => n > 0 ? tone : cold;

    /// <summary>The modules a company has switched on, in menu order. Modules with nothing to show are left out.</summary>
    public async Task<List<ModuleSummary>> GetAsync(Guid companyId, ClaimsPrincipal user, bool isVessel)
    {
        var modules = new List<ModuleSummary>();

        if (await worklist.CountsAsync(companyId) is { } w)
        {
            var verifyHref = isVessel ? "/worklist" : "/worklist/fleet";
            modules.Add(new("worklist", "Worklist", "/worklist",
            [
                new("Overdue", w.Overdue.ToString("N0"), "/worklist", Hot(w.Overdue, "danger"), "Open tasks past their target date", w.Overdue, "worklist tasks overdue"),
                new("Due in 7 days", w.DueThisWeek.ToString("N0"), "/worklist", Hot(w.DueThisWeek, "warning")),
                new("To verify", w.AwaitingVerification.ToString("N0"), verifyHref, Hot(w.AwaitingVerification, "info"),
                    isVessel ? "Completed, waiting for the office" : "Completed by vessels, waiting for you",
                    isVessel ? 0 : w.AwaitingVerification, "worklist tasks to verify"),
            ]));
        }

        if (await pms.CountsAsync(companyId) is { } p)
        {
            var planHref = isVessel ? "/pms" : "/pms/fleet";
            modules.Add(new("maintenance", "Maintenance", planHref,
            [
                new("Overdue", p.Overdue.ToString("N0"), planHref, Hot(p.Overdue, "danger"), $"{p.CriticalOverdue} critical", p.Overdue, "maintenance jobs overdue"),
                new("Due in 7 days", p.DueThisWeek.ToString("N0"), planHref, Hot(p.DueThisWeek, "warning")),
                new("Awaiting a decision", p.PendingDecisions.ToString("N0"), "/pms/approvals", Hot(p.PendingDecisions, "warning"), "Deferral requests", p.PendingDecisions, "deferral requests to decide"),
                new("Hours out of date", p.HoursOutOfDate.ToString("N0"), isVessel ? "/pms/hours" : "/pms/reports", Hot(p.HoursOutOfDate, "info"), "No reading for over a week"),
            ]));
        }

        if (await tanks.CountsAsync(companyId) is { } t)
        {
            var stats = new List<ModuleStat>();
            if (isVessel)
                stats.Add(new("To finish", t.WithVessel.ToString("N0"), "/tanks/inspections", Hot(t.WithVessel, "warning"), "Drafts and those sent back", t.WithVessel, "tank inspections to finish"));
            else
                stats.Add(new("To review", t.WaitingReview.ToString("N0"), "/tanks/review", t.WaitingBad > 0 ? "danger" : Hot(t.WaitingReview, "warning"), $"{t.WaitingBad} with a Bad rating", t.WaitingReview, "tank inspections to review"));
            stats.Add(new("Exceptions now", t.CurrentExceptions.ToString("N0"), "/tanks/exceptions", t.TanksWithBad > 0 ? "danger" : Hot(t.CurrentExceptions, "warning"), $"{t.TanksWithBad} tank(s) with a Bad rating", t.TanksWithBad, "tanks rated Bad"));
            stats.Add(new("Never inspected", t.NeverInspected.ToString("N0"), "/tanks", Hot(t.NeverInspected, "info")));
            modules.Add(new("tanks", "Tank inspection", "/tanks", stats));
        }

        if (await risk.CountsAsync(companyId) is { } r)
        {
            modules.Add(new("risk", "Risk assessment", "/risk",
            [
                new("To sign", r.Waiting.ToString("N0"), "/risk/review", Hot(r.Waiting, "warning"), "Submitted, waiting for a reviewer", r.Waiting, "risk assessments to sign"),
                new("Due for review", r.DueForReview.ToString("N0"), "/risk", r.Overdue > 0 ? "danger" : Hot(r.DueForReview, "warning"), $"{r.Overdue} overdue", r.Overdue, "risk assessments overdue for review"),
                new("Serious risk still signed", r.HighResidual.ToString("N0"), "/risk", Hot(r.HighResidual, "warning"), "High or worse left after controls"),
                new("Drafts gone quiet", r.OldDrafts.ToString("N0"), "/risk", Hot(r.OldDrafts, "info"), $"Not submitted after {RiskAssessmentService.OldDraftDays} days"),
            ]));
        }

        if (isVessel)
        {
            if (await restReview.AttentionAsync(companyId, Actor.From(user)) is { } a)
            {
                var breaches = a.MyUnexplained + a.ToAcknowledge;
                var toSign = a.ToSign + a.ToEndorse;
                modules.Add(new("rest", "Hours of rest", "/rest",
                [
                    new("Breaches", breaches.ToString("N0"), "/rest/breaches", Hot(breaches, "danger"),
                        (a.IsMaster || a.IsDeputy) ? $"{a.MyUnexplained} of yours to explain · {a.ToAcknowledge} to acknowledge" : "Waiting for your explanation", breaches, "rest breaches to deal with"),
                    new("To sign", a.ToSign.ToString("N0"), "/rest/sign", Hot(toSign, "warning"),
                        (a.IsMaster || a.IsDeputy) ? $"{a.ToEndorse} more waiting for your signature as {(a.IsMaster ? "Master" : "deputy")}" : "Periods you have not signed, or must sign again", toSign, "rest periods to sign"),
                    new("Not recorded", a.MissingDays.ToString("N0"), "/rest", Hot(a.MissingDays, "warning"), "Days since your first record"),
                ]));
            }
        }
        else if (await restFleet.CountsAsync(companyId) is { } f)   // null only when no vessel has the module on; all zeros still shows an "All clear" card
        {
            modules.Add(new("rest", "Hours of rest", "/rest/fleet",
            [
                new("Breaches", f.Unresolved.ToString("N0"), "/rest/fleet", Hot(f.Unresolved, "danger"), $"Not dealt with by the Master · {f.CompensatoryOutstanding} compensatory rest owed", f.Unresolved, "rest breaches not dealt with"),
                new("To sign", f.ToSign.ToString("N0"), "/rest/fleet", Hot(f.ToSign + f.OutOfDate, "warning"), $"Periods over and not fully signed · {f.OutOfDate} out of date", f.ToSign, "rest periods to sign"),
                new("Not recorded", f.Missing.ToString("N0"), "/rest/fleet", Hot(f.Missing, "warning"), $"Days across {f.Vessels} vessel(s), last two months"),
            ]));
        }

        if (await incidents.CountsAsync(companyId, Actor.From(user)) is { } ic)
        {
            List<ModuleStat> stats;
            if (isVessel)
                stats =
                [
                    ic.IsMaster
                        ? new("To review", ic.ToReview.ToString("N0"), "/incidents", Hot(ic.ToReview, "warning"), "Reported, waiting for you", ic.ToReview, "incident reports to review")
                        : new("Waiting for the Master", ic.ToReview.ToString("N0"), "/incidents", "info", "Your reports not yet reviewed"),
                    new(ic.IsMaster ? "Open reports" : "My open reports", ic.Open.ToString("N0"), "/incidents", "info", ic.UrgentOpen > 0 ? $"{ic.UrgentOpen} urgent" : null),
                    new("Investigations overdue", ic.Overdue.ToString("N0"), "/incidents", Hot(ic.Overdue, "danger"), "Past the date for their band", ic.Overdue, "incident investigations overdue"),
                ];
            else
                stats =
                [
                    new("To investigate", ic.ToInvestigate.ToString("N0"), "/incidents/queue", Hot(ic.ToInvestigate, "warning"), "Reviewed, no investigation opened or waived", ic.ToInvestigate, "incident reports to investigate"),
                    new("Investigations overdue", ic.Overdue.ToString("N0"), "/incidents/queue", Hot(ic.Overdue, "danger"), "Past the date for their band", ic.Overdue, "incident investigations overdue"),
                    new("Urgent and open", ic.UrgentOpen.ToString("N0"), "/incidents", Hot(ic.UrgentOpen, "danger"), "Serious reports not yet closed"),
                    new("Waiting for the Master", ic.ToReview.ToString("N0"), "/incidents", "info", "Reported, not yet reviewed onboard"),
                ];
            modules.Add(new("incidents", "Incident reports", isVessel ? "/incidents" : "/incidents/queue", stats));
        }

        if (await projects.CountsAsync(companyId) is { } pc)
        {
            var stats = new List<ModuleStat>
            {
                new("Ongoing", pc.Ongoing.ToString("N0"), "/projects?status=Ongoing", "info"),
                new("Not started", pc.NotStarted.ToString("N0"), "/projects", "accent", "Being planned or approved"),
            };
            // The office answers requests and watches the budget; the vessel waits for answers and sees no money.
            stats.Add(new(isVessel ? "Requests waiting" : "Vessel requests to answer", pc.RequestsWaiting.ToString("N0"), "/projects", Hot(pc.RequestsWaiting, isVessel ? "info" : "warning"),
                isVessel ? "Jobs you asked for, not answered yet" : "Jobs the vessel asked to add", isVessel ? 0 : pc.RequestsWaiting, "vessel requests to answer"));
            if (!isVessel) stats.Add(new("Over budget", pc.OverBudget.ToString("N0"), "/projects", Hot(pc.OverBudget, "danger"), "Cost to date above the budget", pc.OverBudget, "projects over budget"));
            modules.Add(new("projects", "Projects", "/projects", stats));
        }

        return modules;
    }

    /// <summary>The company's purchasing card: the numbers that used to be five loose tiles.</summary>
    public static ModuleSummary Procurement(CompanyDashboard c) => new("procurement", "Procurement", "/company/requisitions",
    [
        new("Open requisitions", c.OpenRequisitions.ToString("N0"), "/company/requisitions?f.status=Open", "accent", $"{c.RequisitionsWithoutRfq.Count} not yet sent out"),
        new("RFQs awaiting quotes", c.RfqsAwaitingQuotes.ToString("N0"), "/company/rfqs?f.status=Sent", "info", $"{c.RfqsWithQuotes} with quotes to compare", c.RfqsWithQuotes, "RFQs with quotes to compare"),
        new("Waiting for my approval", c.PendingMyApproval.ToString("N0"), "/company/approvals", Hot(c.PendingMyApproval, "danger"), "Purchase orders and invoices", c.PendingMyApproval, "waiting for your approval"),
        new("Awaiting delivery", c.AwaitingDelivery.ToString("N0"), "/company/purchase-orders?f.status=Approved", "info", "Approved purchase orders"),
        new("Ordered, last 30 days", c.Spend30Days == 0 ? "—" : Money.Format(c.Spend30Days, c.SpendCurrency), "/company/purchase-orders", "accent", "Approved purchase orders"),
    ]);

    /// <summary>The vessel's purchasing card.</summary>
    public static ModuleSummary Procurement(VesselHomeData d) => new("procurement", "Procurement", "/company/requisitions",
    [
        new("Open requisitions", d.OpenRequisitions.ToString("N0"), "/company/requisitions", "accent", "Not yet fully ordered"),
        new("RFQs awaiting quotes", d.RfqsAwaitingQuotes.ToString("N0"), "/vessel/rfqs", "info", $"{d.QuotesToEvaluate} with quotes to compare", d.QuotesToEvaluate, "RFQs with quotes to compare"),
        new("Orders awaiting approval", (d.AwaitingMaster + d.AwaitingOffice).ToString("N0"), "/vessel/orders", Hot(d.NeedsMyApproval.Count, "danger", "info"),
            $"{d.AwaitingMaster} with the Master · {d.AwaitingOffice} with the office", d.NeedsMyApproval.Count, "orders waiting for your approval"),
        new("Deliveries expected", d.Deliveries.Count.ToString("N0"), "/vessel/port-calls", "info", "Approved, not yet fully received"),
    ]);

    /// <summary>What needs looking at across all cards, worst first.</summary>
    public static List<AttentionItem> Attention(IEnumerable<ModuleSummary> modules) => modules
        .SelectMany(m => m.Stats)
        .Where(s => s.AttentionCount > 0 && s.AttentionText is not null)
        .Select(s => new AttentionItem(s.AttentionCount, s.AttentionText!, s.Href, s.Tone == "danger"))
        .OrderByDescending(i => i.Urgent)
        .ToList();
}
