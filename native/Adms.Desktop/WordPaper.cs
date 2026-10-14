using System.Globalization;
using System.Text;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Text;

namespace Adms.Desktop;

/// <summary>
/// Word-style document editing for templates. The accounting engine still stores a template as HTML and CSS
/// and prints from that. This type turns that markup into paragraphs for the rich text box, and writes the
/// document back as the same kind of markup.
/// </summary>
internal static class WordPaper
{
    internal sealed class Run
    {
        public string Text = "";
        public bool Bold;
        public bool Italic;
        public bool Underline;
        public float Size = 11;
        public string Face = "sans";
    }

    internal sealed class Paragraph
    {
        public List<Run> Runs = new();
        public string Align = "left";
        public string List = "";
        public string Role = "p";
    }

    public static IReadOnlyList<Paragraph> ReadHtml(string html)
    {
        var paragraphs = new List<Paragraph>();
        var runs = new List<Run>();
        var stack = new Stack<Frame>();
        stack.Push(new Frame());
        var i = 0;
        while (i < html.Length)
        {
            if (html[i] == '<')
            {
                if (html.AsSpan(i).StartsWith("<!--"))
                {
                    var end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    i = end < 0 ? html.Length : end + 3;
                    continue;
                }
                var close = html.IndexOf('>', i + 1);
                if (close < 0)
                    break;
                var inner = html.Substring(i + 1, close - i - 1).Trim();
                i = close + 1;
                if (inner.Length == 0)
                    continue;
                var selfClosing = inner.EndsWith('/') || inner.EndsWith(" /", StringComparison.Ordinal);
                var closing = inner.StartsWith('/');
                var body = inner.Trim('/').Trim();
                var split = body.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
                var name = (split < 0 ? body : body[..split]).ToLowerInvariant();
                var attrText = split < 0 ? "" : body[split..];
                if (name.Length == 0)
                    continue;
                if (closing)
                {
                    if (name is "p" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "li")
                        Flush(paragraphs, runs, stack, keepEmpty: true);
                    else if (name is "div" or "header" or "footer" or "main" or "section" or "article" or "address" or "blockquote" or "tr" or "table")
                        Flush(paragraphs, runs, stack, keepEmpty: false);
                    Pop(stack, name);
                    continue;
                }
                var frame = Copy(stack.Peek(), name);
                ReadAttributes(attrText, frame);
                if (name is "h1")
                {
                    frame.Bold = true;
                    frame.Size = 22;
                }
                else if (name is "h2")
                {
                    frame.Bold = true;
                    frame.Size = 16;
                }
                else if (name is "h3" or "h4" or "h5" or "h6")
                {
                    frame.Bold = true;
                    frame.Size = 13;
                }
                else if (name is "strong" or "b")
                    frame.Bold = true;
                else if (name is "em" or "i")
                    frame.Italic = true;
                else if (name == "u")
                    frame.Underline = true;
                if (name is "ul")
                    frame.List = "bullet";
                else if (name is "ol")
                    frame.List = "number";
                var block = name is "p" or "div" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "li" or "header" or "footer" or "main" or "section" or "article" or "address" or "blockquote" or "tr" or "table";
                if (block)
                    Flush(paragraphs, runs, stack, keepEmpty: false);
                if ((name is "td" or "th") && runs.Any(run => run.Text.Trim().Length > 0))
                    Append(runs, stack, " | ");
                var voidTag = name is "br" or "hr" or "img" or "col";
                if (!selfClosing && !voidTag)
                    stack.Push(frame);
                if (name is "br" or "hr")
                    Append(runs, stack, "\v");
                else if (name == "img" && Attribute(attrText, "alt") is string alt && alt.Length > 0)
                    Append(runs, stack, alt);
                continue;
            }
            var next = html.IndexOf('<', i);
            if (next < 0)
                next = html.Length;
            var decoded = Decode(html.Substring(i, next - i));
            var leading = decoded.Length > 0 && char.IsWhiteSpace(decoded[0]);
            var collapsed = Collapse(decoded);
            if (leading && runs.Count > 0 && (runs[^1].Text.Length == 0 || !char.IsWhiteSpace(runs[^1].Text[^1])))
                collapsed = collapsed.Length == 0 ? " " : collapsed.StartsWith(' ') ? collapsed : " " + collapsed;
            Append(runs, stack, collapsed);
            i = next;
        }
        Flush(paragraphs, runs, stack, keepEmpty: false);
        if (paragraphs.Count == 0)
            paragraphs.Add(new Paragraph());
        return paragraphs;
    }

