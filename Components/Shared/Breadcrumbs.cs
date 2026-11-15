namespace Chandlery.Components.Shared;

/// <summary>One step of the trail above a page title. A null Href means plain text (a section with no page of its own).</summary>
public record Crumb(string Label, string? Href);

/// <summary>
/// Works out the ancestors of the current page from its address, so every page shows the same
/// Home &rsaquo; Section &rsaquo; List &rsaquo; This page trail without per-page markup. PageHeader adds the last step (its title).
/// </summary>
public static class Breadcrumbs
{
    // Longest matching prefix wins, so a page under a list (detail, "new", edit) gets that list as its parent. Add a page here and it
    // gets its trail; the section is the module (or Admin / Master data), the list is the page a detail or "new" page belongs to.
    private static readonly (string Path, string Section, string List)[] Lists =
    {
        ("/company/approvals", "Work", "My Approvals"),
        ("/company/requisitions", "Procurement", "Requisitions"),
        ("/company/rfqs", "Procurement", "RFQs"),
        ("/company/purchase-orders", "Procurement", "Purchase Orders"),
        ("/company/invoices", "Procurement", "Invoices"),
        ("/company/tax-codes", "Admin", "Tax Codes"),
        ("/company/currencies", "Admin", "Currencies"),
        ("/company/exchange-rates", "Admin", "Exchange Rates"),
        ("/company/suppliers", "Master data", "Suppliers"),
        ("/company/items", "Master data", "Catalog"),
        ("/company/vessels", "Master data", "Vessels"),
        ("/company/departments", "Master data", "Departments"),
        ("/company/ports", "Master data", "Ports"),
        ("/company/uoms", "Master data", "Units of Measure"),
        ("/company/approval-matrix", "Admin", "Approval Matrix"),
        ("/company/profile", "Admin", "Company Profile"),
        ("/company/team", "Admin", "Team"),
        ("/company/onboard-orders", "Procurement", "Onboard orders"),
        ("/company/vessel-policy", "Master data", "Vessel policy"),
        ("/company/crew", "Master data", "Crew"),
        ("/crew", "Vessel", "Crew logins"),
        ("/vessel/rfqs", "Procurement", "RFQs"),
        ("/vessel/orders", "Procurement", "Orders"),
        ("/vessel/port-calls", "Procurement", "Port calls"),

        ("/worklist", "Worklist", "Worklist"),
        ("/worklist/fleet", "Worklist", "Fleet worklist"),
        ("/worklist/reports", "Worklist", "Reports"),
        ("/company/worklist-lists", "Admin", "Worklist lists"),
        ("/conversations", "Conversations", "Conversations"),

        ("/pms", "Maintenance", "Plan"),
        ("/pms/fleet", "Maintenance", "Fleet overview"),
        ("/pms/hours", "Maintenance", "Running hours"),
        ("/pms/machinery", "Maintenance", "Machinery"),
        ("/pms/import", "Maintenance", "Import a plan"),
        ("/pms/approvals", "Maintenance", "Approvals"),
        ("/pms/template-updates", "Maintenance", "Template updates"),
        ("/pms/sister-vessels", "Maintenance", "Sister vessels"),
        ("/pms/reports", "Maintenance", "Reports"),
        ("/pms/apply", "Maintenance", "Apply the library"),
        ("/company/pms-library", "Admin", "Maintenance library"),

        ("/tanks", "Tank inspection", "Tanks"),
        ("/tanks/inspections", "Tank inspection", "Inspections"),
        ("/tanks/exceptions", "Tank inspection", "Exceptions"),
        ("/tanks/review", "Tank inspection", "Review queue"),
        ("/tanks/copy", "Tank inspection", "Copy to sister vessels"),
        ("/company/tank-checklists", "Admin", "Tank checklists"),

        ("/risk", "Risk assessment", "Assessments"),
        ("/risk/review", "Risk assessment", "Sign-off queue"),
        ("/company/risk-library", "Admin", "Risk library"),

        ("/rest", "Hours of rest", "My hours"),
        ("/rest/fleet", "Hours of rest", "Fleet compliance"),
        ("/rest/sheet", "Hours of rest", "Month sheet"),
        ("/rest/sign", "Hours of rest", "Sign hours"),
        ("/rest/breaches", "Hours of rest", "Breaches"),
        ("/rest/table", "Hours of rest", "Working table"),
        ("/rest/reports", "Hours of rest", "Reports"),
        ("/rest/rules", "Hours of rest", "Rules"),
        ("/rest/import", "Hours of rest", "Import history"),

        ("/incidents", "Incident reports", "Reports"),
        ("/incidents/queue", "Incident reports", "Investigation queue"),
        ("/incidents/statistics", "Incident reports", "Statistics"),
        ("/incidents/lessons", "Incident reports", "Lessons learned"),
        ("/incidents/settings", "Incident reports", "Settings"),

        ("/permits", "Permit to work", "Permit board"),
        ("/permits/fleet", "Permit to work", "Fleet permit activity"),
        ("/permits/audit", "Permit to work", "Permit audit"),
        ("/permits/types", "Permit to work", "Types and checklists"),

        ("/drills", "Drills and meetings", "Drill schedule"),
        ("/drills/fleet", "Drills and meetings", "Fleet drill status"),
        ("/drills/crew", "Drills and meetings", "Crew participation"),
        ("/drills/history", "Drills and meetings", "Drill history"),
        ("/drills/types", "Drills and meetings", "Types"),
        ("/meetings", "Drills and meetings", "Meetings"),

        ("/projects", "Projects", "Projects"),
        ("/projects/fleet", "Projects", "Fleet view"),
        ("/projects/groups", "Projects", "Project groups"),

        ("/forms", "Forms", "Forms"),
        ("/forms/review", "Forms", "Submitted forms"),
        ("/forms/due", "Forms", "Forms due"),
        ("/forms/year", "Forms", "The year at a glance"),
        ("/forms/convert", "Forms", "Convert a legacy form"),
        ("/forms/types", "Admin", "Forms"),

        ("/yard", "Yard", "Tenders"),
        ("/yard/profile", "Yard", "Contact details"),

        ("/supplier/rfqs", "Your work", "RFQs"),
        ("/supplier/purchase-orders", "Your work", "Purchase Orders"),
        ("/supplier/invoices", "Your work", "Invoices"),
        ("/supplier/profile", "Your work", "Bank Accounts"),
    };

