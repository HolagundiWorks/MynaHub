using System.Text;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Windows.UI.Core;

namespace Adms.Desktop;

/// <summary>
/// The template page, laid out the way a word processor shows a printed page:
/// a ruler, a header, flowing text, tables and horizontal lines, then a footer.
/// </summary>
internal sealed class TemplateSheet : UserControl
{
    private const double PxPerMm = 96.0 / 25.4;

    private readonly Canvas _ruler = new() { Height = 18 };
    private readonly RichEditBox _header = Editor("Header");
    private readonly RichEditBox _footer = Editor("Footer");
    private readonly StackPanel _flow = new();
    private readonly Border _headerZone;
    private readonly Border _footerZone;
    private readonly Border _sheet;
    private readonly List<Part> _parts = new();
    private readonly TextBlock _headerHint;
    private readonly TextBlock _footerHint;
    private Part? _selected;
    private RichEditBox? _activeEditor;
    private bool _mute;
    private bool _readOnly;
    private bool _design = true;
    private double _pageW = Mm(210);
    private double _pageH = Mm(297);

    public event Action? Changed;
    public event Action? SelectionChanged;

    public RichEditBox? ActiveEditor
    {
        get
        {
            if (_activeEditor != null && (_design || (_activeEditor != _header && _activeEditor != _footer)))
                return _activeEditor;
            return _parts.FirstOrDefault(part => part.Kind == "text")?.Editor;
        }
    }

    public bool IsReadOnly
    {
        get => _readOnly;
        set
        {
            _readOnly = value;
            _header.IsReadOnly = value;
            _footer.IsReadOnly = value;
            foreach (var part in _parts)
                Lock(part);
        }
    }

    public bool Design
    {
        get => _design;
        set
        {
            _design = value;
            var show = value ? Visibility.Visible : Visibility.Collapsed;
            _ruler.Visibility = show;
            _headerZone.Visibility = show;
            _footerZone.Visibility = show;
        }
    }

    public TemplateSheet()
    {
        _headerHint = Hint("Header");
        _footerHint = Hint("Footer");
        _headerZone = Zone("Header", _header, _headerHint, top: true);
        _footerZone = Zone("Footer", _footer, _footerHint, top: false);
        Watch(_header, _headerHint);
        Watch(_footer, _footerHint);

        var page = new Grid();
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.Children.Add(_headerZone);
        Grid.SetRow(_flow, 1);
        page.Children.Add(_flow);
        Grid.SetRow(_footerZone, 2);
        page.Children.Add(_footerZone);

        _sheet = new Border
        {
            Background = Flow.Ink("SurfaceBackgroundBrush"),
            BorderBrush = Flow.Ink("BorderSubtleBrush"),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Child = page,
        };
        var shadow = new Border
        {
            Background = ShadowBrush(),
            Margin = new Thickness(0, 3, -3, -3),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        var paper = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        paper.Children.Add(shadow);
        paper.Children.Add(_sheet);

        var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 28) };
        column.Children.Add(_ruler);
        column.Children.Add(paper);