    public static IReadOnlyList<Paragraph> ReadWording(string body)
    {
        var paragraphs = new List<Paragraph>();
        var lines = body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (var line in lines)
        {
            var paragraph = new Paragraph();
            var text = line;
            if (text.StartsWith("### ", StringComparison.Ordinal))
            {
                paragraph.Role = "h3";
                text = text[4..];
            }
            else if (text.StartsWith("## ", StringComparison.Ordinal))
            {
                paragraph.Role = "h2";
                text = text[3..];
            }
            else if (text.StartsWith("# ", StringComparison.Ordinal))
            {
                paragraph.Role = "h1";
                text = text[2..];
            }
            paragraph.Runs.Add(new Run
            {
                Text = text,
                Bold = paragraph.Role != "p",
                Size = Canonical(paragraph.Role),
            });
            paragraphs.Add(paragraph);
        }
        if (paragraphs.Count == 0)
            paragraphs.Add(new Paragraph());
        return paragraphs;
    }

    public static IReadOnlyList<Paragraph> Capture(RichEditTextDocument document) => ReadDocument(document);

    public static void Load(RichEditBox box, IReadOnlyList<Paragraph> paragraphs)
    {
        var document = box.Document;
        var builder = new StringBuilder();
        for (var i = 0; i < paragraphs.Count; i++)
        {
            if (i > 0)
                builder.Append('\r');
            foreach (var run in paragraphs[i].Runs)
                builder.Append(run.Text);
        }
        document.SetText(TextSetOptions.None, builder.ToString());
        if (builder.Length == 0)
        {
            document.Selection.SetRange(0, 0);
            return;
        }
        var index = 0;
        for (var i = 0; i < paragraphs.Count; i++)
        {
            var paragraph = paragraphs[i];
            var length = 0;
            foreach (var run in paragraph.Runs)
                length += run.Text.Length;
            var cursor = index;
            foreach (var run in paragraph.Runs)
            {
                if (run.Text.Length == 0)
                    continue;
                ApplyRun(document.GetRange(cursor, cursor + run.Text.Length), run);
                cursor += run.Text.Length;
            }
            var mark = document.GetRange(index, Math.Max(index + 1, index + length));
            ApplyParagraph(mark, paragraph);
            if (i < paragraphs.Count - 1)
                index += length + 1;
        }
        document.Selection.SetRange(0, 0);
    }

    public static (string Html, string Css) WriteHtml(RichEditTextDocument document, string paper)
    {
        var paragraphs = ReadDocument(document);
        var builder = new StringBuilder();
        string? openList = null;
        foreach (var paragraph in paragraphs)
        {
            var list = paragraph.List;
            if (list != openList)
            {
                CloseList(builder, openList);
                if (list == "bullet")
                    builder.Append("<ul>\n");
                else if (list == "number")
                    builder.Append("<ol>\n");
                openList = list.Length == 0 ? null : list;
            }
            var role = paragraph.Role.Length == 0 ? RoleOf(paragraph) : paragraph.Role;
            if (list.Length > 0)
                role = "li";
            var text = Inline(paragraph, role);
            var classes = Classes(paragraph, role);
            var style = ParagraphStyle(paragraph, role);
            builder.Append('<').Append(role);
            if (classes.Length > 0)
                builder.Append(" class=\"").Append(classes).Append('"');
            if (style.Length > 0)
                builder.Append(" style=\"").Append(style).Append('"');
            builder.Append('>').Append(text).Append("</").Append(role).Append(">\n");
        }
        CloseList(builder, openList);
        return (builder.ToString().TrimEnd(), DocumentCss(paper));
    }

