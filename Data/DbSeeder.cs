using Chandlery.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Data;

public static class DbSeeder
{
    public static async Task SeedAsync(ChandleryDbContext db)
    {
        // Shared reference data: seeded independently so existing databases pick it up too.
        if (!await db.Ports.AnyAsync())
        {
            db.Ports.AddRange(
                new Port { Name = "Singapore", Country = "Singapore" },
                new Port { Name = "Rotterdam", Country = "Netherlands" },
                new Port { Name = "Fujairah", Country = "United Arab Emirates" },
                new Port { Name = "Houston", Country = "United States" },
                new Port { Name = "Shanghai", Country = "China" },
                new Port { Name = "Hamburg", Country = "Germany" });
            await db.SaveChangesAsync();
        }

        if (await db.Companies.AnyAsync())
        {
            await EnsureApprovalDemoAsync(db);
            await EnsureSpotDemoAsync(db);
            await EnsureTaxCodesAsync(db);
            return;
        }

        var hasher = new PasswordHasher<AppUser>();

        var company = new Company
        {
            Name = "Nimbus Marine Co.",
            ContactEmail = "priya@chandlery.example.com",
            CreatedAt = DateTime.UtcNow,
            ApprovalLevel1Name = "Superintendent review",
            ApprovalLevel2Name = "Fleet manager approval",
            ApprovalLevel3Name = "Management approval"
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();

        var oceanGear = new Supplier { Name = "OceanGear Marine Supplies", ContactEmail = "sales@oceangear.example.com" };
        var poseidon = new Supplier { Name = "Poseidon Ship Stores", ContactEmail = "quotes@poseidon.example.com" };
        db.Suppliers.AddRange(oceanGear, poseidon);
        await db.SaveChangesAsync();

        db.CompanySuppliers.AddRange(
            new CompanySupplier { CompanyId = company.Id, SupplierId = oceanGear.Id, AddedAt = DateTime.UtcNow },
            new CompanySupplier { CompanyId = company.Id, SupplierId = poseidon.Id, AddedAt = DateTime.UtcNow });

        var companyAdmin = new AppUser { Email = "priya@chandlery.example.com", DisplayName = "Priya Menon", Role = UserRole.CompanyAdmin, CompanyId = company.Id, CreatedAt = DateTime.UtcNow };
        companyAdmin.PasswordHash = hasher.HashPassword(companyAdmin, "Passw0rd!");

        var oceanGearUser = new AppUser { Email = "sales@oceangear.example.com", DisplayName = "Rahul Nair", Role = UserRole.SupplierAdmin, SupplierId = oceanGear.Id, CreatedAt = DateTime.UtcNow };
        oceanGearUser.PasswordHash = hasher.HashPassword(oceanGearUser, "Passw0rd!");

        var poseidonUser = new AppUser { Email = "quotes@poseidon.example.com", DisplayName = "Elena Volkov", Role = UserRole.SupplierAdmin, SupplierId = poseidon.Id, CreatedAt = DateTime.UtcNow };
        poseidonUser.PasswordHash = hasher.HashPassword(poseidonUser, "Passw0rd!");

        db.Users.AddRange(companyAdmin, oceanGearUser, poseidonUser);

        // Each company owns its category tree; this is the same illustrative starter tree a newly registered company gets
        // (NOT the real IMPA Marine Stores Guide, which is a licensed commercial catalogue).
        var cats = await StarterCategories.CreateAsync(db, company.Id);
        var ropesRigging = cats["Ropes, Rigging & Lashing"];
        var deckEquipment = cats["Deck Equipment & Machinery"];
        var safetyEquipment = cats["Safety & Life-Saving Equipment"];
        var paintsChemicals = cats["Paints, Chemicals & Cleaning"];
        var lubricantsOils = cats["Lubricants & Oils"];
        var cateringStores = cats["Cabin, Galley & Catering Stores"];
        var engineSpares = cats["Engine Room Spares"];
        var deckSpares = cats["Deck Machinery Spares"];
        var electricalSpares = cats["Electrical & Electronics Spares"];

        var items = new[]
        {
            new Item { CompanyId = company.Id, Code = "ENG-OIL-15W40", Name = "Engine Oil 15W-40 (205L Drum)", CategoryId = lubricantsOils.Id, UnitOfMeasure = "Drum" },
            new Item { CompanyId = company.Id, Code = "FLT-002", Name = "Fuel Filter Element", CategoryId = engineSpares.Id, UnitOfMeasure = "Pcs" },
            new Item { CompanyId = company.Id, Code = "STR-010", Name = "Fire Extinguisher 5kg CO2", CategoryId = safetyEquipment.Id, UnitOfMeasure = "Pcs" },
            new Item { CompanyId = company.Id, Code = "STR-011", Name = "Mooring Rope 20mm Nylon", CategoryId = ropesRigging.Id, UnitOfMeasure = "Coil" },
            new Item { CompanyId = company.Id, Code = "SPR-045", Name = "Fuel Injector Assembly", CategoryId = engineSpares.Id, UnitOfMeasure = "Pcs" },
            new Item { CompanyId = company.Id, Code = "STR-020", Name = "Life Jacket SOLAS Approved", CategoryId = safetyEquipment.Id, UnitOfMeasure = "Pcs" },
            new Item { CompanyId = company.Id, Code = "SPR-102", Name = "Cooling Water Pump Impeller", CategoryId = engineSpares.Id, UnitOfMeasure = "Pcs" },
            new Item { CompanyId = company.Id, Code = "STR-030", Name = "Deck Paint - Anti-Corrosive Grey", CategoryId = paintsChemicals.Id, UnitOfMeasure = "Litre" },
        };
        db.Items.AddRange(items);
        await db.SaveChangesAsync();

        var seaFalcon = new Vessel { CompanyId = company.Id, Name = "MV Sea Falcon", ImoNumber = "9000001" };
        var coralTrader = new Vessel { CompanyId = company.Id, Name = "MV Coral Trader", ImoNumber = "9000002" };
        db.Vessels.AddRange(seaFalcon, coralTrader);

        db.Departments.AddRange(
            new Department { CompanyId = company.Id, Name = "Deck" },
            new Department { CompanyId = company.Id, Name = "Engine" },
            new Department { CompanyId = company.Id, Name = "Electrical" },
            new Department { CompanyId = company.Id, Name = "Catering" },
            new Department { CompanyId = company.Id, Name = "Safety" });
        await db.SaveChangesAsync();

        // Which categories each department may use (a linked category covers everything beneath it).
        var deptIds = await db.Departments.Where(d => d.CompanyId == company.Id).ToDictionaryAsync(d => d.Name, d => d.Id);
        void Link(string department, params Category[] categories)
        {
            foreach (var c in categories)
                db.DepartmentCategories.Add(new DepartmentCategory { CompanyId = company.Id, DepartmentId = deptIds[department], CategoryId = c.Id });
        }
        Link("Deck", ropesRigging, deckEquipment, safetyEquipment, paintsChemicals, deckSpares);
        Link("Engine", lubricantsOils, paintsChemicals, engineSpares);
        Link("Electrical", electricalSpares);
        Link("Catering", cateringStores);
        Link("Safety", safetyEquipment);
        await db.SaveChangesAsync();

        var singapore = await db.Ports.FirstAsync(p => p.Name == "Singapore");

        var fuelFilter = items[1];
        var fuelInjector = items[4];
        var pumpImpeller = items[6];

        var rfq = new Rfq
        {
            CompanyId = company.Id,
            RfqNumber = "RFQ-1001",
            Type = RfqType.Spares,
            Vessel = "MV Sea Falcon",
            VesselId = seaFalcon.Id,
            DeliveryPortId = singapore.Id,
            VesselEta = DateTime.UtcNow.Date.AddDays(14),
            DateRequired = DateTime.UtcNow.Date.AddDays(10),
            DueDate = DateTime.UtcNow.Date.AddDays(2),
            BuyerName = "Priya Menon",
            BuyerDesignation = "Procurement Manager",
            BuyerEmail = "priya@chandlery.example.com",
            EquipmentDescription = "Main Engine No.1",
            EquipmentParticulars = "Example 4-stroke diesel, 6 cylinders",
            EquipmentMaker = "Example Engines Ltd",
            EquipmentSerialNo = "ME-0001",
            CreatedDate = DateTime.UtcNow.AddDays(-3),
            Status = RfqStatus.QuotesReceived,
            InvitedSupplierIds = [oceanGear.Id, poseidon.Id],
            Lines =
            [
                new RfqLine { ItemId = fuelFilter.Id, Quantity = 20, Uom = "Pcs", DrawingNo = "DWG-101" },
                new RfqLine { ItemId = fuelInjector.Id, Quantity = 4, Uom = "Pcs", DrawingNo = "DWG-102" },
                new RfqLine { ItemId = pumpImpeller.Id, Quantity = 6, Uom = "Pcs", DrawingNo = "DWG-103" },
            ]
        };
        db.Rfqs.Add(rfq);
        await db.SaveChangesAsync();

        var line1 = rfq.Lines[0];
        var line2 = rfq.Lines[1];
        var line3 = rfq.Lines[2];

        db.Quotes.AddRange(
            new Quote
            {
                CompanyId = company.Id,
                RfqId = rfq.Id,
                SupplierId = oceanGear.Id,
                SubmittedDate = DateTime.UtcNow.AddDays(-2),
                SupplierRef = "OG-Q-2201",
                DeliveryDays = 10,
                ValidityDate = DateTime.UtcNow.Date.AddDays(30),
                Currency = "USD",
                Lines =
                [
                    new QuoteLine { RfqLineId = line1.Id, UnitPrice = 18.50m, OfferText = "In stock" },
                    new QuoteLine { RfqLineId = line2.Id, UnitPrice = 240.00m },
                    new QuoteLine { RfqLineId = line3.Id, UnitPrice = 95.00m },
                ]
            },
            new Quote
            {
                CompanyId = company.Id,
                RfqId = rfq.Id,
                SupplierId = poseidon.Id,
                SubmittedDate = DateTime.UtcNow.AddDays(-1),
                SupplierRef = "PSD-7781",
                DeliveryDays = 12,
                ValidityDate = DateTime.UtcNow.Date.AddDays(21),
                Currency = "USD",
                DiscountPct = 2m,
                AdditionalCharges = 25m,
                AdditionalChargeDescription = "Packing and handling",
                Lines =
                [
                    new QuoteLine { RfqLineId = line1.Id, UnitPrice = 17.25m },
                    new QuoteLine { RfqLineId = line2.Id, UnitPrice = 255.00m, OfferText = "OEM part" },
                    new QuoteLine { RfqLineId = line3.Id, UnitPrice = 89.50m },
                ]
            });

        await db.SaveChangesAsync();
        await EnsureApprovalDemoAsync(db);
        await EnsureSpotDemoAsync(db);
        await EnsureTaxCodesAsync(db);
    }

    /// <summary>Illustrative tax codes for the first company (rates are examples, not tax advice). Idempotent.</summary>
    private static async Task EnsureTaxCodesAsync(ChandleryDbContext db)
    {
        var companyId = await db.Users.Where(u => u.Email == "priya@chandlery.example.com" && u.CompanyId != null).Select(u => u.CompanyId).FirstOrDefaultAsync();
        if (companyId is not { } id) return;
        if (await db.TaxCodes.IgnoreQueryFilters().AnyAsync(t => t.CompanyId == id)) return;

        db.TaxCodes.AddRange(
            new TaxCode { CompanyId = id, Code = "NIL", Description = "No tax", RatePct = 0m },
            new TaxCode { CompanyId = id, Code = "STD9", Description = "Standard rate (example)", RatePct = 9m },
            new TaxCode { CompanyId = id, Code = "STD18", Description = "Higher rate (example)", RatePct = 18m });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// One approved spot PO to Poseidon with a part delivery, so the Source and Delivery columns and the Deliveries panel have
    /// something to show on a fresh database. Idempotent: skipped once the company has any spot PO.
    /// </summary>
    private static async Task EnsureSpotDemoAsync(ChandleryDbContext db)
    {
        var admin = await db.Users.FirstOrDefaultAsync(u => u.Email == "priya@chandlery.example.com" && u.CompanyId != null);
        if (admin?.CompanyId is not { } companyId) return;
        if (await db.PurchaseOrders.IgnoreQueryFilters().AnyAsync(p => p.CompanyId == companyId && p.IsSpot)) return;

        var poseidon = await db.Suppliers.FirstOrDefaultAsync(s => s.Name == "Poseidon Ship Stores");
        var vessel = await db.Vessels.IgnoreQueryFilters().FirstOrDefaultAsync(v => v.CompanyId == companyId);
        var items = await db.Items.IgnoreQueryFilters().Where(i => i.CompanyId == companyId).OrderBy(i => i.Id).Take(3).ToListAsync();
        if (poseidon is null || vessel is null || items.Count < 2) return;

        var nextPo = 5001 + await db.PurchaseOrders.IgnoreQueryFilters().CountAsync();
        var po = new PurchaseOrder
        {
            CompanyId = companyId,
            PoNumber = $"PO-{nextPo}",
            SupplierId = poseidon.Id,
            CreatedDate = DateTime.UtcNow.AddDays(-2),
            IsSpot = true,
            SupplierRef = "PSD-SPOT-01",
            VesselId = vessel.Id,
            Remarks = "Urgent top-up before departure.",
            Status = PoStatus.Approved,
            Currency = "USD",
            Lines = items.Select((item, index) => new PoLine { ItemId = item.Id, Quantity = 10 * (index + 1), UnitPrice = 12m + index * 7.5m }).ToList()
        };
        po.DeliveryStatus = DeliveryStatus.Partial;
        db.PurchaseOrders.Add(po);
        await db.SaveChangesAsync();

        var nextDo = 1001 + (await db.DeliveryOrders.IgnoreQueryFilters().MaxAsync(d => (int?)d.Id) ?? 0);
        db.DeliveryOrders.Add(new DeliveryOrder
        {
            CompanyId = companyId,
            DoNumber = $"DO-{nextDo}",
            PurchaseOrderId = po.Id,
            SupplierId = poseidon.Id,
            ReceivedDate = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(-1), DateTimeKind.Utc),
            Place = "Singapore anchorage",
            RecordedByUserId = admin.Id,
            RecordedByName = admin.DisplayName,
            CreatedAt = DateTime.UtcNow,
            Lines = [new DeliveryOrderLine { ItemId = items[0].Id, Quantity = po.Lines[0].Quantity }]
        });
        db.AuditEntries.Add(new AuditEntry
        {
            CompanyId = companyId, EntityType = "PurchaseOrder", EntityId = po.Id, UserId = admin.Id, UserName = admin.DisplayName,
            At = DateTime.UtcNow, Text = $"CREATED spot PO {po.PoNumber} for {poseidon.Name} (demo data)"
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Demo approvers and a sample matrix for the first company. Idempotent, so existing dev databases pick it up too.
    /// Tier 1 (up to 2,000): superintendent. Tier 2 (up to 20,000): superintendent then fleet manager. Above that: fleet manager then the admin.
    /// </summary>
    private static async Task EnsureApprovalDemoAsync(ChandleryDbContext db)
    {
        var admin = await db.Users.FirstOrDefaultAsync(u => u.Email == "priya@chandlery.example.com" && u.CompanyId != null);
        if (admin?.CompanyId is not { } companyId) return;
        if (await db.ApprovalRules.IgnoreQueryFilters().AnyAsync(r => r.CompanyId == companyId)) return;

        var hasher = new PasswordHasher<AppUser>();

        async Task<AppUser> EnsureUser(string email, string name)
        {
            var existing = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
            if (existing is not null) return existing;
            var user = new AppUser { Email = email, DisplayName = name, Role = UserRole.CompanyUser, CompanyId = companyId, CreatedAt = DateTime.UtcNow };
            user.PasswordHash = hasher.HashPassword(user, "Passw0rd!");
            db.Users.Add(user);
            await db.SaveChangesAsync();
            return user;
        }

        var superintendent = await EnsureUser("super@chandlery.example.com", "Marcus Tan");
        var fleetManager = await EnsureUser("fleet@chandlery.example.com", "Anita Rao");

        db.ApprovalRules.AddRange(
            new ApprovalRule { CompanyId = companyId, UpToAmount = 2000m, Level = 1, ApproverUserId = superintendent.Id },
            new ApprovalRule { CompanyId = companyId, UpToAmount = 20000m, Level = 1, ApproverUserId = superintendent.Id },
            new ApprovalRule { CompanyId = companyId, UpToAmount = 20000m, Level = 2, ApproverUserId = fleetManager.Id },
            new ApprovalRule { CompanyId = companyId, UpToAmount = null, Level = 1, ApproverUserId = fleetManager.Id },
            new ApprovalRule { CompanyId = companyId, UpToAmount = null, Level = 2, ApproverUserId = admin.Id });
        await db.SaveChangesAsync();
    }
}
