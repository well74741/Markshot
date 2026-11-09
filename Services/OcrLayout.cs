using System.Text;

namespace Markshot.Services;

/// <summary>A table found in the recognised text: rows × columns of cell texts ("" for an empty cell).</summary>
public sealed record OcrTable(IReadOnlyList<IReadOnlyList<string>> Rows)
{
    public int ColumnCount => Rows.Count == 0 ? 0 : Rows.Max(row => row.Count);

    /// <summary>Tab-separated (pastes into a spreadsheet cell by cell).</summary>
    public string ToTsv() => string.Join(Environment.NewLine, Rows.Select(row => string.Join('\t', row)));

    /// <summary>An HTML table (pastes into Word / Excel / web editors as a table).</summary>
    public string ToHtml()
    {
        var html = new StringBuilder("<table border=\"1\" style=\"border-collapse:collapse\">");
        foreach (var row in Rows)
        {
            html.Append("<tr>");
            for (var c = 0; c < ColumnCount; c++)
            {
                var text = c < row.Count ? row[c] : "";
                html.Append("<td style=\"padding:2px 6px\">").Append(System.Net.WebUtility.HtmlEncode(text)).Append("</td>");
            }

            html.Append("</tr>");
        }

        return html.Append("</table>").ToString();
    }
}

/// <summary>What text recognition read: the text with its layout, and the tables in it.</summary>
public sealed record OcrDocument(string Text, IReadOnlyList<OcrTable> Tables)
{
    public static readonly OcrDocument Empty = new("", Array.Empty<OcrTable>());
}

/// <summary>
/// Rebuilds the layout of recognised text from the boxes the recogniser found:
/// <list type="bullet">
/// <item>boxes sharing a row are one line, left to right; indents and paragraph gaps kept;</item>
/// <item>fractions (a box above another, between the rows of the text around them) become (a)/(b);</item>
/// <item>tables: rows of several cells lined up in the same columns — tab-separated in the text, and a grid of cells;</item>
/// <item>text in columns (two columns of prose side by side): read column by column, not across.</item>
/// </list>
/// </summary>
public static class OcrLayout
{
    private sealed class Row
    {
        public List<OcrTextBlock> Blocks { get; } = new();
        public List<Cell> Cells { get; } = new();
        public int Top => Blocks.Min(block => block.Box.Y);
        public int Bottom => Blocks.Max(block => block.Box.Y + block.Box.Height);
        public int Left => Blocks.Min(block => block.Box.X);
    }

    private sealed record Cell(int Left, int Right, string Text)
    {
        public int Width => Right - Left;
    }

    public static OcrDocument Analyze(IReadOnlyList<OcrTextBlock> blocks)
    {
        var items = blocks.Where(block => !string.IsNullOrWhiteSpace(block.Text)).ToList();
        if (items.Count == 0)
        {
            return OcrDocument.Empty;
        }

        var lineHeight = Median(items.Select(block => (double)block.Box.Height));
        items = MergeFractions(items, lineHeight);
        var rows = Rows(items);
        foreach (var row in rows)
        {
            row.Blocks.Sort((a, b) => a.Box.X.CompareTo(b.Box.X));
            row.Cells.AddRange(Cells(row.Blocks, lineHeight));
        }

        var left = items.Min(block => block.Box.X);
        var text = new StringBuilder();
        var tables = new List<OcrTable>();
        var previousBottom = (double?)null;

        void NewLine(int top)
        {
            if (previousBottom is { } bottom)
            {
                text.Append(Environment.NewLine);
                if (top - bottom > lineHeight * 1.1)
                {
                    text.Append(Environment.NewLine); // a paragraph gap
                }
            }
        }

        void Indent(int x)
        {
            var indent = (int)Math.Round((x - left) / Math.Max(1, lineHeight));
            if (indent > 0)
            {
                text.Append(' ', Math.Min(indent * 2, 40));
            }
        }

        for (var i = 0; i < rows.Count;)
        {
            if (Grid(rows, i, lineHeight) is { } grid)
            {
                var (count, columns, prose) = grid;
                var run = rows.GetRange(i, count);
                if (prose)
                {
                    // Columns of text: each column top to bottom, one after the other.
                    for (var c = 0; c < columns.Count; c++)
                    {
                        if (c > 0)
                        {
                            text.Append(Environment.NewLine);
                        }

                        var columnBottom = (double?)null;
                        foreach (var row in run)
                        {
                            var parts = row.Cells.Where(cell => Column(columns, cell) == c).ToList();
                            if (parts.Count == 0)
                            {
                                continue;
                            }

                            var top = row.Top;
                            if (columnBottom is { } bottom)
                            {
                                text.Append(Environment.NewLine);
                                if (top - bottom > lineHeight * 1.1)
                                {
                                    text.Append(Environment.NewLine);
                                }
                            }
                            else if (previousBottom != null || c > 0)
                            {
                                text.Append(Environment.NewLine);
                            }

                            text.Append(string.Join(' ', parts.Select(cell => cell.Text)));
                            columnBottom = row.Bottom;
                        }
                    }

                    previousBottom = run[^1].Bottom;
                }
                else
                {
                    var table = new List<IReadOnlyList<string>>();
                    foreach (var row in run)
                    {
                        var cells = new string[columns.Count];
                        Array.Fill(cells, "");
                        foreach (var cell in row.Cells)
                        {
                            var c = Column(columns, cell);
                            cells[c] = cells[c].Length == 0 ? cell.Text : cells[c] + " " + cell.Text;
                        }

                        table.Add(cells);
                        NewLine(row.Top);
                        text.Append(string.Join('\t', cells));
                        previousBottom = row.Bottom;
                    }

                    tables.Add(new OcrTable(table));
                }

                i += count;
                continue;
            }

            var single = rows[i];
            NewLine(single.Top);
            Indent(single.Left);
            for (var c = 0; c < single.Cells.Count; c++)
            {
                if (c > 0)
                {
                    text.Append('\t'); // another column (a wide gap)
                }

                text.Append(single.Cells[c].Text);
            }

            previousBottom = single.Bottom;
            i++;
        }

        return new OcrDocument(text.ToString(), tables);
    }

