using System.Text;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Adms.Desktop;

internal sealed class ReportBlock
{
    public string Heading { get; init; } = "";
    public string Note { get; init; } = "";
    public string[] Headers { get; init; } = [];
    public IReadOnlyList<string[]> Rows { get; init; } = [];
}

internal static class ReportPaper
{
    public static byte[] Pdf(string title, string period, IReadOnlyList<ReportBlock> blocks)
    {
        var lines = Lines(title, period, blocks);
        var pages = new List<string>();
        const int perPage = 46;
        for (var start = 0; start < lines.Count; start += perPage)
        {
            var stream = new StringBuilder();
            stream.Append("BT\n/F1 11 Tf\n");
            var y = 800;
            var count = Math.Min(perPage, lines.Count - start);
            for (var i = 0; i < count; i++)
            {
                var (text, heading) = lines[start + i];
                if (heading)
                    stream.Append("/F1 13 Tf\n");
                stream.Append($"1 0 0 1 40 {y} Tm ({Escape(text)}) Tj\n");
                if (heading)
                    stream.Append("/F1 11 Tf\n");
                y -= 16;
            }
            stream.Append("ET\n");
            pages.Add(stream.ToString());
        }
        if (pages.Count == 0)
            pages.Add("BT\n/F1 11 Tf\n1 0 0 1 40 800 Tm (No report) Tj\nET\n");
        return Assemble(pages);
    }

    public static IReadOnlyList<UIElement> Pages(string title, string period, IReadOnlyList<ReportBlock> blocks)
    {
        var lines = Lines(title, period, blocks);
        var pages = new List<UIElement>();
        const int perPage = 42;
        for (var start = 0; start < Math.Max(lines.Count, 1); start += perPage)
        {
            var stack = new StackPanel { Spacing = 4 };
            var count = lines.Count == 0 ? 0 : Math.Min(perPage, lines.Count - start);
            for (var i = 0; i < count; i++)
            {
                var (text, heading) = lines[start + i];
                stack.Children.Add(new TextBlock
                {
                    Text = text,
                    TextWrapping = TextWrapping.WrapWholeWords,
                    Foreground = new SolidColorBrush(Colors.Black),
                    FontSize = heading ? 16 : 12,
                    FontFamily = new FontFamily(heading ? "Segoe UI" : "Cascadia Mono, Consolas"),
                    Margin = new Thickness(0, heading ? 8 : 0, 0, 0),
                });
            }
            pages.Add(new Border
            {
                Width = 794,
                MinHeight = 1123,
                Background = new SolidColorBrush(Colors.White),
                Padding = new Thickness(48),
                Child = stack,
            });
        }
        return pages;
    }

    private static List<(string Text, bool Heading)> Lines(string title, string period, IReadOnlyList<ReportBlock> blocks)
    {
        var lines = new List<(string, bool)>
        {
            (title, true),
            (period, false),
            ("Figures are the amounts returned by the company file.", false),
        };
        foreach (var block in blocks)
        {
            lines.Add(("", false));
            if (block.Heading.Length > 0)
                lines.Add((block.Heading, true));
            if (block.Note.Length > 0)
                lines.Add((block.Note, false));
            if (block.Headers.Length > 0)
                lines.Add((string.Join("    ", block.Headers), false));
            foreach (var row in block.Rows)
                lines.Add((string.Join("    ", row.Select(cell => cell.Length == 0 ? "—" : cell)), false));
        }
        return lines;
    }

    private static string Escape(string value)
    {
        var text = value.Replace("₹", "Rs ");
        var builder = new StringBuilder();
        foreach (var ch in text)
        {
            if (ch is '(' or ')' or '\\')
                builder.Append('\\').Append(ch);
            else if (ch is '\n' or '\r' or '\t')
                builder.Append(' ');
            else if (ch is >= ' ' and <= '~')
                builder.Append(ch);
            else
                builder.Append('?');
        }
        return builder.ToString();
    }

    private static byte[] Assemble(List<string> pages)
    {
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>" };
        var kids = new StringBuilder("<< /Type /Pages /Kids [");
        var fontId = 3 + pages.Count * 2;
        for (var i = 0; i < pages.Count; i++)
            kids.Append($"{3 + i * 2} 0 R ");
        kids.Append($"] /Count {pages.Count} >>");
        objects.Add(kids.ToString());
        for (var i = 0; i < pages.Count; i++)
        {
            var contentId = 4 + i * 2;
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents {contentId} 0 R /Resources << /Font << /F1 {fontId} 0 R >> >> >>");
            var content = pages[i];
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream");
        }
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        var builder = new StringBuilder();
        builder.Append("%PDF-1.4\n");
        var offsets = new int[objects.Count + 1];
        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i + 1] = Encoding.ASCII.GetByteCount(builder.ToString());
            builder.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append($"xref\n0 {objects.Count + 1}\n");
        builder.Append("0000000000 65535 f \n");
        for (var i = 1; i <= objects.Count; i++)
            builder.Append($"{offsets[i]:D10} 00000 n \n");
        builder.Append($"trailer << /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }
}
