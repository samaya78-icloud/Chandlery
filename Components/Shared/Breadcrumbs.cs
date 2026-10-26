namespace Chandlery.Components.Shared;

/// <summary>One step of the trail above a page title. A null Href means plain text (a section with no page of its own).</summary>
public record Crumb(string Label, string? Href);

/// <summary>
/// Works out the ancestors of the current page from its address, so every page shows the same
/// Home &rsaquo; Section &rsaquo; List &rsaquo; This page trail without per-page markup. PageHeader adds the last step (its title).
/// </summary>
public static class Breadcrumbs
{
    // Longest matching prefix wins, so a page under a list (detail, "new", import) gets that list as its parent.
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
        ("/company/approval-matrix", "Admin", "Approval Matrix"),
        ("/company/profile", "Admin", "Company Profile"),
        ("/company/team", "Admin", "Team"),
        ("/supplier/rfqs", "Your work", "RFQs"),
        ("/supplier/purchase-orders", "Your work", "Purchase Orders"),
        ("/supplier/invoices", "Your work", "Invoices"),
        ("/supplier/profile", "Your work", "Bank Accounts"),
    };

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

        var trail = new List<Crumb> { new("Home", "/"), new(match.Value.Section, null) };
        // On the list page itself the title is the list, so only deeper pages repeat it as a link.
        if (path != match.Value.Path) trail.Add(new Crumb(match.Value.List, match.Value.Path));
        return trail;
    }
}