    public static string WriteWording(RichEditTextDocument document)
    {
        var builder = new StringBuilder();
        foreach (var paragraph in ReadDocument(document))
        {
            if (builder.Length > 0)
                builder.Append('\n');
            var text = string.Concat(paragraph.Runs.Select(run => run.Text)).Replace("\v", "\n");
            var role = RoleOf(paragraph);
            if (role == "h1" && !text.StartsWith('#'))
                text = "# " + text;
            else if (role == "h2" && !text.StartsWith('#'))
                text = "## " + text;
            else if (role == "h3" && !text.StartsWith('#'))
                text = "### " + text;
            builder.Append(text);
        }
        return builder.ToString();
    }

    public static string WithPage(string css, string paper)
    {
        var rule = PageRule(paper);
        var at = css.IndexOf("@page", StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return rule + "\n" + css;
        var open = css.IndexOf('{', at);
        var close = open < 0 ? -1 : css.IndexOf('}', open);
        if (open < 0 || close < 0)
            return rule + "\n" + css;
        return string.Concat(css.AsSpan(0, at), rule, css.AsSpan(close + 1));
    }

    public static string DetectPaper(string css)
    {
        var at = css.IndexOf("@page", StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return "A4";
        var open = css.IndexOf('{', at);
        var close = open < 0 ? -1 : css.IndexOf('}', open);
        if (open < 0 || close < 0)
            return "A4";
        var body = css.Substring(open, close - open).ToLowerInvariant();
        if (body.Contains("a3"))
            return "A3";
        if (body.Contains("a5"))
            return "A5";
        if (body.Contains("landscape"))
            return "A4 landscape";
        return "A4";
    }

    public static string FaceName(string face) => face switch
    {
        "sans-medium" => "Segoe UI Semilight",
        "sans-semibold" => "Segoe UI Semibold",
        "serif" => "Georgia",
        "serif-semibold" => "Palatino Linotype",
        "mono" => "Consolas",
        _ => "Segoe UI",
    };

    public static string CssFamily(string face) => face switch
    {
        "sans-medium" => "IBM Plex Sans Medium",
        "sans-semibold" => "IBM Plex Sans SemiBold",
        "serif" => "IBM Plex Serif",
        "serif-semibold" => "IBM Plex Serif SemiBold",
        "mono" => "IBM Plex Mono",
        _ => "IBM Plex Sans",
    };

    public static string FaceOf(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return "sans";
        var lower = name.ToLowerInvariant();
        if (lower.Contains("semibold") && lower.Contains("serif"))
            return "serif-semibold";
        if (lower.Contains("palatino"))
            return "serif-semibold";
        if (lower.Contains("georgia") || lower.Contains("serif"))
            return "serif";
        if (lower.Contains("consolas") || lower.Contains("mono") || lower.Contains("cascadia") || lower.Contains("courier"))
            return "mono";
        if (lower.Contains("semilight") || (lower.Contains("medium") && lower.Contains("sans")))
            return "sans-medium";
        if (lower.Contains("semibold"))
            return "sans-semibold";
        return "sans";
    }

    private static List<Paragraph> ReadDocument(RichEditTextDocument document)
    {
        document.GetText(TextGetOptions.None, out var text);
        text = text.TrimEnd('\r');
        var paragraphs = new List<Paragraph>();
        if (text.Length == 0)
        {
            paragraphs.Add(new Paragraph());
            return paragraphs;
        }
        var index = 0;
        while (index <= text.Length)
        {
            var end = text.IndexOf('\r', index);
            if (end < 0)
                end = text.Length;
            var paragraph = new Paragraph();
            if (end > index)
            {
                var span = document.GetRange(index, end);
                paragraph.Align = AlignOf(span.ParagraphFormat.Alignment);
                paragraph.List = ListOf(span.ParagraphFormat.ListType);
                var cursor = index;
                while (cursor < end)
                {
                    var runEnd = RunEnd(document, cursor, end);
                    var format = document.GetRange(cursor, cursor + 1).CharacterFormat;
                    paragraph.Runs.Add(new Run
                    {
                        Text = text.Substring(cursor, runEnd - cursor),
                        Bold = format.Bold == FormatEffect.On,
                        Italic = format.Italic == FormatEffect.On,
                        Underline = Underlined(format.Underline),
                        Size = RoundSize((float)format.Size),
                        Face = FaceOf(format.Name),
                    });
                    cursor = runEnd;
                }
            }
            paragraph.Role = RoleOf(paragraph);
            paragraphs.Add(paragraph);
            if (end >= text.Length)
                break;
            index = end + 1;
        }
        return paragraphs;
    }

    private static int RunEnd(RichEditTextDocument document, int start, int limit)
    {
        var first = document.GetRange(start, start + 1).CharacterFormat;
        var bold = first.Bold == FormatEffect.On;
        var italic = first.Italic == FormatEffect.On;
        var underline = Underlined(first.Underline);
        var size = first.Size;
        var face = first.Name ?? "";
        var low = start + 1;
        var high = limit;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            var format = document.GetRange(start, mid).CharacterFormat;
            var same = format.Bold != FormatEffect.Undefined
                && format.Italic != FormatEffect.Undefined
                && format.Underline != UnderlineType.Undefined
                && (format.Bold == FormatEffect.On) == bold
                && (format.Italic == FormatEffect.On) == italic
                && Underlined(format.Underline) == underline
                && Math.Abs(format.Size - size) < 0.4
                && string.Equals(format.Name, face, StringComparison.OrdinalIgnoreCase);
            if (same)
                low = mid;
            else
                high = mid - 1;
        }
        return low;
    }

    private static void ApplyRun(ITextRange range, Run run)
    {
        range.CharacterFormat.Name = FaceName(run.Face);
        range.CharacterFormat.Size = run.Size;
        range.CharacterFormat.Bold = run.Bold ? FormatEffect.On : FormatEffect.Off;
        range.CharacterFormat.Italic = run.Italic ? FormatEffect.On : FormatEffect.Off;
        range.CharacterFormat.Underline = run.Underline ? UnderlineType.Single : UnderlineType.None;
    }

    private static void ApplyParagraph(ITextRange range, Paragraph paragraph)
    {
        range.ParagraphFormat.Alignment = paragraph.Align switch
        {
            "center" => ParagraphAlignment.Center,
            "right" => ParagraphAlignment.Right,
            "justify" => ParagraphAlignment.Justify,
            _ => ParagraphAlignment.Left,
        };
        range.ParagraphFormat.ListType = paragraph.List switch
        {
            "bullet" => MarkerType.Bullet,
            "number" => MarkerType.Arabic,
            _ => MarkerType.None,
        };
        if (paragraph.List == "number")
            range.ParagraphFormat.ListStyle = MarkerStyle.Plain;
        range.ParagraphFormat.SpaceBefore = 0;
        range.ParagraphFormat.SpaceAfter = paragraph.Role == "p" ? 8 : 4;
    }

    private static string RoleOf(Paragraph paragraph)
    {
        var meaningful = paragraph.Runs.Where(run => run.Text.Replace("\v", "").Trim().Length > 0).ToList();
        if (meaningful.Count == 0 || meaningful.Any(run => !run.Bold))
            return "p";
        var size = meaningful.Max(run => run.Size);
        if (size >= 20)
            return "h1";
        if (size >= 15)
            return "h2";
        if (size >= 12.5f)
            return "h3";
        return "p";
    }

    private static float Canonical(string role) => role switch
    {
        "h1" => 22,
        "h2" => 16,
        "h3" => 13,
        _ => 11,
    };

    private static string Inline(Paragraph paragraph, string role)
    {
        var heading = role is "h1" or "h2" or "h3";
        var sharedFace = SharedFace(paragraph);
        var sharedSize = SharedSize(paragraph);
        var builder = new StringBuilder();
        foreach (var run in paragraph.Runs)
        {
            if (run.Text.Length == 0)
                continue;
            var parts = run.Text.Split('\v');
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                    builder.Append("<br/>");
                if (parts[i].Length == 0)
                    continue;
                var text = Escape(parts[i]);
                if (run.Bold && !heading)
                    text = "<strong>" + text + "</strong>";
                if (run.Italic)
                    text = "<em>" + text + "</em>";
                if (run.Underline)
                    text = "<u>" + text + "</u>";
                if (run.Face != "sans" && run.Face != sharedFace)
                    text = "<span style=\"font-family: '" + CssFamily(run.Face) + "'\">" + text + "</span>";
                if (sharedSize == null && Math.Abs(run.Size - Canonical(role)) > 0.6)
                    text = "<span style=\"font-size: " + Points(run.Size) + "\">" + text + "</span>";
                builder.Append(text);
            }
        }
        return builder.ToString();
    }

