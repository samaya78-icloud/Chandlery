using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace Chandlery.Services;

/// <summary>One page in the menu. Icon is a name the menu maps to a picture; Badge is a count to show beside it (0 for none); Section is the sub-heading it sits under inside its group (empty for none).</summary>
public record NavEntry(string Href, string Label, string Icon, int Badge = 0, string Section = "");

/// <summary>A menu heading with its pages. Aliases are other words people use for it, so the finder can match them ("pms" for Maintenance).</summary>
public record NavGroup(string Title, List<NavEntry> Items, string Aliases = "");

/// <summary>
/// Every page a person can reach from the menu, in menu order: by their role, and by which modules are switched on for their company. The menu and the
/// Ctrl+K finder both read this, so a new page or module only has to be added here.
/// </summary>
public class NavCatalogService(
    ApprovalService approvals, InvoiceService invoices, WorklistService worklist, PmsService pms, RestHoursService rest,
    TankService tanks, TankInspectionService tankReviews, TankTrendService tankTrends, RiskLibraryService riskLibrary, RiskAssessmentService risk, ProjectService projects,
    IncidentService incidents, IAuthorizationService authorization)
{
    public async Task<List<NavGroup>> GetAsync(ClaimsPrincipal user, bool isVessel)
    {
        var groups = new List<NavGroup>();
        if (user.Identity?.IsAuthenticated != true) return groups;

        var isAdmin = user.IsInRole("CompanyAdmin");
        if ((isAdmin || user.IsInRole("CompanyUser"))
            && Guid.TryParse(user.FindFirst("CompanyId")?.Value, out var companyId)
            && Guid.TryParse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
        {
            groups.AddRange(await CompanyGroupsAsync(companyId, userId, isVessel, isAdmin));
        }

        if ((await authorization.AuthorizeAsync(user, "CrewAdmin")).Succeeded)
            groups.Add(new("Vessel", [new("/crew", "Crew logins", "team")]));

        if (user.IsInRole("SupplierAdmin") || user.IsInRole("SupplierUser"))
            groups.Add(new("Workspace",
            [
                new("/", "Dashboard", "home"),
                new("/supplier/rfqs", "RFQs", "rfq"),
                new("/supplier/purchase-orders", "Purchase Orders", "po"),
                new("/supplier/invoices", "Invoices", "invoice"),
                new("/supplier/profile", "Bank Accounts", "bank"),
            ]));

        return groups;
    }

    private async Task<List<NavGroup>> CompanyGroupsAsync(Guid companyId, Guid userId, bool isVessel, bool isAdmin)
    {
        var pendingApprovals = await approvals.GetPendingCountForUserAsync(companyId, userId) + (await invoices.GetPendingForUserAsync(companyId, userId)).Count + (await projects.PendingForUserAsync(companyId, userId)).Count;

        List<NavEntry> work = [new("/", "Dashboard", "home"), new("/company/approvals", "My Approvals", "approval", pendingApprovals)];
        List<NavEntry> onboard =
        [
            new("/", "Dashboard", "home"),
            new("/company/requisitions", "Requisitions", "reqn"),
            new("/vessel/rfqs", "RFQs", "rfq"),
            new("/vessel/orders", "Orders", "po"),
            new("/vessel/port-calls", "Port calls", "port"),
            new("/company/items", "Catalogue", "item"),
        ];
        List<NavEntry> procurement =
        [
            new("/company/requisitions", "Requisitions", "reqn"),
            new("/company/rfqs", "RFQs", "rfq"),
            new("/company/purchase-orders", "Purchase Orders", "po"),
            new("/company/invoices", "Invoices", "invoice"),
            new("/company/onboard-orders", "Onboard orders", "po"),
        ];
        List<NavEntry> master =
        [
            new("/company/suppliers", "Suppliers", "supplier"),
            new("/company/items", "Catalog", "item"),
            new("/company/vessels", "Vessels", "vessel"),
            new("/company/crew", "Crew", "team"),
            new("/company/departments", "Departments", "department"),
            new("/company/ports", "Ports", "port"),
            new("/company/uoms", "Units of Measure", "unit"),
        ];
        List<NavEntry> admin =
        [
            new("/company/approval-matrix", "Approval Matrix", "matrix"),
            new("/company/tax-codes", "Tax Codes", "tax"),
            new("/company/currencies", "Currencies", "currency"),
            new("/company/exchange-rates", "Exchange Rates", "rates"),
            new("/company/profile", "Company Profile", "profile"),
            new("/company/team", "Team", "team"),
        ];
        List<NavEntry> maintenance = [], tankItems = [], riskItems = [], safety = [], projectItems = [];

        if (await worklist.IsEnabledForCompanyAsync(companyId))
        {
            work.Insert(1, new("/worklist", "Worklist", "worklist"));
            onboard.Insert(1, new("/worklist", "Worklist", "worklist"));
            work.Insert(2, new("/worklist/fleet", "Fleet worklist", "worklist"));
            admin.Add(new("/company/worklist-lists", "Worklist lists", "worklist"));
        }

        if (await pms.IsEnabledForCompanyAsync(companyId))
        {
            var waiting = (await pms.PendingDeferralsAsync(companyId)).Count;
            if (isVessel)
            {
                maintenance.AddRange([new("/pms", "Plan", "pms"), new("/pms/hours", "Running hours", "pms"), new("/pms/machinery", "Machinery", "pms"),
                    new("/pms/import", "Import a plan", "pms"), new("/pms/approvals", "Approvals", "pms", waiting)]);
            }
            else
            {
                waiting += (await pms.PlanChangesAsync(companyId, pendingOnly: true)).Count;
                maintenance.AddRange([new("/pms", "Plan", "pms"), new("/pms/fleet", "Fleet overview", "pms"), new("/pms/hours", "Running hours", "pms"),
                    new("/pms/machinery", "Machinery", "pms"), new("/pms/import", "Import a plan", "pms"), new("/pms/approvals", "Approvals", "pms", waiting),
                    new("/pms/template-updates", "Template updates", "pms"), new("/pms/sister-vessels", "Sister vessels", "pms"), new("/pms/reports", "Reports", "pms")]);
                admin.Add(new("/company/pms-library", "Maintenance library", "pms"));
            }
        }

        var restOn = await rest.IsEnabledForCompanyAsync(companyId);
        const string hoursOfRest = "Hours of rest", incidentReports = "Incident reports";
        if (restOn && !isVessel)
            safety.AddRange([new("/rest/fleet", "Fleet compliance", "rest", Section: hoursOfRest), new("/rest/table", "Working table", "rest", Section: hoursOfRest),
                new("/rest/reports", "Reports", "rest", Section: hoursOfRest), new("/rest/rules", "Rules", "rest", Section: hoursOfRest)]);
        else if (restOn)
            safety.AddRange([new("/rest", "My hours", "rest", Section: hoursOfRest), new("/rest/sheet", "Month sheet", "rest", Section: hoursOfRest), new("/rest/sign", "Sign hours", "rest", Section: hoursOfRest),
                new("/rest/breaches", "Breaches", "rest", Section: hoursOfRest), new("/rest/table", "Working table", "rest", Section: hoursOfRest)]);

        if (await incidents.IsEnabledForCompanyAsync(companyId))
        {
            safety.Add(new("/incidents", "Reports", "incident", Section: incidentReports));
            if (!isVessel) safety.Add(new("/incidents/queue", "Investigation queue", "incident", (await incidents.QueueAsync(companyId, new Actor(userId, ""))).Count, incidentReports));
            safety.Add(new("/incidents/new", "Report an incident", "incident", Section: incidentReports));
            if (!isVessel) safety.Add(new("/incidents/statistics", "Statistics", "incident", Section: incidentReports));
            safety.Add(new("/incidents/lessons", "Lessons learned", "incident", Section: incidentReports));
            if (!isVessel) admin.Add(new("/incidents/settings", "Incident settings", "incident"));
        }

        if (await tanks.IsEnabledForCompanyAsync(companyId))
        {
            tankItems.Add(new("/tanks", "Tanks", "tank"));
            tankItems.Add(new("/tanks/inspections", "Inspections", "tank"));
            tankItems.Add(new("/tanks/exceptions", "Exceptions", "tank", (await tankTrends.CountsAsync(companyId))?.CurrentExceptions ?? 0));
            if (!isVessel)
            {
                tankItems.Add(new("/tanks/review", "Review queue", "tank", (await tankReviews.ReviewWaitingAsync(companyId)).Count));
                if ((await tanks.EnabledVesselsAsync(companyId)).Count > 1) tankItems.Add(new("/tanks/copy", "Copy to sister vessels", "tank"));
                admin.Add(new("/company/tank-checklists", "Tank checklists", "tank"));
            }
        }

        if (await riskLibrary.IsEnabledForCompanyAsync(companyId))
        {
            riskItems.Add(new("/risk", "Assessments", "risk", (await risk.CountsAsync(companyId))?.DueForReview ?? 0));
            riskItems.Add(new("/risk/new", "New assessment", "risk"));
            riskItems.Add(new("/risk/review", "Sign-off queue", "risk", (await risk.SignOffQueueAsync(companyId)).Count));
            if (!isVessel) admin.Add(new("/company/risk-library", "Risk library", "risk"));
        }

        if (await projects.IsEnabledForCompanyAsync(companyId))
        {
            projectItems.Add(new("/projects", "Projects", "project"));
            if (!isVessel) projectItems.Add(new("/projects/fleet", "Fleet view", "project"));
            if (!isVessel) projectItems.Add(new("/projects/groups", "Project groups", "project"));
            if (!isVessel && await projects.MayManageAsync(companyId, userId)) projectItems.Add(new("/projects/new", "New project", "project"));
        }

        var groups = new List<NavGroup> { new(isVessel ? "Onboard" : "Work", isVessel ? onboard : work) };
        if (maintenance.Count > 0) groups.Add(new("Maintenance", maintenance, "pms planned maintenance"));
        if (tankItems.Count > 0) groups.Add(new("Tank inspection", tankItems));
        if (riskItems.Count > 0) groups.Add(new("Risk assessment", riskItems, "risk ra hazard"));
        if (safety.Count > 0) groups.Add(new("Safety", safety, "hours of rest rest hours incident accident near miss report lessons"));
        if (projectItems.Count > 0) groups.Add(new("Projects", projectItems, "drydock yard technical project"));
        if (!isVessel)
        {
            groups.Add(new("Procurement", procurement));
            groups.Add(new("Master data", master));
            if (isAdmin) groups.Add(new("Admin", admin));
        }
        return groups;
    }
}