        Content = new ScrollViewer
        {
            Background = DeskBrush(),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = column,
        };
        AutomationProperties.SetName(_sheet, "Template page");
        SetPaper("A4");
        _parts.Add(MakeText(null));
        Rebuild();
    }

    public void SetPaper(string paper)
    {
        (_pageW, _pageH) = paper switch
        {
            "A4 landscape" => (Mm(297), Mm(210)),
            "A3" => (Mm(297), Mm(420)),
            "A5" => (Mm(148), Mm(210)),
            _ => (Mm(210), Mm(297)),
        };
        _sheet.Width = _pageW;
        _sheet.MinHeight = _pageH;
        _ruler.Width = _pageW;
        DrawRuler();
        FitLast();
    }

    public void Load(string html)
    {
        _mute = true;
        try
        {
            Design = true;
            _parts.Clear();
            _flow.Children.Clear();
            _selected = null;
            _activeEditor = null;
            Fill(_header, "");
            Fill(_footer, "");
            Read(html);
            if (!_parts.Exists(part => part.Kind == "text"))
                _parts.Add(MakeText(null));
            Rebuild();
        }
        finally
        {
            _mute = false;
        }
    }

    public void LoadPlain(string body)
    {
        _mute = true;
        try
        {
            Design = false;
            _parts.Clear();
            _flow.Children.Clear();
            _selected = null;
            _activeEditor = null;
            var part = MakeText(null);
            part.Pending = WordPaper.ReadWording(body);
            _parts.Add(part);
            Rebuild();
            if (part.Editor!.IsLoaded)
                Commit(part);
        }
        finally
        {
            _mute = false;
        }
    }

    public string Write()
    {
        var builder = new StringBuilder();
        if (!Blank(_header))
            builder.Append("<header>\n").Append(Fragment(_header)).Append("\n</header>\n");
        foreach (var part in _parts)
        {
            if (part.Kind == "line")
                builder.Append("<hr/>\n");
            else if (part.Kind == "table")
                builder.Append("<table>\n").Append(TableBody(part)).Append("</table>\n");
            else if (part.Editor != null)
                builder.Append(Fragment(part.Editor)).Append('\n');
        }
        if (!Blank(_footer))
            builder.Append("<footer>\n").Append(Fragment(_footer)).Append("\n</footer>\n");
        return builder.ToString().TrimEnd();
    }

    public string WriteWording()
    {
        var builder = new StringBuilder();
        foreach (var part in _parts)
        {
            if (part.Kind != "text" || part.Editor == null || !part.Editor.IsLoaded)
                continue;
            if (builder.Length > 0)
                builder.Append('\n');
            builder.Append(WordPaper.WriteWording(part.Editor.Document));
        }
        return builder.ToString();
    }

    public void AddText()
    {
        var editor = ActiveEditor;
        editor?.Focus(FocusState.Programmatic);
    }

    public void AddHeader()
    {
        if (_readOnly || !_design)
            return;
        if (Blank(_header))
            Fill(_header, "<p>{{ company.name }}</p>");
        _header.Focus(FocusState.Programmatic);
    }

    public void AddFooter()
    {
        if (_readOnly || !_design)
            return;
        if (Blank(_footer))
            Fill(_footer, "<p>{{ document.number }}</p>");
        _footer.Focus(FocusState.Programmatic);
    }

    public void AddLine() => InsertObject(MakeLine());

    public void AddTable(int rows, int columns, bool header)
    {
        rows = Math.Clamp(rows, 1, 12);
        columns = Math.Clamp(columns, 1, 8);
        InsertObject(MakeTable(rows, columns, header, null));
    }

    public void RemoveSelected()
    {
        if (_readOnly)
            return;
        if (ReferenceEquals(_activeEditor, _header))
        {
            Fill(_header, "");
            Touch();
            return;
        }
        if (ReferenceEquals(_activeEditor, _footer))
        {
            Fill(_footer, "");
            Touch();
            return;
        }
        if (_selected == null || _selected.Kind == "text")
            return;
        _parts.Remove(_selected);
        _selected = null;
        if (!_parts.Exists(part => part.Kind == "text"))
            _parts.Add(MakeText(null));
        Rebuild();
        Touch();
        SelectionChanged?.Invoke();
    }

    public bool Insert(string token)
    {
        if (_readOnly)
            return false;
        var cell = FocusedCell();
        if (cell != null)
        {
            var at = cell.SelectionStart;
            cell.Text = cell.Text.Insert(at, token);
            cell.SelectionStart = at + token.Length;
            cell.Focus(FocusState.Programmatic);
            return true;
        }
        var editor = ActiveEditor;
        if (editor == null || !editor.IsLoaded)
            return false;
        editor.Document.Selection.SetText(TextSetOptions.None, token);
        var caret = editor.Document.Selection.EndPosition;
        editor.Document.Selection.SetRange(caret, caret);
        editor.Focus(FocusState.Programmatic);
        return true;
    }

    public bool ApplyFace(string face)
    {
        var cell = FocusedCell();
        if (cell == null)
            return false;
        cell.FontFamily = new FontFamily(WordPaper.FaceName(face));
        cell.Tag = face;
        Touch();
        return true;
    }

    public bool ApplySize(int size)
    {
        var cell = FocusedCell();
        if (cell == null)
            return false;
        cell.FontSize = size;
        Touch();
        return true;
    }

    private void InsertObject(Part created)
    {
        if (_readOnly || !_design)
            return;
        var host = _parts.FirstOrDefault(part => part.Kind == "text" && part.Editor == _activeEditor);
        if (host?.Editor == null || !host.Editor.IsLoaded)
        {
            _parts.Add(created);
            _parts.Add(MakeText(null));
            Rebuild();
            (created.FocusTarget() ?? _parts[^1].Editor)?.Focus(FocusState.Programmatic);
            Touch();
            return;
        }
        var index = _parts.IndexOf(host);
        host.Editor.Document.GetText(TextGetOptions.None, out var text);
        var caret = host.Editor.Document.Selection.StartPosition;
        var end = text.TrimEnd('\r').Length;
        if (caret <= 0)
            _parts.Insert(index, created);
        else if (caret >= end)
            _parts.Insert(index + 1, created);
        else
        {
            var rest = Split(host, caret);
            _parts.Insert(index + 1, created);
            if (rest != null)
                _parts.Insert(index + 2, rest);
        }
        var follow = _parts.IndexOf(created) + 1;
        if (follow >= _parts.Count || _parts[follow].Kind != "text")
            _parts.Insert(follow, MakeText(null));
        Rebuild();
        var target = created.FocusTarget();
        if (target == null)
        {
            var next = _parts.IndexOf(created) + 1;
            if (next < _parts.Count)
                target = _parts[next].Editor;
        }
        target?.Focus(FocusState.Programmatic);
        Touch();
    }

    private Part? Split(Part host, int caret)
    {
        var document = host.Editor!.Document;
        var all = WordPaper.Capture(document);
        var before = new List<WordPaper.Paragraph>();
        var after = new List<WordPaper.Paragraph>();
        var seen = 0;
        foreach (var paragraph in all)
        {
            var length = paragraph.Runs.Sum(run => run.Text.Length);
            if (seen + length <= caret)
                before.Add(paragraph);
            else if (seen >= caret)
                after.Add(paragraph);
            else
            {
                var cut = caret - seen;
                before.Add(Slice(paragraph, 0, cut));
                after.Add(Slice(paragraph, cut, length));
            }
            seen += length + 1;
        }
        _mute = true;
        try
        {
            WordPaper.Load(host.Editor, before.Count > 0 ? before : [new WordPaper.Paragraph()]);
        }
        finally
        {
            _mute = false;
        }
        if (after.Count == 0 || after.All(paragraph => paragraph.Runs.All(run => run.Text.Length == 0)))
            return null;
        var rest = MakeText(null);
        rest.Pending = after;
        return rest;
    }

    private static WordPaper.Paragraph Slice(WordPaper.Paragraph source, int start, int end)
    {
        var copy = new WordPaper.Paragraph { Align = source.Align, List = source.List, Role = source.Role };
        var cursor = 0;
        foreach (var run in source.Runs)
        {
            var runEnd = cursor + run.Text.Length;
            var from = Math.Max(start, cursor);
            var to = Math.Min(end, runEnd);
            if (to > from)
            {
                copy.Runs.Add(new WordPaper.Run
                {
                    Text = run.Text[(from - cursor)..(to - cursor)],
                    Bold = run.Bold,
                    Italic = run.Italic,
                    Underline = run.Underline,
                    Size = run.Size,
                    Face = run.Face,
                });
            }
            cursor = runEnd;
        }
        if (copy.Runs.Count == 0)
            copy.Runs.Add(new WordPaper.Run());
        return copy;
    }

    private void Read(string html)
    {
        var flow = new StringBuilder();
        var band = false;
        var headerTaken = false;
        var footerTaken = false;
        var i = 0;
        void Flush()
        {
            if (flow.Length == 0)
                return;
            _parts.Add(MakeText(flow.ToString()));
            flow.Clear();
        }
        while (i < html.Length)
        {
            while (i < html.Length && char.IsWhiteSpace(html[i]))
                i++;
            if (i >= html.Length)
                break;
            if (html.AsSpan(i).StartsWith("<!--", StringComparison.Ordinal))
            {
                var end = html.IndexOf("-->", i, StringComparison.Ordinal);
                if (end < 0)
                    break;
                var comment = html.Substring(i + 4, end - (i + 4));
                band = comment.Contains("adms band", StringComparison.Ordinal);
                i = end + 3;
                continue;
            }
            if (html[i] != '<')
            {
                i++;
                continue;
            }
            var (name, outer, next) = ReadElement(html, i);
            i = next;
            if (name.Length == 0)
                continue;
            if (name == "header" && !headerTaken)
            {
                Flush();
                Fill(_header, outer);
                headerTaken = true;
                continue;
            }
            if (name == "footer" && !footerTaken)
            {
                Flush();
                Fill(_footer, outer);
                footerTaken = true;
                continue;
            }
            if (name == "hr")
            {
                Flush();
                _parts.Add(MakeLine());
                band = false;
                continue;
            }
            if (name == "table")
            {
                Flush();
                if (band)
                    Flatten(outer);
                else
                    _parts.Add(MakeTable(0, 0, false, outer));
                band = false;
                continue;
            }
            flow.Append(outer).Append('\n');
        }
        Flush();
    }

    private void Flatten(string tableHtml)
    {
        var cells = CellsOf(tableHtml).Where(cell => cell.Text.Trim().Length > 0 || cell.Html.Contains('<', StringComparison.Ordinal)).ToList();
        if (cells.Count == 0)
        {
            _parts.Add(MakeTable(0, 0, false, tableHtml));
            return;
        }
        foreach (var cell in cells)
        {
            var html = cell.Html.Trim();
            if (html.StartsWith("<hr", StringComparison.OrdinalIgnoreCase))
                _parts.Add(MakeLine());
            else if (html.StartsWith("<table", StringComparison.OrdinalIgnoreCase))
                _parts.Add(MakeTable(0, 0, false, html));
            else
                _parts.Add(MakeText(html));
        }
    }

    private void Rebuild()
    {
        _flow.Children.Clear();
        foreach (var part in _parts)
            _flow.Children.Add(part.Root);
        FitLast();
    }

    private Part MakeText(string? html)
    {
        var editor = Editor("Document");
        var host = new Grid { Margin = new Thickness(48, 4, 48, 4) };
        host.Children.Add(editor);
        var part = new Part { Kind = "text", Editor = editor, Root = host, PendingHtml = html };
        editor.TextChanged += (_, _) =>
        {
            Grow(editor);
            if (!_mute)
                Touch();
        };
        editor.GotFocus += (_, _) => Activate(editor, null);
        editor.SelectionChanged += (_, _) =>
        {
            _activeEditor = editor;
            SelectionChanged?.Invoke();
        };
        editor.Loaded += (_, _) => Commit(part);
        editor.PreviewKeyDown += (_, e) => BlockCharacterKeys(e);
        return part;
    }

    private Part MakeLine()
    {
        var rule = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 10, 0, 10),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Flow.Ink("TextPrimaryBrush"),
        };
        AutomationProperties.SetName(rule, "Horizontal line");
        var part = new Part { Kind = "line" };
        part.Root = Row(part, rule);
        return part;
    }

    private Part MakeTable(int rows, int columns, bool header, string? html)
    {
        List<List<string>>? parsed = null;
        if (html != null)
        {
            var found = CellsOf(html);
            header = found.Any(cell => cell.Row == 0 && cell.Header);
            parsed = found.GroupBy(cell => cell.Row).OrderBy(group => group.Key).Select(group => group.Select(cell => cell.Text).ToList()).ToList();
            if (parsed.Count == 0)
                parsed = null;
            else
            {
                rows = parsed.Count;
                columns = Math.Max(1, parsed.Max(row => row.Count));
            }
        }
        if (rows < 1 || columns < 1)
        {
            rows = 2;
            columns = 2;
        }
        var grid = new Grid();
        var cells = new List<List<TextBox>>();
        for (var row = 0; row < rows; row++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var column = 0; column < columns; column++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var row = 0; row < rows; row++)
        {
            var line = new List<TextBox>();
            for (var column = 0; column < columns; column++)
            {
                var box = new TextBox
                {
                    BorderThickness = new Thickness(0),
                    TextWrapping = TextWrapping.Wrap,
                    AcceptsReturn = true,
                    MinHeight = 28,
                    Padding = new Thickness(6, 4, 6, 4),
                    FontFamily = new FontFamily(row == 0 && header ? "Segoe UI Semibold" : "Segoe UI"),
                    FontSize = 15,
                    Background = row == 0 && header ? Flow.Ink("SelectedFillBrush") : Flow.Ink("SurfaceBackgroundBrush"),
                    Foreground = Flow.Ink("TextPrimaryBrush"),
                    Tag = row == 0 && header ? "sans-medium" : "sans",
                    Text = parsed != null && row < parsed.Count && column < parsed[row].Count ? parsed[row][column] : "",
                };
                box.TextChanged += (_, _) => { if (!_mute) Touch(); };
                var frame = new Border
                {
                    BorderBrush = Flow.Ink("BorderStrongBrush"),
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(-0.5),
                    Child = box,
                };
                Grid.SetRow(frame, row);
                Grid.SetColumn(frame, column);
                grid.Children.Add(frame);
                line.Add(box);
            }
            cells.Add(line);
        }
        var outline = new Border
        {
            BorderBrush = Flow.Ink("BorderStrongBrush"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 8, 0, 8),
            Child = grid,
        };
        AutomationProperties.SetName(outline, "Table");
        var part = new Part { Kind = "table", Cells = cells, Header = header, Frame = outline };
        part.Root = Row(part, outline);
        foreach (var row in cells)
        {
            foreach (var cell in row)
                cell.GotFocus += (_, _) => Activate(null, part);
        }
        Lock(part);
        return part;
    }

    private Grid Row(Part part, FrameworkElement body)
    {
        var grip = new TextBlock
        {
            Text = "⋮⋮",
            FontSize = 12,
            Opacity = 0,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Flow.Ink("TextSecondaryBrush"),
            IsHitTestVisible = true,
        };
        AutomationProperties.SetName(grip, part.Kind == "line" ? "Move line" : "Move table");
        var grid = new Grid { Margin = new Thickness(28, 0, 48, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(grip);
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        grid.PointerEntered += (_, _) => grip.Opacity = _readOnly ? 0 : 1;
        grid.PointerExited += (_, _) => { if (!part.Dragging) grip.Opacity = 0; };
        HookMove(grip, part);
        grid.Tag = part;
        return grid;
    }

    private void HookMove(UIElement grip, Part part)
    {
        grip.PointerPressed += (_, e) =>
        {
            if (_readOnly)
                return;
            part.Dragging = true;
            Select(part);
            grip.CapturePointer(e.Pointer);
            e.Handled = true;
        };
        grip.PointerReleased += (_, e) =>
        {
            if (!part.Dragging)
                return;
            part.Dragging = false;
            grip.ReleasePointerCapture(e.Pointer);
            var y = e.GetCurrentPoint(_flow).Position.Y;
            var index = 0;
            var found = _parts.Count;
            foreach (var item in _parts)
            {
                var top = item.Root.ActualOffset.Y;
                var mid = top + Math.Max(item.Root.ActualHeight, 8) / 2;
                if (y < mid)
                {
                    found = index;
                    break;
                }
                index++;
            }
            Move(part, found);
            e.Handled = true;
        };
    }

    private void Move(Part part, int index)
    {
        var from = _parts.IndexOf(part);
        if (from < 0)
            return;
        _parts.RemoveAt(from);
        if (index > from)
            index--;
        index = Math.Clamp(index, 0, _parts.Count);
        _parts.Insert(index, part);
        Rebuild();
        Touch();
    }

    private void Commit(Part part)
    {
        if (part.Editor == null)
            return;
        _mute = true;
        try
        {
            if (part.Pending != null)
                WordPaper.Load(part.Editor, part.Pending);
            else if (part.PendingHtml != null)
                WordPaper.Load(part.Editor, WordPaper.ReadHtml(part.PendingHtml));
            part.Pending = null;
            part.PendingHtml = null;
            Grow(part.Editor);
            FitLast();
        }
        finally
        {
            _mute = false;
        }
    }

    private void Fill(RichEditBox box, string html)
    {
        void Go()
        {
            _mute = true;
            try { WordPaper.Load(box, WordPaper.ReadHtml(html)); }
            finally { _mute = false; }
            RefreshHint(box);
        }
        if (box.IsLoaded)
            Go();
        else
            box.Loaded += (_, _) => Go();
    }

    private void Activate(RichEditBox? editor, Part? part)
    {
        if (editor != null)
            _activeEditor = editor;
        Select(part);
        SelectionChanged?.Invoke();
    }

    private void Select(Part? part)
    {
        foreach (var item in _parts)
        {
            if (item.Frame != null)
                item.Frame.BorderBrush = Flow.Ink("BorderStrongBrush");
        }
        _selected = part is { Kind: "text" } ? null : part;
        if (_selected?.Frame != null)
            _selected.Frame.BorderBrush = Flow.Ink("TextPrimaryBrush");
    }

    private void Lock(Part part)
    {
        if (part.Editor != null)
            part.Editor.IsReadOnly = _readOnly;
        if (part.Cells == null)
            return;
        foreach (var row in part.Cells)
        {
            foreach (var cell in row)
                cell.IsReadOnly = _readOnly;
        }
    }

    private void BlockCharacterKeys(KeyRoutedEventArgs e)
    {
        if (_design || _readOnly)
            return;
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        if (ctrl && e.Key is VirtualKey.B or VirtualKey.I or VirtualKey.U)
            e.Handled = true;
    }

    private TextBox? FocusedCell()
    {
        if (XamlRoot == null)
            return null;
        return FocusManager.GetFocusedElement(XamlRoot) is TextBox box && Owns(box) ? box : null;
    }

    private bool Owns(TextBox box) => _parts.Any(part => part.Cells != null && part.Cells.Any(row => row.Contains(box)));

    private void FitLast()
    {
        var last = _parts.LastOrDefault(part => part.Kind == "text");
        if (last?.Editor != null)
            Grow(last.Editor);
    }

    private void Grow(RichEditBox box)
    {
        if (!box.IsLoaded)
            return;
        box.Document.GetText(TextGetOptions.None, out var text);
        var width = box.ActualWidth > 40 ? box.ActualWidth : Math.Max(200, _pageW - 120);
        var perLine = Math.Max(20, (int)(width / 8));
        var lines = 0;
        foreach (var paragraph in text.Replace('\n', '\r').Split('\r'))
        {
            var plain = paragraph.Replace("\v", "");
            var wraps = Math.Max(1, (plain.Length + perLine - 1) / perLine);
            lines += wraps + paragraph.Count(ch => ch == '\v');
        }
        var height = Math.Max(32.0, lines * 22 + 12);
        var last = _parts.LastOrDefault(part => part.Kind == "text")?.Editor;
        if (ReferenceEquals(box, last))
            height = Math.Max(height, Math.Max(160, _pageH - 180));
        box.MinHeight = height;
    }

    private void DrawRuler()
    {
        _ruler.Children.Clear();
        var millimetres = _pageW / PxPerMm;
        var ink = Flow.Ink("TextSecondaryBrush");
        for (var mark = 0; mark <= millimetres; mark += 5)
        {
            var x = mark * PxPerMm;
            var tick = new Border
            {
                Width = 1,
                Height = mark % 10 == 0 ? 10 : 6,
                Background = ink,
            };
            Canvas.SetLeft(tick, x);
            Canvas.SetTop(tick, mark % 10 == 0 ? 8 : 12);
            _ruler.Children.Add(tick);
            if (mark % 10 == 0 && mark > 0)
            {
                var label = new TextBlock { Text = (mark / 10).ToString(), FontSize = 10, Foreground = ink };
                Canvas.SetLeft(label, x + 3);
                Canvas.SetTop(label, 0);
                _ruler.Children.Add(label);
            }
        }
    }

    private void Watch(RichEditBox box, TextBlock hint)
    {
        box.TextChanged += (_, _) =>
        {
            RefreshHint(box);
            if (!_mute)
                Touch();
        };
        box.GotFocus += (_, _) =>
        {
            _activeEditor = box;
            hint.Visibility = Visibility.Collapsed;
            Select(null);
            SelectionChanged?.Invoke();
        };
        box.LostFocus += (_, _) => RefreshHint(box);
        box.SelectionChanged += (_, _) =>
        {
            _activeEditor = box;
            SelectionChanged?.Invoke();
        };
    }

    private void RefreshHint(RichEditBox box)
    {
        var hint = ReferenceEquals(box, _header) ? _headerHint : _footerHint;
        hint.Visibility = Blank(box) && box.FocusState == FocusState.Unfocused ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool Blank(RichEditBox box)
    {
        if (!box.IsLoaded)
            return true;
        box.Document.GetText(TextGetOptions.None, out var text);
        return text.Trim().Length == 0;
    }

    private static string Fragment(RichEditBox box)
    {
        if (!box.IsLoaded)
            return "<p></p>";
        var (html, _) = WordPaper.WriteHtml(box.Document, "A4");
        return html.Length == 0 ? "<p></p>" : html;
    }

    private static string TableBody(Part part)
    {
        if (part.Cells == null)
            return "";
        var builder = new StringBuilder();
        for (var row = 0; row < part.Cells.Count; row++)
        {
            builder.Append("<tr>");
            foreach (var cell in part.Cells[row])
            {
                var tag = row == 0 && part.Header ? "th" : "td";
                var face = cell.Tag as string ?? "sans";
                builder.Append('<').Append(tag);
                if (face != "sans")
                    builder.Append(" style=\"font-family: '").Append(WordPaper.CssFamily(face)).Append("'\"");
                builder.Append('>').Append(Escape(cell.Text).Replace("\r\n", "<br/>").Replace("\n", "<br/>")).Append("</").Append(tag).Append('>');
            }
            builder.Append("</tr>\n");
        }
        return builder.ToString();
    }

    private void Touch()
    {
        if (!_mute)
            Changed?.Invoke();
    }

    private static RichEditBox Editor(string name)
    {
        var box = new RichEditBox
        {
            BorderThickness = new Thickness(0),
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 15,
            Background = Flow.Ink("SurfaceBackgroundBrush"),
            Foreground = Flow.Ink("TextPrimaryBrush"),
            Padding = new Thickness(0),
        };
        AutomationProperties.SetName(box, name);
        return box;
    }

    private Border Zone(string caption, RichEditBox editor, TextBlock hint, bool top)
    {
        var rule = new Line
        {
            Stroke = Flow.Ink("BorderStrongBrush"),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 2, 2 },
            VerticalAlignment = VerticalAlignment.Center,
        };
        var label = new Border
        {
            Background = Flow.Ink("SurfaceBackgroundBrush"),
            Padding = new Thickness(6, 0, 6, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 24, 0),
            Child = new TextBlock { Text = caption, FontSize = 11, Foreground = Flow.Ink("TextSecondaryBrush") },
        };
        var separator = new Grid { Height = 16, Margin = new Thickness(16, 0, 16, 0) };
        separator.Children.Add(rule);
        separator.Children.Add(label);
        separator.SizeChanged += (_, _) => rule.X2 = separator.ActualWidth;
        var host = new Grid { Margin = new Thickness(48, top ? 18 : 4, 48, top ? 0 : 12) };
        host.Children.Add(editor);
        host.Children.Add(hint);
        var stack = new StackPanel();
        if (top)
        {
            stack.Children.Add(host);
            stack.Children.Add(separator);
        }
        else
        {
            stack.Children.Add(separator);
            stack.Children.Add(host);
        }
        return new Border { Child = stack };
    }

    private static TextBlock Hint(string caption) => new()
    {
        Text = caption,
        FontSize = 15,
        Foreground = Flow.Ink("TextSecondaryBrush"),
        Opacity = 0.7,
        IsHitTestVisible = false,
        VerticalAlignment = VerticalAlignment.Top,
    };

    private static SolidColorBrush DeskBrush()
    {
        var surface = Flow.Ink("SurfaceBackgroundBrush") as SolidColorBrush;
        var dark = surface != null && surface.Color.R < 80;
        var color = dark
            ? Microsoft.UI.ColorHelper.FromArgb(255, 0x1A, 0x1A, 0x18)
            : Microsoft.UI.ColorHelper.FromArgb(255, 0xD9, 0xD6, 0xCE);
        return new SolidColorBrush(color);
    }

    private static SolidColorBrush ShadowBrush()
    {
        var surface = Flow.Ink("SurfaceBackgroundBrush") as SolidColorBrush;
        var dark = surface != null && surface.Color.R < 80;
        var color = dark
            ? Microsoft.UI.ColorHelper.FromArgb(255, 0x00, 0x00, 0x00)
            : Microsoft.UI.ColorHelper.FromArgb(40, 0x1A, 0x1A, 0x1A);
        return new SolidColorBrush(color);
    }

    private static (string Name, string Outer, int Next) ReadElement(string html, int start)
    {
        var close = html.IndexOf('>', start);
        if (close < 0)
            return ("", "", html.Length);
        var inner = html.Substring(start + 1, close - start - 1).Trim();
        var closing = inner.StartsWith('/');
        var body = inner.Trim('/').Trim();
        var split = body.IndexOfAny([' ', '\t', '\r', '\n']);
        var name = (split < 0 ? body : body[..split]).ToLowerInvariant();
        if (closing || inner.EndsWith('/') || name is "hr" or "br" or "img" or "col")
            return (name, html.Substring(start, close - start + 1), close + 1);
        var depth = 1;
        var p = close + 1;
        while (p < html.Length && depth > 0)
        {
            var lt = html.IndexOf('<', p);
            if (lt < 0)
                break;
            if (html.AsSpan(lt).StartsWith("<!--", StringComparison.Ordinal))
            {
                var comment = html.IndexOf("-->", lt, StringComparison.Ordinal);
                p = comment < 0 ? html.Length : comment + 3;
                continue;
            }
            var gt = html.IndexOf('>', lt);
            if (gt < 0)
                break;
            var tag = html.Substring(lt + 1, gt - lt - 1).Trim();
            var tagBody = tag.Trim('/').Trim();
            var tagSplit = tagBody.IndexOfAny([' ', '\t', '\r', '\n']);
            var tagName = (tagSplit < 0 ? tagBody : tagBody[..tagSplit]).ToLowerInvariant();
            var voidTag = tag.EndsWith('/') || tagName is "hr" or "br" or "img" or "col";
            if (tag.StartsWith('/'))
            {
                if (tagName == name)
                    depth--;
            }
            else if (!voidTag && tagName == name)
                depth++;
            p = gt + 1;
        }
        return (name, html.Substring(start, Math.Min(p, html.Length) - start), Math.Min(p, html.Length));
    }

    private static List<Cell> CellsOf(string html)
    {
        var cells = new List<Cell>();
        var row = -1;
        var i = 0;
        while (i < html.Length)
        {
            var lt = html.IndexOf('<', i);
            if (lt < 0)
                break;
            var gt = html.IndexOf('>', lt);
            if (gt < 0)
                break;
            var tag = html.Substring(lt + 1, gt - lt - 1).Trim();
            var name = tag.Trim('/').Trim().Split([' ', '\t'])[0].ToLowerInvariant();
            if (name == "tr" && !tag.StartsWith('/'))
                row++;
            if ((name is "td" or "th") && !tag.StartsWith('/') && row >= 0)
            {
                var end = html.IndexOf("</" + name, gt, StringComparison.OrdinalIgnoreCase);
                var inner = end < 0 ? "" : html.Substring(gt + 1, end - gt - 1);
                cells.Add(new Cell(row, name == "th", Plain(inner), inner));
                i = end < 0 ? gt + 1 : end;
                continue;
            }
            i = gt + 1;
        }
        return cells;
    }

    private static string Plain(string html)
    {
        var builder = new StringBuilder();
        var i = 0;
        while (i < html.Length)
        {
            if (html[i] == '<')
            {
                var gt = html.IndexOf('>', i);
                if (gt < 0)
                    break;
                var tag = html.Substring(i + 1, gt - i - 1).Trim().Trim('/').Trim().ToLowerInvariant();
                if (tag.StartsWith("br"))
                    builder.Append('\n');
                i = gt + 1;
                continue;
            }
            var next = html.IndexOf('<', i);
            if (next < 0)
                next = html.Length;
            builder.Append(html.AsSpan(i, next - i));
            i = next;
        }
        return builder.ToString().Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&#39;", "'").Replace("&quot;", "\"");
    }

    private static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static double Mm(double mm) => mm * PxPerMm;

    private sealed class Part
    {
        public string Kind = "text";
        public Grid Root = null!;
        public RichEditBox? Editor;
        public List<List<TextBox>>? Cells;
        public bool Header;
        public Border? Frame;
        public string? PendingHtml;
        public IReadOnlyList<WordPaper.Paragraph>? Pending;
        public bool Dragging;

        public Control? FocusTarget()
        {
            if (Editor != null)
                return Editor;
            if (Cells is { Count: > 0 } && Cells[0].Count > 0)
                return Cells[0][0];
            return null;
        }
    }

    private sealed record Cell(int Row, bool Header, string Text, string Html);
}
