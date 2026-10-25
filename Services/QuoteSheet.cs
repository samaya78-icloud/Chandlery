using System.Globalization;
using System.Text;
using Chandlery.Models;
using ClosedXML.Excel;

namespace Chandlery.Services;

public class QuoteSheetTerms
{
    public string? SupplierRef { get; set; }
    public int? DeliveryDays { get; set; }
    public DateTime? ValidityDate { get; set; }
    public string? Currency { get; set; }
    public decimal? DiscountPct { get; set; }
    public decimal? AdditionalCharges { get; set; }
    public string? AdditionalChargeDescription { get; set; }
    public string? Remarks { get; set; }

    public bool HasAny => SupplierRef is not null || DeliveryDays.HasValue || ValidityDate.HasValue || Currency is not null
        || DiscountPct.HasValue || AdditionalCharges.HasValue || AdditionalChargeDescription is not null || Remarks is not null;
}

public class QuoteSheetLine
{
    public int RfqLineId { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DiscountPct { get; set; }
    public string? Uom { get; set; }
    public string OfferText { get; set; } = "";
}

public class QuoteSheetResult
{
    public QuoteSheetTerms Terms { get; } = new();
    public List<QuoteSheetLine> Lines { get; } = new();
    public List<string> Warnings { get; } = new();
    public int RowsRead { get; set; }
    public int Priced => Lines.Count(l => l.UnitPrice > 0);
}

/// <summary>
/// The supplier's quotation worksheet: one row per RFQ line plus a Terms sheet, as .xlsx or CSV.
/// Uploading never submits a quote; it only fills the form for the supplier to review.
/// </summary>
public static class QuoteSheet
{
    public const long MaxBytes = 5 * 1024 * 1024;
    public const int MaxRows = 5000;

    private const string QuoteSheetName = "Quote";
    private const string TermsSheetName = "Terms";

    private static readonly string[] Headers =
        { "Ref", "Item code", "Item", "Description", "Drawing no.", "Qty", "UOM", "Unit price", "Disc %", "Offered UOM", "Offer / alternative" };

    private record SheetRow(int LineId, string Code, string ItemName, string Description, string DrawingNo,
        decimal Qty, string Uom, decimal Price, decimal Disc, string OfferedUom, string Offer);

    private static List<SheetRow> BuildRows(Rfq rfq, IReadOnlyDictionary<int, Item> items, Quote? quote) =>
        rfq.Lines.Select(line =>
        {
            var item = items.GetValueOrDefault(line.ItemId);
            var ql = quote?.Lines.FirstOrDefault(l => l.RfqLineId == line.Id);
            return new SheetRow(line.Id, item?.Code ?? "", item?.Name ?? "", line.Description ?? "", line.DrawingNo ?? "",
                line.Quantity, line.Uom, ql?.UnitPrice ?? 0m, ql?.DiscountPct ?? 0m, ql?.Uom ?? line.Uom, ql?.OfferText ?? "");
        }).ToList();

    // ---------------------------------------------------------------- build

    public static byte[] BuildXlsx(Rfq rfq, IReadOnlyDictionary<int, Item> items, Quote? quote)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(QuoteSheetName);

        for (var c = 0; c < Headers.Length; c++)
            ws.Cell(1, c + 1).SetValue(Headers[c]);
        var header = ws.Range(1, 1, 1, Headers.Length);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.LightGray;

        var rows = BuildRows(rfq, items, quote);
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var row = i + 2;
            ws.Cell(row, 1).SetValue(r.LineId);
            SetText(ws.Cell(row, 2), r.Code);
            SetText(ws.Cell(row, 3), r.ItemName);
            SetText(ws.Cell(row, 4), r.Description);
            SetText(ws.Cell(row, 5), r.DrawingNo);
            ws.Cell(row, 6).SetValue(r.Qty);
            ws.Cell(row, 6).Style.NumberFormat.Format = "0.###";
            SetText(ws.Cell(row, 7), r.Uom);
            if (r.Price > 0) ws.Cell(row, 8).SetValue(r.Price);
            if (r.Disc > 0) ws.Cell(row, 9).SetValue(r.Disc);
            SetText(ws.Cell(row, 10), r.OfferedUom);
            SetText(ws.Cell(row, 11), r.Offer);
        }

        if (rows.Count > 0)
        {
            ws.Range(2, 8, rows.Count + 1, 8).Style.NumberFormat.Format = "#,##0.00";
            var inputs = ws.Range(2, 8, rows.Count + 1, 11);
            inputs.Style.Fill.BackgroundColor = XLColor.LightYellow;

            var disc = ws.Range(2, 9, rows.Count + 1, 9).CreateDataValidation();
            disc.Decimal.Between(0, 100);
            disc.ErrorMessage = "Discount must be between 0 and 100.";

            var price = ws.Range(2, 8, rows.Count + 1, 8).CreateDataValidation();
            price.Decimal.GreaterThan(-1);
            price.ErrorMessage = "Unit price must be a number, 0 or more.";
        }

