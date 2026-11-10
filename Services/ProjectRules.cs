using Chandlery.Models;

namespace Chandlery.Services;

/// <summary>A project's status, worked out from what the office recorded, the same way on both sides.</summary>
public static class ProjectStatusRules
{
    /// <summary>Oldest first. Time then id, so two sides holding the same records always put them in the same order.</summary>
    public static List<ProjectStatusEvent> Ordered(IEnumerable<ProjectStatusEvent> events) => events.OrderBy(e => e.At).ThenBy(e => e.Id).ToList();

    public static ProjectStatus Of(IEnumerable<ProjectStatusEvent> events) => Ordered(events).LastOrDefault()?.Kind switch
    {
        ProjectEventKind.Approved => ProjectStatus.Approved,
        ProjectEventKind.Started => ProjectStatus.Ongoing,
        ProjectEventKind.Completed => ProjectStatus.Complete,
        ProjectEventKind.Closed => ProjectStatus.Closed,
        _ => ProjectStatus.Planning,       // nothing yet, or approval withdrawn
    };

    /// <summary>Planning to Approved to Ongoing to Complete to Closed; an approval may be withdrawn until work starts. Nothing else.</summary>
    public static bool Allows(ProjectStatus from, ProjectEventKind kind) => (from, kind) switch
    {
        (ProjectStatus.Planning, ProjectEventKind.Approved) => true,
        (ProjectStatus.Approved, ProjectEventKind.Started) => true,
        (ProjectStatus.Approved, ProjectEventKind.Withdrawn) => true,
        (ProjectStatus.Ongoing, ProjectEventKind.Completed) => true,
        (ProjectStatus.Complete, ProjectEventKind.Closed) => true,
        _ => false,
    };

    public static bool IsEditable(ProjectStatus status) => status != ProjectStatus.Closed;

    /// <summary>The day work actually started and finished, from the events that said so.</summary>
    public static DateTime? ActualStart(IEnumerable<ProjectStatusEvent> events) => Ordered(events).LastOrDefault(e => e.Kind == ProjectEventKind.Started)?.Date.Date;

    public static DateTime? ActualEnd(IEnumerable<ProjectStatusEvent> events) => Ordered(events).LastOrDefault(e => e.Kind == ProjectEventKind.Completed)?.Date.Date;

    /// <summary>What stands in the way of closing: every specification item must be Done or Dropped, and the reconciliation must be written down.</summary>
    public static List<string> CloseProblems(IEnumerable<ProjectItem> items, string note)
    {
        var problems = new List<string>();
        var open = items.Count(i => i.Status == ProjectItemStatus.Open);
        if (open > 0) problems.Add($"{open} specification item(s) are still open: mark each Done or Dropped.");
        if (string.IsNullOrWhiteSpace(note)) problems.Add("Write the reconciliation note: how the cost came out against the budget.");
        return problems;
    }
}

/// <summary>The project a document belongs to, from the links written about it.</summary>
public static class ProjectLinkRules
{
    /// <summary>For each document, the project named by the latest link written for it; a document whose latest link is a removal belongs to none.</summary>
    public static Dictionary<(ProjectLinkKind Kind, Guid DocId), Guid> Effective(IEnumerable<ProjectLink> links)
        => links.OrderBy(l => l.At).ThenBy(l => l.Id)
            .GroupBy(l => (l.Kind, l.DocId)).Select(g => g.Last())
            .Where(l => l.Added).ToDictionary(l => (l.Kind, l.DocId), l => l.ProjectId);
}

/// <summary>A purchase order linked to a project and approved, in US dollars at the rate it carries.</summary>
public record CostPo(Guid Id, decimal TotalUsd);

/// <summary>A finalized invoice, in US dollars: of a linked purchase order (name it) or linked on its own (a spot invoice).</summary>
public record CostInvoice(Guid? PurchaseOrderId, decimal AmountUsd);

public record ProjectCost(decimal Committed, decimal Invoiced, decimal Other)
{
    public decimal ToDate => Committed + Invoiced + Other;
}

/// <summary>Budget against cost, and days against plan. All pure, so the list page, the detail page and the tests read the same numbers.</summary>
public static class ProjectCostRules
{
    /// <summary>
    /// Committed is the total of approved orders that have no finalized invoice yet; Invoiced is what finalized invoices came to; Other is entries made by hand. An order that has
    /// finalized invoices counts at the invoiced amount instead of its own total, so the same money is never counted twice.
    /// </summary>
    public static ProjectCost Of(IEnumerable<CostPo> orders, IEnumerable<CostInvoice> invoices, IEnumerable<decimal> entries)
    {
        var invoiceList = invoices.ToList();
        var settled = invoiceList.Where(i => i.PurchaseOrderId is not null).Select(i => i.PurchaseOrderId!.Value).ToHashSet();
        return new ProjectCost(
            orders.Where(o => !settled.Contains(o.Id)).Sum(o => o.TotalUsd),
            invoiceList.Sum(i => i.AmountUsd),
            entries.Sum());
    }

    public static decimal Budget(IEnumerable<ProjectBudgetLine> lines) => lines.Sum(l => l.PlannedUsd);

    /// <summary>The reserve held against the budget: each line's contingency percentage of its own amount.</summary>
    public static decimal Contingency(IEnumerable<ProjectBudgetLine> lines) => lines.Sum(l => Math.Round(l.PlannedUsd * l.ContingencyPct / 100m, 2, MidpointRounding.AwayFromZero));

    /// <summary>Budget minus cost to date: negative when over.</summary>
    public static decimal Variance(decimal budget, decimal toDate) => budget - toDate;

    /// <summary>Whole days from start to end, both counted; null when either is missing or the end is before the start.</summary>
    public static int? PlannedDays(DateTime? start, DateTime? end) => start is { } s && end is { } e && e.Date >= s.Date ? (e.Date - s.Date).Days + 1 : null;

    /// <summary>Days so far (or in all, once finished): from the day work started to the day it ended, or to today while it goes on. Null before it starts.</summary>
    public static int? ActualDays(DateTime? start, DateTime? end, DateTime today)
    {
        if (start is not { } s) return null;
        var last = end ?? today;
        return last.Date >= s.Date ? (last.Date - s.Date).Days + 1 : null;
    }
}
