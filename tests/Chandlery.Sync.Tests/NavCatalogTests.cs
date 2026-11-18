using System.Security.Claims;
using Chandlery.Models;
using Chandlery.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Chandlery.Sync.Tests;

/// <summary>The pages the menu and the Ctrl+K finder offer: by role, and by which modules are switched on.</summary>
public class NavCatalogTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private NavCatalogService _catalog = null!;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        var factory = _s.ShoreFactory;
        var worklist = new WorklistService(factory);
        var pms = new PmsService(factory);
        var inspections = new TankInspectionService(factory, worklist, pms);
        var authorization = new ServiceCollection().AddAuthorization(o => o.AddPolicy("CrewAdmin", p => p.RequireClaim("CrewAdmin", "true")))
            .AddLogging().BuildServiceProvider().GetRequiredService<IAuthorizationService>();
        _catalog = new NavCatalogService(new ApprovalService(factory), new InvoiceService(factory, new NoFiles()), worklist, pms, new RestHoursService(factory),
            new TankService(factory), inspections, new TankTrendService(factory, inspections), new RiskLibraryService(factory), new RiskAssessmentService(factory, worklist), new ProjectService(factory), new IncidentService(factory, worklist), new DrillService(factory, worklist), new DrillFleetService(new DrillService(factory, worklist), new MeetingService(factory, worklist)), new PermitService(factory, worklist), authorization, new ChatService(factory), new FormService(factory), new CertificateService(factory)) { MemoSeconds = 0 };
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private sealed class NoFiles : IFileStore
    {
        public Task<string> SaveAsync(Guid companyId, Stream content, CancellationToken ct = default) => throw new NotSupportedException();
        public Stream? OpenRead(string key) => null;
        public void Delete(string key) { }
    }

    private ClaimsPrincipal Person(string role, params Claim[] extra) => Person(role, _s.User.UserId!.Value, extra);

    private ClaimsPrincipal Person(string role, Guid userId, params Claim[] extra) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Role, role), new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim("CompanyId", _s.CompanyId.ToString()), .. extra], "test"));

    private async Task<Guid> AddClerkAsync(bool projectManager = false)
    {
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        var user = new AppUser { Id = Guid.CreateVersion7(), Email = $"clerk{Guid.NewGuid():N}@test.example.com", PasswordHash = "x", Role = UserRole.CompanyUser, DisplayName = "Clerk", CompanyId = _s.CompanyId, CreatedAt = DateTime.UtcNow, IsProjectManager = projectManager };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task SwitchOnAsync(params string[] modules)
    {
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            foreach (var module in modules)
                db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = module, Enabled = true });
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
    }

    private static List<string> Titles(List<NavGroup> groups) => groups.Select(g => g.Title).ToList();

    private static List<string> Under(List<NavGroup> groups, string group, string section) => groups.Single(g => g.Title == group).Items.Where(i => i.Section == section).Select(i => i.Label).ToList();

    private static List<string> Pages(List<NavGroup> groups, string group) => groups.Single(g => g.Title == group).Items.Select(i => i.Label).ToList();

    [Fact]
    public async Task TheInboxIsOfferedOnlyWhereConversationsAreOn_WithItsUnreadCountAsTheBadge()
    {
        var admin = Person("CompanyAdmin");
        Assert.DoesNotContain("Conversations", Pages(await _catalog.GetAsync(admin, isVessel: false), "Work"));

        await SwitchOnAsync(PolicyModules.Conversations);
        var work = (await _catalog.GetAsync(admin, isVessel: false)).Single(g => g.Title == "Work");
        var inbox = work.Items.Single(i => i.Label == "Conversations");
        Assert.Equal(("/conversations", 0), (inbox.Href, inbox.Badge));
        Assert.Contains("Conversations", Pages(await _catalog.GetAsync(admin, isVessel: true), "Onboard"));
    }

    [Fact]
    public async Task TheFormsDesignerIsOfferedToTheCompanyAdminOnlyWhereFormsAreOn()
    {
        var admin = Person("CompanyAdmin");
        Assert.DoesNotContain("Forms", Titles(await _catalog.GetAsync(admin, isVessel: false)));
        await SwitchOnAsync(PolicyModules.Forms);
        Assert.Contains("Form types", Pages(await _catalog.GetAsync(admin, isVessel: false), "Forms"));
        Assert.DoesNotContain("Form types", Pages(await _catalog.GetAsync(Person("CompanyUser", await AddClerkAsync()), isVessel: false), "Forms"));      // the designer is not for everyone
        Assert.DoesNotContain("Forms", Pages(await _catalog.GetAsync(admin, isVessel: true), "Onboard"));
        Assert.Equal(["Submitted forms", "Forms due", "Forms", "Form types"], Pages(await _catalog.GetAsync(admin, isVessel: false), "Forms"));                  // the office reviews what was sent, has the fill screen too, and the admin the designer
        Assert.Equal(["Forms"], Pages(await _catalog.GetAsync(Person("CompanyUser"), isVessel: true), "Forms"));                                           // the vessel has only the fill screen, and not onboard
    }

    [Fact]
    public async Task AModuleThatIsOffHasNoPages_AndOnceSwitchedOnEveryOneOfItsPagesIsOffered()
    {
        var admin = Person("CompanyAdmin");
        Assert.Equal(["Work", "Master data", "Procurement"], Titles(await _catalog.GetAsync(admin, isVessel: false)));

        await SwitchOnAsync(PolicyModules.Worklist, PolicyModules.Pms, PolicyModules.RestHours, PolicyModules.TankInspection, PolicyModules.RiskAssessment, PolicyModules.Projects);
        var groups = await _catalog.GetAsync(admin, isVessel: false);

        Assert.Equal(["Work", "Master data", "Technical", "Safety", "Procurement"], Titles(groups));
        Assert.Contains("Worklist", Pages(groups, "Work"));
        Assert.Contains("Fleet worklist", Pages(groups, "Work"));
        Assert.Equal(["Plan", "Fleet overview", "Running hours", "Machinery", "Import a plan", "Approvals", "Certificates", "Template updates", "Sister vessels", "Reports", "Maintenance library"], Under(groups, "Technical", "Maintenance"));
        Assert.Equal(["Tanks", "Inspections", "Exceptions", "Review queue", "Tank checklists"], Under(groups, "Technical", "Tank inspection"));
        Assert.Equal(["Assessments", "New assessment", "Sign-off queue", "Risk library"], Under(groups, "Safety", "Risk assessment"));
        Assert.Equal(["Fleet compliance", "Working table", "Reports", "Rules"], Under(groups, "Safety", "Hours of rest"));
        Assert.Equal(["Projects", "Fleet view", "Project groups", "New project"], Under(groups, "Technical", "Projects"));
        // The admin's setup pages sit with the module they set up, there is no Admin menu: approvals and worklist lists under Work, finance and company under Master data.
        Assert.Equal(["Approval Matrix", "Worklist lists"], Pages(groups, "Work").Where(l => l is "Approval Matrix" or "Worklist lists").ToList());
        Assert.Equal(["Tax Codes", "Currencies", "Exchange Rates"], Under(groups, "Master data", "Finance"));
        Assert.Equal(["Company Profile", "Team"], Under(groups, "Master data", "Company"));
        Assert.Equal("pms planned maintenance tank tanks inspection drydock yard project projects", groups.Single(g => g.Title == "Technical").Aliases);                                            // what the finder also matches
    }

    [Fact]
    public async Task EveryoneSeesOnlyTheirOwnPages()
    {
        await SwitchOnAsync(PolicyModules.Pms, PolicyModules.RiskAssessment, PolicyModules.Projects);

        var user = await _catalog.GetAsync(Person("CompanyUser", await AddClerkAsync()), isVessel: false);
        Assert.Equal(["Projects", "Fleet view", "Project groups"], Under(user, "Technical", "Projects"));                                                                                  // a clerk can read projects but not start one
        Assert.Equal(["Projects", "Fleet view", "Project groups", "New project"], Under(await _catalog.GetAsync(Person("CompanyUser", await AddClerkAsync(projectManager: true)), isVessel: false), "Technical", "Projects"));
        Assert.DoesNotContain("Maintenance library", Under(user, "Technical", "Maintenance"));                                               // setup pages are for the company admin
        Assert.DoesNotContain("Risk library", Pages(user, "Safety"));
        Assert.DoesNotContain("Team", Pages(user, "Master data"));
        Assert.DoesNotContain("Approval Matrix", Pages(user, "Work"));
        Assert.Contains("Technical", Titles(user));

        var onboard = await _catalog.GetAsync(Person("CompanyUser"), isVessel: true);
        Assert.Equal(["Onboard", "Technical", "Safety"], Titles(onboard));
        Assert.Equal(["Projects"], Under(onboard, "Technical", "Projects"));                                                                               // the vessel reads its projects but never plans one
        Assert.Equal(["Plan", "Running hours", "Machinery", "Import a plan", "Approvals", "Certificates"], Under(onboard, "Technical", "Maintenance"));                  // the vessel's own set

        var crewAdmin = await _catalog.GetAsync(Person("CompanyUser", new Claim("CrewAdmin", "true")), isVessel: true);
        Assert.Equal("Vessel", crewAdmin[^1].Title);

        var supplier = await _catalog.GetAsync(Person("SupplierAdmin"), isVessel: false);
        Assert.Equal(["Workspace"], Titles(supplier));
        Assert.Equal(["Dashboard", "RFQs", "Purchase Orders", "Invoices", "Bank Accounts"], Pages(supplier, "Workspace"));

        Assert.Empty(await _catalog.GetAsync(new ClaimsPrincipal(new ClaimsIdentity()), isVessel: false));                                    // not signed in: nothing
    }

    [Fact]
    public async Task DrillsAndMeetingsHaveTheirOwnSubHeading_TheVesselCanRecordAndTheOfficeCannot()
    {
        await SwitchOnAsync(PolicyModules.Drills);
        var vessel = (await _catalog.GetAsync(Person("CompanyUser"), isVessel: true)).Single(g => g.Title == "Safety").Items.Where(i => i.Section == "Drills and meetings").Select(i => i.Label).ToList();
        var office = (await _catalog.GetAsync(Person("CompanyAdmin"), isVessel: false)).Single(g => g.Title == "Safety").Items.Where(i => i.Section == "Drills and meetings").Select(i => i.Label).ToList();
        Assert.Equal(["Drill schedule", "Record a drill", "Drill history", "Meetings", "Record a meeting"], vessel);
        Assert.Equal(["Fleet drill status", "Drill schedule", "Crew participation", "Drill history", "Meetings", "Drill and meeting types"], office);   // drills and meetings are recorded onboard; the admin sets the types
    }

    [Fact]
    public async Task SafetyHasASubHeadingForEachOfItsModules_AndTheFinderNamesThePageWithIt()
    {
        await SwitchOnAsync(PolicyModules.RestHours, PolicyModules.Incidents);
        List<string> Sections(List<NavGroup> groups) => groups.Single(g => g.Title == "Safety").Items.Select(i => i.Section).Distinct().ToList();
        List<string> In(List<NavGroup> groups, string section) => groups.Single(g => g.Title == "Safety").Items.Where(i => i.Section == section).Select(i => i.Label).ToList();

        var office = await _catalog.GetAsync(Person("CompanyAdmin"), isVessel: false);
        Assert.Equal(["Hours of rest", "Incident reports"], Sections(office));
        Assert.Equal(["Fleet compliance", "Working table", "Reports", "Rules"], In(office, "Hours of rest"));
        Assert.Equal(["Reports", "Investigation queue", "Report an incident", "Statistics", "Lessons learned", "Incident settings"], In(office, "Incident reports"));   // the admin sets the rules and the cause list here

        var vessel = await _catalog.GetAsync(Person("CompanyUser"), isVessel: true);
        Assert.Equal(["Hours of rest", "Incident reports"], Sections(vessel));
        Assert.Equal(["My hours", "Month sheet", "Sign hours", "Breaches", "Working table"], In(vessel, "Hours of rest"));
        Assert.Equal(["Reports", "Report an incident", "Lessons learned"], In(vessel, "Incident reports"));

        // Every page of a section sits together: a section never reappears after another one.
        var runs = vessel.Single(g => g.Title == "Safety").Items.Select(i => i.Section).Aggregate(new List<string>(), (list, s) => { if (list.Count == 0 || list[^1] != s) list.Add(s); return list; });
        Assert.Equal(runs.Distinct().Count(), runs.Count);
    }

    [Fact]
    public async Task TheBreadcrumbAndTheStripAgreeWithTheMenu_ForEverySetupPage()
    {
        await SwitchOnAsync(PolicyModules.Worklist, PolicyModules.Pms, PolicyModules.RestHours, PolicyModules.TankInspection, PolicyModules.RiskAssessment, PolicyModules.Forms, PolicyModules.Incidents, PolicyModules.Drills, PolicyModules.PermitToWork);
        var admin = Person("CompanyAdmin");
        var groups = await _catalog.GetAsync(admin, isVessel: false);
        Assert.DoesNotContain("Admin", Titles(groups));

        // Each setup page: the group it sits in, and the section the breadcrumb names (the same module).
        (string Path, string Group, string Section)[] expected =
        [
            ("/company/approval-matrix", "Work", "Work"), ("/company/worklist-lists", "Work", "Worklist"),
            ("/company/tax-codes", "Master data", "Master data"), ("/company/currencies", "Master data", "Master data"), ("/company/exchange-rates", "Master data", "Master data"),
            ("/company/profile", "Master data", "Master data"), ("/company/team", "Master data", "Master data"),
            ("/company/pms-library", "Technical", "Maintenance"), ("/company/tank-checklists", "Technical", "Tank inspection"),
            ("/company/risk-library", "Safety", "Risk assessment"), ("/incidents/settings", "Safety", "Incident reports"),
            ("/permits/types", "Safety", "Permit to work"), ("/drills/types", "Safety", "Drills and meetings"),
            ("/forms/types", "Forms", "Forms"),
        ];
        foreach (var (path, group, section) in expected)
        {
            Assert.Contains(groups.Single(g => g.Title == group).Items, i => i.Href == path);
            Assert.Equal(section, Chandlery.Components.Shared.Breadcrumbs.SectionOf(path));
            Assert.Equal(section, Chandlery.Components.Shared.Breadcrumbs.Ancestors(path).First(c => c.Href is null).Label);
            var (strip, current) = await _catalog.NeighboursAsync(admin, false, path);
            Assert.Equal(path, current);                                                                                                        // the page is marked in its own strip
            Assert.True(strip.Count > 1, path);
        }
        // The strip of a module's page now offers its setup page, and the setup page's strip offers the module's pages.
        Assert.Contains((await _catalog.NeighboursAsync(admin, false, "/incidents")).Pages, i => i.Href == "/incidents/settings");
        Assert.Contains((await _catalog.NeighboursAsync(admin, false, "/company/risk-library")).Pages, i => i.Href == "/risk");
    }
}