        ws.Column(1).Width = 8;
        ws.Column(1).Style.Font.FontColor = XLColor.Gray;
        ws.Column(2).Width = 14;
        ws.Column(3).Width = 42;
        ws.Column(4).Width = 30;
        ws.Column(5).Width = 12;
        ws.Column(6).Width = 10;
        ws.Column(7).Width = 8;
        ws.Column(8).Width = 12;
        ws.Column(9).Width = 8;
        ws.Column(10).Width = 12;
        ws.Column(11).Width = 36;
        ws.SheetView.FreezeRows(1);

        var terms = wb.Worksheets.Add(TermsSheetName);
        terms.Cell(1, 1).SetValue("Field");
        terms.Cell(1, 2).SetValue("Value");
        terms.Cell(1, 3).SetValue("Notes");
        terms.Range(1, 1, 1, 3).Style.Font.Bold = true;
        terms.Range(1, 1, 1, 3).Style.Fill.BackgroundColor = XLColor.LightGray;

        var termRows = new (string Label, string Note)[]
        {
            ("Supplier ref", "Your own quotation number"),
            ("Delivery days", "Whole days"),
            ("Valid until", "Date, e.g. 2026-12-31"),
            ("Currency", "One of: " + string.Join(", ", Money.Currencies)),
            ("Discount %", "Applied to the whole quotation, 0-100"),
            ("Additional charges", "Freight, packing etc., in the currency above"),
            ("Charge description", ""),
            ("Remarks", "")
        };
        for (var i = 0; i < termRows.Length; i++)
        {
            terms.Cell(i + 2, 1).SetValue(termRows[i].Label);
            terms.Cell(i + 2, 3).SetValue(termRows[i].Note);
        }
        if (quote is not null)
        {
            SetText(terms.Cell(2, 2), quote.SupplierRef);
            if (quote.DeliveryDays.HasValue) terms.Cell(3, 2).SetValue(quote.DeliveryDays.Value);
            if (quote.ValidityDate.HasValue) terms.Cell(4, 2).SetValue(quote.ValidityDate.Value.Date);
            SetText(terms.Cell(8, 2), quote.AdditionalChargeDescription);
            SetText(terms.Cell(9, 2), quote.Remarks);
            if (quote.DiscountPct > 0) terms.Cell(6, 2).SetValue(quote.DiscountPct);
            if (quote.AdditionalCharges > 0) terms.Cell(7, 2).SetValue(quote.AdditionalCharges);
        }
        terms.Cell(4, 2).Style.DateFormat.Format = "yyyy-mm-dd";
        terms.Cell(5, 2).SetValue(quote?.Currency ?? "USD");
        terms.Cell(5, 2).CreateDataValidation().List(string.Join(",", Money.Currencies), true);
        terms.Range(2, 2, termRows.Length + 1, 2).Style.Fill.BackgroundColor = XLColor.LightYellow;

