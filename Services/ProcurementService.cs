using Chandlery.Data;
using Chandlery.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

/// <summary>
/// EF-Core-backed data access for the whole app. Every context is short-lived — created per
/// operation via IDbContextFactory, per Blazor Server + EF Core guidance — and CurrentCompanyId
/// is set immediately after creation so the DbContext's tenant query filters apply correctly.
/// </summary>
public class ProcurementService(IDbContextFactory<ChandleryDbContext> factory)
{
    private readonly PasswordHasher<AppUser> _hasher = new();

    // ---- Companies ----

    public async Task<Company?> GetCompanyAsync(int companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Companies.FirstOrDefaultAsync(c => c.Id == companyId);
    }

    public async Task<Company> CreateCompanyAsync(string name, string contactEmail)
    {
        await using var db = await factory.CreateDbContextAsync();
        var company = new Company { Name = name, ContactEmail = contactEmail, CreatedAt = DateTime.UtcNow };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        return company;
    }

    public async Task UpdateCompanyAsync(int companyId, string name, string? registrationNumber, string? address, string contactEmail)
    {
        await using var db = await factory.CreateDbContextAsync();
        var company = await db.Companies.FirstAsync(c => c.Id == companyId);
        company.Name = name;
        company.RegistrationNumber = registrationNumber;
        company.Address = address;
        company.ContactEmail = contactEmail;
        await db.SaveChangesAsync();
    }

    // ---- Users & auth ----

