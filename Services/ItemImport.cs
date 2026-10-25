using System.Text;

namespace Chandlery.Services;

public record ItemImportRow(string Code, string Name, string CategoryPath, string Uom);

public record ItemImportResult(int Created, int SkippedDuplicates, int CategoriesCreated);

public static class ItemImport
{
    public const int MaxRows = 5000;

    /// <summary>
    /// Parses CSV with columns Code, Name, Category path (Store &gt; Provisions &gt; Dry Goods), UOM.
    /// A header row is optional. Returns rows plus per-row problems; bad rows are skipped, not fatal.
    /// </summary>
    public static (List<ItemImportRow> Rows, List<string> Errors) Parse(string csv)
    {
        var rows = new List<ItemImportRow>();
        var errors = new List<string>();
        var records = Csv.Read(csv);

        for (var i = 0; i < records.Count; i++)
        {
            var f = records[i];
            if (f.All(string.IsNullOrWhiteSpace)) continue;
            if (i == 0 && f[0].Trim().Equals("code", StringComparison.OrdinalIgnoreCase)) continue;

            var line = i + 1;
            if (rows.Count >= MaxRows)
            {
                errors.Add($"Stopped at row {line}: a single import is limited to {MaxRows} items.");
                break;
            }
            if (f.Count < 4)
            {
                errors.Add($"Row {line}: expected 4 columns (Code, Name, Category, UOM).");
                continue;
            }

            var code = f[0].Trim();
            var name = f[1].Trim();
            var path = string.Join(" > ", f[2].Replace('›', '>').Split('>', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            var uom = f[3].Trim();

            if (code.Length == 0 || name.Length == 0 || path.Length == 0 || uom.Length == 0)
                errors.Add($"Row {line}: code, name, category and UOM are all required.");
            else if (code.Length > 50)
                errors.Add($"Row {line}: code is longer than 50 characters.");
            else
                rows.Add(new ItemImportRow(code, name, path, uom));
        }

        return (rows, errors);
    }
}
