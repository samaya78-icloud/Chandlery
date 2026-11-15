using Chandlery.Models;
using Chandlery.Services;

namespace Chandlery.Sync.Tests;

/// <summary>The Ctrl+K palette finds a project by its code, title or yard, and links to its page; another company never sees it.</summary>
public class SearchRecordsTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();

    public Task InitializeAsync() => _s.InitializeAsync();

    public Task DisposeAsync() => _s.DisposeAsync();

    [Fact]
    public async Task AProjectIsFoundByCodeTitleOrYard_AndOnlyInItsOwnCompany()
    {
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = PolicyModules.Projects, Enabled = true });
            await db.SaveChangesAsync();
        }
        var project = await new ProjectService(_s.ShoreFactory).CreateAsync(_s.CompanyId, new NewProject(_s.VesselId, ProjectType.Drydock, "Special survey drydock", "Dock", Yard: "Zephyr Dockyard"), _s.User);
        var search = new SearchService(_s.ShoreFactory);

        foreach (var term in new[] { project.Code, "survey drydock", "zephyr" })
        {
            var hit = Assert.Single((await search.SearchCompanyAsync(_s.CompanyId, term)).Where(h => h.Group == "Projects"));
            Assert.Equal(project.Code, hit.Title);
            Assert.Equal($"/projects/{project.Id}", hit.Href);
        }
        Assert.DoesNotContain(await search.SearchCompanyAsync(Guid.NewGuid(), "zephyr"), h => h.Group == "Projects");
        Assert.Equal("/risk/" + project.Id, DocLinks.For(DocKind.RiskAssessment, project.Id));
        Assert.Equal("/tanks/inspections/" + project.Id, DocLinks.For(DocKind.TankInspection, project.Id));
        Assert.Equal("/worklist/" + project.Id, DocLinks.For(DocKind.WorklistTask, project.Id));
    }
}