    private static string Classes(Paragraph paragraph, string role)
    {
        var classes = new List<string>();
        if (paragraph.Align is "center" or "right" or "justify")
            classes.Add(paragraph.Align);
        return string.Join(' ', classes);
    }

    private static string ParagraphStyle(Paragraph paragraph, string role)
    {
        var parts = new List<string>();
        var face = SharedFace(paragraph);
        if (face != null && face != "sans")
            parts.Add("font-family: '" + CssFamily(face) + "'");
        var size = SharedSize(paragraph);
        if (size != null && Math.Abs(size.Value - Canonical(role)) > 0.6)
            parts.Add("font-size: " + Points(size.Value));
        return string.Join("; ", parts);
    }

    private static string? SharedFace(Paragraph paragraph)
    {
        string? face = null;
        foreach (var run in paragraph.Runs)
        {
            if (run.Text.Trim().Length == 0)
                continue;
            if (face == null)
                face = run.Face;
            else if (face != run.Face)
                return null;
        }
        return face;
    }

    private static float? SharedSize(Paragraph paragraph)
    {
        float? size = null;
        foreach (var run in paragraph.Runs)
        {
            if (run.Text.Replace("\v", "").Trim().Length == 0)
                continue;
            if (size == null)
                size = run.Size;
            else if (Math.Abs(size.Value - run.Size) > 0.6)
                return null;
        }
        return size;
    }

