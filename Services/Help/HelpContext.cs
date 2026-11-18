using Chandlery.Components.Shared;

namespace Chandlery.Services;

/// <summary>Where the person is, as far as the assistant is told: the module and the name of the page. Nothing else about the page, the record or the person.</summary>
public record HelpPage(string ModuleKey, string PageLabel);

/// <summary>Works out the help module and page name from an address alone. The address is cut at any query or fragment, and a record id in it is never read.</summary>
public static class HelpContext
{
    // Longest match wins. Admin and master-data pages that belong to a module sit with that module; everything else is "general".
    private static readonly (string Prefix, string Module)[] Modules =
    {
        ("/company/requisitions", "procurement"), ("/company/rfqs", "procurement"), ("/company/purchase-orders", "procurement"),
        ("/company/invoices", "procurement"), ("/company/onboard-orders", "procurement"), ("/vessel", "procurement"),
        ("/worklist", "worklist"), ("/company/worklist-lists", "worklist"),
        ("/pms", "pms"), ("/company/pms-library", "pms"),
        ("/tanks", "tanks"), ("/company/tank-checklists", "tanks"),
        ("/risk", "risk"), ("/company/risk-library", "risk"),
        ("/rest", "rest"),
        ("/incidents", "incidents"),
        ("/drills", "drills"), ("/meetings", "drills"),
        ("/permits", "permits"),
        ("/projects", "projects"),
        ("/forms", "forms"),
        ("/conversations", "conversations"),
    };

    public static HelpPage Build(string? path)
    {
        var clean = "/" + (path ?? "").Split('?', '#')[0].Trim('/').ToLowerInvariant();
        var module = Modules
            .Where(m => clean == m.Prefix || clean.StartsWith(m.Prefix + "/"))
            .OrderByDescending(m => m.Prefix.Length)
            .Select(m => m.Module)
            .FirstOrDefault() ?? HelpContent.GeneralKey;
        var label = clean == "/" ? "Dashboard" : Breadcrumbs.ListOf(clean) ?? "this page";
        return new HelpPage(module, label);
    }
}