    /// <summary>Boxes sharing a row (their centres within each other's height).</summary>
    private static List<Row> Rows(List<OcrTextBlock> items)
    {
        var rows = new List<Row>();
        foreach (var block in items.OrderBy(block => block.Box.Y + block.Box.Height / 2.0))
        {
            var center = block.Box.Y + block.Box.Height / 2.0;
            var row = rows.LastOrDefault(candidate =>
            {
                var top = candidate.Top;
                var bottom = candidate.Bottom;
                return center > top && center < bottom &&
                       Math.Abs(center - (top + bottom) / 2.0) < Math.Max(block.Box.Height, bottom - top) * 0.5;
            });
            if (row == null)
            {
                row = new Row();
                rows.Add(row);
            }

            row.Blocks.Add(block);
        }

        return rows;
    }

    /// <summary>The boxes of a row joined into cells: a gap wider than 1.5 lines starts another cell.</summary>
    private static IEnumerable<Cell> Cells(List<OcrTextBlock> row, double lineHeight)
    {
        var start = 0;
        for (var i = 1; i <= row.Count; i++)
        {
            if (i < row.Count && row[i].Box.X - (row[i - 1].Box.X + row[i - 1].Box.Width) <= lineHeight * 1.5)
            {
                continue;
            }

            var text = new StringBuilder();
            for (var j = start; j < i; j++)
            {
                if (j > start)
                {
                    var gap = row[j].Box.X - (row[j - 1].Box.X + row[j - 1].Box.Width);
                    var cjk = IsCjk(row[j - 1].Text.TrimEnd()[^1]) || IsCjk(row[j].Text.TrimStart()[0]);
                    if (!cjk || gap > lineHeight * 0.6)
                    {
                        text.Append(' ');
                    }
                }

                text.Append(row[j].Text.Trim());
            }

            yield return new Cell(row[start].Box.X, row[i - 1].Box.X + row[i - 1].Box.Width, text.ToString());
            start = i;
        }
    }

    /// <summary>
    /// Rows from <paramref name="start"/> on that line up in columns (at least two rows of two cells or more, the cells
    /// in the same columns): how many, the columns, and whether they are columns of prose rather than a table.
    /// </summary>
    private static (int Count, List<(int Left, int Right)> Columns, bool Prose)? Grid(List<Row> rows, int start, double lineHeight)
    {
        if (rows[start].Cells.Count < 2)
        {
            return null;
        }

        var end = start + 1;
        while (end < rows.Count && rows[end].Cells.Count >= 2 && rows[end].Top - rows[end - 1].Bottom <= lineHeight * 2.5)
        {
            end++;
        }

        // Shorten the run until its rows agree on the columns.
        for (; end - start >= 2; end--)
        {
            var run = rows.GetRange(start, end - start);
            var columns = Gutters(run, lineHeight);
            if (columns.Count < 2)
            {
                continue;
            }

            var fitting = run.Count(row => row.Cells.Select(cell => Column(columns, cell)).Distinct().Count() == row.Cells.Count &&
                                           row.Cells.All(cell => Fits(columns[Column(columns, cell)], cell)));
            if (fitting < run.Count * 0.7)
            {
                continue;
            }

            // Prose: few wide columns, the lines filling them, long lines of text.
            var widths = columns.Select(column => column.Right - column.Left).ToList();
            var cells = run.SelectMany(row => row.Cells).ToList();
            var filling = cells.Count(cell => cell.Width >= (columns[Column(columns, cell)].Right - columns[Column(columns, cell)].Left) * 0.6);
            var prose = columns.Count <= 3 && run.Count >= 3 && widths.All(width => width >= lineHeight * 8) &&
                        filling >= cells.Count * 0.7 && cells.Average(cell => cell.Text.Length) >= 12;
            return (run.Count, columns, prose);
        }

        return null;
    }

