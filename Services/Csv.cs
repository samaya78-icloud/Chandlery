using System.Text;

namespace Chandlery.Services;

public static class Csv
{
    /// <summary>Minimal RFC-4180 reader: quoted fields, commas and newlines inside quotes, "" as an escaped quote.</summary>
    public static List<List<string>> Read(string text)
    {
        var records = new List<List<string>>();
        var field = new StringBuilder();
        var current = new List<string>();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else field.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { current.Add(field.ToString()); field.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                current.Add(field.ToString());
                field.Clear();
                records.Add(current);
                current = new List<string>();
            }
            else field.Append(c);
        }

        if (field.Length > 0 || current.Count > 0)
        {
            current.Add(field.ToString());
            records.Add(current);
        }
        return records;
    }

    /// <summary>Quotes a cell for CSV output. Cells starting with = + - @ are prefixed so spreadsheets don't run them as formulas.</summary>
    public static string Cell(string? value)
    {
        var text = value ?? "";
        if (text.Length > 0 && "=+-@".Contains(text[0])) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
