using Chandlery.Models;

namespace Chandlery.Data;

/// <summary>
/// The classification tree a new company starts with. Illustrative only: NOT the real IMPA Marine Stores Guide, which is a
/// licensed commercial catalogue. The company can rename, add, disable or delete any of it.
/// </summary>
public static class StarterCategories
{
    public static async Task<Dictionary<string, Category>> CreateAsync(ChandleryDbContext db, int companyId)
    {
        var now = DateTime.UtcNow;
        var all = new Dictionary<string, Category>();

        async Task<Category> Add(string name, Category? parent)
        {
            var category = new Category { CompanyId = companyId, Name = name, ParentCategoryId = parent?.Id, CreatedAt = now };
            db.Categories.Add(category);
            await db.SaveChangesAsync(); // children need the parent's generated id
            all[name] = category;
            return category;
        }

        var store = await Add("Store", null);
        var spare = await Add("Spare", null);
        var central = await Add("Central Stores", store);
        var impa = await Add("IMPA", central);
        foreach (var name in new[]
                 {
                     "Ropes, Rigging & Lashing", "Deck Equipment & Machinery", "Safety & Life-Saving Equipment",
                     "Paints, Chemicals & Cleaning", "Lubricants & Oils", "Cabin, Galley & Catering Stores"
                 })
            await Add(name, impa);
        foreach (var name in new[] { "Engine Room Spares", "Deck Machinery Spares", "Electrical & Electronics Spares" })
            await Add(name, spare);

        return all;
    }
}