    /// <summary>The section a page belongs to (Forms, Master data, Hours of rest...), or null for a page we don't know.</summary>
    public static string? SectionOf(string path)
    {
        path = "/" + (path.Split('?', '#')[0].Trim('/')).ToLowerInvariant();
        return Lists
            .Where(l => path == l.Path || path.StartsWith(l.Path + "/"))
            .OrderByDescending(l => l.Path.Length)
            .Select(l => l.Section)
            .FirstOrDefault();
    }

    /// <summary>The steps before the current page. Empty on the dashboard and on pages we don't know.</summary>
    public static List<Crumb> Ancestors(string path)
    {
        path = "/" + (path.Split('?', '#')[0].Trim('/')).ToLowerInvariant();
        if (path == "/") return new List<Crumb>();

        var match = Lists
            .Where(l => path == l.Path || path.StartsWith(l.Path + "/"))
            .OrderByDescending(l => l.Path.Length)
            .Cast<(string Path, string Section, string List)?>()
            .FirstOrDefault();
        if (match is null) return new List<Crumb>();

        var trail = new List<Crumb> { new("Home", "/") };
        // On the list page itself the title is the list, so only deeper pages repeat it as a link.
        // A module whose section and list share a name (Projects, Forms) shows it once, as the link.
        var deeper = path != match.Value.Path;
        if (!(deeper && string.Equals(match.Value.Section, match.Value.List, StringComparison.OrdinalIgnoreCase))) trail.Add(new Crumb(match.Value.Section, null));
        if (deeper) trail.Add(new Crumb(match.Value.List, match.Value.Path));
        return trail;
    }
}
