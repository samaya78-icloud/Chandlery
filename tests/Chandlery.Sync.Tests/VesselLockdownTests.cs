using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>What a vessel installation may and may not do. The office-only work is refused in the data layer, whatever page asks.</summary>
public class VesselLockdownTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    public Task InitializeAsync() => _s.InitializeAsync();
    public Task DisposeAsync() => _s.DisposeAsync();

    [Fact]
    public async Task AVesselCannotSaveOfficeRecords_ButStillRaisesRequisitions()
    {
        await using (var db = await _s.VesselFactory.CreateDbContextAsync())
        {
            db.TaxCodes.Add(new TaxCode { CompanyId = _s.CompanyId, Code = "VX", Description = "no", RatePct = 1 });
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.Contains("handled by the office", error.Message);
        }
        await using (var db = await _s.VesselFactory.CreateDbContextAsync())
        {
            db.Suppliers.Add(new Supplier { Name = "Onboard supplier", ContactEmail = "x@example.com" });
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        await using (var db = await _s.VesselFactory.CreateDbContextAsync())
        {
            db.ApprovalRules.Add(new ApprovalRule { CompanyId = _s.CompanyId, UpToAmount = 1, Level = 1, ApproverUserId = _s.User.UserId!.Value });
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        var made = await _s.VesselRequisitions.CreateAsync(_s.CompanyId, _s.NewRequisition("still allowed"), _s.User);
        Assert.NotEqual(Guid.Empty, made.Id);
    }

    [Fact]
    public async Task TheOffice_IsNotRestricted()
    {
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        db.TaxCodes.Add(new TaxCode { CompanyId = _s.CompanyId, Code = "OFFICE-OK", Description = "fine", RatePct = 1 });
        await db.SaveChangesAsync();
    }

    [Fact]
    public void OnlyTheIntendedPagesOpenOnAVessel()
    {
        var allowed = typeof(CrewMember).Assembly.GetTypes()
            .Where(t => t.IsDefined(typeof(VesselAllowedAttribute), inherit: false))
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToList();

        // Adding a page to this list is a decision: it will be reachable from a ship.
        Assert.Equal(["ChangePassword", "CrewAdmin", "Home", "Items", "OrderDetail", "Orders", "PmsBoard", "PmsHoursPage", "PmsJobPage", "PmsMachineryPage", "PurchaseOrderNewSpot", "RequisitionDetail", "RequisitionNew", "Requisitions", "VesselPortCalls", "VesselRfqDetail", "VesselRfqs", "WorklistBoard", "WorklistTaskPage"], allowed);
    }
}
