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
            new TankService(factory), inspections, new TankTrendService(factory, inspections), new RiskLibraryService(factory), new RiskAssessmentService(factory, worklist), new ProjectService(factory), new IncidentService(factory, worklist), new DrillService(factory, worklist), authorization);
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

    private static List<string> Pages(List<NavGroup> groups, string group) => groups.Single(g => g.Title == group).Items.Select(i => i.Label).ToList();

    [Fact]
    public async Task AModuleThatIsOffHasNoPages_AndOnceSwitchedOnEveryOneOfItsPagesIsOffered()
    {
        var admin = Person("CompanyAdmin");
        Assert.Equal(["Work", "Procurement", "Master data", "Admin"], Titles(await _catalog.GetAsync(admin, isVessel: false)));

        await SwitchOnAsync(PolicyModules.Worklist, PolicyModules.Pms, PolicyModules.RestHours, PolicyModules.TankInspection, PolicyModules.RiskAssessment, PolicyModules.Projects);
        var groups = await _catalog.GetAsync(admin, isVessel: false);

        Assert.Equal(["Work", "Maintenance", "Tank inspection", "Risk assessment", "Safety", "Projects", "Procurement", "Master data", "Admin"], Titles(groups));
        Assert.Contains("Worklist", Pages(groups, "Work"));
        Assert.Contains("Fleet worklist", Pages(groups, "Work"));
        Assert.Equal(["Plan", "Fleet overview", "Running hours", "Machinery", "Import a plan", "Approvals", "Template updates", "Sister vessels", "Reports"], Pages(groups, "Maintenance"));
        Assert.Equal(["Tanks", "Inspections", "Exceptions", "Review queue"], Pages(groups, "Tank inspection"));
        Assert.Equal(["Assessments", "New assessment", "Sign-off queue"], Pages(groups, "Risk assessment"));
        Assert.Equal(["Fleet compliance", "Working table", "Reports", "Rules"], Pages(groups, "Safety"));
        Assert.All(groups.Single(g => g.Title == "Safety").Items, i => Assert.Equal("Hours of rest", i.Section));
        Assert.Equal(["Projects", "New project"], Pages(groups, "Projects"));
        Assert.Equal(["Worklist lists", "Maintenance library", "Tank checklists", "Risk library"], Pages(groups, "Admin").Skip(6));          // each module adds its setup page for the admin
        Assert.Equal("pms planned maintenance", groups.Single(g => g.Title == "Maintenance").Aliases);                                            // what the finder also matches
    }

    [Fact]
    public async Task EveryoneSeesOnlyTheirOwnPages()
    {
        await SwitchOnAsync(PolicyModules.Pms, PolicyModules.RiskAssessment, PolicyModules.Projects);

        var user = await _catalog.GetAsync(Person("CompanyUser", await AddClerkAsync()), isVessel: false);
        Assert.Equal(["Projects"], Pages(user, "Projects"));                                                                                  // a clerk can read projects but not start one
        Assert.Equal(["Projects", "New project"], Pages(await _catalog.GetAsync(Person("CompanyUser", await AddClerkAsync(projectManager: true)), isVessel: false), "Projects"));
        Assert.DoesNotContain("Admin", Titles(user));                                                                                         // setup pages are for the company admin
        Assert.Contains("Maintenance", Titles(user));

        var onboard = await _catalog.GetAsync(Person("CompanyUser"), isVessel: true);
        Assert.Equal(["Onboard", "Maintenance", "Risk assessment", "Projects"], Titles(onboard));
        Assert.Equal(["Projects"], Pages(onboard, "Projects"));                                                                               // the vessel reads its projects but never plans one
        Assert.Equal(["Plan", "Running hours", "Machinery", "Import a plan", "Approvals"], Pages(onboard, "Maintenance"));                  // the vessel's own set

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
        Assert.Equal(["Drill schedule", "Record a drill", "Drill history"], vessel);
        Assert.Equal(["Drill schedule", "Drill history"], office);                                    // drills are recorded onboard
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
        Assert.Equal(["Reports", "Investigation queue", "Report an incident", "Statistics", "Lessons learned"], In(office, "Incident reports"));
        Assert.Contains("Incident settings", Pages(office, "Admin"));                                                                         // the office sets the rules and the cause list

        var vessel = await _catalog.GetAsync(Person("CompanyUser"), isVessel: true);
        Assert.Equal(["Hours of rest", "Incident reports"], Sections(vessel));
        Assert.Equal(["My hours", "Month sheet", "Sign hours", "Breaches", "Working table"], In(vessel, "Hours of rest"));
        Assert.Equal(["Reports", "Report an incident", "Lessons learned"], In(vessel, "Incident reports"));

        // Every page of a section sits together: a section never reappears after another one.
        var runs = vessel.Single(g => g.Title == "Safety").Items.Select(i => i.Section).Aggregate(new List<string>(), (list, s) => { if (list.Count == 0 || list[^1] != s) list.Add(s); return list; });
        Assert.Equal(runs.Distinct().Count(), runs.Count);
    }
}