    public async Task<AppUser?> FindUserByEmailAsync(string email)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == email.ToLower());
    }

    public async Task<AppUser?> VerifyLoginAsync(string email, string password)
    {
        var user = await FindUserByEmailAsync(email);
        if (user is null) return null;

        var result = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        return result == PasswordVerificationResult.Failed ? null : user;
    }

    public async Task<(Company Company, AppUser Admin)> RegisterCompanyAsync(string companyName, string adminName, string email, string password)
    {
        await using var db = await factory.CreateDbContextAsync();

        if (await db.Users.AnyAsync(u => u.Email.ToLower() == email.ToLower()))
            throw new InvalidOperationException("An account with that email already exists.");

        var company = new Company { Name = companyName, ContactEmail = email, CreatedAt = DateTime.UtcNow };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        await StarterCategories.CreateAsync(db, company.Id);

        var admin = new AppUser
        {
            Email = email,
            DisplayName = adminName,
            Role = UserRole.CompanyAdmin,
            CompanyId = company.Id,
            CreatedAt = DateTime.UtcNow
        };
        admin.PasswordHash = _hasher.HashPassword(admin, password);
        db.Users.Add(admin);
        await db.SaveChangesAsync();

        return (company, admin);
    }

    public async Task<List<AppUser>> GetCompanyUsersAsync(int companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Users.Where(u => u.CompanyId == companyId).OrderBy(u => u.DisplayName).ToListAsync();
    }

    public async Task<AppUser> AddCompanyUserAsync(int companyId, string email, string displayName, string password, UserRole role)
    {
        await using var db = await factory.CreateDbContextAsync();

        if (await db.Users.AnyAsync(u => u.Email.ToLower() == email.ToLower()))
            throw new InvalidOperationException("An account with that email already exists.");

        var user = new AppUser
        {
            Email = email,
            DisplayName = displayName,
            Role = role,
            CompanyId = companyId,
            CreatedAt = DateTime.UtcNow
        };
        user.PasswordHash = _hasher.HashPassword(user, password);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    // ---- Items ----

    public async Task<List<Item>> GetItemsAsync(int companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.Items.Where(i => i.CompanyId == companyId).OrderBy(i => i.Code).ToListAsync();
    }

    public async Task<List<Item>> GetItemsByIdsAsync(IEnumerable<int> itemIds)
    {
        await using var db = await factory.CreateDbContextAsync();
        var ids = itemIds.Distinct().ToList();
        return await db.Items.IgnoreQueryFilters().Where(i => ids.Contains(i.Id)).ToListAsync();
    }

    public async Task<Item> AddItemAsync(int companyId, string code, string name, int categoryId, string uom)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        code = (code ?? "").Trim();
        name = (name ?? "").Trim();
        if (code == "" || name == "") throw new InvalidOperationException("Code and name are required.");
        await EnsureCategoryUsableAsync(db, companyId, categoryId);
        if (await db.Items.AnyAsync(i => i.CompanyId == companyId && i.Code.ToLower() == code.ToLower()))
            throw new InvalidOperationException($"An item with code {code} already exists.");

        var item = new Item { CompanyId = companyId, Code = code, Name = name, CategoryId = categoryId, UnitOfMeasure = (uom ?? "").Trim() };
        db.Items.Add(item);
        await db.SaveChangesAsync();
        return item;
    }

    private static async Task EnsureCategoryUsableAsync(ChandleryDbContext db, int companyId, int categoryId)
    {
        var all = await db.Categories.Where(c => c.CompanyId == companyId).ToListAsync();
        if (!all.Any(c => c.Id == categoryId)) throw new InvalidOperationException("Choose a category from your tree.");
        if (!Category.ActiveIds(all).Contains(categoryId)) throw new InvalidOperationException("That category is disabled, so items cannot be added to it.");
    }

    public async Task<Item> UpdateItemAsync(int companyId, int itemId, string code, string name, int categoryId, string uom)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == itemId && i.CompanyId == companyId)
            ?? throw new InvalidOperationException("Item not found.");
        code = (code ?? "").Trim();
        name = (name ?? "").Trim();
        if (code == "" || name == "") throw new InvalidOperationException("Code and name are required.");
        if (categoryId != item.CategoryId) await EnsureCategoryUsableAsync(db, companyId, categoryId);
        if (await db.Items.AnyAsync(i => i.CompanyId == companyId && i.Id != itemId && i.Code.ToLower() == code.ToLower()))
            throw new InvalidOperationException($"Another item already uses the code {code}.");

        item.Code = code;
        item.Name = name;
        item.CategoryId = categoryId;
        item.UnitOfMeasure = (uom ?? "").Trim();
        await db.SaveChangesAsync();
        return item;
    }

    /// <summary>Which of these items appear on any requisition, RFQ, purchase order or delivery (so they can be disabled but not deleted).</summary>
    public async Task<HashSet<int>> GetUsedItemIdsAsync(int companyId, IReadOnlyCollection<int> itemIds)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var ids = itemIds.ToList();
        var used = new HashSet<int>();
        used.UnionWith(await db.Requisitions.Where(r => r.CompanyId == companyId).SelectMany(r => r.Lines).Where(l => ids.Contains(l.ItemId)).Select(l => l.ItemId).Distinct().ToListAsync());
        used.UnionWith(await db.Rfqs.Where(r => r.CompanyId == companyId).SelectMany(r => r.Lines).Where(l => ids.Contains(l.ItemId)).Select(l => l.ItemId).Distinct().ToListAsync());
        used.UnionWith(await db.PurchaseOrders.Where(p => p.CompanyId == companyId).SelectMany(p => p.Lines).Where(l => ids.Contains(l.ItemId)).Select(l => l.ItemId).Distinct().ToListAsync());
        used.UnionWith(await db.DeliveryOrders.Where(d => d.CompanyId == companyId).SelectMany(d => d.Lines).Where(l => ids.Contains(l.ItemId)).Select(l => l.ItemId).Distinct().ToListAsync());
        return used;
    }

    public async Task DeleteItemAsync(int companyId, int itemId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == itemId && i.CompanyId == companyId)
            ?? throw new InvalidOperationException("Item not found.");
        if ((await GetUsedItemIdsAsync(companyId, new[] { itemId })).Contains(itemId))
            throw new InvalidOperationException($"{item.Code} is on requisitions, RFQs or purchase orders, so it cannot be deleted. Disable it instead.");
        db.Items.Remove(item);
        await db.SaveChangesAsync();
    }

    public async Task SetItemActiveAsync(int companyId, int itemId, bool active)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == itemId && i.CompanyId == companyId)
            ?? throw new InvalidOperationException("Item not found.");
        if (active && !item.IsActive)
            await EnsureCategoryUsableAsync(db, companyId, item.CategoryId); // cannot re-enable an item inside a disabled category
        item.IsActive = active;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Bulk-load catalog items. Missing categories along each path are created in the company's own tree,
    /// items whose code already exists for this company are skipped.
    /// </summary>
    public async Task<ItemImportResult> ImportItemsAsync(int companyId, List<ItemImportRow> rows)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        var categories = await db.Categories.ToListAsync();
        var byKey = new Dictionary<(int Parent, string Name), Category>();
        foreach (var c in categories)
            byKey.TryAdd((c.ParentCategoryId ?? 0, c.Name.ToLowerInvariant()), c);

        var categoriesCreated = 0;
        var pathCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        async Task<int> EnsurePathAsync(string path)
        {
            if (pathCache.TryGetValue(path, out var cached)) return cached;

            int? parentId = null;
            foreach (var segment in path.Split('>', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var key = (parentId ?? 0, segment.ToLowerInvariant());
                if (!byKey.TryGetValue(key, out var category))
                {
                    category = new Category { CompanyId = companyId, Name = segment, ParentCategoryId = parentId, CreatedAt = DateTime.UtcNow };
                    db.Categories.Add(category);
                    await db.SaveChangesAsync(); // children need the parent's generated Id
                    byKey[key] = category;
                    categoriesCreated++;
                }
                parentId = category.Id;
            }

            pathCache[path] = parentId!.Value;
            return parentId.Value;
        }

        var existingCodes = (await db.Items.Where(i => i.CompanyId == companyId).Select(i => i.Code).ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var created = 0;
        var skipped = 0;
        foreach (var row in rows)
        {
            if (!existingCodes.Add(row.Code)) { skipped++; continue; }

            db.Items.Add(new Item
            {
                CompanyId = companyId,
                Code = row.Code,
                Name = row.Name,
                CategoryId = await EnsurePathAsync(row.CategoryPath),
                UnitOfMeasure = row.Uom
            });
            created++;
        }

        await db.SaveChangesAsync();
        return new ItemImportResult(created, skipped, categoriesCreated);
    }

    // ---- Vessels (company-scoped) ----

    public async Task<List<Vessel>> GetVesselsAsync(int companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.Vessels.Where(v => v.CompanyId == companyId).OrderBy(v => v.Name).ToListAsync();
    }

    public async Task<Vessel> AddVesselAsync(int companyId, string name, string? imoNumber)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        if (await db.Vessels.AnyAsync(v => v.CompanyId == companyId && v.Name.ToLower() == name.ToLower()))
            throw new InvalidOperationException("A vessel with that name already exists.");

        var vessel = new Vessel { CompanyId = companyId, Name = name, ImoNumber = string.IsNullOrWhiteSpace(imoNumber) ? null : imoNumber };
        db.Vessels.Add(vessel);
        await db.SaveChangesAsync();
        return vessel;
    }

    // ---- Departments (company-scoped) ----

    public async Task<List<Department>> GetDepartmentsAsync(int companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.Departments.Where(d => d.CompanyId == companyId).OrderBy(d => d.Name).ToListAsync();
    }

    public async Task<Department> AddDepartmentAsync(int companyId, string name)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        if (await db.Departments.AnyAsync(d => d.CompanyId == companyId && d.Name.ToLower() == name.ToLower()))
            throw new InvalidOperationException("A department with that name already exists.");

        var department = new Department { CompanyId = companyId, Name = name };
        db.Departments.Add(department);
        await db.SaveChangesAsync();
        return department;
    }

    // ---- Ports (global) ----

    public async Task<List<Port>> GetPortsAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Ports.OrderBy(p => p.Country).ThenBy(p => p.Name).ToListAsync();
    }

    public async Task<Port> AddPortAsync(string name, string country)
    {
        await using var db = await factory.CreateDbContextAsync();

        if (await db.Ports.AnyAsync(p => p.Name.ToLower() == name.ToLower() && p.Country.ToLower() == country.ToLower()))
            throw new InvalidOperationException("That port already exists.");

        var port = new Port { Name = name, Country = country };
        db.Ports.Add(port);
        await db.SaveChangesAsync();
        return port;
    }

    // ---- Categories (company-owned) ----

    /// <summary>The company's categories. With <paramref name="activeOnly"/>, disabled ones and everything beneath them are left out.</summary>
    public async Task<List<Category>> GetCategoriesAsync(int companyId, bool activeOnly = false)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var all = await db.Categories.Where(c => c.CompanyId == companyId).OrderBy(c => c.Name).ToListAsync();
        if (!activeOnly) return all;
        var ok = Category.ActiveIds(all);
        return all.Where(c => ok.Contains(c.Id)).ToList();
    }

    private static string CleanCategoryName(string? name)
    {
        name = (name ?? "").Trim();
        if (name == "") throw new InvalidOperationException("Enter a category name.");
        if (name.Length > 80) throw new InvalidOperationException("A category name can be at most 80 characters.");
        return name;
    }

    public async Task<Category> AddCategoryAsync(int companyId, string name, int? parentCategoryId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        name = CleanCategoryName(name);
        if (parentCategoryId.HasValue && !await db.Categories.AnyAsync(c => c.Id == parentCategoryId.Value && c.CompanyId == companyId))
            throw new InvalidOperationException("Parent category not found.");
        if (await db.Categories.AnyAsync(c => c.CompanyId == companyId && c.ParentCategoryId == parentCategoryId && c.Name.ToLower() == name.ToLower()))
            throw new InvalidOperationException($"{name} already exists at that level.");

        var category = new Category { CompanyId = companyId, Name = name, ParentCategoryId = parentCategoryId, CreatedAt = DateTime.UtcNow };
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        return category;
    }

    /// <summary>Renames a category and/or moves it under another one (never under itself or one of its own sub-categories).</summary>
    public async Task UpdateCategoryAsync(int companyId, int categoryId, string name, int? parentCategoryId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var all = await db.Categories.Where(c => c.CompanyId == companyId).ToListAsync();
        var category = all.FirstOrDefault(c => c.Id == categoryId) ?? throw new InvalidOperationException("Category not found.");
        name = CleanCategoryName(name);

        if (parentCategoryId.HasValue)
        {
            if (!all.Any(c => c.Id == parentCategoryId.Value)) throw new InvalidOperationException("Parent category not found.");
            if (CategoryIds.Descendants(all, categoryId).Contains(parentCategoryId.Value))
                throw new InvalidOperationException("A category cannot be moved under itself or one of its own sub-categories.");
        }
        if (all.Any(c => c.Id != categoryId && c.ParentCategoryId == parentCategoryId && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"{name} already exists at that level.");

        category.Name = name;
        category.ParentCategoryId = parentCategoryId;
        await db.SaveChangesAsync();
    }

    /// <summary>How many items (any status) and sub-categories each category has, so the page knows whether it can be deleted.</summary>
    public async Task<Dictionary<int, (int Items, int Children)>> GetCategoryUsageAsync(int companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var items = await db.Items.Where(i => i.CompanyId == companyId).GroupBy(i => i.CategoryId).Select(g => new { Id = g.Key, N = g.Count() }).ToListAsync();
        var children = await db.Categories.Where(c => c.CompanyId == companyId && c.ParentCategoryId != null)
            .GroupBy(c => c.ParentCategoryId!.Value).Select(g => new { Id = g.Key, N = g.Count() }).ToListAsync();
        var map = new Dictionary<int, (int, int)>();
        foreach (var i in items) map[i.Id] = (i.N, map.GetValueOrDefault(i.Id).Item2);
        foreach (var c in children) map[c.Id] = (map.GetValueOrDefault(c.Id).Item1, c.N);
        return map;
    }

    /// <summary>A category can only be deleted when it holds no items and no sub-categories; otherwise it can be disabled.</summary>
    public async Task DeleteCategoryAsync(int companyId, int categoryId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == categoryId && c.CompanyId == companyId)
            ?? throw new InvalidOperationException("Category not found.");
        if (await db.Categories.AnyAsync(c => c.ParentCategoryId == categoryId))
            throw new InvalidOperationException($"{category.Name} has sub-categories. Delete or move them first, or disable it.");
        if (await db.Items.AnyAsync(i => i.CategoryId == categoryId))
            throw new InvalidOperationException($"{category.Name} still has items. Delete or move them first, or disable it.");

        db.DepartmentCategories.RemoveRange(await db.DepartmentCategories.Where(x => x.CategoryId == categoryId).ToListAsync());
        db.Categories.Remove(category);
        await db.SaveChangesAsync();
    }

    public async Task SetCategoryActiveAsync(int companyId, int categoryId, bool active)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == categoryId && c.CompanyId == companyId)
            ?? throw new InvalidOperationException("Category not found.");
        category.IsActive = active;
        await db.SaveChangesAsync();
    }

    // ---- Department <-> category links ----

    /// <summary>The categories ticked for a department (not expanded to their sub-categories).</summary>
    public async Task<HashSet<int>> GetDepartmentCategoryIdsAsync(int companyId, int departmentId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return (await db.DepartmentCategories.Where(x => x.CompanyId == companyId && x.DepartmentId == departmentId)
            .Select(x => x.CategoryId).ToListAsync()).ToHashSet();
    }

    public async Task<Dictionary<int, int>> GetDepartmentLinkCountsAsync(int companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.DepartmentCategories.Where(x => x.CompanyId == companyId)
            .GroupBy(x => x.DepartmentId).ToDictionaryAsync(g => g.Key, g => g.Count());
    }

    /// <summary>Replaces the department's linked categories. Linking a category also covers everything beneath it.</summary>
    public async Task SetDepartmentCategoriesAsync(int companyId, int departmentId, IEnumerable<int> categoryIds)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (!await db.Departments.AnyAsync(d => d.Id == departmentId && d.CompanyId == companyId))
            throw new InvalidOperationException("Department not found.");

        var wanted = categoryIds.Distinct().ToList();
        var valid = (await db.Categories.Where(c => c.CompanyId == companyId && wanted.Contains(c.Id)).Select(c => c.Id).ToListAsync()).ToHashSet();
        if (valid.Count != wanted.Count) throw new InvalidOperationException("One of the categories was not found.");

        var existing = await db.DepartmentCategories.Where(x => x.DepartmentId == departmentId).ToListAsync();
        db.DepartmentCategories.RemoveRange(existing.Where(x => !valid.Contains(x.CategoryId)));
        foreach (var id in valid.Where(id => existing.All(x => x.CategoryId != id)))
            db.DepartmentCategories.Add(new DepartmentCategory { CompanyId = companyId, DepartmentId = departmentId, CategoryId = id });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The category ids that may be used for a department: its linked categories and everything beneath them, minus disabled ones.
    /// With no department (a company that has not set any up) every enabled category may be used.
    /// A department with no links gets an empty set, so nothing can be chosen until it is set up.
    /// </summary>
    public async Task<HashSet<int>> GetPickableCategoryIdsAsync(int companyId, int? departmentId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await PickableCategoryIdsAsync(db, companyId, departmentId);
    }

    internal static async Task<HashSet<int>> PickableCategoryIdsAsync(ChandleryDbContext db, int companyId, int? departmentId)
    {
        var all = await db.Categories.Where(c => c.CompanyId == companyId).ToListAsync();
        var active = Category.ActiveIds(all);
        if (departmentId is null) return active;

        var linked = await db.DepartmentCategories.Where(x => x.CompanyId == companyId && x.DepartmentId == departmentId)
            .Select(x => x.CategoryId).ToListAsync();
        var allowed = new HashSet<int>();
        foreach (var id in linked) allowed.UnionWith(CategoryIds.Descendants(all, id));
        allowed.IntersectWith(active);
        return allowed;
    }

    /// <summary>
    /// Server-side guard for requisitions and purchase orders: the department is required when the company has any,
    /// and every item must be enabled and sit in a category the department may use.
    /// </summary>
    internal static async Task EnsureItemsAllowedAsync(ChandleryDbContext db, int companyId, int? departmentId, IEnumerable<int> itemIds)
    {
        if (departmentId is null && await db.Departments.AnyAsync(d => d.CompanyId == companyId))
            throw new InvalidOperationException("Choose a department.");

        var pickable = await PickableCategoryIdsAsync(db, companyId, departmentId);
        var ids = itemIds.Distinct().ToList();
        var bad = await db.Items.Where(i => i.CompanyId == companyId && ids.Contains(i.Id) && (!i.IsActive || !pickable.Contains(i.CategoryId)))
            .Select(i => i.Code).Take(5).ToListAsync();
        if (bad.Count > 0)
            throw new InvalidOperationException(
                $"These items cannot be used for this department (disabled, or not in its categories): {string.Join(", ", bad)}.");
    }

    // ---- Suppliers (global) & CompanySupplier links ----

    public async Task<Supplier?> GetSupplierAsync(int supplierId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Suppliers.FirstOrDefaultAsync(s => s.Id == supplierId);
    }

    public async Task<List<Supplier>> GetSuppliersByIdsAsync(IEnumerable<int> supplierIds)
    {
        await using var db = await factory.CreateDbContextAsync();
        var ids = supplierIds.Distinct().ToList();
        return await db.Suppliers.Where(s => ids.Contains(s.Id)).ToListAsync();
    }

    /// <summary>A company's own address-book of suppliers it can invite — not the global directory.</summary>
    public async Task<List<Supplier>> GetCompanySuppliersAsync(int companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var supplierIds = await db.CompanySuppliers.Where(cs => cs.CompanyId == companyId)
            .Select(cs => cs.SupplierId).ToListAsync();
        return await db.Suppliers.Where(s => supplierIds.Contains(s.Id)).OrderBy(s => s.Name).ToListAsync();
    }

    /// <summary>Search-or-create: reuses an existing global Supplier by email if found, otherwise creates a new Supplier + SupplierAdmin login.</summary>
    public async Task<(Supplier Supplier, bool WasCreated)> AddOrLinkSupplierAsync(int companyId, string name, string contactEmail, string initialPassword)
    {
        await using var db = await factory.CreateDbContextAsync();

        var existing = await db.Suppliers.FirstOrDefaultAsync(s => s.ContactEmail.ToLower() == contactEmail.ToLower());
        var wasCreated = false;

        if (existing is null)
        {
            existing = new Supplier { Name = name, ContactEmail = contactEmail };
            db.Suppliers.Add(existing);
            await db.SaveChangesAsync();

            var supplierUser = new AppUser
            {
                Email = contactEmail,
                DisplayName = name,
                Role = UserRole.SupplierAdmin,
                SupplierId = existing.Id,
                CreatedAt = DateTime.UtcNow
            };
            supplierUser.PasswordHash = _hasher.HashPassword(supplierUser, initialPassword);
            db.Users.Add(supplierUser);
            wasCreated = true;
        }

        var alreadyLinked = await db.CompanySuppliers.IgnoreQueryFilters()
            .AnyAsync(cs => cs.CompanyId == companyId && cs.SupplierId == existing.Id);
        if (!alreadyLinked)
        {
            db.CompanySuppliers.Add(new CompanySupplier { CompanyId = companyId, SupplierId = existing.Id, AddedAt = DateTime.UtcNow });
        }

        await db.SaveChangesAsync();
        return (existing, wasCreated);
    }

    // ---- RFQs ----

    public async Task<List<Rfq>> GetRfqsAsync(int companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.Rfqs.Include(r => r.Lines).Where(r => r.CompanyId == companyId)
            .OrderByDescending(r => r.CreatedDate).ToListAsync();
    }

    public async Task<Rfq?> GetRfqAsync(int companyId, int rfqId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.Rfqs.Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == rfqId && r.CompanyId == companyId);
    }

    /// <summary>Unscoped batch lookup — used by Supplier-side pages to show RFQ number/vessel for POs spanning multiple companies.</summary>
    public async Task<List<Rfq>> GetRfqsByIdsAsync(IEnumerable<int> rfqIds)
    {
        await using var db = await factory.CreateDbContextAsync();
        var ids = rfqIds.Distinct().ToList();
        return await db.Rfqs.IgnoreQueryFilters().Where(r => ids.Contains(r.Id)).ToListAsync();
    }

    public async Task<List<Rfq>> GetRfqsForSupplierAsync(int supplierId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Rfqs.IgnoreQueryFilters().Include(r => r.Lines)
            .Where(r => r.InvitedSupplierIds.Contains(supplierId))
            .OrderByDescending(r => r.CreatedDate).ToListAsync();
    }

    public async Task<Rfq?> GetRfqForSupplierAsync(int supplierId, int rfqId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Rfqs.IgnoreQueryFilters().Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == rfqId && r.InvitedSupplierIds.Contains(supplierId));
    }

    /// <summary>Date-only values from the UI are stored as UTC midnight (Npgsql rejects non-UTC DateTimes for timestamptz).</summary>
    private static DateTime? Utc(DateTime? d) => d.HasValue ? DateTime.SpecifyKind(d.Value.Date, DateTimeKind.Utc) : null;

    public async Task<Rfq> CreateRfqAsync(int companyId, NewRfq request, Actor? actor = null)
    {
        actor ??= Actor.System;
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        Requisition? requisition = null;
        if (request.RequisitionId.HasValue)
        {
            requisition = await db.Requisitions.Include(r => r.Lines)
                .FirstOrDefaultAsync(r => r.Id == request.RequisitionId.Value && r.CompanyId == companyId)
                ?? throw new InvalidOperationException("Requisition not found.");
            if (requisition.Status is RequisitionStatus.Cancelled or RequisitionStatus.Ordered)
                throw new InvalidOperationException("This requisition is closed, so no more RFQs can be posted from it.");

            var validLineIds = requisition.Lines.Select(l => l.Id).ToHashSet();
            if (request.Lines.Any(l => !l.RequisitionLineId.HasValue || !validLineIds.Contains(l.RequisitionLineId.Value)))
                throw new InvalidOperationException("One or more lines do not belong to this requisition.");

            request.VesselId = requisition.VesselId;
            request.Type = requisition.Type;
        }

        var vesselName = request.VesselName.Trim();
        if (request.VesselId.HasValue)
        {
            var chosen = await db.Vessels.FirstOrDefaultAsync(v => v.Id == request.VesselId.Value && v.CompanyId == companyId)
                ?? throw new InvalidOperationException("Vessel not found for this company.");
            vesselName = chosen.Name;
        }
        if (string.IsNullOrWhiteSpace(vesselName))
            throw new InvalidOperationException("Vessel is required.");

        if (request.DeliveryPortId.HasValue && !await db.Ports.AnyAsync(p => p.Id == request.DeliveryPortId.Value))
            throw new InvalidOperationException("Delivery port not found.");

        if (request.Lines.Count == 0)
            throw new InvalidOperationException("Add at least one line item.");
        if (request.InvitedSupplierIds.Count == 0)
            throw new InvalidOperationException("Invite at least one supplier.");

        var linkedSupplierIds = await db.CompanySuppliers.Where(cs => cs.CompanyId == companyId)
            .Select(cs => cs.SupplierId).ToListAsync();
        if (request.InvitedSupplierIds.Any(id => !linkedSupplierIds.Contains(id)))
            throw new InvalidOperationException("You can only invite suppliers linked to your company.");

        var itemIds = request.Lines.Select(l => l.ItemId).Distinct().ToList();
        var items = await db.Items.Where(i => i.CompanyId == companyId && itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
        if (items.Count != itemIds.Count)
            throw new InvalidOperationException("One or more items were not found in your catalog.");

        var nextSeq = 1001 + await db.Rfqs.IgnoreQueryFilters().CountAsync();
        var isSpares = request.Type == RfqType.Spares;

        var rfq = new Rfq
        {
            CompanyId = companyId,
            RfqNumber = $"RFQ-{nextSeq}",
            RequisitionId = requisition?.Id,
            Type = request.Type,
            Vessel = vesselName,
            VesselId = request.VesselId,
            DeliveryPortId = request.DeliveryPortId,
            VesselEta = Utc(request.VesselEta),
            DateRequired = Utc(request.DateRequired),
            DueDate = Utc(request.DueDate),
            BuyerName = request.BuyerName.Trim(),
            BuyerDesignation = request.BuyerDesignation.Trim(),
            BuyerEmail = request.BuyerEmail.Trim(),
            Remarks = request.Remarks.Trim(),
            EquipmentDescription = isSpares ? request.EquipmentDescription.Trim() : "",
            EquipmentParticulars = isSpares ? request.EquipmentParticulars.Trim() : "",
            EquipmentMaker = isSpares ? request.EquipmentMaker.Trim() : "",
            EquipmentSerialNo = isSpares ? request.EquipmentSerialNo.Trim() : "",
            CreatedDate = DateTime.UtcNow,
            Status = RfqStatus.Sent,
            InvitedSupplierIds = request.InvitedSupplierIds.ToList(),
            Lines = request.Lines.Select(l => new RfqLine
            {
                ItemId = l.ItemId,
                Quantity = l.Quantity,
                Uom = items[l.ItemId].UnitOfMeasure,
                Description = string.IsNullOrWhiteSpace(l.Description) ? null : l.Description.Trim(),
                DrawingNo = isSpares && !string.IsNullOrWhiteSpace(l.DrawingNo) ? l.DrawingNo.Trim() : null,
                RequisitionLineId = requisition is null ? null : l.RequisitionLineId
            }).ToList()
        };
        db.Rfqs.Add(rfq);
        await db.SaveChangesAsync();

        var supplierNames = await db.Suppliers.Where(s => rfq.InvitedSupplierIds.Contains(s.Id)).OrderBy(s => s.Name).Select(s => s.Name).ToListAsync();
        AuditLog.Add(db, companyId, AuditEntityTypes.Rfq, rfq.Id, actor,
            requisition is null
                ? $"CREATED New RFQ {rfq.RfqNumber} with {rfq.Lines.Count} lines"
                : $"CREATED New RFQ {rfq.RfqNumber} from {requisition.ReqnNumber} with {rfq.Lines.Count} lines");
        AuditLog.Add(db, companyId, AuditEntityTypes.Rfq, rfq.Id, actor,
            $"POSTED RFQ to {supplierNames.Count} supplier(s): {string.Join(", ", supplierNames)}");
        if (requisition is not null)
        {
            if (requisition.Status == RequisitionStatus.Open) requisition.Status = RequisitionStatus.RfqPosted;
            AuditLog.Add(db, companyId, AuditEntityTypes.Requisition, requisition.Id, actor,
                $"POSTED {rfq.RfqNumber} ({rfq.Lines.Count} lines) to {string.Join(", ", supplierNames)}");
        }
        await db.SaveChangesAsync();
        return rfq;
    }

    // ---- Quotes ----

    public async Task<List<Quote>> GetQuotesForRfqAsync(int companyId, int rfqId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.Quotes.Where(q => q.RfqId == rfqId && q.CompanyId == companyId).ToListAsync();
    }

    /// <summary>All quotes across the company's RFQs — used to show a per-RFQ quote count on the RFQ list without N+1 queries.</summary>
    public async Task<List<Quote>> GetQuotesForCompanyAsync(int companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.Quotes.Where(q => q.CompanyId == companyId).ToListAsync();
    }

    public async Task<Quote?> GetQuoteAsync(int rfqId, int supplierId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Quotes.IgnoreQueryFilters().FirstOrDefaultAsync(q => q.RfqId == rfqId && q.SupplierId == supplierId);
    }

    /// <summary>Whether a supplier may still submit or revise a quote on this RFQ (not past due, nothing awarded to them).</summary>
    public static bool CanQuote(Rfq rfq, int supplierId, bool hasPo) =>
        !rfq.IsPastDue
        && rfq.Status != RfqStatus.Awarded
        && !hasPo
        && !rfq.Lines.Any(l => l.WinningSupplierId == supplierId);

    public async Task<Quote> SubmitQuoteAsync(int rfqId, int supplierId, QuoteInput input)
    {
        await using var db = await factory.CreateDbContextAsync();

        var rfq = await db.Rfqs.IgnoreQueryFilters().Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == rfqId && r.InvitedSupplierIds.Contains(supplierId))
            ?? throw new InvalidOperationException("This supplier was not invited to that RFQ.");

        var hasPo = await db.PurchaseOrders.IgnoreQueryFilters().AnyAsync(p => p.RfqId == rfqId && p.SupplierId == supplierId);
        if (rfq.IsPastDue)
            throw new InvalidOperationException("This RFQ is closed — the due date has passed.");
        if (!CanQuote(rfq, supplierId, hasPo))
            throw new InvalidOperationException("This RFQ has already been awarded, so the quote can no longer be changed.");

        if (!Money.Currencies.Contains(input.Currency))
            throw new InvalidOperationException("Choose a valid currency.");
        if (input.DiscountPct < 0 || input.DiscountPct > 100)
            throw new InvalidOperationException("Discount must be between 0 and 100%.");
        if (input.AdditionalCharges < 0)
            throw new InvalidOperationException("Additional charges cannot be negative.");

        var validLineIds = rfq.Lines.Select(l => l.Id).ToHashSet();
        var lines = input.Lines.Where(l => validLineIds.Contains(l.RfqLineId) && l.UnitPrice > 0).ToList();
        if (lines.Count == 0)
            throw new InvalidOperationException("Enter a unit price for at least one line.");
        if (lines.Any(l => l.DiscountPct < 0 || l.DiscountPct > 100))
            throw new InvalidOperationException("Line discounts must be between 0 and 100%.");

        var existing = await db.Quotes.IgnoreQueryFilters().FirstOrDefaultAsync(q => q.RfqId == rfqId && q.SupplierId == supplierId);
        var quote = existing ?? new Quote { CompanyId = rfq.CompanyId, RfqId = rfqId, SupplierId = supplierId };

        quote.SubmittedDate = DateTime.UtcNow;
        quote.SupplierRef = input.SupplierRef.Trim();
        quote.DeliveryDays = input.DeliveryDays;
        quote.ValidityDate = Utc(input.ValidityDate);
        quote.Currency = input.Currency;
        quote.DiscountPct = input.DiscountPct;
        quote.AdditionalCharges = input.AdditionalCharges;
        quote.AdditionalChargeDescription = input.AdditionalChargeDescription.Trim();
        quote.Remarks = input.Remarks.Trim();

        quote.Lines.Clear();
        foreach (var l in lines)
            quote.Lines.Add(new QuoteLine
            {
                RfqLineId = l.RfqLineId,
                UnitPrice = l.UnitPrice,
                DiscountPct = l.DiscountPct,
                Uom = string.IsNullOrWhiteSpace(l.Uom) ? null : l.Uom.Trim(),
                OfferText = l.OfferText.Trim()
            });

        if (existing is null)
        {
            db.Quotes.Add(quote);
            if (rfq.Status == RfqStatus.Sent)
                rfq.Status = RfqStatus.QuotesReceived;
        }

        await db.SaveChangesAsync();
        return quote;
    }

    /// <summary>Invite further suppliers to an existing RFQ, so the same items aren't re-entered on a new RFQ.</summary>
    public async Task AddSuppliersToRfqAsync(int companyId, int rfqId, IEnumerable<int> supplierIds)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        var rfq = await db.Rfqs.FirstOrDefaultAsync(r => r.Id == rfqId && r.CompanyId == companyId)
            ?? throw new InvalidOperationException("RFQ not found for this company.");
        if (rfq.Status == RfqStatus.Awarded)
            throw new InvalidOperationException("This RFQ has already been awarded.");

        var linked = await db.CompanySuppliers.Where(cs => cs.CompanyId == companyId).Select(cs => cs.SupplierId).ToListAsync();
        var toAdd = supplierIds.Distinct().Where(id => !rfq.InvitedSupplierIds.Contains(id)).ToList();
        if (toAdd.Any(id => !linked.Contains(id)))
            throw new InvalidOperationException("You can only invite suppliers linked to your company.");

        // Assign a new list: EF's change tracking doesn't see in-place edits to a mapped array column.
        rfq.InvitedSupplierIds = rfq.InvitedSupplierIds.Concat(toAdd).ToList();
        await db.SaveChangesAsync();
    }

    // ---- Server-side paged lists (filters run in SQL; used by DataGrid's ItemsProvider) ----

    public async Task<GridResult<RfqListRow>> GetRfqPageAsync(int companyId, GridRequest req)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        var q = db.Rfqs.Where(r => r.CompanyId == companyId);

        if (req.Like("rfq") is { } rfqLike) q = q.Where(r => EF.Functions.ILike(r.RfqNumber, rfqLike));
        if (req.Like("vessel") is { } vesselLike) q = q.Where(r => EF.Functions.ILike(r.Vessel, vesselLike));
        if (Enum.TryParse<RfqType>(req.Select("type"), out var type)) q = q.Where(r => r.Type == type);
        if (Enum.TryParse<RfqStatus>(req.Select("status"), out var status)) q = q.Where(r => r.Status == status);
        if (req.FromUtc("created") is { } createdFrom) q = q.Where(r => r.CreatedDate >= createdFrom);
        if (req.ToUtcExclusive("created") is { } createdTo) q = q.Where(r => r.CreatedDate < createdTo);
        if (req.FromUtc("due") is { } dueFrom) q = q.Where(r => r.DueDate >= dueFrom);
        if (req.ToUtcExclusive("due") is { } dueTo) q = q.Where(r => r.DueDate < dueTo);
        if (int.TryParse(req.Select("quotes"), out var quoteCount))
            q = q.Where(r => db.Quotes.Count(x => x.RfqId == r.Id) == quoteCount);
        if (req.Like("suppliers") is { } supplierLike)
        {
            var supplierIds = await db.Suppliers.Where(s => EF.Functions.ILike(s.Name, supplierLike)).Select(s => s.Id).ToListAsync();
            q = q.Where(r => r.InvitedSupplierIds.Any(id => supplierIds.Contains(id)));
        }

        var total = await q.CountAsync();
        var desc = req.SortDesc;
        IOrderedQueryable<Rfq> ordered = req.SortKey switch
        {
            "rfq" => GridSort.By(q, r => r.RfqNumber, desc),
            "vessel" => GridSort.By(q, r => r.Vessel, desc),
            "type" => GridSort.By(q, r => r.Type, desc),
            "created" => GridSort.By(q, r => r.CreatedDate, desc),
            "due" => GridSort.By(q, r => r.DueDate, desc),
            "quotes" => GridSort.By(q, r => db.Quotes.Count(x => x.RfqId == r.Id), desc),
            "status" => GridSort.By(q, r => r.Status, desc),
            _ => q.OrderByDescending(r => r.CreatedDate)
        };
        var page = await ordered.ThenByDescending(r => r.Id)
            .Skip(req.Skip).Take(req.PageSize)
            .Select(r => new { Rfq = r, Quotes = db.Quotes.Count(x => x.RfqId == r.Id) })
            .ToListAsync();

        var invitedIds = page.SelectMany(x => x.Rfq.InvitedSupplierIds).Distinct().ToList();
        var names = await db.Suppliers.Where(s => invitedIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name);

        var rows = page.Select(x => new RfqListRow(
            x.Rfq,
            x.Quotes,
            x.Rfq.InvitedSupplierIds.Where(id => names.ContainsKey(id)).Select(id => new NameRef(id, names[id])).OrderBy(n => n.Name).ToList())).ToList();
        return new GridResult<RfqListRow>(rows, total);
    }

    public async Task<int> GetRfqCountAsync(int companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.Rfqs.CountAsync(r => r.CompanyId == companyId);
    }

    /// <summary>Distinct "quotes in" counts across the company's RFQs, for the list's drop-down filter.</summary>
    public async Task<List<string>> GetRfqQuoteCountOptionsAsync(int companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var counts = await db.Rfqs.Where(r => r.CompanyId == companyId)
            .Select(r => db.Quotes.Count(x => x.RfqId == r.Id)).Distinct().ToListAsync();
        return counts.Order().Select(n => n.ToString()).ToList();
    }

    public async Task<GridResult<PoListRow>> GetPurchaseOrderPageAsync(int companyId, GridRequest req)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        var q = db.PurchaseOrders.Where(p => p.CompanyId == companyId);

        if (req.Like("po") is { } poLike) q = q.Where(p => EF.Functions.ILike(p.PoNumber, poLike));
        if (Enum.TryParse<PoStatus>(req.Select("status"), out var poStatus)) q = q.Where(p => p.Status == poStatus);
        if (req.FromUtc("created") is { } createdFrom) q = q.Where(p => p.CreatedDate >= createdFrom);
        if (req.ToUtcExclusive("created") is { } createdTo) q = q.Where(p => p.CreatedDate < createdTo);
        if (int.TryParse(req.Select("lines"), out var lineCount)) q = q.Where(p => p.Lines.Count == lineCount);
        if (req.Like("supplier") is { } supplierLike)
        {
            var supplierIds = await db.Suppliers.Where(s => EF.Functions.ILike(s.Name, supplierLike)).Select(s => s.Id).ToListAsync();
            q = q.Where(p => supplierIds.Contains(p.SupplierId));
        }
        if (req.Like("rfq") is { } rfqLike)
        {
            var rfqIds = await db.Rfqs.Where(r => r.CompanyId == companyId && EF.Functions.ILike(r.RfqNumber, rfqLike)).Select(r => r.Id).ToListAsync();
            q = q.Where(p => p.RfqId != null && rfqIds.Contains(p.RfqId.Value));
        }
        if (req.Select("type") == "Spot") q = q.Where(p => p.IsSpot);
        else if (req.Select("type") == "RFQ") q = q.Where(p => !p.IsSpot);
        if (Enum.TryParse<DeliveryStatus>(req.Select("delivery"), out var deliveryStatus)) q = q.Where(p => p.DeliveryStatus == deliveryStatus);

        var total = await q.CountAsync();
        var desc = req.SortDesc;
        IOrderedQueryable<PurchaseOrder> ordered = req.SortKey switch
        {
            "po" => GridSort.By(q, p => p.PoNumber, desc),
            "supplier" => GridSort.By(q, p => db.Suppliers.Where(s => s.Id == p.SupplierId).Select(s => s.Name).FirstOrDefault(), desc),
            "rfq" => GridSort.By(q, p => db.Rfqs.Where(r => r.Id == p.RfqId).Select(r => r.RfqNumber).FirstOrDefault(), desc),
            "created" => GridSort.By(q, p => p.CreatedDate, desc),
            "lines" => GridSort.By(q, p => p.Lines.Count, desc),
            "status" => GridSort.By(q, p => p.Status, desc),
            "type" => GridSort.By(q, p => p.IsSpot, desc),
            "delivery" => GridSort.By(q, p => p.DeliveryStatus, desc),
            _ => q.OrderByDescending(p => p.CreatedDate)
        };
        var pos = await ordered.ThenByDescending(p => p.Id)
            .Skip(req.Skip).Take(req.PageSize).ToListAsync();

        var supplierIdsOnPage = pos.Select(p => p.SupplierId).Distinct().ToList();
        var rfqIdsOnPage = pos.Where(p => p.RfqId.HasValue).Select(p => p.RfqId!.Value).Distinct().ToList();
        var supplierNames = await db.Suppliers.Where(s => supplierIdsOnPage.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name);
        var rfqNumbers = await db.Rfqs.Where(r => rfqIdsOnPage.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.RfqNumber);

        var rows = pos.Select(p => new PoListRow(p, supplierNames.GetValueOrDefault(p.SupplierId) ?? "",
            p.RfqId.HasValue ? rfqNumbers.GetValueOrDefault(p.RfqId.Value) ?? "" : "")).ToList();
        return new GridResult<PoListRow>(rows, total);
    }

    public async Task<int> GetPurchaseOrderCountAsync(int companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.PurchaseOrders.CountAsync(p => p.CompanyId == companyId);
    }

    public async Task<List<string>> GetPurchaseOrderLineCountOptionsAsync(int companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var counts = await db.PurchaseOrders.Where(p => p.CompanyId == companyId).Select(p => p.Lines.Count).Distinct().ToListAsync();
        return counts.Order().Select(n => n.ToString()).ToList();
    }

    /// <summary>One page of the company's items. <paramref name="categoryIds"/> restricts to those categories (null = all).</summary>
    public async Task<GridResult<Item>> GetItemPageAsync(int companyId, GridRequest req, IReadOnlyCollection<int>? categoryIds)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        var q = db.Items.Where(i => i.CompanyId == companyId);

        if (categoryIds is not null)
        {
            var ids = categoryIds.ToList();
            q = q.Where(i => ids.Contains(i.CategoryId));
        }
        if (req.Like("code") is { } codeLike) q = q.Where(i => EF.Functions.ILike(i.Code, codeLike));
        if (req.Like("name") is { } nameLike) q = q.Where(i => EF.Functions.ILike(i.Name, nameLike));
        if (req.Select("uom") is { Length: > 0 } uom) q = q.Where(i => i.UnitOfMeasure == uom);
        if (req.Select("status") == "Active") q = q.Where(i => i.IsActive);
        else if (req.Select("status") == "Disabled") q = q.Where(i => !i.IsActive);

        var total = await q.CountAsync();
        var desc = req.SortDesc;
        IOrderedQueryable<Item> ordered = req.SortKey switch
        {
            "name" => GridSort.By(q, i => i.Name, desc),
            "uom" => GridSort.By(q, i => i.UnitOfMeasure, desc),
            "code" => GridSort.By(q, i => i.Code, desc),
            "status" => GridSort.By(q, i => i.IsActive, desc),
            _ => q.OrderBy(i => i.Code)
        };
        var rows = await ordered.ThenBy(i => i.Id).Skip(req.Skip).Take(req.PageSize).ToListAsync();
        return new GridResult<Item>(rows, total);
    }

    private static IQueryable<Item> ItemSearchQuery(ChandleryDbContext db, int companyId, string? term, IReadOnlyCollection<int>? categoryIds, IReadOnlyCollection<int> pickableCategoryIds)
    {
        var pickable = pickableCategoryIds.ToList();
        var q = db.Items.Where(i => i.CompanyId == companyId && i.IsActive && pickable.Contains(i.CategoryId));
        if (categoryIds is not null)
        {
            var ids = categoryIds.ToList();
            q = q.Where(i => ids.Contains(i.CategoryId));
        }
        if (!string.IsNullOrWhiteSpace(term))
        {
            var like = "%" + term.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            q = q.Where(i => EF.Functions.ILike(i.Code, like) || EF.Functions.ILike(i.Name, like));
        }
        return q;
    }

    /// <summary>One page of enabled items in the allowed categories matching a code/name search (and optionally a narrower category set), for the item pickers.</summary>
    public async Task<(List<Item> Rows, int Total)> SearchItemsAsync(int companyId, string? term, IReadOnlyCollection<int>? categoryIds, IReadOnlyCollection<int> pickableCategoryIds, int skip, int take)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var q = ItemSearchQuery(db, companyId, term, categoryIds, pickableCategoryIds);
        var total = await q.CountAsync();
        var rows = await q.OrderBy(i => i.Code).ThenBy(i => i.Id).Skip(skip).Take(take).ToListAsync();
        return (rows, total);
    }

    /// <summary>Every item matching the search, for "add all matching".</summary>
    public async Task<List<Item>> FindItemsAsync(int companyId, string? term, IReadOnlyCollection<int>? categoryIds, IReadOnlyCollection<int> pickableCategoryIds)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await ItemSearchQuery(db, companyId, term, categoryIds, pickableCategoryIds).OrderBy(i => i.Code).ThenBy(i => i.Id).ToListAsync();
    }

    /// <summary>Looks items up by code (case-insensitive) for "paste from Excel".</summary>
    public async Task<List<Item>> GetItemsByCodesAsync(int companyId, IEnumerable<string> codes)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var lower = codes.Select(c => c.Trim().ToLower()).Distinct().ToList();
        return await db.Items.Where(i => i.CompanyId == companyId && lower.Contains(i.Code.ToLower())).ToListAsync();
    }

    public async Task<int> GetItemCountAsync(int companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.Items.CountAsync(i => i.CompanyId == companyId);
    }

    public async Task<List<string>> GetItemUomOptionsAsync(int companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.Items.Where(i => i.CompanyId == companyId && i.UnitOfMeasure != "")
            .Select(i => i.UnitOfMeasure).Distinct().OrderBy(u => u).ToListAsync();
    }

    public async Task<GridResult<Port>> GetPortPageAsync(GridRequest req)
    {
        await using var db = await factory.CreateDbContextAsync();

        var q = db.Ports.AsQueryable();
        if (req.Like("port") is { } portLike) q = q.Where(p => EF.Functions.ILike(p.Name, portLike));
        if (req.Select("country") is { Length: > 0 } country) q = q.Where(p => p.Country == country);

        var total = await q.CountAsync();
        var desc = req.SortDesc;
        IOrderedQueryable<Port> ordered = req.SortKey switch
        {
            "port" => GridSort.By(q, p => p.Name, desc),
            "country" => GridSort.By(q, p => p.Country, desc),
            _ => q.OrderBy(p => p.Country).ThenBy(p => p.Name)
        };
        var rows = await ordered.ThenBy(p => p.Id)
            .Skip(req.Skip).Take(req.PageSize).ToListAsync();
        return new GridResult<Port>(rows, total);
    }

    public async Task<List<string>> GetPortCountryOptionsAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Ports.Select(p => p.Country).Distinct().OrderBy(c => c).ToListAsync();
    }

    // ---- Supplier-side RFQ list ----

    public async Task<SupplierRfqPage> GetSupplierRfqPageAsync(int supplierId, SupplierRfqTab tab, string? search, int page, int pageSize)
    {
        await using var db = await factory.CreateDbContextAsync();

        var rfqs = await db.Rfqs.IgnoreQueryFilters().Include(r => r.Lines)
            .Where(r => r.InvitedSupplierIds.Contains(supplierId))
            .OrderByDescending(r => r.CreatedDate).ToListAsync();

        var quotes = (await db.Quotes.IgnoreQueryFilters().Where(q => q.SupplierId == supplierId).ToListAsync())
            .ToDictionary(q => q.RfqId);
        var poNumbers = (await db.PurchaseOrders.IgnoreQueryFilters().Where(p => p.SupplierId == supplierId && p.Status == PoStatus.Approved)
                .Select(p => new { p.RfqId, p.PoNumber }).ToListAsync())
            .ToLookup(p => p.RfqId, p => p.PoNumber);
        var archived = (await db.RfqArchives.Where(a => a.SupplierId == supplierId).Select(a => a.RfqId).ToListAsync()).ToHashSet();

        var rows = rfqs.Select(rfq =>
        {
            quotes.TryGetValue(rfq.Id, out var quote);
            var pos = poNumbers[rfq.Id].ToList();

            SupplierRfqStatus status;
            if (pos.Count > 0) status = SupplierRfqStatus.Ordered;
            else if (quote is not null) status = rfq.Status == RfqStatus.Awarded ? SupplierRfqStatus.NotSelected : SupplierRfqStatus.QuotationSubmitted;
            else status = rfq.IsPastDue ? SupplierRfqStatus.Closed : SupplierRfqStatus.Pending;

            return new SupplierRfqRow(rfq, status, quote?.Lines.Count ?? 0, pos, archived.Contains(rfq.Id));
        });

        rows = tab switch
        {
            SupplierRfqTab.Archived => rows.Where(r => r.Archived),
            SupplierRfqTab.Orders => rows.Where(r => !r.Archived && r.PoNumbers.Count > 0),
            _ => rows.Where(r => !r.Archived && r.PoNumbers.Count == 0)
        };

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            rows = rows.Where(r => r.Rfq.RfqNumber.Contains(term, StringComparison.OrdinalIgnoreCase)
                                   || r.Rfq.Vessel.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        var all = rows.ToList();
        var paged = all.Skip(Math.Max(0, page - 1) * pageSize).Take(pageSize).ToList();
        return new SupplierRfqPage(paged, all.Count);
    }

    public async Task SetRfqArchivedAsync(int supplierId, int rfqId, bool archived)
    {
        await using var db = await factory.CreateDbContextAsync();

        var invited = await db.Rfqs.IgnoreQueryFilters().AnyAsync(r => r.Id == rfqId && r.InvitedSupplierIds.Contains(supplierId));
        if (!invited) throw new InvalidOperationException("This supplier was not invited to that RFQ.");

        var existing = await db.RfqArchives.FirstOrDefaultAsync(a => a.RfqId == rfqId && a.SupplierId == supplierId);
        if (archived && existing is null)
            db.RfqArchives.Add(new RfqArchive { RfqId = rfqId, SupplierId = supplierId, ArchivedAt = DateTime.UtcNow });
        else if (!archived && existing is not null)
            db.RfqArchives.Remove(existing);

        await db.SaveChangesAsync();
    }

    public async Task UpdateSupplierNotificationEmailsAsync(int supplierId, string emails)
    {
        await using var db = await factory.CreateDbContextAsync();
        var supplier = await db.Suppliers.FirstAsync(s => s.Id == supplierId);
        supplier.NotificationEmails = emails.Trim();
        await db.SaveChangesAsync();
    }

    // ---- Award & Purchase Orders ----

    public async Task AwardLineAsync(int companyId, int rfqId, int rfqLineId, int supplierId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        var rfq = await db.Rfqs.Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == rfqId && r.CompanyId == companyId)
            ?? throw new InvalidOperationException("RFQ not found for this company.");

        var line = rfq.Lines.First(l => l.Id == rfqLineId);
        line.WinningSupplierId = supplierId;
        await db.SaveChangesAsync();
    }

    public async Task<List<PurchaseOrder>> RaisePurchaseOrdersAsync(int companyId, int rfqId, Actor? actor = null)
    {
        actor ??= Actor.System;
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        var rfq = await db.Rfqs.Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == rfqId && r.CompanyId == companyId)
            ?? throw new InvalidOperationException("RFQ not found for this company.");

        var requisition = rfq.RequisitionId.HasValue
            ? await db.Requisitions.Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == rfq.RequisitionId.Value)
            : null;

        var quotes = await db.Quotes.Where(q => q.RfqId == rfqId && q.CompanyId == companyId).ToListAsync();

        var alreadyPoItemIds = await db.PurchaseOrders.Where(po => po.RfqId == rfqId && po.CompanyId == companyId)
            .SelectMany(po => po.Lines).Select(l => l.ItemId).ToListAsync();
        var alreadyPoItemIdSet = alreadyPoItemIds.ToHashSet();

        var nextSeq = 5001 + await db.PurchaseOrders.IgnoreQueryFilters().CountAsync();
        var newPos = new List<PurchaseOrder>();

        var awardedGroups = rfq.Lines
            .Where(l => l.WinningSupplierId.HasValue && !alreadyPoItemIdSet.Contains(l.ItemId))
            .GroupBy(l => l.WinningSupplierId!.Value);

        foreach (var group in awardedGroups)
        {
            var quote = quotes.FirstOrDefault(q => q.SupplierId == group.Key);
            var chargesAlreadyApplied = await db.PurchaseOrders.AnyAsync(p => p.RfqId == rfqId && p.SupplierId == group.Key && p.CompanyId == companyId);
            var po = new PurchaseOrder
            {
                CompanyId = companyId,
                PoNumber = $"PO-{nextSeq++}",
                RfqId = rfqId,
                SupplierId = group.Key,
                CreatedDate = DateTime.UtcNow,
                Status = PoStatus.PendingApproval,
                RequisitionId = requisition?.Id,
                DepartmentId = requisition?.DepartmentId,
                Currency = quote?.Currency ?? "USD",
                DiscountPct = quote?.DiscountPct ?? 0m,
                AdditionalCharges = chargesAlreadyApplied ? 0m : quote?.AdditionalCharges ?? 0m,
                AdditionalChargeDescription = chargesAlreadyApplied ? "" : quote?.AdditionalChargeDescription ?? "",
                Lines = group.Select(line =>
                {
                    var quoteLine = quote?.Lines.FirstOrDefault(ql => ql.RfqLineId == line.Id);
                    return new PoLine
                    {
                        ItemId = line.ItemId,
                        Quantity = line.Quantity,
                        UnitPrice = quoteLine?.UnitPrice ?? 0m,
                        DiscountPct = quoteLine?.DiscountPct ?? 0m
                    };
                }).ToList()
            };
            db.PurchaseOrders.Add(po);
            newPos.Add(po);
        }

        if (rfq.Lines.All(l => l.WinningSupplierId.HasValue))
            rfq.Status = RfqStatus.Awarded;

        await db.SaveChangesAsync(); // gives the new POs their ids

        var supplierNames = await db.Suppliers.Where(s => newPos.Select(p => p.SupplierId).Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name);
        foreach (var po in newPos)
        {
            var supplierName = supplierNames.GetValueOrDefault(po.SupplierId) ?? "supplier";
            AuditLog.Add(db, companyId, AuditEntityTypes.PurchaseOrder, po.Id, actor,
                $"CREATED PO {po.PoNumber} for {supplierName} from {rfq.RfqNumber}: {po.Lines.Count} lines, total {Money.Format(po.Total, po.Currency)}");
            await ApprovalEngine.RouteAsync(db, po, actor);
            AuditLog.Add(db, companyId, AuditEntityTypes.Rfq, rfq.Id, actor,
                $"RAISED {po.PoNumber} for {supplierName} ({po.Lines.Count} lines)");
        }

        if (requisition is not null && rfq.Status == RfqStatus.Awarded)
        {
            var coveredLineIds = await db.Rfqs
                .Where(r => r.RequisitionId == requisition.Id && r.Status == RfqStatus.Awarded)
                .SelectMany(r => r.Lines).Where(l => l.RequisitionLineId != null)
                .Select(l => l.RequisitionLineId!.Value).ToListAsync();
            var covered = coveredLineIds.ToHashSet();
            if (requisition.Lines.Where(l => l.ApprovedQuantity > 0).All(l => covered.Contains(l.Id)))
            {
                requisition.Status = RequisitionStatus.Ordered;
                AuditLog.Add(db, companyId, AuditEntityTypes.Requisition, requisition.Id, actor,
                    $"ORDERED all lines are on awarded RFQs; purchase orders raised from {rfq.RfqNumber}");
            }
        }

        await db.SaveChangesAsync();
        return newPos;
    }

    /// <summary>Raises a PO directly with a supplier (no RFQ). Priced lines are required unless it is a record-only PO.</summary>
    public async Task<PurchaseOrder> CreateSpotPoAsync(int companyId, NewSpotPo request, Actor? actor = null)
    {
        actor ??= Actor.System;
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        if (!request.VesselId.HasValue)
            throw new InvalidOperationException("Choose the vessel this order is for.");
        var vessel = await db.Vessels.FirstOrDefaultAsync(v => v.Id == request.VesselId.Value && v.CompanyId == companyId)
            ?? throw new InvalidOperationException("Vessel not found for this company.");

        if (request.DepartmentId.HasValue && !await db.Departments.AnyAsync(d => d.Id == request.DepartmentId.Value && d.CompanyId == companyId))
            throw new InvalidOperationException("Department not found for this company.");
        if (request.OrderPortId.HasValue && !await db.Ports.AnyAsync(p => p.Id == request.OrderPortId.Value))
            throw new InvalidOperationException("Order port not found.");

        var supplier = await db.Suppliers.FirstOrDefaultAsync(s => s.Id == request.SupplierId
                && db.CompanySuppliers.Any(cs => cs.CompanyId == companyId && cs.SupplierId == s.Id))
            ?? throw new InvalidOperationException("Choose a supplier linked to your company.");

        if (request.Lines.Count == 0)
            throw new InvalidOperationException("Add at least one line item.");
        if (request.Lines.Any(l => l.Quantity <= 0))
            throw new InvalidOperationException("Every line needs a quantity above zero.");
        if (request.Lines.GroupBy(l => l.ItemId).Any(g => g.Count() > 1))
            throw new InvalidOperationException("An item can only appear once on a purchase order.");
        if (!request.IsDummy && request.Lines.Any(l => l.UnitPrice <= 0))
            throw new InvalidOperationException("Every line needs a unit price above zero (or mark the order as a record-only PO).");
        if (request.Lines.Any(l => l.DiscountPct is < 0 or > 100) || request.DiscountPct is < 0 or > 100)
            throw new InvalidOperationException("Discounts must be between 0 and 100 percent.");
        if (request.AdditionalCharges < 0)
            throw new InvalidOperationException("Additional charges cannot be negative.");

        var currency = string.IsNullOrWhiteSpace(request.Currency) ? "USD" : request.Currency.Trim().ToUpperInvariant();

        var itemIds = request.Lines.Select(l => l.ItemId).Distinct().ToList();
        var itemCount = await db.Items.CountAsync(i => i.CompanyId == companyId && itemIds.Contains(i.Id));
        if (itemCount != itemIds.Count)
            throw new InvalidOperationException("One or more items were not found in your catalog.");
        await EnsureItemsAllowedAsync(db, companyId, request.DepartmentId, itemIds);

        var nextSeq = 5001 + await db.PurchaseOrders.IgnoreQueryFilters().CountAsync();
        var po = new PurchaseOrder
        {
            CompanyId = companyId,
            PoNumber = $"PO-{nextSeq}",
            RfqId = null,
            SupplierId = supplier.Id,
            CreatedDate = DateTime.UtcNow,
            IsSpot = true,
            IsDummy = request.IsDummy,
            SupplierRef = request.SupplierRef.Trim(),
            VesselId = vessel.Id,
            DepartmentId = request.DepartmentId,
            OrderPortId = request.OrderPortId,
            Remarks = request.Remarks.Trim(),
            Status = PoStatus.PendingApproval,
            Currency = currency,
            DiscountPct = request.IsDummy ? 0m : request.DiscountPct,
            AdditionalCharges = request.IsDummy ? 0m : request.AdditionalCharges,
            AdditionalChargeDescription = request.IsDummy ? "" : request.AdditionalChargeDescription.Trim(),
            Lines = request.Lines.Select(l => new PoLine
            {
                ItemId = l.ItemId,
                Quantity = l.Quantity,
                UnitPrice = request.IsDummy ? 0m : l.UnitPrice,
                DiscountPct = request.IsDummy ? 0m : l.DiscountPct
            }).ToList()
        };
        db.PurchaseOrders.Add(po);
        await db.SaveChangesAsync(); // gives the PO its id

        AuditLog.Add(db, companyId, AuditEntityTypes.PurchaseOrder, po.Id, actor,
            $"CREATED {(po.IsDummy ? "record-only " : "")}spot PO {po.PoNumber} for {supplier.Name} on {vessel.Name}: {po.Lines.Count} lines" +
            (po.IsDummy ? "" : $", total {Money.Format(po.Total, po.Currency)}"));
        if (po.IsDummy)
        {
            po.Status = PoStatus.Approved;
            AuditLog.Add(db, companyId, AuditEntityTypes.PurchaseOrder, po.Id, actor,
                "APPROVED automatically: a record-only PO has no value, so it skips the approval matrix");
        }
        else
        {
            await ApprovalEngine.RouteAsync(db, po, actor);
        }

        await db.SaveChangesAsync();
        return po;
    }

    public async Task<List<PurchaseOrder>> GetPurchaseOrdersAsync(int companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.PurchaseOrders.Where(p => p.CompanyId == companyId).OrderByDescending(p => p.CreatedDate).ToListAsync();
    }

    public async Task<PurchaseOrder?> GetPurchaseOrderAsync(int companyId, int poId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.PurchaseOrders.FirstOrDefaultAsync(p => p.Id == poId && p.CompanyId == companyId);
    }

    public async Task<List<PurchaseOrder>> GetPurchaseOrdersForSupplierAsync(int supplierId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.PurchaseOrders.IgnoreQueryFilters()
            .Where(p => p.SupplierId == supplierId && p.Status == PoStatus.Approved).OrderByDescending(p => p.CreatedDate).ToListAsync();
    }
}
