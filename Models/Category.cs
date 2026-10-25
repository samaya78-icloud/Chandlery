namespace Chandlery.Models;

/// <summary>
/// A node in the shared, global item classification tree (e.g. Store > Central Stores > IMPA >
/// Ropes & Rigging). Shared across all companies — not CompanyId-scoped — since a classification
/// standard benefits every tenant equally.
/// </summary>
public class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? ParentCategoryId { get; set; }
    public DateTime CreatedAt { get; set; }

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
