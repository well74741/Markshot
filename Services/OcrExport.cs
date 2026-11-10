using System.IO.Compression;
using System.Security;
using System.Text;

namespace Markshot.Services;

/// <summary>
/// The recognised text saved as a file: plain text, Markdown (tables as Markdown tables) or an Excel workbook (each
/// line a row, tab-separated parts in their own cells, numbers as numbers, the first row of a table bold).
/// Works from the text as shown (edits included): two or more lines in a row with the same number of tabs are a table.
/// </summary>
public static class OcrExport
{
    public static readonly (string Label, string Extension)[] Formats = { ("Excel", ".xlsx"), ("Markdown", ".md"), ("文本", ".txt") };

    public static void Save(string path, string text)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".xlsx":
                File.WriteAllBytes(path, Xlsx(text));
                break;
            case ".md":
                File.WriteAllText(path, Markdown(text), new UTF8Encoding(false));
                break;
            default:
                File.WriteAllText(path, Normalize(text), new UTF8Encoding(true)); // (Notepad reads UTF-8 with its mark everywhere)
                break;
        }
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);

    /// <summary>Lines, and for each whether it belongs to a table (and is that table's first row).</summary>
    private static List<(string[] Cells, bool Table, bool First)> Lines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var result = lines.Select(line => (line.Split('\t'), false, false)).ToList();
        for (var i = 0; i < lines.Length;)
        {
            var tabs = lines[i].Count(c => c == '\t');
            var end = i + 1;
            while (tabs > 0 && end < lines.Length && lines[end].Count(c => c == '\t') == tabs)
            {
                end++;
            }

            if (tabs > 0 && end - i >= 2)
            {
                for (var j = i; j < end; j++)
                {
                    result[j] = (lines[j].Split('\t'), true, j == i);
                }

                i = end;
            }
            else
            {
                i++;
            }
        }

        return result;
    }

    public static string Markdown(string text)
    {
        var markdown = new StringBuilder();
        foreach (var (cells, table, first) in Lines(text))
        {
            if (table)
            {
                markdown.Append("| ").Append(string.Join(" | ", cells.Select(Cell))).Append(" |").AppendLine();
                if (first)
                {
                    markdown.Append('|').Append(string.Concat(cells.Select(_ => " --- |"))).AppendLine();
                }

                continue;
            }

            // Plain lines: the indent dropped (four spaces would make a code block), a line break kept as a break.
            var line = string.Join("  ", cells).TrimStart();
            markdown.Append(line.Length == 0 ? "" : line + (line.EndsWith("  ") ? "" : "  ")).AppendLine();
        }

        return markdown.ToString().TrimEnd() + Environment.NewLine;

        static string Cell(string cell) => cell.Trim().Replace("|", "\\|");
    }

    public static byte[] Xlsx(string text)
    {
        var rows = Lines(text);
        var widths = new Dictionary<int, double>();
        var sheet = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>")
            .Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        var data = new StringBuilder("<sheetData>");
        for (var r = 0; r < rows.Count; r++)
        {
            var (cells, _, first) = rows[r];
            data.Append($"<row r=\"{r + 1}\">");
            for (var c = 0; c < cells.Length; c++)
            {
                var value = cells[c].Trim();
                if (value.Length == 0)
                {
                    continue;
                }

                widths[c] = Math.Max(widths.GetValueOrDefault(c), Math.Min(60, value.Sum(ch => ch > 0x2E80 ? 2.1 : 1.1) + 2));
                var reference = Column(c) + (r + 1);
                var style = first ? " s=\"1\"" : "";
                if (IsNumber(value))
                {
                    data.Append($"<c r=\"{reference}\"{style}><v>{value}</v></c>");
                }
                else
                {
                    data.Append($"<c r=\"{reference}\" t=\"inlineStr\"{style}><is><t xml:space=\"preserve\">{SecurityElement.Escape(value)}</t></is></c>");
                }
            }

            data.Append("</row>");
        }

        data.Append("</sheetData>");
        if (widths.Count > 0)
        {
            sheet.Append("<cols>");
            foreach (var (c, width) in widths.OrderBy(pair => pair.Key))
            {
                sheet.Append($"<col min=\"{c + 1}\" max=\"{c + 1}\" width=\"{Math.Max(8, width):0.#}\" customWidth=\"1\"/>");
            }

            sheet.Append("</cols>");
        }

        sheet.Append(data).Append("</worksheet>");

        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            void Add(string name, string content)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }

            Add("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>");
            Add("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            Add("xl/workbook.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
                "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"识别结果\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Add("xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
            Add("xl/styles.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"等线\"/></font><font><b/><sz val=\"11\"/><name val=\"等线\"/></font></fonts>" +
                "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
                "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
                "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                "<cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/></cellXfs></styleSheet>");
            Add("xl/worksheets/sheet1.xml", sheet.ToString());
        }

        return memory.ToArray();
    }

    /// <summary>A number Excel should calculate with (not a code: no leading zero, not too long).</summary>
    private static bool IsNumber(string value) =>
        value.Length <= 15 && !(value.Length > 1 && value[0] == '0' && value[1] != '.') &&
        double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _) &&
        !value.Contains('e', StringComparison.OrdinalIgnoreCase);

    private static string Column(int index)
    {
        var name = "";
        for (index++; index > 0; index = (index - 1) / 26)
        {
            name = (char)('A' + (index - 1) % 26) + name;
        }

        return name;
    }
}
