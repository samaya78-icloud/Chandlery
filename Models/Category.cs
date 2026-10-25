namespace Chandlery.Models;

/// <summary>
/// A node in a company's own item classification tree (e.g. Store > Central Stores > IMPA > Ropes & Rigging).
/// Each company has its own tree, so editing, disabling or deleting a category never affects another company.
/// A disabled category (and everything beneath it) can no longer be chosen on new documents.
/// </summary>
public class Category
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public string Name { get; set; } = "";
    public int? ParentCategoryId { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Ids of the categories that are enabled and have no disabled category above them, i.e. the ones that may be chosen.</summary>
    public static HashSet<int> ActiveIds(List<Category> all)
    {
        var byParent = all.ToLookup(c => c.ParentCategoryId);
        var result = new HashSet<int>();

        void Walk(int? parentId)
        {
            foreach (var category in byParent[parentId])
            {
                if (!category.IsActive || !result.Add(category.Id)) continue;
                Walk(category.Id);
            }
        }

        Walk(null);
        return result;
    }

    /// <summary>Depth-first walk of a flat category list into (Category, breadcrumb path, depth) tuples, siblings alphabetized.</summary>
    public static List<(Category Category, string Path, int Depth)> BuildTree(List<Category> all)
    {
        var byParent = all.ToLookup(c => c.ParentCategoryId);
        var result = new List<(Category, string, int)>();

        void Walk(int? parentId, string parentPath, int depth)
        {
            foreach (var category in byParent[parentId].OrderBy(c => c.Name))
            {
                var path = string.IsNullOrEmpty(parentPath) ? category.Name : $"{parentPath} › {category.Name}";
                result.Add((category, path, depth));
                Walk(category.Id, path, depth + 1);
            }
        }

        Walk(null, "", 0);
        return result;
    }
}

public static class CategoryIds
{
    /// <summary>The category itself plus everything beneath it, so filtering by "Provisions" includes all its sub-categories.</summary>
    public static HashSet<int> Descendants(List<Category> all, int rootId)
    {
        var byParent = all.ToLookup(c => c.ParentCategoryId);
        var result = new HashSet<int> { rootId };
        var queue = new Queue<int>();
        queue.Enqueue(rootId);

        while (queue.Count > 0)
        {
            foreach (var child in byParent[queue.Dequeue()])
                if (result.Add(child.Id))
                    queue.Enqueue(child.Id);
        }
        return result;
    }
}