    private static void CloseList(StringBuilder builder, string? list)
    {
        if (list == "bullet")
            builder.Append("</ul>\n");
        else if (list == "number")
            builder.Append("</ol>\n");
    }

    public static string DocumentCss(string paper) => PageRule(paper) + "\n" + """
        p, li { font-family: 'IBM Plex Sans'; font-size: 11pt; margin-top: 0; margin-bottom: 8pt; }
        h1 { font-family: 'IBM Plex Sans SemiBold'; font-size: 22pt; font-weight: 400; margin-top: 12pt; margin-bottom: 4pt; }
        h2 { font-family: 'IBM Plex Sans SemiBold'; font-size: 16pt; font-weight: 400; margin-top: 12pt; margin-bottom: 4pt; }
        h3 { font-family: 'IBM Plex Sans Medium'; font-size: 13pt; font-weight: 400; margin-top: 10pt; margin-bottom: 2pt; }
        .center { text-align: center; }
        .right { text-align: right; }
        .justify { text-align: justify; }
        strong { font-family: 'IBM Plex Sans SemiBold'; font-weight: 400; }
        ul, ol { margin-top: 0; margin-bottom: 8pt; }
        header, footer { font-family: 'IBM Plex Sans'; font-size: 11pt; }
        table { border-collapse: collapse; }
        td, th { border-bottom: 0.6pt solid #D5D2CA; padding: 3pt 4pt; vertical-align: top; font-family: 'IBM Plex Sans'; font-size: 11pt; }
        th { font-family: 'IBM Plex Sans Medium'; font-weight: 400; }
        hr { border: none; border-top: 1pt solid #1A1A1A; margin: 0; }
        """;

    private static string PageRule(string paper) => paper switch
    {
        "A4 landscape" => "@page { size: A4 landscape; margin: 16mm 18mm 16mm 18mm; }",
        "A3" => "@page { size: A3; margin: 18mm 16mm 20mm 16mm; }",
        "A5" => "@page { size: A5; margin: 14mm 12mm 16mm 12mm; }",
        _ => "@page { size: A4; margin: 18mm 16mm 20mm 16mm; }",
    };