    /// <summary>The columns of a run of rows: the stretches between the gaps no cell crosses.</summary>
    private static List<(int Left, int Right)> Gutters(List<Row> run, double lineHeight)
    {
        var cells = run.SelectMany(row => row.Cells).OrderBy(cell => cell.Left).ToList();
        var columns = new List<(int Left, int Right)>();
        foreach (var cell in cells)
        {
            if (columns.Count > 0 && cell.Left <= columns[^1].Right + lineHeight * 0.3)
            {
                columns[^1] = (columns[^1].Left, Math.Max(columns[^1].Right, cell.Right));
            }
            else
            {
                columns.Add((cell.Left, cell.Right));
            }
        }

        return columns;
    }

    private static int Column(List<(int Left, int Right)> columns, Cell cell)
    {
        var best = 0;
        var bestOverlap = int.MinValue;
        for (var c = 0; c < columns.Count; c++)
        {
            var overlap = Math.Min(columns[c].Right, cell.Right) - Math.Max(columns[c].Left, cell.Left);
            if (overlap > bestOverlap)
            {
                best = c;
                bestOverlap = overlap;
            }
        }

        return best;
    }

    private static bool Fits((int Left, int Right) column, Cell cell) => cell.Left >= column.Left - 2 && cell.Right <= column.Right + 2;

    /// <summary>
    /// A box right above another (sharing most of their width, close together) with text beside the pair at their
    /// middle height: a fraction. Joined into one box "(numerator)/(denominator)".
    /// </summary>
    private static List<OcrTextBlock> MergeFractions(List<OcrTextBlock> items, double lineHeight)
    {
        var used = new HashSet<OcrTextBlock>();
        var result = new List<OcrTextBlock>();
        foreach (var top in items.OrderBy(block => block.Box.Y))
        {
            if (used.Contains(top))
            {
                continue;
            }

            var bottom = items.FirstOrDefault(other => !used.Contains(other) && !ReferenceEquals(other, top) && Stacked(top, other, lineHeight) &&
                                                       items.Any(beside => !ReferenceEquals(beside, top) && !ReferenceEquals(beside, other) && Beside(beside, top, other, lineHeight)));
            if (bottom == null)
            {
                continue;
            }

            used.Add(top);
            used.Add(bottom);
            var x = Math.Min(top.Box.X, bottom.Box.X);
            var right = Math.Max(top.Box.X + top.Box.Width, bottom.Box.X + bottom.Box.Width);
            var middle = (top.Box.Y + top.Box.Height + bottom.Box.Y) / 2;
            var height = Math.Max(top.Box.Height, bottom.Box.Height);
            result.Add(new OcrTextBlock($"{Group(top.Text)}/{Group(bottom.Text)}",
                new System.Windows.Int32Rect(x, middle - height / 2, right - x, height)));
        }

        result.AddRange(items.Where(block => !used.Contains(block)));
        return result;

        static string Group(string part)
        {
            part = part.Trim();
            return part.Length == 1 || part.All(char.IsLetterOrDigit) ? part : $"({part})";
        }
    }

    private static bool Stacked(OcrTextBlock top, OcrTextBlock bottom, double lineHeight)
    {
        var gap = bottom.Box.Y - (top.Box.Y + top.Box.Height);
        var overlap = Math.Min(top.Box.X + top.Box.Width, bottom.Box.X + bottom.Box.Width) - Math.Max(top.Box.X, bottom.Box.X);
        var narrow = Math.Min(top.Box.Width, bottom.Box.Width);
        return gap >= -lineHeight * 0.4 && gap <= lineHeight * 0.6 && overlap >= narrow * 0.6 &&
               top.Box.Width <= lineHeight * 12 && bottom.Box.Width <= lineHeight * 12;
    }

    /// <summary>A box just left or right of the pair whose middle lies between the two (the line the fraction is in).</summary>
    private static bool Beside(OcrTextBlock beside, OcrTextBlock top, OcrTextBlock bottom, double lineHeight)
    {
        // Its middle at the fraction bar (between the two), not level with either (cells of a table are level with them).
        var middle = beside.Box.Y + beside.Box.Height / 2.0;
        var bar = (top.Box.Y + top.Box.Height + bottom.Box.Y) / 2.0;
        if (Math.Abs(middle - bar) > lineHeight * 0.35)
        {
            return false;
        }

        var pairLeft = Math.Min(top.Box.X, bottom.Box.X);
        var pairRight = Math.Max(top.Box.X + top.Box.Width, bottom.Box.X + bottom.Box.Width);
        var gap = beside.Box.X >= pairRight ? beside.Box.X - pairRight : pairLeft - (beside.Box.X + beside.Box.Width);
        return gap >= -2 && gap <= lineHeight * 2;
    }

    private static bool IsCjk(char c) => c is >= '一' and <= '鿿' or >= '　' and <= '〿' or >= '＀' and <= '￯';

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(value => value).ToList();
        return sorted.Count == 0 ? 16 : sorted[sorted.Count / 2];
    }
}