        terms.Cell(termRows.Length + 4, 1).SetValue($"RFQ {rfq.RfqNumber} - {rfq.Vessel}");
        terms.Cell(termRows.Length + 4, 1).Style.Font.Bold = true;
        terms.Cell(termRows.Length + 5, 1).SetValue("On the Quote sheet, fill in the yellow columns only (Unit price, Disc %, Offered UOM, Offer / alternative). Leave Unit price empty for items you can't supply. Do not change the Ref column.");
        terms.Column(1).Width = 22;
        terms.Column(2).Width = 24;
        terms.Column(3).Width = 60;

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    public static string BuildCsv(Rfq rfq, IReadOnlyDictionary<int, Item> items, Quote? quote)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(',', Headers.Select(Csv.Cell))).Append("\r\n");

        foreach (var r in BuildRows(rfq, items, quote))
        {
            sb.Append(string.Join(',', new[]
            {
                r.LineId.ToString(CultureInfo.InvariantCulture), Csv.Cell(r.Code), Csv.Cell(r.ItemName), Csv.Cell(r.Description),
                Csv.Cell(r.DrawingNo), r.Qty.ToString("0.###", CultureInfo.InvariantCulture), Csv.Cell(r.Uom),
                r.Price > 0 ? r.Price.ToString("0.00", CultureInfo.InvariantCulture) : "",
                r.Disc > 0 ? r.Disc.ToString("0.##", CultureInfo.InvariantCulture) : "",
                Csv.Cell(r.OfferedUom), Csv.Cell(r.Offer)
            })).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>Text cells are written as text; anything that looks like a formula gets a quote prefix so Excel won't evaluate it.</summary>
    private static void SetText(IXLCell cell, string? text)
    {
        var value = text ?? "";
        cell.SetValue(value);
        if (value.Length > 0 && "=+-@".Contains(value[0]))
            cell.Style.IncludeQuotePrefix = true;
    }

    // ---------------------------------------------------------------- parse

    public static QuoteSheetResult ParseXlsx(Stream stream, Rfq rfq, IReadOnlyDictionary<int, Item> items)
    {
        var result = new QuoteSheetResult();
        try
        {
            using var wb = new XLWorkbook(stream);
            var ws = wb.Worksheets.FirstOrDefault(w => w.Name.Equals(QuoteSheetName, StringComparison.OrdinalIgnoreCase))
                     ?? wb.Worksheets.FirstOrDefault();
            if (ws is null)
            {
                result.Warnings.Add("The workbook has no sheets.");
                return result;
            }

            var rows = new List<(int RowNumber, Dictionary<string, string> Cells)>();
            var headerRow = ws.FirstRowUsed();
            if (headerRow is not null)
            {
                var columns = new Dictionary<int, string>();
                foreach (var cell in headerRow.CellsUsed())
                    columns[cell.Address.ColumnNumber] = Normalize(cell.GetString());

                foreach (var row in ws.RowsUsed().Where(r => r.RowNumber() > headerRow.RowNumber()))
                {
                    if (rows.Count >= MaxRows)
                    {
                        result.Warnings.Add($"Only the first {MaxRows} rows were read.");
                        break;
                    }
                    var cells = new Dictionary<string, string>();
                    foreach (var (col, name) in columns)
                        cells[name] = CellText(row.Cell(col));
                    rows.Add((row.RowNumber(), cells));
                }
            }

            ApplyRows(rows, rfq, items, result);

            var termsSheet = wb.Worksheets.FirstOrDefault(w => w.Name.Equals(TermsSheetName, StringComparison.OrdinalIgnoreCase));
            if (termsSheet is not null)
                ReadTerms(termsSheet, result);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Untrusted upload: anything the Excel reader rejects (bad zip, FileFormatException, malformed XML...)
            // becomes a plain message instead of an unhandled error.
            result.Warnings.Add("That file couldn't be read as an Excel workbook. Download the worksheet again and use the .xlsx format.");
        }
        return result;
    }

    public static QuoteSheetResult ParseCsv(string text, Rfq rfq, IReadOnlyDictionary<int, Item> items)
    {
        var result = new QuoteSheetResult();
        var records = Csv.Read(text.TrimStart('﻿'));
        var headerIndex = records.FindIndex(r => r.Any(c => !string.IsNullOrWhiteSpace(c)));
        if (headerIndex < 0)
        {
            result.Warnings.Add("The file is empty.");
            return result;
        }

        var columns = records[headerIndex].Select(Normalize).ToList();
        var rows = new List<(int RowNumber, Dictionary<string, string> Cells)>();
        for (var i = headerIndex + 1; i < records.Count; i++)
        {
            if (records[i].All(string.IsNullOrWhiteSpace)) continue;
            if (rows.Count >= MaxRows)
            {
                result.Warnings.Add($"Only the first {MaxRows} rows were read.");
                break;
            }
            var cells = new Dictionary<string, string>();
            for (var c = 0; c < columns.Count && c < records[i].Count; c++)
                cells[columns[c]] = records[i][c];
            rows.Add((i + 1, cells));
        }

        ApplyRows(rows, rfq, items, result);
        return result;
    }

    private static string Normalize(string header) => header.Trim().ToLowerInvariant();

    /// <summary>Numbers are read as invariant text so both formats go through the same parser.</summary>
    private static string CellText(IXLCell cell)
    {
        if (cell.IsEmpty()) return "";
        if (cell.DataType == XLDataType.Number)
            return cell.GetDouble().ToString("R", CultureInfo.InvariantCulture);
        if (cell.DataType == XLDataType.DateTime)
            return cell.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return cell.GetString().Trim();
    }

    private static string? Pick(Dictionary<string, string> cells, params string[] names)
    {
        foreach (var n in names)
            if (cells.TryGetValue(n, out var v)) return v;
        return null;
    }

    private static bool TryDecimal(string? text, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().TrimEnd('%').Trim();
        if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out value)) return true;
        // "12,5" written with a decimal comma
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^\d+,\d{1,3}$"))
            return decimal.TryParse(s.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out value);
        return false;
    }

    private static void ApplyRows(List<(int RowNumber, Dictionary<string, string> Cells)> rows, Rfq rfq,
        IReadOnlyDictionary<int, Item> items, QuoteSheetResult result)
    {
        if (rows.Count > 0 && !rows[0].Cells.ContainsKey("unit price"))
        {
            result.Warnings.Add("Couldn't find a 'Unit price' column. Use the worksheet downloaded from this RFQ.");
            return;
        }

        var linesById = rfq.Lines.ToDictionary(l => l.Id);
        var linesByCode = rfq.Lines
            .GroupBy(l => items.GetValueOrDefault(l.ItemId)?.Code ?? "", StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var seen = new HashSet<int>();
        var warnings = new List<string>();

        foreach (var (rowNumber, cells) in rows)
        {
            result.RowsRead++;

            RfqLine? line = null;
            var refText = Pick(cells, "ref");
            if (!string.IsNullOrWhiteSpace(refText))
            {
                if (TryDecimal(refText, out var refId) && linesById.TryGetValue((int)refId, out var byRef))
                    line = byRef;
            }
            if (line is null)
            {
                var code = Pick(cells, "item code", "code")?.Trim();
                if (!string.IsNullOrEmpty(code) && linesByCode.TryGetValue(code, out var matches) && matches.Count == 1)
                    line = matches[0];
            }
            if (line is null)
            {
                warnings.Add($"Row {rowNumber}: doesn't match any line on this RFQ, skipped.");
                continue;
            }
            if (!seen.Add(line.Id))
                warnings.Add($"Row {rowNumber}: line appears more than once; the later row is used.");

            decimal price = 0;
            var priceText = Pick(cells, "unit price", "price");
            if (!string.IsNullOrWhiteSpace(priceText))
            {
                if (!TryDecimal(priceText, out price) || price < 0)
                {
                    warnings.Add($"Row {rowNumber}: unit price '{priceText}' isn't a valid number, skipped.");
                    continue;
                }
                var rounded = Money.Round(price);
                if (rounded != price)
                {
                    warnings.Add($"Row {rowNumber}: unit price {price} rounded to {rounded} (prices are kept to 2 decimals).");
                    price = rounded;
                }
            }

            decimal disc = 0;
            var discText = Pick(cells, "disc %", "discount %", "disc", "discount");
            if (!string.IsNullOrWhiteSpace(discText) && (!TryDecimal(discText, out disc) || disc < 0 || disc > 100))
            {
                warnings.Add($"Row {rowNumber}: discount '{discText}' must be between 0 and 100, skipped.");
                continue;
            }

            var uom = Pick(cells, "offered uom", "offered unit")?.Trim();
            var offer = Pick(cells, "offer / alternative", "offer", "alternative")?.Trim() ?? "";

            result.Lines.RemoveAll(l => l.RfqLineId == line.Id);
            result.Lines.Add(new QuoteSheetLine
            {
                RfqLineId = line.Id,
                UnitPrice = price,
                DiscountPct = disc,
                Uom = string.IsNullOrWhiteSpace(uom) ? null : uom,
                OfferText = offer
            });
        }

        // Summarise so a bad file doesn't bury the page in hundreds of lines.
        result.Warnings.AddRange(warnings.Take(15));
        if (warnings.Count > 15)
            result.Warnings.Add($"…and {warnings.Count - 15} more rows with problems.");
    }

    private static void ReadTerms(IXLWorksheet sheet, QuoteSheetResult result)
    {
        var t = result.Terms;
        foreach (var row in sheet.RowsUsed().Skip(1))
        {
            var label = Normalize(row.Cell(1).GetString());
            var cell = row.Cell(2);
            if (cell.IsEmpty()) continue;
            var text = CellText(cell);

            switch (label)
            {
                case "supplier ref": t.SupplierRef = text; break;
                case "delivery days":
                    if (TryDecimal(text, out var days) && days >= 0 && days == Math.Truncate(days)) t.DeliveryDays = (int)days;
                    else result.Warnings.Add($"Terms: delivery days '{text}' isn't a whole number, ignored.");
                    break;
                case "valid until":
                    if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var valid)) t.ValidityDate = valid.Date;
                    else result.Warnings.Add($"Terms: valid-until date '{text}' wasn't recognised, ignored.");
                    break;
                case "currency":
                    var code = text.Trim().ToUpperInvariant();
                    if (Money.Currencies.Contains(code)) t.Currency = code;
                    else result.Warnings.Add($"Terms: currency '{text}' isn't supported ({string.Join(", ", Money.Currencies)}), ignored.");
                    break;
                case "discount %":
                    if (TryDecimal(text, out var d) && d >= 0 && d <= 100) t.DiscountPct = d;
                    else result.Warnings.Add($"Terms: discount '{text}' must be between 0 and 100, ignored.");
                    break;
                case "additional charges":
                    if (TryDecimal(text, out var charges) && charges >= 0) t.AdditionalCharges = Money.Round(charges);
                    else result.Warnings.Add($"Terms: additional charges '{text}' isn't a valid amount, ignored.");
                    break;
                case "charge description": t.AdditionalChargeDescription = text; break;
                case "remarks": t.Remarks = text; break;
            }
        }
    }
}