    private static void Flush(List<Paragraph> paragraphs, List<Run> runs, Stack<Frame> stack, bool keepEmpty)
    {
        Trim(runs);
        var hasText = runs.Any(run => run.Text.Replace("\v", "").Trim().Length > 0 || run.Text.Contains('\v'));
        if (!hasText && !keepEmpty)
        {
            runs.Clear();
            return;
        }
        var frame = stack.Peek();
        var paragraph = new Paragraph
        {
            Runs = runs.Select(run => new Run
            {
                Text = run.Text,
                Bold = run.Bold,
                Italic = run.Italic,
                Underline = run.Underline,
                Size = run.Size,
                Face = run.Face,
            }).ToList(),
            Align = frame.Align,
            List = frame.List,
        };
        if (paragraph.Runs.Count == 0)
            paragraph.Runs.Add(new Run { Size = frame.Size, Bold = frame.Bold, Face = frame.Face });
        paragraph.Role = frame.Tag switch
        {
            "h1" => "h1",
            "h2" => "h2",
            "h3" or "h4" or "h5" or "h6" => "h3",
            _ => RoleOf(paragraph),
        };
        paragraphs.Add(paragraph);
        runs.Clear();
    }

    private static void Append(List<Run> runs, Stack<Frame> stack, string text)
    {
        if (text.Length == 0)
            return;
        var frame = stack.Peek();
        if (runs.Count == 0)
            text = text.TrimStart();
        if (text.Length == 0)
            return;
        var last = runs.Count > 0 ? runs[^1] : null;
        if (last != null && last.Bold == frame.Bold && last.Italic == frame.Italic && last.Underline == frame.Underline && Math.Abs(last.Size - frame.Size) < 0.2 && last.Face == frame.Face)
            last.Text += text;
        else
            runs.Add(new Run
            {
                Text = text,
                Bold = frame.Bold,
                Italic = frame.Italic,
                Underline = frame.Underline,
                Size = frame.Size,
                Face = frame.Face,
            });
    }

    private static void Trim(List<Run> runs)
    {
        while (runs.Count > 0 && runs[0].Text.TrimStart().Length == 0 && !runs[0].Text.Contains('\v'))
            runs.RemoveAt(0);
        if (runs.Count > 0 && !runs[0].Text.Contains('\v'))
            runs[0].Text = runs[0].Text.TrimStart();
        while (runs.Count > 0 && runs[^1].Text.TrimEnd().Length == 0 && !runs[^1].Text.Contains('\v'))
            runs.RemoveAt(runs.Count - 1);
        if (runs.Count > 0 && !runs[^1].Text.Contains('\v'))
            runs[^1].Text = runs[^1].Text.TrimEnd();
        if (runs.Count == 1 && runs[0].Text.Length == 0)
            runs.Clear();
    }

    private static void Pop(Stack<Frame> stack, string name)
    {
        if (stack.Count > 1 && stack.Peek().Tag == name)
        {
            stack.Pop();
            return;
        }
        var kept = new Stack<Frame>();
        while (stack.Count > 1 && stack.Peek().Tag != name)
            kept.Push(stack.Pop());
        if (stack.Count > 1 && stack.Peek().Tag == name)
            stack.Pop();
        while (kept.Count > 0)
            stack.Push(kept.Pop());
    }

    private static Frame Copy(Frame parent, string tag) => new()
    {
        Tag = tag,
        Bold = parent.Bold,
        Italic = parent.Italic,
        Underline = parent.Underline,
        Size = parent.Size,
        Face = parent.Face,
        Align = parent.Align,
        List = parent.List,
    };

