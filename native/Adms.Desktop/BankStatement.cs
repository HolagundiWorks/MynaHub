using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace Adms.Desktop;

internal sealed class StatementFile
{
    public required string Text { get; init; }
    public required string[] Headers { get; init; }
    public required string[] Labels { get; init; }
    public required string[][] Sample { get; init; }
    public required bool Heading { get; init; }
}

internal static class BankStatement
{
    public static StatementFile Open(string path)
    {
        if (path.EndsWith(".xls", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Save the workbook as .xlsx or .csv, then choose it again.");
        var rows = path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
            ? ReadXlsx(path)
            : ReadDelimited(File.ReadAllText(path));
        rows = rows.Where(row => row.Any(cell => cell.Trim().Length > 0)).ToList();
        if (rows.Count == 0)
            throw new InvalidOperationException("The file has no rows.");
        var headerAt = FindHeader(rows);
        if (headerAt >= 0)
        {
            var table = rows.Skip(headerAt).ToList();
            var width = table.Max(row => row.Length);
            var headers = new string[width];
            var labels = new string[width];
            var sampleRow = table.Count > 1 ? table[1] : [];
            for (var i = 0; i < width; i++)
            {
                headers[i] = i < table[0].Length && table[0][i].Length > 0 ? table[0][i] : $"Column {i + 1}";
                labels[i] = i < sampleRow.Length && sampleRow[i].Length > 0 ? $"{headers[i]} — {sampleRow[i]}" : headers[i];
            }
            return new StatementFile
            {
                Text = ToCsv(table),
                Headers = headers,
                Labels = labels,
                Sample = table.Skip(1).Take(2).ToArray(),
                Heading = true,
            };
        }
        var columns = rows.Max(row => row.Length);
        var names = new string[columns];
        var shown = new string[columns];
        for (var i = 0; i < columns; i++)
        {
            names[i] = $"Column {i + 1}";
            var sample = Sample(rows, i);
            shown[i] = sample.Length == 0 ? names[i] : $"{names[i]} — {sample}";
        }
        return new StatementFile
        {
            Text = ToCsv(rows),
            Headers = names,
            Labels = shown,
            Sample = rows.Take(2).ToArray(),
            Heading = false,
        };
    }

    public static int? Suggest(string[] headers, params string[] keys)
    {
        for (var i = 0; i < headers.Length; i++)
        {
            if (Hit(headers[i], keys))
                return i;
        }
        return null;
    }

    private static int FindHeader(List<string[]> rows)
    {
        var limit = Math.Min(rows.Count, 40);
        for (var i = 0; i < limit; i++)
        {
            var date = rows[i].Any(cell => Hit(cell, "date"));
            var particulars = rows[i].Any(cell => Hit(cell, "particulars", "narration", "description", "remarks", "details"));
            var money = rows[i].Any(cell => Hit(cell, "credit", "debit", "deposit", "withdrawal", "withdraw", "cr", "dr"));
            if ((date && particulars) || (date && money) || (particulars && money))
                return i;
        }
        return -1;
    }

    private static bool Hit(string cell, params string[] keys)
    {
        var header = cell.Trim().Trim('"').ToLowerInvariant();
        if (header.Length == 0 || header.Length > 40)
            return false;
        foreach (var key in keys)
        {
            if (key.Length <= 2)
            {
                if (header == key || header == key + ".")
                    return true;
            }
            else if (header.Contains(key, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static string Sample(List<string[]> rows, int column)
    {
        foreach (var row in rows)
        {
            if (column < row.Length && row[column].Trim().Length > 0)
                return row[column].Trim();
        }
        return "";
    }

    private static List<string[]> ReadDelimited(string text)
    {
        var lines = text.Replace("\uFEFF", "").Split('\n').Select(line => line.Trim('\r')).Where(line => line.Trim().Length > 0).ToList();
        var delimiter = Delimiter(lines);
        return lines.Select(line => Split(line, delimiter)).ToList();
    }

    private static string Delimiter(List<string> lines)
    {
        var best = ",";
        var count = 1;
        foreach (var line in lines.Take(20))
        {
            foreach (var mark in new[] { ",", ";", "\t", "|" })
            {
                var n = Split(line, mark).Length;
                if (n > count)
                {
                    count = n;
                    best = mark;
                }
            }
        }
        return best;
    }

    private static string[] Split(string line, string delimiter)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var mark = delimiter.Length == 0 ? ',' : delimiter[0];
        foreach (var c in line)
        {
            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (!quoted && c == mark)
            {
                parts.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        parts.Add(current.ToString().Trim());
        return parts.ToArray();
    }

    private static string ToCsv(List<string[]> rows)
    {
        var width = rows.Max(row => row.Length);
        var text = new StringBuilder();
        foreach (var row in rows)
        {
            for (var i = 0; i < width; i++)
            {
                if (i > 0)
                    text.Append(',');
                var cell = i < row.Length ? row[i] : "";
                if (cell.Contains('"') || cell.Contains(',') || cell.Contains('\n'))
                    text.Append('"').Append(cell.Replace("\"", "\"\"")).Append('"');
                else
                    text.Append(cell);
            }
            text.Append('\n');
        }
        return text.ToString();
    }

    private static List<string[]> ReadXlsx(string path)
    {
        try
        {
            using var zip = new ZipArchive(File.OpenRead(path), ZipArchiveMode.Read);
            var strings = SharedStrings(zip);
            var dates = DateStyles(zip);
            var entry = zip.GetEntry(FirstSheet(zip)) ?? throw new InvalidOperationException("The workbook has no sheet.");
            using var stream = entry.Open();
            var sheet = XDocument.Load(stream);
            var rows = new List<string[]>();
            foreach (var row in sheet.Descendants().Where(element => element.Name.LocalName == "row"))
            {
                var cells = Enumerable.Repeat("", 32).ToArray();
                foreach (var cell in row.Elements().Where(element => element.Name.LocalName == "c"))
                {
                    var column = ColumnIndex((string?)cell.Attribute("r") ?? "");
                    if (column < 0 || column >= cells.Length)
                        continue;
                    cells[column] = CellText(cell, strings, dates);
                }
                var last = cells.Length;
                while (last > 0 && cells[last - 1].Length == 0)
                    last--;
                if (last == 0)
                    continue;
                rows.Add(cells[..last]);
            }
            return rows;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new InvalidOperationException("This Excel file could not be read. Save it as .xlsx or .csv and choose it again.");
        }
    }

    private static string FirstSheet(ZipArchive zip)
    {
        var rels = zip.GetEntry("xl/_rels/workbook.xml.rels");
        if (rels != null)
        {
            using var stream = rels.Open();
            var doc = XDocument.Load(stream);
            var sheet = doc.Descendants().FirstOrDefault(element =>
                element.Name.LocalName == "Relationship"
                && ((string?)element.Attribute("Type") ?? "").Contains("worksheet", StringComparison.OrdinalIgnoreCase));
            var target = (string?)sheet?.Attribute("Target");
            if (!string.IsNullOrWhiteSpace(target))
            {
                var path = target.Replace('\\', '/').TrimStart('/');
                if (!path.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
                    path = "xl/" + path;
                return path;
            }
        }
        return "xl/worksheets/sheet1.xml";
    }

    private static List<string> SharedStrings(ZipArchive zip)
    {
        var list = new List<string>();
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry == null)
            return list;
        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        foreach (var item in doc.Descendants().Where(element => element.Name.LocalName == "si"))
            list.Add(string.Concat(item.Descendants().Where(element => element.Name.LocalName == "t").Select(element => element.Value)));
        return list;
    }

    private static HashSet<int> DateStyles(ZipArchive zip)
    {
        var dates = new HashSet<int>();
        var entry = zip.GetEntry("xl/styles.xml");
        if (entry == null)
            return dates;
        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        var custom = new Dictionary<int, string>();
        foreach (var format in doc.Descendants().Where(element => element.Name.LocalName == "numFmt"))
        {
            if (int.TryParse((string?)format.Attribute("numFmtId"), out var id))
                custom[id] = (string?)format.Attribute("formatCode") ?? "";
        }
        var index = 0;
        foreach (var style in doc.Descendants().Where(element => element.Name.LocalName == "xf" && element.Parent?.Name.LocalName == "cellXfs"))
        {
            var id = int.TryParse((string?)style.Attribute("numFmtId"), out var parsed) ? parsed : 0;
            custom.TryGetValue(id, out var code);
            if (IsDateFormat(id, code))
                dates.Add(index);
            index++;
        }
        return dates;
    }

    private static bool IsDateFormat(int id, string? code)
    {
        if (id is >= 14 and <= 22 or >= 27 and <= 36 or >= 45 and <= 47 or >= 50 and <= 58)
            return true;
        if (string.IsNullOrEmpty(code))
            return false;
        var format = code.ToLowerInvariant();
        if (format.Contains('h') && !format.Contains('d') && !format.Contains('y'))
            return false;
        return format.Contains('y') || format.Contains('d');
    }

    private static string CellText(XElement cell, List<string> strings, HashSet<int> dates)
    {
        var type = (string?)cell.Attribute("t") ?? "";
        if (type == "inlineStr")
            return string.Concat(cell.Descendants().Where(element => element.Name.LocalName == "t").Select(element => element.Value)).Trim();
        var raw = cell.Elements().FirstOrDefault(element => element.Name.LocalName == "v")?.Value ?? "";
        if (type == "s" && int.TryParse(raw, out var index) && index >= 0 && index < strings.Count)
            return strings[index].Trim();
        if (type == "d")
            return raw.Trim();
        if (raw.Length == 0 || type is "b" or "e" or "str")
            return raw.Trim();
        var style = int.TryParse((string?)cell.Attribute("s"), out var styleIndex) ? styleIndex : -1;
        if (dates.Contains(style) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
        {
            try
            {
                return DateTime.FromOADate(serial).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
            }
            catch (ArgumentException)
            {
                return raw.Trim();
            }
        }
        return raw.Trim();
    }

    private static int ColumnIndex(string reference)
    {
        var index = 0;
        var seen = false;
        foreach (var c in reference)
        {
            if (!char.IsLetter(c))
                break;
            seen = true;
            index = index * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
        }
        return seen ? index - 1 : -1;
    }
}