    private static void ReadAttributes(string attrs, Frame frame)
    {
        var cls = Attribute(attrs, "class");
        if (cls != null)
        {
            foreach (var token in cls.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                switch (token)
                {
                    case "firm-name":
                        frame.Bold = true;
                        frame.Size = 16;
                        break;
                    case "meta":
                    case "registration":
                        frame.Size = 9;
                        break;
                    case "doc-ref":
                        frame.Face = "mono";
                        frame.Size = 9;
                        break;
                    case "serif":
                        frame.Face = "serif";
                        break;
                    case "mono":
                        frame.Face = "mono";
                        break;
                    case "center":
                        frame.Align = "center";
                        break;
                    case "right":
                    case "amount":
                        frame.Align = "right";
                        break;
                    case "justify":
                        frame.Align = "justify";
                        break;
                }
            }
        }
        var style = Attribute(attrs, "style");
        if (style == null)
            return;
        foreach (var decl in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = decl.Split(':', 2);
            if (parts.Length != 2)
                continue;
            var prop = parts[0].Trim().ToLowerInvariant();
            var value = parts[1].Trim().ToLowerInvariant();
            switch (prop)
            {
                case "font-weight" when value is "bold" or "semibold" or "600" or "700":
                    frame.Bold = true;
                    break;
                case "font-style" when value == "italic":
                    frame.Italic = true;
                    break;
                case "text-decoration" when value.Contains("underline"):
                    frame.Underline = true;
                    break;
                case "text-align" when value is "center" or "right" or "justify" or "left":
                    frame.Align = value;
                    break;
                case "font-size" when value.EndsWith("pt") && float.TryParse(value[..^2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var size):
                    frame.Size = Math.Max(8, size);
                    break;
                case "font-family":
                    frame.Face = FaceOf(value);
                    break;
            }
        }
    }

    private static string? Attribute(string attrs, string name)
    {
        var search = name + "=";
        var at = attrs.IndexOf(search, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return null;
        var value = attrs[(at + search.Length)..].TrimStart();
        if (value.Length == 0)
            return null;
        if (value[0] is '"' or '\'')
        {
            var end = value.IndexOf(value[0], 1);
            return end < 0 ? value[1..] : value[1..end];
        }
        var stop = value.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
        return stop < 0 ? value : value[..stop];
    }

    private static string Collapse(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pending = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                pending = builder.Length > 0;
                continue;
            }
            if (pending)
                builder.Append(' ');
            pending = false;
            builder.Append(character);
        }
        if (pending)
            builder.Append(' ');
        return builder.ToString();
    }

    private static string Decode(string text)
    {
        if (text.IndexOf('&') < 0)
            return text;
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '&')
            {
                builder.Append(text[i]);
                continue;
            }
            var semi = text.IndexOf(';', i + 1);
            if (semi < 0 || semi - i > 10)
            {
                builder.Append('&');
                continue;
            }
            var token = text.Substring(i + 1, semi - i - 1);
            string? decoded = token switch
            {
                "amp" => "&",
                "lt" => "<",
                "gt" => ">",
                "quot" => "\"",
                "apos" => "'",
                "nbsp" => " ",
                _ => null,
            };
            if (decoded == null && token.StartsWith('#'))
            {
                var hex = token.StartsWith("#x", StringComparison.OrdinalIgnoreCase);
                var digits = token[(hex ? 2 : 1)..];
                if (int.TryParse(digits, hex ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) && code is > 0 and < 0x10000)
                    decoded = char.ConvertFromUtf32(code);
            }
            if (decoded == null)
            {
                builder.Append('&');
                continue;
            }
            builder.Append(decoded);
            i = semi;
        }
        return builder.ToString();
    }

    private static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Points(float size) => size.ToString("0.#", CultureInfo.InvariantCulture) + "pt";

    private static string AlignOf(ParagraphAlignment alignment) => alignment switch
    {
        ParagraphAlignment.Center => "center",
        ParagraphAlignment.Right => "right",
        ParagraphAlignment.Justify => "justify",
        _ => "left",
    };

    private static string ListOf(MarkerType list) => list switch
    {
        MarkerType.Bullet => "bullet",
        MarkerType.Arabic or MarkerType.ArabicWide => "number",
        _ => "",
    };

    private static bool Underlined(UnderlineType underline) => underline is not UnderlineType.None and not UnderlineType.Undefined;

    private static float RoundSize(float size)
    {
        if (size < 1)
            return 11;
        return (float)(Math.Round(size * 2, MidpointRounding.AwayFromZero) / 2);
    }

    private sealed class Frame
    {
        public string Tag = "";
        public bool Bold;
        public bool Italic;
        public bool Underline;
        public float Size = 11;
        public string Face = "sans";
        public string Align = "left";
        public string List = "";
    }
}
