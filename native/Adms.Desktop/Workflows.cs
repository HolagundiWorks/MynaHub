using System.Globalization;
using Hcw.Ui.Controls;
using System.Text.Json;
using Adms.Interop;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Adms.Desktop;

internal static class Flow
{
    /// <summary>A closable notice that a screen reader announces without interrupting. (A subclass of InfoBar crashes WinUI, so this sets the live region on a plain one.)</summary>
    public static InfoBar Notice()
    {
        var bar = new InfoBar { IsOpen = false, IsClosable = true };
        AutomationProperties.SetLiveSetting(bar, AutomationLiveSetting.Polite);
        return bar;
    }

    /// <summary>
    /// Reports a problem with one field: shows the message, outlines the field in the error colour, and moves the keyboard to it.
    /// The outline clears when the person edits the field.
    /// </summary>
    public static void Fault(Control field, InfoBar bar, string message)
    {
        bar.Severity = InfoBarSeverity.Error;
        bar.Message = message;
        bar.IsOpen = true;
        Mark(field, message);
    }

    /// <summary>Outlines a field in the error colour, gives it the message as help text and moves the keyboard to it. The outline clears when it is edited.</summary>
    public static void Mark(Control field, string message)
    {
        var critical = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        // The controls draw their border from these resources in every state, so the red stays while the field has the keyboard.
        // A password box takes the same text-control resources as a text box.
        string[] keys = field is ComboBox
            ? new[] { "ComboBoxBorderBrush", "ComboBoxBorderBrushPointerOver", "ComboBoxBorderBrushFocused", "ComboBoxBorderBrushPressed" }
            : new[] { "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" };
        foreach (var key in keys)
            field.Resources[key] = critical;
        AutomationProperties.SetHelpText(field, message);
        void Clear()
        {
            foreach (var key in keys)
                field.Resources.Remove(key);
            AutomationProperties.SetHelpText(field, "");
        }
        if (field is TextBox box)
        {
            TextChangedEventHandler? clear = null;
            clear = (_, _) =>
            {
                box.TextChanged -= clear;
                Clear();
            };
            box.TextChanged += clear;
        }
        else if (field is PasswordBox secret)
        {
            RoutedEventHandler? clear = null;
            clear = (_, _) =>
            {
                secret.PasswordChanged -= clear;
                Clear();
            };
            secret.PasswordChanged += clear;
        }
        else if (field is ComboBox combo)
        {
            SelectionChangedEventHandler? clear = null;
            clear = (_, _) =>
            {
                combo.SelectionChanged -= clear;
                Clear();
            };
            combo.SelectionChanged += clear;
        }
        field.Focus(FocusState.Programmatic);
    }

    public static Style Look(string key) => (Style)Application.Current.Resources[key];

    public static Brush Ink(string key)
    {
        var theme = AppTheme.ActiveDictionary;
        foreach (var dictionary in Walk(Application.Current.Resources))
        {
            if (dictionary.ThemeDictionaries.TryGetValue(theme, out var match)
                && match is ResourceDictionary themed
                && themed.TryGetValue(key, out var value)
                && value is Brush brush)
                return brush;
        }
        return (Brush)Application.Current.Resources[key];
    }

    private static IEnumerable<ResourceDictionary> Walk(ResourceDictionary root)
    {
        yield return root;
        foreach (var merged in root.MergedDictionaries)
        {
            foreach (var child in Walk(merged))
                yield return child;
        }
    }

    public static StackPanel SheetMark(string sheet, string section, string page)
    {
        var mark = new TextBlock
        {
            Text = $"O-Studio {sheet}  ·  {section}",
            Style = Look("CaptionStyle"),
        };
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 2) };
        panel.Children.Add(mark);
        AutomationProperties.SetName(panel, $"O-Studio {sheet}, {section}, {page}");
        return panel;
    }

    public static TextBlock Heading(string text)
    {
        var block = new TextBlock { Text = text, Style = Look("PageTitleStyle") };
        AutomationProperties.SetHeadingLevel(block, AutomationHeadingLevel.Level1);
        return block;
    }

    public static TextBlock Section(string text)
    {
        var block = new TextBlock { Text = text, Style = Look("SectionTitleStyle") };
        AutomationProperties.SetHeadingLevel(block, AutomationHeadingLevel.Level2);
        return block;
    }

    public static TextBlock Secondary(string text) => new()
    {
        Text = text,
        Style = Look("SecondaryStyle"),
        TextWrapping = TextWrapping.WrapWholeWords,
    };

    public static TextBox Input(string header, bool multiline = false) => new()
    {
        Header = header,
        AcceptsReturn = multiline,
        TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
        MinHeight = multiline ? 120 : 0,
    };

    public static Button Command(string label, Action action, bool primary = false)
    {
        var button = new Button { Content = label };
        if (primary)
            button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        button.Click += (_, _) => action();
        return button;
    }

    public static void Pin(Panel host, params (string Label, Action Run)[] actions)
    {
        if (actions.Length == 0)
            return;
        var button = new Button
        {
            Content = actions.Length == 1 ? actions[0].Label : "Create",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 12, 12),
            MinHeight = 36,
            Padding = new Thickness(16, 4, 16, 4),
            CornerRadius = new CornerRadius(8),
        };
        AutomationProperties.SetName(button, actions.Length == 1 ? actions[0].Label : "Create");
        if (actions.Length == 1)
            button.Click += (_, _) => actions[0].Run();
        else
        {
            var menu = new MenuFlyout();
            foreach (var (label, run) in actions)
            {
                var item = new MenuFlyoutItem { Text = label };
                item.Click += (_, _) => run();
                menu.Items.Add(item);
            }
            button.Flyout = menu;
        }
        host.Children.Add(button);
    }

    public static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content,
        HorizontalScrollMode = ScrollMode.Disabled,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };

    public static FlexBar Bar(params UIElement[] items)
    {
        var bar = new FlexBar();
        foreach (var item in items)
            bar.Children.Add(item);
        return bar;
    }

    public static Grid Pair(UIElement left, UIElement right)
    {
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(left);
        grid.Children.Add(right);
        if (right is FrameworkElement start)
            Grid.SetColumn(start, 1);
        void PlacePair()
        {
            var narrow = grid.ActualWidth > 0 && grid.ActualWidth < 560;
            grid.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            if (right is FrameworkElement field)
            {
                Grid.SetColumn(field, narrow ? 0 : 1);
                Grid.SetRow(field, narrow ? 1 : 0);
            }
        }
        grid.SizeChanged += (_, _) => PlacePair();
        return grid;
    }

    public static Grid Line(UIElement body, Button action)
    {
        var line = new Grid { ColumnSpacing = 8 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.Children.Add(body);
        action.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(action, 1);
        line.Children.Add(action);
        return line;
    }

    public static Button Edit(Action open)
    {
        var button = new Button { Content = "Edit", MinWidth = 72 };
        AutomationProperties.SetName(button, "Edit");
        button.Click += (_, _) => open();
        return button;
    }

    public static async Task<bool> Discard(UIElement owner, bool dirty)
    {
        if (!dirty)
            return true;
        if (owner.XamlRoot == null)
            return false;
        var previous = FocusManager.GetFocusedElement(owner.XamlRoot);
        var dialog = new ContentDialog
        {
            Title = "Discard unsaved changes?",
            Content = "These changes have not been saved.",
            PrimaryButtonText = "Discard",
            CloseButtonText = "Keep editing",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = owner.XamlRoot,
        };
        var result = await dialog.ShowAsync() == ContentDialogResult.Primary;
        if (previous is UIElement element)
            element.Focus(FocusState.Programmatic);
        return result;
    }

    public static async Task<bool> Confirm(UIElement owner, string title, string message, string confirm)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.WrapWholeWords, MaxWidth = 420 },
            PrimaryButtonText = confirm,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = owner.XamlRoot,
        };
        var previous = FocusManager.GetFocusedElement(owner.XamlRoot);
        var result = await dialog.ShowAsync() == ContentDialogResult.Primary;
        if (previous is UIElement element)
            element.Focus(FocusState.Programmatic);
        return result;
    }

    public static Grid Header(params string[] cells) => Cells(true, cells, null, null, false);

    public static Grid Columns(params string[] cells) => Cells(false, cells, null, null, false);

    public static Grid Header(string[] cells, GridLength[] widths, bool[]? alignEnd = null) => Cells(true, cells, widths, alignEnd, false);

    public static Grid Columns(string[] cells, GridLength[] widths, bool[]? alignEnd = null, bool emphasize = false) => Cells(false, cells, widths, alignEnd, emphasize);

    public static DataSheet Table(string name, string[] headers, IReadOnlyList<string[]> rows, bool[] alignEnd, int emphasizeFrom = -1)
    {
        var panel = new StackPanel();
        var cells = new List<FrameworkElement[]>();
        var widths = Widths(headers.Length, alignEnd);
        var header = Cells(true, headers, widths, alignEnd, false);
        NameCells(header, headers, null, 0);
        cells.Add(CellElements(header));
        panel.Children.Add(header);
        for (var r = 0; r < rows.Count; r++)
        {
            var line = Cells(false, rows[r], widths, alignEnd, emphasizeFrom >= 0 && r >= emphasizeFrom);
            NameCells(line, headers, rows[r], r + 1);
            cells.Add(CellElements(line));
            panel.Children.Add(line);
        }
        return new DataSheet(name, headers, cells, panel);
    }

    private static void NameCells(Grid line, string[] headers, string[]? values, int row)
    {
        var cells = CellElements(line);
        for (var c = 0; c < cells.Length && c < headers.Length; c++)
        {
            var value = values != null && c < values.Length ? values[c] : "";
            var spoken = row == 0 ? $"{headers[c]} column" : value.Length == 0 ? headers[c] : $"{headers[c]} {value}";
            AutomationProperties.SetName(cells[c], spoken);
        }
    }

    private static FrameworkElement[] CellElements(Grid line) =>
        line.Children.OfType<FrameworkElement>().OrderBy(Grid.GetColumn).ToArray();

    public static void Place(Grid grid, IReadOnlyList<UIElement> items, double minItemWidth, bool fill)
    {
        grid.Children.Clear();
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        if (items.Count == 0)
            return;
        var host = grid.ActualWidth > 1 ? grid.ActualWidth : grid.Parent is FrameworkElement parent && parent.ActualWidth > 1 ? parent.ActualWidth : 720;
        var cols = Math.Max(1, (int)(host / minItemWidth));
        cols = Math.Min(cols, items.Count);
        for (var c = 0; c < cols; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = fill ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        var rowCount = (items.Count + cols - 1) / cols;
        for (var r = 0; r < rowCount; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is not FrameworkElement element)
                continue;
            Grid.SetRow(element, i / cols);
            Grid.SetColumn(element, i % cols);
            if (!fill)
                element.Margin = new Thickness(0, 0, 8, 8);
            grid.Children.Add(element);
        }
    }

    private static GridLength[] Widths(int count, bool[] alignEnd)
    {
        var widths = new GridLength[count];
        for (var i = 0; i < count; i++)
            widths[i] = alignEnd[i] ? new GridLength(160) : i == 0 ? new GridLength(2, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
        return widths;
    }

    private static Grid Cells(bool header, string[] cells, GridLength[]? widths, bool[]? alignEnd, bool emphasize)
    {
        var grid = new Grid();
        var brush = Ink(header || emphasize ? "BorderStrongBrush" : "BorderSubtleBrush");
        for (var i = 0; i < cells.Length; i++)
        {
            var width = widths != null && i < widths.Length
                ? widths[i]
                : i == 1 ? new GridLength(2, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            var align = alignEnd != null && i < alignEnd.Length && alignEnd[i];
            var text = new TextBlock
            {
                Text = cells[i],
                Style = Look(header || emphasize ? "TableHeaderStyle" : "TableCellStyle"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = align ? TextAlignment.Right : TextAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (align)
                text.FontFamily = new FontFamily("Cascadia Mono, Consolas");
            var cell = new Border
            {
                BorderBrush = brush,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(8, 8, 8, 8),
                Child = text,
            };
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }
        return grid;
    }

    public static void UseTable(ListView list)
    {
        var style = new Style(typeof(ListViewItem));
        if (Application.Current.Resources.TryGetValue(typeof(ListViewItem), out var existing) && existing is Style basis)
            style.BasedOn = basis;
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0d));
        list.ItemContainerStyle = style;
        list.Padding = new Thickness(0);
        Surface(list);
    }

    public static void Surface(FrameworkElement element)
    {
        void Paint()
        {
            var brush = Ink("SurfaceBackgroundBrush");
            switch (element)
            {
                case Control control:
                    control.Background = brush;
                    break;
                case Panel panel:
                    panel.Background = brush;
                    break;
            }
        }
        element.ActualThemeChanged += (_, _) => Paint();
        element.Loaded += (_, _) => Paint();
        Paint();
    }

    public static string Money(long paise) =>
        (paise / 100m).ToString("C", CultureInfo.GetCultureInfo("en-IN"));

    public static JsonElement? Try(AppShell shell, string operation, object? args = null)
    {
        try { return shell.Call(operation, args); }
        catch (AdmsException) { return null; }
    }

    public static object PageQuery(Dictionary<string, string>? filters = null, int pageSize = 10) => new
    {
        search = "",
        sort = "",
        dir = "",
        page = 0,
        page_size = pageSize,
        filters = filters ?? new Dictionary<string, string>(),
    };

    public static (long Id, string Kind)? TakeOpen(AppShell shell, string page)
    {
        if (shell.PendingOpen is not { } pending || pending.Page != page)
            return null;
        shell.PendingOpen = null;
        return (pending.Id, pending.Kind);
    }

    public static bool TakeCreate(AppShell shell, string page)
    {
        if (shell.PendingCreate != page)
            return false;
        shell.PendingCreate = null;
        return true;
    }
}

internal sealed class DashboardPage : UserControl, IPageCommands, ILiveRefresh
{
    public bool CanRefreshLive => true;

    public void RefreshLive() => Load();

    private readonly AppShell _shell;
    private readonly Grid _metrics = new() { RowSpacing = 8, ColumnSpacing = 8 };
    private readonly StackPanel _attention = new() { Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly TextBlock _attentionHeading;
    private readonly StackPanel _projectList = new() { Spacing = 4 };
    private readonly StackPanel _next = new() { Spacing = 4 };
    private readonly TextBlock _nextHeading = Flow.Section("Next on your projects");
    private readonly TextBlock _context = Flow.Secondary("Loading the company overview…");
    private readonly List<UIElement> _metricItems = [];
    private readonly List<(string Label, Action Run)> _creates = [];

    public DashboardPage(AppShell shell)
    {
        _shell = shell;
        if (shell.CanEdit)
        {
            _creates.Add(("New client", () => shell.Navigate?.Invoke(Mark("clients"))));
            _creates.Add(("New project", () => shell.Navigate?.Invoke(Mark("projects"))));
            _creates.Add(("New letter", () => shell.Navigate?.Invoke(Mark("documents"))));
        }
        if (shell.CanIssue)
            _creates.Add(("New invoice", () => shell.Navigate?.Invoke(Mark("invoices"))));
        if (shell.CanPost)
            _creates.Add(("Record expense", () => shell.Navigate?.Invoke(Mark("acc-expenses"))));

        var page = new StackPanel
        {
            Spacing = 12,
            Margin = (Thickness)Application.Current.Resources["PageMargin"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        page.Children.Add(Flow.SheetMark("00", "Practice", "Pulse"));
        page.Children.Add(Flow.Heading("Pulse"));
        page.Children.Add(_context);
        page.Children.Add(Flow.Command("Refresh", Load));
        _nextHeading.Visibility = Visibility.Collapsed;
        page.Children.Add(_nextHeading);
        page.Children.Add(_next);
        _attentionHeading = Flow.Section("Needs attention");
        _attentionHeading.Visibility = Visibility.Collapsed;
        page.Children.Add(_attentionHeading);
        page.Children.Add(_attention);
        page.Children.Add(Flow.Section("Overview"));
        page.Children.Add(_metrics);
        page.Children.Add(Flow.Section("Projects"));
        page.Children.Add(_projectList);
        var scroller = Flow.Scroll(page);
        var layer = new Grid();
        layer.Children.Add(scroller);
        Flow.Pin(layer, [.. _creates]);
        Content = layer;
        SizeChanged += (_, _) => Flow.Place(_metrics, _metricItems, 220, true);
        Load();
    }

    private string Mark(string page)
    {
        _shell.PendingCreate = page;
        return page;
    }

    private void Load()
    {
        _context.Text = "Loading the company overview…";
        _ = _shell.Run("Loading overview…", () =>
        {
            var profile = Flow.Try(_shell, "company_get_profile");
            var company = profile is JsonElement p ? Kit.Str(p, "name") : "";
            var clients = Flow.Try(_shell, "client_list", new { include_archived = false });
            var projects = Flow.Try(_shell, "project_list", new { client_id = (long?)null, include_archived = false });
            var drafts = Flow.Try(_shell, "doc_page", new { query = Flow.PageQuery(new Dictionary<string, string> { ["status"] = "draft" }) });
            JsonElement? invoices = _shell.CanFinance ? Flow.Try(_shell, "invoice_page", new { query = Flow.PageQuery() }) : null;
            JsonElement? draftInvoices = _shell.CanFinance ? Flow.Try(_shell, "invoice_page", new { query = Flow.PageQuery(new Dictionary<string, string> { ["status"] = "draft" }) }) : null;
            JsonElement? bank = _shell.CanFinance ? Flow.Try(_shell, "bank_summary", new { account_id = (long?)null }) : null;
            var year = FinancialYear();
            var upNext = Flow.Try(_shell, "project_attention");
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                Paint(company, year, clients, projects, drafts, draftInvoices, invoices, bank);
                PaintNext(upNext);
            });
        });
    }

    private string? FinancialYear()
    {
        var periods = Flow.Try(_shell, "period_list");
        if (periods is not JsonElement list || list.ValueKind != JsonValueKind.Array)
            return null;
        var today = Kit.Today();
        string? open = null;
        foreach (var period in list.EnumerateArray())
        {
            if (Kit.Str(period, "status") != "open")
                continue;
            open = Kit.Str(period, "fy");
            var start = Kit.Str(period, "start_date");
            var end = Kit.Str(period, "end_date");
            if (string.CompareOrdinal(today, start) >= 0 && string.CompareOrdinal(today, end) <= 0)
                return open;
        }
        return open;
    }

    private void Paint(string company, string? year, JsonElement? clients, JsonElement? projects, JsonElement? drafts, JsonElement? draftInvoices, JsonElement? invoices, JsonElement? bank)
    {
        _context.Text = string.IsNullOrWhiteSpace(company)
            ? "Figures come from the open company file."
            : year == null ? $"{company}. Figures come from the open company file." : $"{company}. Financial year {year}. Figures come from the open company file.";
        _metricItems.Clear();
        _attention.Children.Clear();
        if (clients is JsonElement clientRows && clientRows.ValueKind == JsonValueKind.Array)
            Metric("Clients", clientRows.GetArrayLength().ToString(), "Not archived", "clients");
        if (projects is JsonElement projectRows && projectRows.ValueKind == JsonValueKind.Array)
        {
            var active = 0;
            var shown = 0;
            foreach (var row in projectRows.EnumerateArray())
            {
                if (row.TryGetProperty("archived", out var archived) && archived.ValueKind == JsonValueKind.True)
                    continue;
                shown++;
                if (Kit.Str(row, "status") == "active")
                    active++;
            }
            Metric("Active projects", active.ToString(), $"{shown} not archived", "projects");
        }
        if (drafts is JsonElement draftPage)
        {
            var total = Kit.Str(draftPage, "total");
            Metric("Draft documents", total, "Letters and agreements not yet issued", "documents");
            if (int.TryParse(total, out var count) && count > 0)
                Attention($"{count} draft document{(count == 1 ? "" : "s")} not yet issued", "documents");
        }
        if (draftInvoices is JsonElement invoiceDrafts)
        {
            var total = Kit.Str(invoiceDrafts, "total");
            Metric("Draft invoices", total, "Not issued", "invoices");
            if (int.TryParse(total, out var count) && count > 0)
                Attention($"{count} draft invoice{(count == 1 ? "" : "s")} not issued", "invoices");
        }
        if (invoices is JsonElement invoicePage && invoicePage.TryGetProperty("sums", out var sums) && sums.TryGetProperty("outstanding", out var outstanding) && outstanding.TryGetInt64(out var paise))
        {
            Metric("Outstanding", Flow.Money(paise), "Balance on issued tax invoices", "invoices");
            if (paise > 0)
                Attention($"Outstanding fees {Flow.Money(paise)}", "invoices");
        }
        if (bank is JsonElement summary)
        {
            var unmatched = Kit.Str(summary, "unmatched");
            Metric("Unreconciled", unmatched, $"{Kit.Str(summary, "partial")} partly matched. {Kit.Str(summary, "rejected_rows")} rejected rows.", "bank");
            if (int.TryParse(unmatched, out var count) && count > 0)
                Attention($"{count} bank transaction{(count == 1 ? "" : "s")} not matched", "bank");
        }
        if (_metricItems.Count == 0)
            _metricItems.Add(Flow.Secondary("The overview could not be loaded."));
        Flow.Place(_metrics, _metricItems, 220, true);
        PaintProjects(projects);
        var showAttention = _attention.Children.Count > 0;
        _attention.Visibility = showAttention ? Visibility.Visible : Visibility.Collapsed;
        _attentionHeading.Visibility = showAttention ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>One line per project that has something to do now: money to collect first, then bills to raise, then set-up.</summary>
    private void PaintNext(JsonElement? list)
    {
        _next.Children.Clear();
        var any = false;
        if (list is JsonElement rows && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray())
            {
                if (!row.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.Object)
                    continue;
                if (!ProjectPathView.CanAct(_shell, Kit.Str(action, "kind")))
                    continue;
                var id = row.TryGetProperty("project_id", out var pid) && pid.ValueKind == JsonValueKind.Number ? pid.GetInt64() : 0;
                var captured = action.Clone();
                var text = $"{Kit.Str(row, "project")}  ·  {Kit.Str(row, "client")}  ·  {Kit.Str(captured, "label")}";
                var button = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Content = text };
                AutomationProperties.SetName(button, text);
                button.Click += (_, _) => ProjectPathView.Go(_shell, id, captured);
                _next.Children.Add(button);
                any = true;
            }
        }
        _nextHeading.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        _next.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PaintProjects(JsonElement? projects)
    {
        _projectList.Children.Clear();
        if (projects is not JsonElement rows || rows.ValueKind != JsonValueKind.Array)
        {
            _projectList.Children.Add(Flow.Secondary("Projects could not be loaded."));
            return;
        }
        var shown = 0;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.TryGetProperty("archived", out var archived) && archived.ValueKind == JsonValueKind.True)
                continue;
            if (Kit.Str(row, "status") != "active")
                continue;
            if (shown == 8)
                break;
            shown++;
            var name = Kit.Str(row, "name");
            var client = Kit.Str(row, "client_name");
            var service = Consultancy.Label(Kit.Str(row, "consultancy_type"));
            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = $"{name}  ·  {client}  ·  {service}",
            };
            AutomationProperties.SetName(button, $"{name}, {client}, {service}");
            var projectId = Kit.Id(row) ?? 0;
            button.Click += (_, _) =>
            {
                _shell.PendingOpen = projectId > 0 ? ("projects", projectId, "open") : null;
                _shell.Navigate?.Invoke("projects");
            };
            _projectList.Children.Add(button);
        }
        if (shown == 0)
            _projectList.Children.Add(Flow.Secondary("No active projects yet. Create one when a client engages the practice."));
    }

    private void Metric(string label, string value, string hint, string page)
    {
        var caption = new TextBlock
        {
            Text = label,
            Style = Flow.Look("CaptionStyle"),
        };
        var figure = new TextBlock
        {
            Text = value,
            Style = Flow.Look("MetricValueStyle"),
            Margin = new Thickness(0, 2, 0, 2),
        };
        var note = new TextBlock
        {
            Text = hint,
            Style = Flow.Look("CaptionStyle"),
            TextWrapping = TextWrapping.WrapWholeWords,
            MaxHeight = 32,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var copy = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(caption);
        copy.Children.Add(figure);
        copy.Children.Add(note);
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var mark = new Border { Background = Flow.Ink("BrandAccentBrush") };
        Grid.SetColumn(copy, 1);
        copy.Margin = new Thickness(12, 10, 12, 10);
        body.Children.Add(mark);
        body.Children.Add(copy);
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Padding = new Thickness(0),
            MinHeight = 88,
            Background = Flow.Ink("SurfaceBackgroundBrush"),
            BorderBrush = Flow.Ink("BorderSubtleBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Content = body,
        };
        AutomationProperties.SetName(button, $"{label}, {value}. {hint}");
        button.Click += (_, _) => _shell.Navigate?.Invoke(page);
        _metricItems.Add(button);
    }

    private void Attention(string text, string page)
    {
        var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, MinHeight = 32 };
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => _shell.Navigate?.Invoke(page);
        _attention.Children.Add(button);
    }

    public void NewItem() { }
    public void Save() { }
    public void Print() { }
    public void FocusSearch() { }
}

internal sealed class ProjectsPage : UserControl, IPageCommands, ILiveRefresh
{
    public bool CanRefreshLive => !_dirty && ListPane.Visibility == Visibility.Visible;

    /// <summary>Reloads the list and the client choices after another app changed them. The editor is hidden when this runs.</summary>
    public void RefreshLive() => _ = _shell.Run("Updating from another app…", () =>
    {
        var projects = _shell.Call("project_list", new { client_id = (long?)null, include_archived = false });
        var clients = _shell.Call("client_list", new { include_archived = false });
        _shell.Window.DispatcherQueue.TryEnqueue(() =>
        {
            _rows.Clear();
            if (projects.ValueKind == JsonValueKind.Array)
                foreach (var row in projects.EnumerateArray())
                    _rows.Add(row.Clone());
            _client.Items.Clear();
            _client.Items.Add(Item("Select a client", 0L));
            if (clients.ValueKind == JsonValueKind.Array)
                foreach (var row in clients.EnumerateArray())
                    _client.Items.Add(Item(Kit.Str(row, "name"), Kit.Id(row) ?? 0));
            PaintList();
        });
    });

    private readonly AppShell _shell;
    private readonly List<JsonElement> _rows = [];
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly ContentControl _header = new();
    private readonly TextBlock _summary = Flow.Secondary("Loading projects…");
    private readonly TextBlock _empty = Flow.Secondary("No projects match.");
    private readonly TextBox _search = new() { PlaceholderText = "Code, name, client or site", Header = "Search" };
    private readonly ComboBox _status = new() { Header = "Status" };
    private static readonly GridLength[] ProjectWidths = [new(120), new(1.6, GridUnitType.Star), new(1.8, GridUnitType.Star), new(1.2, GridUnitType.Star), new(110)];
    private readonly StackPanel _editor = new() { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _client = new() { Header = "Client" };
    private readonly TextBox _code = Flow.Input("Project code");
    private readonly TextBox _name = Flow.Input("Project name");
    private readonly ComboBox _state = new() { Header = "Status" };
    private readonly ComboBox _consultancy = new() { Header = "Consultancy type" };
    private readonly TextBox _floors = Flow.Input("Floors");
    private readonly TextBlock _phase = Flow.Secondary("");
    private readonly StackPanel _stageList = new() { Spacing = 8 };
    private readonly StackPanel _path = new() { Spacing = 8 };
    private readonly Button _assignStages = new() { Content = "Assign standard stages" };
    private readonly InfoBar _notice = Flow.Notice();
    private readonly TextBox _site = Flow.Input("Site address", true);
    private readonly TextBlock _title = Flow.Heading("Projects");
    private readonly Button _save = new();
    private Button _archive = new() { Content = "Archive", Visibility = Visibility.Collapsed };
    private readonly Button _report = new() { Content = "Project report", Visibility = Visibility.Collapsed };
    private readonly StackPanel _brief = new() { Spacing = 8 };
    private readonly Dictionary<string, TextBox> _briefFields = new();
    private readonly InfoBar _error = new() { IsOpen = false, IsClosable = false, Severity = InfoBarSeverity.Error };
    private long? _id;
    private bool _dirty;
    private bool _filling;
    private bool _saving;

    public ProjectsPage(AppShell shell)
    {
        _shell = shell;
        Flow.UseTable(_list);
        Flow.Surface(_header);
        AutomationProperties.SetName(_list, "Projects");
        AutomationProperties.SetName(_search, "Search projects");
        AutomationProperties.SetHelpText(_search, "Filters the project list by code, name, client or site.");
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);
        _empty.Visibility = Visibility.Collapsed;
        _status.Items.Add(Item("All statuses", ""));
        _status.Items.Add(Item("Active", "active"));
        _status.Items.Add(Item("On hold", "on_hold"));
        _status.Items.Add(Item("Completed", "completed"));
        _status.SelectedIndex = 0;
        _state.Items.Add(Item("Active", "active"));
        _state.Items.Add(Item("On hold", "on_hold"));
        _state.Items.Add(Item("Completed", "completed"));
        foreach (var (key, label) in Consultancy.Types)
            _consultancy.Items.Add(Item(label, key));
        _consultancy.SelectedIndex = 0;
        _search.TextChanged += (_, _) => PaintList();
        _status.SelectionChanged += (_, _) => PaintList();
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is ListViewItem item && item.Tag is JsonElement row)
                Open(row);
        };
        foreach (var box in new[] { _code, _name, _site })
            box.TextChanged += (_, _) => { if (!_filling) _dirty = true; };
        _client.SelectionChanged += (_, _) => { if (!_filling) _dirty = true; };
        _state.SelectionChanged += (_, _) => { if (!_filling) _dirty = true; };
        _consultancy.SelectionChanged += (_, _) => { if (!_filling) _dirty = true; };

        var create = Flow.Command("New project", () => _ = BeginNew(), primary: true);
        AutomationProperties.SetName(create, "New project");
        if (!_shell.CanEdit)
            create.Visibility = Visibility.Collapsed;
        var titleRow = new Grid { ColumnSpacing = 12 };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel { Spacing = 4 };
        titles.Children.Add(Flow.SheetMark("01", "Projects", "Projects"));
        titles.Children.Add(Flow.Heading("Projects"));
        titles.Children.Add(Flow.Secondary("A coordinated project record."));
        titleRow.Children.Add(titles);
        Grid.SetColumn(create, 1);
        create.VerticalAlignment = VerticalAlignment.Top;
        titleRow.Children.Add(create);
        var listPane = new Grid { RowSpacing = 12 };
        listPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        listPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        listPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        listPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        listPane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var filters = new Grid();
        _search.HorizontalAlignment = HorizontalAlignment.Stretch;
        _status.HorizontalAlignment = HorizontalAlignment.Stretch;
        listPane.Children.Add(titleRow);
        Grid.SetRow(filters, 1);
        listPane.Children.Add(filters);
        SizeChanged += (_, _) => Flow.Place(filters, [_search, _status], 280, true);
        Grid.SetRow(_summary, 2);
        listPane.Children.Add(_summary);
        var table = new Grid();
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        table.Children.Add(_header);
        Grid.SetRow(_list, 1);
        table.Children.Add(_list);
        Grid.SetRow(_empty, 1);
        _empty.VerticalAlignment = VerticalAlignment.Top;
        _empty.Margin = new Thickness(8, 12, 8, 0);
        table.Children.Add(_empty);
        Grid.SetRow(table, 3);
        Grid.SetRowSpan(table, 2);
        listPane.Children.Add(table);

        _save.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        _save.Click += (_, _) => SaveProject();
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += async (_, _) => { if (await CanLeave()) ShowList(); };
        var archive = new Button { Content = "Archive" };
        AutomationProperties.SetName(archive, "Archive project");
        archive.Click += async (_, _) => await ArchiveProject();
        archive.Visibility = Visibility.Collapsed;
        _archive = archive;
        AutomationProperties.SetName(_report, "Project report");
        AutomationProperties.SetHelpText(_report, "Prepares a project report from the records stored on this project.");
        _report.Click += (_, _) => Print();
        var actions = Flow.Bar(cancel, _report, archive, _save);
        _editor.Children.Add(_title);
        _editor.Children.Add(Flow.Secondary("A client can have many projects. Each project keeps its own agreements, letters and invoices."));
        _editor.Children.Add(_path);
        _editor.Children.Add(Flow.Section("Project details"));
        _editor.Children.Add(Flow.Pair(_name, _code));
        _editor.Children.Add(Flow.Pair(_client, _state));
        _editor.Children.Add(Flow.Section("Consultancy"));
        _editor.Children.Add(Flow.Secondary("The service follows the Council of Architecture Conditions of Engagement. The number of floors splits the working-drawing stages on a building."));
        _floors.Text = "1";
        _floors.TextChanged += (_, _) => { if (!_filling) _dirty = true; };
        _editor.Children.Add(Flow.Pair(_consultancy, _floors));
        _editor.Children.Add(Flow.Section("Stages"));
        _editor.Children.Add(Flow.Secondary("Save the consultancy and the floors, then assign the standard stages. Marking a stage records the phase and reminds you of the payment. It does not raise an invoice."));
        _editor.Children.Add(_phase);
        AutomationProperties.SetName(_assignStages, "Assign standard stages");
        _assignStages.Click += (_, _) => AssignStages();
        _editor.Children.Add(_assignStages);
        _editor.Children.Add(_stageList);
        _editor.Children.Add(_notice);
        _editor.Children.Add(Flow.Section("Site"));
        _editor.Children.Add(_site);
        BuildBrief();
        _editor.Children.Add(_brief);
        _editor.Children.Add(_error);
        _editor.Children.Add(actions);

        var host = new Grid { Margin = (Thickness)Application.Current.Resources["PageMargin"] };
        host.Children.Add(listPane);
        var editorScroll = Flow.Scroll(_editor);
        editorScroll.Visibility = Visibility.Collapsed;
        host.Children.Add(editorScroll);
        if (_shell.CanEdit)
            Flow.Pin(host, ("New project", () => _ = BeginNew()));
        Content = host;
        _ = _shell.Run("Loading projects…", () =>
        {
            var projects = _shell.Call("project_list", new { client_id = (long?)null, include_archived = false });
            var clients = _shell.Call("client_list", new { include_archived = false });
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                _rows.Clear();
                if (projects.ValueKind == JsonValueKind.Array)
                    foreach (var row in projects.EnumerateArray())
                        _rows.Add(row.Clone());
                _client.Items.Clear();
                _client.Items.Add(Item("Select a client", 0L));
                if (clients.ValueKind == JsonValueKind.Array)
                    foreach (var row in clients.EnumerateArray())
                        _client.Items.Add(Item(Kit.Str(row, "name"), Kit.Id(row) ?? 0));
                PaintList();
                if (Flow.TakeCreate(_shell, "projects"))
                    _ = BeginNew();
                else if (Flow.TakeOpen(_shell, "projects") is { } target)
                {
                    var row = _rows.FirstOrDefault(r => Kit.Id(r) == target.Id);
                    if (row.ValueKind == JsonValueKind.Object)
                        Open(row);
                }
            });
        });
    }

    private Grid HostGrid => (Grid)Content;
    private UIElement ListPane => HostGrid.Children[0];
    private ScrollViewer EditorPane => (ScrollViewer)HostGrid.Children[1];

    private void ShowList()
    {
        _dirty = false;
        _id = null;
        EditorPane.Visibility = Visibility.Collapsed;
        ListPane.Visibility = Visibility.Visible;
        _list.SelectedItem = null;
    }

    private async Task BeginNew()
    {
        if (!await CanLeave())
            return;
        if (_client.Items.Count <= 1)
        {
            _shell.Problem("Add a client before creating a project.");
            return;
        }
        _filling = true;
        _id = null;
        _code.Text = "";
        _name.Text = "";
        _site.Text = "";
        _client.SelectedIndex = 0;
        _client.IsEnabled = true;
        _state.SelectedIndex = 0;
        _consultancy.SelectedIndex = 0;
        _floors.Text = "1";
        _phase.Text = "";
        _stageList.Children.Clear();
        _path.Children.Clear();
        _assignStages.Visibility = Visibility.Collapsed;
        _notice.IsOpen = false;
        _title.Text = "New project";
        _save.Content = "Create";
        _archive.Visibility = Visibility.Collapsed;
        _report.Visibility = Visibility.Collapsed;
        _brief.Visibility = Visibility.Collapsed;
        ClearBrief();
        _error.IsOpen = false;
        _filling = false;
        _dirty = false;
        ListPane.Visibility = Visibility.Collapsed;
        EditorPane.Visibility = Visibility.Visible;
        _name.Focus(FocusState.Programmatic);
    }

    private void Open(JsonElement row)
    {
        _filling = true;
        _id = Kit.Id(row);
        _code.Text = Kit.Str(row, "code");
        _name.Text = Kit.Str(row, "name");
        _site.Text = Kit.Str(row, "site_address");
        Select(_client, Kit.Long(Kit.Str(row, "client_id")));
        _client.IsEnabled = false;
        Select(_state, Kit.Str(row, "status"));
        Select(_consultancy, string.IsNullOrEmpty(Kit.Str(row, "consultancy_type")) ? "comprehensive" : Kit.Str(row, "consultancy_type"));
        var floors = Kit.Str(row, "floors");
        _floors.Text = floors.Length == 0 || floors == "0" ? "1" : floors;
        _assignStages.Visibility = _shell.CanEdit ? Visibility.Visible : Visibility.Collapsed;
        _notice.IsOpen = false;
        _title.Text = _name.Text.Length == 0 ? "Project" : _name.Text;
        _save.Content = "Save changes";
        _archive.Visibility = _shell.CanEdit ? Visibility.Visible : Visibility.Collapsed;
        _report.Visibility = Visibility.Visible;
        _brief.Visibility = Visibility.Visible;
        _error.IsOpen = false;
        _filling = false;
        _dirty = false;
        ListPane.Visibility = Visibility.Collapsed;
        EditorPane.Visibility = Visibility.Visible;
        if (_id is long id)
        {
            LoadBrief(id);
            LoadStages(id);
        }
    }

    private TextBox BriefBox(string key, string header, bool multiline = false)
    {
        var box = Flow.Input(header, multiline);
        box.TextChanged += (_, _) => { if (!_filling) _dirty = true; };
        _briefFields[key] = box;
        return box;
    }

    private void BuildBrief()
    {
        _brief.Children.Add(Flow.Section("Project brief"));
        _brief.Children.Add(Flow.Secondary("This is the pre-design questionnaire. Concept design waits until the budget and built-up area are stated, the kitchen is decided, Vastu is yes, no, or a named consultant, the main rooms have notes, and the client has approved the brief."));
        _brief.Children.Add(Flow.Section("Client and site"));
        _brief.Children.Add(Flow.Pair(BriefBox("client_name", "Client name"), BriefBox("mobile", "Mobile")));
        _brief.Children.Add(Flow.Pair(BriefBox("email", "Email"), BriefBox("occupation", "Occupation")));
        _brief.Children.Add(BriefBox("current_address", "Current address", true));
        _brief.Children.Add(BriefBox("site_address", "Site address, if different", true));
        _brief.Children.Add(BriefBox("plot_size", "Plot size or dimensions"));
        _brief.Children.Add(Flow.Pair(BriefBox("terrain", "Terrain"), BriefBox("vegetation", "Vegetation")));
        _brief.Children.Add(BriefBox("orientation_notes", "Orientation, soil and natural features", true));
        _brief.Children.Add(Flow.Section("Project"));
        _brief.Children.Add(BriefBox("intended_use", "Intended use", true));
        _brief.Children.Add(Flow.Pair(BriefBox("built_up_area", "Built-up area"), BriefBox("phased", "Phased construction")));
        _brief.Children.Add(Flow.Pair(BriefBox("tentative_start", "Tentative start"), BriefBox("budget_note", "Budget excluding legal fees")));
        _brief.Children.Add(BriefBox("finance_note", "Finance"));
        _brief.Children.Add(Flow.Section("Occupants"));
        _brief.Children.Add(BriefBox("household", "Household: name, relation, age, occupation", true));
        _brief.Children.Add(BriefBox("staff_requirements", "Staff requirements", true));
        _brief.Children.Add(Flow.Section("Design drivers"));
        _brief.Children.Add(Flow.Pair(BriefBox("orientation", "Orientation"), BriefBox("door_direction", "Main and secondary doors")));
        _brief.Children.Add(Flow.Pair(BriefBox("views", "Views"), BriefBox("basement", "Basement")));
        _brief.Children.Add(BriefBox("vastu", "Vastu"));
        _brief.Children.Add(BriefBox("style", "Sense of style", true));
        _brief.Children.Add(BriefBox("loved_places", "Places loved", true));
        _brief.Children.Add(BriefBox("activities", "Important activities", true));
        _brief.Children.Add(BriefBox("indoor_prefs", "Indoor preferences", true));
        _brief.Children.Add(BriefBox("outdoor_prefs", "Outdoor preferences", true));
        _brief.Children.Add(Flow.Section("Spaces, materials and rooms"));
        _brief.Children.Add(BriefBox("space_schedule", "Space, area, floor, description", true));
        _brief.Children.Add(Flow.Pair(BriefBox("construction", "Construction"), BriefBox("flooring", "Flooring")));
        _brief.Children.Add(Flow.Pair(BriefBox("walls", "Wall finish"), BriefBox("cabinetry", "Cabinetry")));
        _brief.Children.Add(Flow.Pair(BriefBox("seating", "Seating"), BriefBox("beds", "Beds")));
        _brief.Children.Add(BriefBox("room_details", "Room notes, including the kitchen type", true));
        _brief.Children.Add(Flow.Section("Assumptions and approval"));
        _brief.Children.Add(BriefBox("assumptions", "Assumptions and exclusions", true));
        _brief.Children.Add(BriefBox("approval_note", "Client approval", true));
        _brief.Children.Add(BriefBox("approved_at", "Approved on (YYYY-MM-DD)"));
    }

    private void ClearBrief()
    {
        foreach (var box in _briefFields.Values)
            box.Text = "";
    }

    private Dictionary<string, string> BriefPayload()
    {
        var brief = new Dictionary<string, string>();
        foreach (var (key, box) in _briefFields)
            brief[key] = box.Text.Trim();
        return brief;
    }

    private void LoadBrief(long id)
    {
        _ = _shell.Run("Loading the project brief…", () =>
        {
            var payload = _shell.Call("project_brief_get", new { project_id = id });
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (_id != id || !payload.TryGetProperty("brief", out var brief))
                    return;
                _filling = true;
                foreach (var (key, box) in _briefFields)
                    box.Text = Kit.Str(brief, key);
                _filling = false;
            });
        });
    }

    private void PaintList()
    {
        var needle = _search.Text.Trim().ToLowerInvariant();
        var status = ((ComboBoxItem?)_status.SelectedItem)?.Tag as string ?? "";
        _list.Items.Clear();
        _header.Content = Flow.Header(["Code", "Project", "Consultancy", "Client", "Status"], ProjectWidths);
        var count = 0;
        foreach (var row in _rows)
        {
            if (status.Length > 0 && Kit.Str(row, "status") != status)
                continue;
            var service = Consultancy.Label(Kit.Str(row, "consultancy_type"));
            var phase = Kit.Str(row, "phase");
            if (phase.Length > 0)
                service = $"{service} · {phase}";
            var blob = $"{Kit.Str(row, "code")} {Kit.Str(row, "name")} {Kit.Str(row, "client_name")} {Kit.Str(row, "site_address")} {service}".ToLowerInvariant();
            if (needle.Length > 0 && !blob.Contains(needle))
                continue;
            var cells = new[] { Kit.Str(row, "code"), Kit.Str(row, "name"), service, Kit.Str(row, "client_name"), StatusLabel(Kit.Str(row, "status")) };
            var item = new ListViewItem { Content = Flow.Line(Flow.Columns(cells, ProjectWidths), Flow.Edit(() => Open(row))), Tag = row };
            AutomationProperties.SetName(item, $"{cells[1]}, code {cells[0]}, client {cells[2]}, {cells[3]}");
            _list.Items.Add(item);
            count++;
        }
        var noun = count == 1 ? "project" : "projects";
        if (count == 0 && needle.Length == 0 && status.Length == 0)
        {
            _summary.Text = "This company has no projects yet.";
            _empty.Text = "Create a project to start an engagement.";
        }
        else if (count == 0)
        {
            _summary.Text = "No projects match this search.";
            _empty.Text = "Change the search or status filter.";
        }
        else
        {
            _summary.Text = $"{count} {noun}.";
            _empty.Text = "";
        }
        _empty.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveProject()
    {
        if (_saving)
            return;
        var clientId = ((ComboBoxItem?)_client.SelectedItem)?.Tag is long id ? id : 0;
        if (clientId == 0) { Fail("Select a client."); return; }
        if (_name.Text.Trim().Length == 0) { Fail("Project name is required."); return; }
        if (_code.Text.Trim().Length == 0) { Fail("Project code is required."); return; }
        if (Floors() == 0) { Fail("Enter the number of floors from 1 to 40."); return; }
        var project = new
        {
            id = _id,
            client_id = clientId,
            code = _code.Text.Trim(),
            name = _name.Text.Trim(),
            site_address = _site.Text.Trim(),
            consultancy_type = ((ComboBoxItem?)_consultancy.SelectedItem)?.Tag as string ?? "comprehensive",
            floors = Floors(),
            status = ((ComboBoxItem?)_state.SelectedItem)?.Tag as string ?? "active",
            archived = false,
            client_name = "",
        };
        _saving = true;
        _save.IsEnabled = false;
        var briefId = _id;
        var brief = BriefPayload();
        _ = _shell.Run(_id == null ? "Creating project…" : "Saving project…", () =>
        {
            _shell.Call("project_save", new { project });
            if (briefId is long id)
                _shell.Call("project_brief_save", new { project_id = id, brief });
        })
            .ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                _saving = false;
                _save.IsEnabled = true;
                if (task.Status == TaskStatus.RanToCompletion && task.Result)
                {
                    _dirty = false;
                    _shell.Navigate?.Invoke("projects");
                    _shell.Window.DispatcherQueue.TryEnqueue(ReloadAfterSave);
                }
            }));
    }

    private void ReloadAfterSave()
    {
        ShowList();
        _ = _shell.Run("Loading projects…", () =>
        {
            var projects = _shell.Call("project_list", new { client_id = (long?)null, include_archived = false });
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                _rows.Clear();
                if (projects.ValueKind == JsonValueKind.Array)
                    foreach (var row in projects.EnumerateArray())
                        _rows.Add(row.Clone());
                PaintList();
            });
        });
    }

    private async Task ArchiveProject()
    {
        if (_id is not long id || !_shell.CanEdit)
            return;
        if (!await Flow.Confirm(this, "Archive this project?", "The project leaves the active list. Its letters and invoices stay in the company file.", "Archive"))
            return;
        _ = _shell.Run("Archiving project…", () => _shell.Call("project_set_archived", new { id, archived = true }))
            .ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (!task.Result)
                    return;
                _dirty = false;
                ReloadAfterSave();
            }));
    }

    private int Floors()
    {
        return int.TryParse(_floors.Text.Trim(), out var floors) && floors is >= 1 and <= 40 ? floors : 0;
    }

    private void LoadStages(long id)
    {
        _ = _shell.Run("Loading stages…", () =>
        {
            var plan = _shell.Call("project_stages", new { project_id = id });
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (_id != id)
                    return;
                PaintStages(plan);
            });
        });
    }

    /// <summary>Reloads where the project stands and what to do next. Called whenever the stages change.</summary>
    private void LoadPath(long id)
    {
        _ = _shell.Run("Checking the project path…", () =>
        {
            var path = _shell.Call("project_path", new { project_id = id });
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (_id != id)
                    return;
                ProjectPathView.Paint(_shell, _path, path, LocalAction);
            });
        });
    }

    /// <summary>Actions that are done on this very page: choosing stages, and reminding to mark one when it is reached.</summary>
    private bool LocalAction(string kind)
    {
        if (kind == "assign_stages")
        {
            AssignStages();
            return true;
        }
        if (kind == "mark_stage")
        {
            Notice("Mark a stage as reached in the Stages list below when the work for it is done. Its payment then appears here as the next thing to bill.");
            return true;
        }
        return false;
    }

    private void PaintStages(JsonElement plan)
    {
        if (_id is long pathId)
            LoadPath(pathId);
        var phase = Kit.Str(plan, "phase");
        _phase.Text = phase.Length == 0 ? "No stages yet." : phase == "Complete" ? "Phase: complete." : $"Phase: {phase}";
        _stageList.Children.Clear();
        if (!plan.TryGetProperty("stages", out var stages) || stages.ValueKind != JsonValueKind.Array)
            return;
        foreach (var stage in stages.EnumerateArray())
        {
            var name = Kit.Str(stage, "name");
            var marked = Kit.Str(stage, "marked_on");
            var line = $"{name} · {Kit.Str(stage, "share")}% · {(marked.Length == 0 ? "Open" : marked)} · {Kit.Str(stage, "payment")}";
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            if (_shell.CanEdit && stage.TryGetProperty("id", out var stageId) && stageId.TryGetInt64(out var id))
            {
                if (marked.Length == 0)
                {
                    var mark = new Button { Content = "Mark", MinHeight = 32 };
                    mark.Click += (_, _) => MarkStage(id);
                    row.Children.Add(mark);
                }
                else
                {
                    var clear = new Button { Content = "Clear", MinHeight = 32 };
                    clear.Click += (_, _) => UnmarkStage(id);
                    row.Children.Add(clear);
                }
            }
            _stageList.Children.Add(row);
        }
    }

    private void AssignStages()
    {
        if (_id is not long id || !_shell.CanEdit)
            return;
        var floors = Floors();
        if (floors == 0) { Fail("Enter the number of floors from 1 to 40."); return; }
        var consultancy = ((ComboBoxItem?)_consultancy.SelectedItem)?.Tag as string ?? "comprehensive";
        _ = _shell.Run("Assigning stages…", () =>
        {
            var plan = _shell.Call("project_stages_apply", new { project_id = id, consultancy, floors });
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (_id != id)
                    return;
                PaintStages(plan);
                Notice(Kit.Str(plan, "note"));
                _dirty = false;
            });
        });
    }

    private void MarkStage(long stageId)
    {
        if (_id is not long id)
            return;
        _ = _shell.Run("Marking stage…", () =>
        {
            var plan = _shell.Call("project_stage_mark", new { stage_id = stageId, marked_on = Kit.Today() });
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (_id != id)
                    return;
                PaintStages(plan);
                Notice(Kit.Str(plan, "note"));
            });
        });
    }

    private void UnmarkStage(long stageId)
    {
        if (_id is not long id)
            return;
        _ = _shell.Run("Clearing stage…", () =>
        {
            var plan = _shell.Call("project_stage_unmark", new { stage_id = stageId });
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (_id != id)
                    return;
                PaintStages(plan);
                _notice.IsOpen = false;
            });
        });
    }

    private void Notice(string message)
    {
        if (message.Length == 0)
        {
            _notice.IsOpen = false;
            return;
        }
        _notice.Message = message;
        _notice.IsOpen = true;
    }

    private void Fail(string message)
    {
        _error.Severity = InfoBarSeverity.Error;
        _error.Message = message;
        _error.IsOpen = true;
    }

    public async void NewItem() => await BeginNew();
    public void Save() => SaveProject();
    public void Print()
    {
        if (_id is not long id)
        {
            _shell.Problem("Open a project to prepare its report.");
            return;
        }
        var name = string.IsNullOrWhiteSpace(_code.Text) ? _name.Text : _code.Text;
        _ = _shell.Run("Preparing the project report…", () =>
        {
            var encoded = _shell.Call("project_report", new { project_id = id }).GetString() ?? "";
            _shell.Window.DispatcherQueue.TryEnqueue(async () => await Kit.PreviewPdf(_shell, encoded, string.IsNullOrWhiteSpace(name) ? "project-report" : name));
        });
    }
    public void FocusSearch() => _search.Focus(FocusState.Programmatic);
    public async Task<bool> CanLeave() => await Flow.Discard(this, _dirty);

    private static ComboBoxItem Item(string label, object tag) => new() { Content = label, Tag = tag };
    private static void Select(ComboBox box, object tag)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            if (Equals(item.Tag, tag) || item.Tag?.ToString() == tag.ToString())
            {
                box.SelectedItem = item;
                return;
            }
        }
    }
    private static string StatusLabel(string status) => status switch { "on_hold" => "On hold", "completed" => "Completed", "active" => "Active", _ => status };
}

internal sealed class ClientsPage : UserControl, IPageCommands, ILiveRefresh
{
    public bool CanRefreshLive => !_dirty && Host.Children[0].Visibility == Visibility.Visible;

    public void RefreshLive() => Reload();

    private readonly AppShell _shell;
    private readonly List<JsonElement> _rows = [];
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly ContentControl _header = new();
    private readonly TextBlock _summary = Flow.Secondary("Loading clients…");
    private readonly TextBlock _empty = Flow.Secondary("");
    private readonly TextBox _search = new() { PlaceholderText = "Name, contact, phone or email", Header = "Search", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _name = Flow.Input("Name");
    private readonly TextBox _contact = Flow.Input("Contact person");
    private readonly TextBox _email = Flow.Input("Email");
    private readonly TextBox _phone = Flow.Input("Phone");
    private readonly TextBox _address = Flow.Input("Correspondence address", true);
    private readonly TextBox _billing = Flow.Input("Billing address", true);
    private readonly ComboBox _preference = new() { Header = "Communication preference" };
    private readonly ComboBox _kind = new() { Header = "Kind" };
    private readonly ComboBox _gstRegistration = new() { Header = "GST registration" };
    private readonly TextBox _legal = Flow.Input("Legal name");
    private readonly TextBox _city = Flow.Input("City");
    private readonly TextBox _state = Flow.Input("State");
    private readonly TextBox _pan = Flow.Input("PAN");
    private readonly TextBox _tan = Flow.Input("TAN");
    private readonly TextBox _gstin = Flow.Input("GSTIN");
    private readonly TextBox[] _repName = [Flow.Input("Name"), Flow.Input("Name"), Flow.Input("Name")];
    private readonly TextBox[] _repRole = [Flow.Input("Designation"), Flow.Input("Designation"), Flow.Input("Designation")];
    private readonly TextBox[] _repPhone = [Flow.Input("Phone"), Flow.Input("Phone"), Flow.Input("Phone")];
    private readonly TextBlock _title = Flow.Heading("New client");
    private readonly Button _save = new();
    private readonly Button _archive = new() { Content = "Archive", Visibility = Visibility.Collapsed };
    private readonly Button _edit = new() { Content = "Edit", Visibility = Visibility.Collapsed };
    private readonly Button _back = new() { Content = "Back to clients" };
    private readonly Button _cancelEdit = new() { Content = "Cancel" };
    private readonly TextBlock _readOnlyNote = Flow.Secondary("These details are read-only. Press Edit to change them.");
    private readonly Pivot _tabs = new();
    private readonly List<PivotItem> _recordTabs = [];
    private readonly StackPanel _invoicesTab = new() { Spacing = 8 };
    private readonly StackPanel _paymentsTab = new() { Spacing = 8 };
    private readonly StackPanel _lettersTab = new() { Spacing = 8 };
    private readonly StackPanel _reportsTab = new() { Spacing = 8 };
    private JsonElement _current;
    private long? _savedId;
    private bool _editing;
    private readonly InfoBar _error = new() { IsOpen = false, IsClosable = false, Severity = InfoBarSeverity.Error };
    private static readonly GridLength[] ClientWidths = [new(2, GridUnitType.Star), new(1.4, GridUnitType.Star), new(1, GridUnitType.Star)];
    private long? _id;
    private bool _archived;
    private bool _dirty;
    private bool _filling;
    private bool _saving;

    public ClientsPage(AppShell shell)
    {
        _shell = shell;
        Flow.UseTable(_list);
        Flow.Surface(_header);
        AutomationProperties.SetName(_list, "Clients");
        AutomationProperties.SetName(_search, "Search clients");
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);
        _empty.Visibility = Visibility.Collapsed;
        _search.TextChanged += (_, _) => Paint();
        _list.SelectionChanged += (_, _) => { if (_list.SelectedItem is ListViewItem item && item.Tag is JsonElement row) Open(row); };
        foreach (var (label, tag) in new[] { ("Select", ""), ("Email", "EMAIL"), ("WhatsApp", "WHATSAPP"), ("Phone", "PHONE"), ("Client portal", "PORTAL") })
            _preference.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
        _preference.SelectedIndex = 0;
        foreach (var (label, tag) in new[] { ("Individual", "individual"), ("Company", "company"), ("Architect firm", "architect_firm") })
            _kind.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
        _kind.SelectedIndex = 0;
        foreach (var (label, tag) in new[] { ("Not recorded", ""), ("Regular", "regular"), ("Composition", "composition"), ("Unregistered", "unregistered"), ("SEZ", "sez") })
            _gstRegistration.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
        _gstRegistration.SelectedIndex = 0;
        _preference.SelectionChanged += (_, _) => { if (!_filling) _dirty = true; };
        _kind.SelectionChanged += (_, _) => { if (!_filling) _dirty = true; };
        _gstRegistration.SelectionChanged += (_, _) => { if (!_filling) _dirty = true; };
        foreach (var box in new[] { _name, _contact, _email, _phone, _address, _billing, _legal, _city, _state, _pan, _tan, _gstin }.Concat(_repName).Concat(_repRole).Concat(_repPhone))
            box.TextChanged += (_, _) => { if (!_filling) _dirty = true; };
        var create = Flow.Command("New client", () => _ = BeginNew(), primary: true);
        AutomationProperties.SetName(create, "New client");
        if (!_shell.CanEdit)
            create.Visibility = Visibility.Collapsed;
        var titleRow = new Grid { ColumnSpacing = 12 };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel { Spacing = 4 };
        titles.Children.Add(Flow.SheetMark("02.01", "Third Parties", "Clients"));
        titles.Children.Add(Flow.Heading("Clients"));
        titles.Children.Add(Flow.Secondary("People and firms this practice works with."));
        titleRow.Children.Add(titles);
        Grid.SetColumn(create, 1);
        create.VerticalAlignment = VerticalAlignment.Top;
        titleRow.Children.Add(create);
        var list = new Grid { RowSpacing = 12 };
        list.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        list.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        list.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        list.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        list.Children.Add(titleRow);
        Grid.SetRow(_search, 1);
        list.Children.Add(_search);
        Grid.SetRow(_summary, 2);
        list.Children.Add(_summary);
        var table = new Grid();
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        table.Children.Add(_header);
        Grid.SetRow(_list, 1);
        table.Children.Add(_list);
        Grid.SetRow(_empty, 1);
        _empty.Margin = new Thickness(8, 12, 8, 0);
        _empty.VerticalAlignment = VerticalAlignment.Top;
        table.Children.Add(_empty);
        Grid.SetRow(table, 3);
        list.Children.Add(table);
        _save.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        _save.Click += (_, _) => SaveClient();
        // Opening a client shows its details read-only. They become editable only after Edit is pressed.
        _cancelEdit.Click += async (_, _) =>
        {
            if (!await CanLeave())
                return;
            if (_id == null)
                ShowList();
            else
                Open(_current);
        };
        _back.Click += async (_, _) => { if (await CanLeave()) ShowList(); };
        _edit.Click += (_, _) => SetEditing(true);
        AutomationProperties.SetName(_edit, "Edit client details");
        _archive.Click += async (_, _) => await ArchiveClient();
        var actions = Flow.Bar(_cancelEdit, _save);
        var details = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 8, 0, 0) };
        details.Children.Add(_readOnlyNote);
        details.Children.Add(Flow.Section("Client"));
        details.Children.Add(Flow.Pair(_name, _contact));
        details.Children.Add(Flow.Pair(_phone, _email));
        details.Children.Add(Flow.Section("Onboarding"));
        details.Children.Add(Flow.Secondary("Correspondence is where letters go. The legal name, GSTIN, PAN, TAN and state are what a tax invoice and a TDS certificate need. Send the pre-design questionnaire after the proposal is accepted, and before concept design."));
        details.Children.Add(Flow.Pair(_kind, _legal));
        details.Children.Add(_address);
        details.Children.Add(_billing);
        details.Children.Add(Flow.Pair(_city, _state));
        details.Children.Add(Flow.Pair(_pan, _tan));
        details.Children.Add(Flow.Pair(_gstin, _gstRegistration));
        details.Children.Add(_preference);
        details.Children.Add(Flow.Section("Authorised representatives"));
        for (var i = 0; i < 3; i++)
        {
            details.Children.Add(Flow.Pair(_repName[i], _repRole[i]));
            details.Children.Add(_repPhone[i]);
        }
        details.Children.Add(actions);
        _tabs.Items.Add(new PivotItem { Header = "Details", Content = details });
        foreach (var (header, panel) in new[] { ("Invoices", _invoicesTab), ("Payments", _paymentsTab), ("Letters and communication", _lettersTab), ("Reports", _reportsTab) })
        {
            var item = new PivotItem { Header = header, Content = Flow.Scroll(panel) };
            _recordTabs.Add(item);
        }
        var form = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        form.Children.Add(_title);
        form.Children.Add(Flow.Bar(_back, _edit, _archive));
        form.Children.Add(_error);
        form.Children.Add(_tabs);
        var host = new Grid { Margin = (Thickness)Application.Current.Resources["PageMargin"] };
        host.Children.Add(list);
        var editorScroll = Flow.Scroll(form);
        editorScroll.Visibility = Visibility.Collapsed;
        host.Children.Add(editorScroll);
        if (_shell.CanEdit)
            Flow.Pin(host, ("New client", () => _ = BeginNew()));
        Content = host;
        Reload();
        if (Flow.TakeCreate(_shell, "clients"))
            _ = BeginNew();
    }

    private Grid Host => (Grid)Content;
    private void ShowList() { _dirty = false; Host.Children[1].Visibility = Visibility.Collapsed; Host.Children[0].Visibility = Visibility.Visible; }

    private async Task BeginNew()
    {
        if (!await CanLeave()) return;
        _filling = true;
        _id = null;
        _archived = false;
        foreach (var box in new[] { _name, _contact, _email, _phone, _address, _billing, _legal, _city, _state, _pan, _tan, _gstin }.Concat(_repName).Concat(_repRole).Concat(_repPhone)) box.Text = "";
        _preference.SelectedIndex = 0;
        _kind.SelectedIndex = 0;
        _gstRegistration.SelectedIndex = 0;
        _title.Text = "New client";
        _save.Content = "Create";
        _error.IsOpen = false;
        _filling = false;
        _dirty = false;
        ShowRecordTabs(false);
        SetEditing(true);
        Host.Children[0].Visibility = Visibility.Collapsed;
        Host.Children[1].Visibility = Visibility.Visible;
        _name.Focus(FocusState.Programmatic);
    }

    private void Open(JsonElement row)
    {
        _current = row.Clone();
        _filling = true;
        _id = Kit.Id(row);
        _archived = row.TryGetProperty("archived", out var flag) && flag.ValueKind == JsonValueKind.True;
        _name.Text = Kit.Str(row, "name");
        _contact.Text = Kit.Str(row, "contact_person");
        _email.Text = Kit.Str(row, "email");
        _phone.Text = Kit.Str(row, "phone");
        _address.Text = Kit.Str(row, "address");
        _billing.Text = Kit.Str(row, "billing_address");
        _legal.Text = Kit.Str(row, "legal_name");
        _city.Text = Kit.Str(row, "city");
        _state.Text = Kit.Str(row, "state");
        _pan.Text = Kit.Str(row, "pan");
        _tan.Text = Kit.Str(row, "tan");
        _gstin.Text = Kit.Str(row, "gstin");
        SelectTag(_kind, Kit.Str(row, "kind").Length == 0 ? "individual" : Kit.Str(row, "kind"));
        SelectTag(_gstRegistration, Kit.Str(row, "gst_registration"));
        SelectPreference(Kit.Str(row, "communication_preference"));
        for (var i = 0; i < 3; i++)
        {
            _repName[i].Text = "";
            _repRole[i].Text = "";
            _repPhone[i].Text = "";
        }
        if (row.TryGetProperty("representatives", out var reps) && reps.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var rep in reps.EnumerateArray())
            {
                if (i >= 3)
                    break;
                _repName[i].Text = Kit.Str(rep, "name");
                _repRole[i].Text = Kit.Str(rep, "designation");
                _repPhone[i].Text = Kit.Str(rep, "phone");
                i++;
            }
        }
        _title.Text = _name.Text;
        _save.Content = "Save changes";
        _error.IsOpen = false;
        _filling = false;
        _dirty = false;
        ShowRecordTabs(true);
        SetEditing(false);
        _tabs.SelectedIndex = 0;
        Host.Children[0].Visibility = Visibility.Collapsed;
        Host.Children[1].Visibility = Visibility.Visible;
        if (_id is long id)
            LoadActivity(id);
    }

    /// <summary>Details are read-only until Edit is pressed. Only then can a field change, and only then is Save offered.</summary>
    private void SetEditing(bool editing)
    {
        _editing = editing;
        var fields = new[] { _name, _contact, _email, _phone, _address, _billing, _legal, _city, _state, _pan, _tan, _gstin }.Concat(_repName).Concat(_repRole).Concat(_repPhone);
        foreach (var box in fields)
            box.IsReadOnly = !editing;
        foreach (var combo in new[] { _preference, _kind, _gstRegistration })
            combo.IsEnabled = editing;
        _save.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        _cancelEdit.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        _readOnlyNote.Visibility = editing || _id == null ? Visibility.Collapsed : Visibility.Visible;
        var existing = _id != null;
        _edit.Visibility = !editing && existing && _shell.CanEdit ? Visibility.Visible : Visibility.Collapsed;
        _archive.Visibility = !editing && existing && _shell.CanEdit ? Visibility.Visible : Visibility.Collapsed;
        if (editing && existing)
            _name.Focus(FocusState.Programmatic);
    }

    /// <summary>A client that has not been saved has no records yet, so the record tabs only appear for a saved client.</summary>
    private void ShowRecordTabs(bool show)
    {
        foreach (var item in _recordTabs)
        {
            var present = _tabs.Items.Contains(item);
            if (show && !present)
                _tabs.Items.Add(item);
            else if (!show && present)
                _tabs.Items.Remove(item);
        }
    }

    private void LoadActivity(long id)
    {
        foreach (var panel in new[] { _invoicesTab, _paymentsTab, _lettersTab, _reportsTab })
        {
            panel.Children.Clear();
            panel.Children.Add(Flow.Secondary("Loading…"));
        }
        _ = _shell.Run("Loading the client's records…", () =>
        {
            var activity = _shell.Call("client_activity", new { client_id = id });
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (_id == id)
                    PaintActivity(activity);
            });
        });
    }

    private static Button RowButton(Action open, string label = "Open")
    {
        var button = new Button { Content = label, MinWidth = 72 };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => open();
        return button;
    }

    private void Go(string page, long id, string kind = "open")
    {
        _shell.PendingOpen = (page, id, kind);
        _shell.Navigate?.Invoke(page);
    }

    private static IEnumerable<JsonElement> Rows(JsonElement activity, string key) =>
        activity.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Select(row => row.Clone()) : [];

    private static void Table(StackPanel host, string[] headers, GridLength[] widths, IEnumerable<(string[] Cells, Action? Open)> lines, string empty, bool[]? alignEnd = null)
    {
        var any = false;
        var rows = new StackPanel { Spacing = 2 };
        foreach (var (cells, open) in lines)
        {
            any = true;
            var body = Flow.Columns(cells, widths, alignEnd);
            rows.Children.Add(open == null ? body : Flow.Line(body, RowButton(open)));
        }
        if (!any)
        {
            host.Children.Add(Flow.Secondary(empty));
            return;
        }
        host.Children.Add(Flow.Header(headers, widths, alignEnd));
        host.Children.Add(rows);
    }

    private void PaintActivity(JsonElement activity)
    {
        var finance = activity.TryGetProperty("finance", out var f) && f.ValueKind == JsonValueKind.True;
        foreach (var panel in new[] { _invoicesTab, _paymentsTab, _lettersTab, _reportsTab })
            panel.Children.Clear();

        if (!finance)
        {
            _invoicesTab.Children.Add(Flow.Secondary("Invoices are shown to roles that have access to the accounts."));
            _paymentsTab.Children.Add(Flow.Secondary("Payments are shown to roles that have access to the accounts."));
        }
        else
        {
            _invoicesTab.Children.Add(Flow.Secondary($"Billed {Kit.Str(activity, "billed_display")}  ·  received {Kit.Str(activity, "received_display")}  ·  outstanding {Kit.Str(activity, "outstanding_display")}. Totals count issued tax invoices only."));
            Table(_invoicesTab, ["Invoice", "Project", "Status", "Issued", "Total", "Balance"], [new(1.3, GridUnitType.Star), new(1.5, GridUnitType.Star), new(90), new(100), new(1, GridUnitType.Star), new(1, GridUnitType.Star)],
                Rows(activity, "invoices").Select<JsonElement, (string[] Cells, Action? Open)>(row =>
                {
                    var id = Kit.Id(row) ?? 0;
                    var number = Kit.Str(row, "number");
                    return (new[] { number.Length == 0 ? "Draft" : number, Kit.Str(row, "project"), Kit.Str(row, "status"), Kit.Str(row, "issue_date"), Kit.Str(row, "total_display"), Kit.Str(row, "balance_display") },
                        (Action?)(() => Go("invoices", id)));
                }),
                "No invoices for this client yet. Create one from the project's path.", [false, false, false, false, true, true]);

            Table(_paymentsTab, ["Date", "Receipt", "Invoice", "Mode", "Amount", "TDS"], [new(100), new(1.2, GridUnitType.Star), new(1.2, GridUnitType.Star), new(80), new(1, GridUnitType.Star), new(1, GridUnitType.Star)],
                Rows(activity, "payments").Select<JsonElement, (string[] Cells, Action? Open)>(row =>
                {
                    var invoiceId = row.TryGetProperty("invoice_id", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
                    var reversed = row.TryGetProperty("reversed", out var r) && r.ValueKind == JsonValueKind.True;
                    var number = Kit.Str(row, "number") + (reversed ? " (reversed)" : "");
                    return (new[] { Kit.Str(row, "date"), number, Kit.Str(row, "invoice"), Kit.Str(row, "mode"), Kit.Str(row, "amount_display"), Kit.Str(row, "tds_display") },
                        invoiceId > 0 ? (Action?)(() => Go("invoices", invoiceId)) : null);
                }),
                "No payments recorded for this client yet.", [false, false, false, false, true, true]);
        }

        Table(_lettersTab, ["Date", "Type", "Reference", "Subject", "Project", "Status"], [new(100), new(110), new(1.1, GridUnitType.Star), new(2, GridUnitType.Star), new(1.3, GridUnitType.Star), new(100)],
            Rows(activity, "communications").Select<JsonElement, (string[] Cells, Action? Open)>(row =>
            {
                var kind = Kit.Str(row, "kind");
                var id = Kit.Id(row) ?? 0;
                var label = kind switch { "letter_out" => "Letter sent", "letter_in" => "Letter received", "agreement" => "Agreement", "transmittal" => "Transmittal", _ => kind };
                Action open = kind == "transmittal" ? () => _shell.Navigate?.Invoke("transmittals") : () => Go("documents", id);
                return (new[] { Kit.Str(row, "date"), label, Kit.Str(row, "number"), Kit.Str(row, "subject"), Kit.Str(row, "project"), Kit.Str(row, "status") }, (Action?)open);
            }),
            "No letters, agreements or transmittals for this client yet.");

        Table(_reportsTab, ["Updated", "Type", "Title", "Project", "Status"], [new(110), new(110), new(2, GridUnitType.Star), new(1.3, GridUnitType.Star), new(100)],
            Rows(activity, "reports").Select<JsonElement, (string[] Cells, Action? Open)>(row =>
                (new[] { Kit.Str(row, "updated").Split(' ')[0], Kit.Str(row, "kind") == "presentation" ? "Presentation" : "Report", Kit.Str(row, "title"), Kit.Str(row, "project"), Kit.Str(row, "status") },
                    (Action?)(() => _shell.Navigate?.Invoke("boards")))),
            "No project reports or presentations for this client yet.");
    }

    private void Paint()
    {
        var needle = _search.Text.Trim().ToLowerInvariant();
        _list.Items.Clear();
        _header.Content = Flow.Header(["Client", "Contact", "Phone"], ClientWidths);
        var count = 0;
        foreach (var row in _rows)
        {
            var blob = $"{Kit.Str(row, "name")} {Kit.Str(row, "contact_person")} {Kit.Str(row, "phone")} {Kit.Str(row, "email")}".ToLowerInvariant();
            if (needle.Length > 0 && !blob.Contains(needle)) continue;
            var cells = new[] { Kit.Str(row, "name"), Kit.Str(row, "contact_person"), Kit.Str(row, "phone") };
            var item = new ListViewItem { Content = Flow.Line(Flow.Columns(cells, ClientWidths), Flow.Edit(() => Open(row))), Tag = row };
            AutomationProperties.SetName(item, $"{cells[0]}, contact {cells[1]}, phone {cells[2]}");
            _list.Items.Add(item);
            count++;
        }
        if (count == 0 && needle.Length == 0)
        {
            _summary.Text = "This company has no clients yet.";
            _empty.Text = "Create a client before starting a project.";
        }
        else if (count == 0)
        {
            _summary.Text = "No clients match this search.";
            _empty.Text = "Change the search.";
        }
        else
        {
            _summary.Text = count == 1 ? "1 client." : $"{count} clients.";
            _empty.Text = "";
        }
        _empty.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Reload(Action? after = null) => _ = _shell.Run("Loading clients…", () =>
    {
        var rows = _shell.Call("client_list", new { include_archived = false });
        _shell.Window.DispatcherQueue.TryEnqueue(() =>
        {
            _rows.Clear();
            if (rows.ValueKind == JsonValueKind.Array)
                foreach (var row in rows.EnumerateArray()) _rows.Add(row.Clone());
            Paint();
            after?.Invoke();
        });
    });

    private void SaveClient()
    {
        if (_saving) return;
        if (_name.Text.Trim().Length == 0) { Flow.Fault(_name, _error, "Name is required."); return; }
        var representatives = new List<object>();
        for (var i = 0; i < 3; i++)
        {
            if (_repName[i].Text.Trim().Length == 0)
                continue;
            representatives.Add(new { name = _repName[i].Text.Trim(), designation = _repRole[i].Text.Trim(), phone = _repPhone[i].Text.Trim() });
        }
        var client = new
        {
            id = _id,
            name = _name.Text.Trim(),
            contact_person = _contact.Text.Trim(),
            email = _email.Text.Trim(),
            phone = _phone.Text.Trim(),
            address = _address.Text.Trim(),
            billing_address = _billing.Text.Trim(),
            communication_preference = ((ComboBoxItem?)_preference.SelectedItem)?.Tag as string ?? "",
            representatives,
            pan = _pan.Text.Trim(),
            tan = _tan.Text.Trim(),
            gstin = _gstin.Text.Trim(),
            kind = ((ComboBoxItem?)_kind.SelectedItem)?.Tag as string ?? "individual",
            city = _city.Text.Trim(),
            state = _state.Text.Trim(),
            legal_name = _legal.Text.Trim(),
            gst_registration = ((ComboBoxItem?)_gstRegistration.SelectedItem)?.Tag as string ?? "",
            archived = _archived,
        };
        _saving = true;
        _save.IsEnabled = false;
        _savedId = null;
        _ = _shell.Run(_id == null ? "Creating client…" : "Saving client…", () =>
            {
                var saved = _shell.Call("client_save", new { client });
                if (saved.ValueKind == JsonValueKind.Number)
                    _savedId = saved.GetInt64();
            })
            .ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                _saving = false;
                _save.IsEnabled = true;
                if (task.Result)
                {
                    _dirty = false;
                    var target = _id ?? _savedId;
                    // back to the saved client, read-only again
                    Reload(() =>
                    {
                        var row = target == null ? default : _rows.FirstOrDefault(r => Kit.Id(r) == target);
                        if (row.ValueKind == JsonValueKind.Object)
                            Open(row);
                        else
                            ShowList();
                    });
                }
            }));
    }

    private async Task ArchiveClient()
    {
        if (_id is not long id || !_shell.CanEdit)
            return;
        if (!await Flow.Confirm(this, "Archive this client?", "The client leaves the active list. Projects already recorded stay.", "Archive"))
            return;
        _ = _shell.Run("Archiving client…", () => _shell.Call("client_set_archived", new { id, archived = true }))
            .ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (!task.Result)
                    return;
                _dirty = false;
                ShowList();
                Reload();
            }));
    }

    private void SelectPreference(string tag) => SelectTag(_preference, tag);

    private static void SelectTag(ComboBox box, string tag)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            if (item.Tag as string == tag)
            {
                box.SelectedItem = item;
                return;
            }
        }
        box.SelectedIndex = 0;
    }

    public async void NewItem() => await BeginNew();
    public void Save() { if (_editing) SaveClient(); }
    public void Print() { }
    public void FocusSearch() => _search.Focus(FocusState.Programmatic);
    public async Task<bool> CanLeave() => await Flow.Discard(this, _dirty);
}

internal sealed class LettersPage : UserControl, IPageCommands
{
    private readonly AppShell _shell;
    private readonly List<JsonElement> _rows = [];
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly ContentControl _header = new();
    private readonly TextBlock _summary = Flow.Secondary("Loading letters…");
    private readonly TextBlock _empty = Flow.Secondary("");
    private readonly TextBox _search = new() { PlaceholderText = "Number, subject, party or reference", Header = "Search", HorizontalAlignment = HorizontalAlignment.Stretch };
    private static readonly GridLength[] LetterWidths = [new(140), new(2, GridUnitType.Star), new(1.4, GridUnitType.Star), new(120)];
    private readonly ComboBox _kind = new() { Header = "Document type" };
    private readonly TextBox _date = Flow.Input("Date");
    private readonly TextBox _reference = Flow.Input("Reference");
    private readonly ComboBox _project = new() { Header = "Project" };
    private readonly TextBox _party = Flow.Input("Party");
    private readonly TextBox _address = Flow.Input("Address", true);
    private readonly TextBox _subject = Flow.Input("Subject");
    private readonly TextBox _body = Flow.Input("Body", true);
    private readonly TextBlock _title = Flow.Heading("New letter");
    private readonly TextBlock _state = Flow.Secondary("Draft. Save draft keeps it editable. Issue gives it a number and freezes the content.");
    private readonly Button _save = new() { Content = "Save draft" };
    private readonly Button _preview = new() { Content = "Preview" };
    private readonly Button _check = new() { Content = "Check language" };
    private readonly Button _issue = new() { Content = "Issue" };
    private readonly StackPanel _language = new() { Spacing = 8 };
    private readonly DispatcherTimer _languageTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private ulong _revision;
    private int _languageRequest;
    private readonly Button _revise = new() { Content = "Revise", Visibility = Visibility.Collapsed };
    private readonly Button _attach = new() { Content = "Attach signed copy", Visibility = Visibility.Collapsed };
    private readonly Button _sign = new() { Content = "Mark signed", Visibility = Visibility.Collapsed };
    private readonly TextBox _cancelReason = Flow.Input("Reason for cancelling");
    private readonly Button _cancelDoc = new() { Content = "Cancel document", Visibility = Visibility.Collapsed };
    private readonly InfoBar _error = new() { IsOpen = false, IsClosable = false, Severity = InfoBarSeverity.Error };
    private long? _id;
    private string _status = "draft";
    private bool _dirty;
    private bool _filling;
    private bool _saving;

    public LettersPage(AppShell shell)
    {
        _shell = shell;
        Flow.UseTable(_list);
        Flow.Surface(_header);
        AutomationProperties.SetName(_list, "Letters");
        AutomationProperties.SetName(_search, "Search letters");
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);
        _empty.Visibility = Visibility.Collapsed;
        _kind.Items.Add(Item("Outward letter", "letter_out"));
        _kind.Items.Add(Item("Agreement", "agreement"));
        _body.MinHeight = 220;
        _search.TextChanged += (_, _) => Paint();
        _list.SelectionChanged += (_, _) => { if (_list.SelectedItem is ListViewItem item && item.Tag is JsonElement row) Open(row); };
        foreach (var box in new[] { _date, _reference, _party, _address, _subject, _body })
            box.TextChanged += (_, _) => { if (!_filling) _dirty = true; };
        _body.TextChanged += (_, _) =>
        {
            _revision++;
            _language.Children.Clear();
            if (_filling || !_body.IsEnabled)
                return;
            _languageTimer.Stop();
            _languageTimer.Start();
        };
        _languageTimer.Tick += (_, _) =>
        {
            _languageTimer.Stop();
            CheckLanguage();
        };
        AutomationProperties.SetName(_check, "Check the letter language");
        AutomationProperties.SetHelpText(_check, "Suggests changes to the letter. Accepting one updates the draft. An issued document is left as it was.");
        _check.Click += (_, _) => CheckLanguage();
        var letter = Flow.Command("New letter", () => _ = BeginNew("letter_out"), primary: true);
        var agreement = Flow.Command("New agreement", () => _ = BeginNew("agreement"));
        AutomationProperties.SetName(letter, "New letter");
        AutomationProperties.SetName(agreement, "New agreement");
        if (!_shell.CanEdit)
            letter.Visibility = agreement.Visibility = Visibility.Collapsed;
        var commands = Flow.Bar(letter, agreement);
        var titleRow = new Grid { ColumnSpacing = 12 };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel { Spacing = 4 };
        titles.Children.Add(Flow.SheetMark("03.01", "Office", "Letters"));
        titles.Children.Add(Flow.Heading("Letters and agreements"));
        titles.Children.Add(Flow.Secondary("Drafts stay editable. Issued documents are frozen."));
        titleRow.Children.Add(titles);
        Grid.SetColumn(commands, 1);
        commands.VerticalAlignment = VerticalAlignment.Top;
        titleRow.Children.Add(commands);
        var list = new Grid { RowSpacing = 12 };
        list.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        list.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        list.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        list.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        list.Children.Add(titleRow);
        Grid.SetRow(_search, 1);
        list.Children.Add(_search);
        Grid.SetRow(_summary, 2);
        list.Children.Add(_summary);
        var table = new Grid();
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        table.Children.Add(_header);
        Grid.SetRow(_list, 1);
        table.Children.Add(_list);
        Grid.SetRow(_empty, 1);
        _empty.Margin = new Thickness(8, 12, 8, 0);
        _empty.VerticalAlignment = VerticalAlignment.Top;
        table.Children.Add(_empty);
        Grid.SetRow(table, 3);
        list.Children.Add(table);

        _save.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        _save.Click += (_, _) => SaveDraft();
        _preview.Click += (_, _) => Preview();
        _issue.Click += async (_, _) => await Issue();
        _revise.Click += async (_, _) => await Revise();
        _attach.Click += async (_, _) => await AttachSigned();
        _sign.Click += async (_, _) => await MarkSigned();
        _cancelDoc.Click += async (_, _) => await CancelDocument();
        _cancelReason.Visibility = Visibility.Collapsed;
        var cancel = new Button { Content = "Back" };
        cancel.Click += async (_, _) => { if (await CanLeave()) ShowList(); };
        var actions = Flow.Bar(cancel, _save, _check, _preview, _issue, _revise, _attach, _sign, _cancelDoc);
        var form = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        form.Children.Add(_title);
        form.Children.Add(_state);
        form.Children.Add(Flow.Section("Document details"));
        form.Children.Add(Flow.Pair(_kind, _date));
        form.Children.Add(Flow.Pair(_reference, _project));
        form.Children.Add(Flow.Section("Recipient"));
        form.Children.Add(_party);
        form.Children.Add(_address);
        form.Children.Add(Flow.Section("Content"));
        form.Children.Add(_subject);
        form.Children.Add(_body);
        form.Children.Add(Flow.Secondary("Language check reads this draft. A suggestion changes the letter only when you accept it. Issued documents and the accounts are left as they are."));
        form.Children.Add(_language);
        form.Children.Add(_cancelReason);
        form.Children.Add(_error);
        form.Children.Add(actions);
        var host = new Grid { Margin = (Thickness)Application.Current.Resources["PageMargin"] };
        host.Children.Add(list);
        var editorScroll = Flow.Scroll(form);
        editorScroll.Visibility = Visibility.Collapsed;
        host.Children.Add(editorScroll);
        if (_shell.CanEdit)
            Flow.Pin(host, ("New letter", () => _ = BeginNew("letter_out")), ("New agreement", () => _ = BeginNew("agreement")));
        Content = host;
        Reload();
        if (Flow.TakeCreate(_shell, "documents"))
            _ = BeginNew("letter_out");
        else if (Flow.TakeOpen(_shell, "documents") is { } target)
        {
            if (target.Kind == "new_agreement")
                _ = BeginNewAgreementFor(target.Id);
            else
                OpenById(target.Id);
        }
    }

    /// <summary>Starts an agreement with the project already chosen, so its client and stages are not typed again.</summary>
    private async Task BeginNewAgreementFor(long projectId)
    {
        await BeginNew("agreement");
        _filling = true;
        LoadProjects(projectId);
        _filling = false;
    }

    private void OpenById(long documentId)
    {
        _ = _shell.Run("Opening document…", () =>
        {
            var detail = _shell.Call("doc_get", new { id = documentId });
            _shell.Window.DispatcherQueue.TryEnqueue(() => Apply(detail));
        });
    }

    private Grid Host => (Grid)Content;
    private void ShowList() { _dirty = false; Host.Children[1].Visibility = Visibility.Collapsed; Host.Children[0].Visibility = Visibility.Visible; }

    private async Task BeginNew(string kind)
    {
        if (!await CanLeave()) return;
        _filling = true;
        _id = null;
        _status = "draft";
        _date.Text = Kit.Today();
        _reference.Text = _party.Text = _address.Text = _subject.Text = _body.Text = "";
        Select(_kind, kind);
        LoadProjects(null);
        _title.Text = kind == "agreement" ? "New agreement" : "New letter";
        _state.Text = "Draft. Save draft keeps it editable. Issue gives it a number and freezes the content.";
        SetEditable(true);
        ShowIssuedActions(false, false);
        _error.IsOpen = false;
        _language.Children.Clear();
        _languageTimer.Stop();
        _filling = false;
        _dirty = false;
        Host.Children[0].Visibility = Visibility.Collapsed;
        Host.Children[1].Visibility = Visibility.Visible;
        _subject.Focus(FocusState.Programmatic);
    }

    private void Open(JsonElement row)
    {
        var id = Kit.Id(row);
        if (id is not long docId) return;
        _ = _shell.Run("Opening document…", () =>
        {
            var detail = _shell.Call("doc_get", new { id = docId });
            _shell.Window.DispatcherQueue.TryEnqueue(() => Apply(detail));
        });
    }

    private void Apply(JsonElement detail)
    {
        _filling = true;
        var input = detail.TryGetProperty("input", out var body) ? body : detail;
        _id = Kit.Id(input);
        _status = Kit.Str(detail, "status");
        if (_status.Length == 0) _status = "draft";
        Select(_kind, Kit.Str(input, "kind"));
        _date.Text = Kit.Str(input, "doc_date");
        _reference.Text = Kit.Str(input, "reference");
        _party.Text = Kit.Str(input, "party_name");
        _address.Text = Kit.Str(input, "party_address");
        _subject.Text = Kit.Str(input, "subject");
        _body.Text = Kit.Str(input, "body");
        var project = input.TryGetProperty("project_id", out var projectId) && projectId.ValueKind == JsonValueKind.Number ? projectId.GetInt64() : (long?)null;
        LoadProjects(project);
        var number = Kit.Str(detail, "number");
        var draft = _status == "draft";
        _title.Text = number.Length > 0 ? number : _subject.Text;
        _state.Text = draft ? "Draft. Issue gives this document a number. After that, the content cannot be edited." : $"{_status}. This document is frozen. A change is a revision, not an edit.";
        SetEditable(draft && _shell.CanEdit);
        _issue.Visibility = draft && _shell.CanIssue ? Visibility.Visible : Visibility.Collapsed;
        ShowIssuedActions(_status is "issued" or "signed", Kit.Str(input, "kind") == "agreement" && _status == "issued");
        _error.IsOpen = false;
        _filling = false;
        _dirty = false;
        Host.Children[0].Visibility = Visibility.Collapsed;
        Host.Children[1].Visibility = Visibility.Visible;
        if (_body.IsEnabled && _body.Text.Length > 0)
            CheckLanguage();
    }

    private void CheckLanguage()
    {
        if (!_body.IsEnabled)
            return;
        var text = _body.Text;
        var revision = _revision;
        var request = ++_languageRequest;
        if (text.Trim().Length == 0)
        {
            _language.Children.Clear();
            return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                var result = _shell.Call("grammar_check", new { api_version = 1, document_revision = revision, dialect = "Indian", text, protected_ranges = Array.Empty<object>() });
                _shell.Window.DispatcherQueue.TryEnqueue(() =>
                {
                    if (request != _languageRequest || revision != _revision || text != _body.Text)
                        return;
                    ShowLanguage(result, text, revision);
                });
            }
            catch (AdmsException ex)
            {
                _shell.Window.DispatcherQueue.TryEnqueue(() =>
                {
                    if (request != _languageRequest || revision != _revision)
                        return;
                    _language.Children.Clear();
                    _language.Children.Add(Flow.Secondary(ex.Message));
                });
            }
        });
    }

    private void ShowLanguage(JsonElement result, string text, ulong revision)
    {
        _language.Children.Clear();
        if (!result.TryGetProperty("issues", out var issues) || issues.ValueKind != JsonValueKind.Array || issues.GetArrayLength() == 0)
        {
            _language.Children.Add(Flow.Secondary("No changes suggested."));
            return;
        }
        foreach (var issue in issues.EnumerateArray())
        {
            var start = issue.TryGetProperty("start_utf16", out var startValue) && startValue.TryGetInt32(out var startIndex) ? startIndex : -1;
            var length = issue.TryGetProperty("length_utf16", out var lengthValue) && lengthValue.TryGetInt32(out var lengthIndex) ? lengthIndex : -1;
            var original = Kit.Str(issue, "original");
            var message = Kit.Str(issue, "message");
            var row = new StackPanel { Spacing = 4 };
            var title = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
            row.Children.Add(title);
            if (original.Length > 0)
                row.Children.Add(Flow.Secondary(original));
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (issue.TryGetProperty("suggestions", out var suggestions) && suggestions.ValueKind == JsonValueKind.Array)
            {
                foreach (var suggestion in suggestions.EnumerateArray())
                {
                    var replacement = suggestion.GetString() ?? "";
                    var label = replacement.Length == 0 ? "Remove" : replacement.Length > 48 ? replacement[..48] + "…" : replacement;
                    var accept = new Button { Content = label, MinHeight = 32 };
                    var capturedStart = start;
                    var capturedLength = length;
                    var capturedOriginal = original;
                    var capturedReplacement = replacement;
                    accept.Click += (_, _) => AcceptSuggestion(revision, text, capturedStart, capturedLength, capturedOriginal, capturedReplacement);
                    actions.Children.Add(accept);
                }
            }
            var ignore = new Button { Content = "Ignore", MinHeight = 32 };
            ignore.Click += (_, _) => row.Visibility = Visibility.Collapsed;
            actions.Children.Add(ignore);
            row.Children.Add(actions);
            row.Tapped += (_, _) =>
            {
                if (capturedRange(start, length, text))
                    _body.Select(start, length);
            };
            _language.Children.Add(row);
        }
    }

    private static bool capturedRange(int start, int length, string text) => start >= 0 && length >= 0 && start + length <= text.Length;

    private void AcceptSuggestion(ulong revision, string checkedText, int start, int length, string original, string replacement)
    {
        if (revision != _revision || _body.Text != checkedText || !capturedRange(start, length, _body.Text))
            return;
        if (_body.Text.Substring(start, length) != original)
            return;
        _body.Text = _body.Text.Remove(start, length).Insert(start, replacement);
        _body.Select(start, replacement.Length);
    }

    private void SetEditable(bool editable)
    {
        foreach (var box in new Control[] { _kind, _date, _reference, _project, _party, _address, _subject, _body })
            box.IsEnabled = editable;
        _save.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        _check.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        if (!editable)
        {
            _languageTimer.Stop();
            _language.Children.Clear();
        }
    }

    private void ShowIssuedActions(bool issued, bool signable)
    {
        _revise.Visibility = issued && _shell.CanEdit ? Visibility.Visible : Visibility.Collapsed;
        _cancelDoc.Visibility = _status == "issued" && _shell.CanIssue ? Visibility.Visible : Visibility.Collapsed;
        _cancelReason.Visibility = _cancelDoc.Visibility;
        _attach.Visibility = signable && _shell.CanEdit ? Visibility.Visible : Visibility.Collapsed;
        _sign.Visibility = signable && _shell.CanIssue ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task Revise()
    {
        if (_id is not long id || !_shell.CanEdit)
            return;
        if (!await Flow.Confirm(this, "Revise this document?", "A new draft is created. The issued document stays as it was.", "Revise"))
            return;
        _ = _shell.Run("Creating a revision…", () =>
        {
            var created = _shell.Call("doc_revise", new { id });
            var next = created.ValueKind == JsonValueKind.Number ? created.GetInt64() : id;
            var detail = _shell.Call("doc_get", new { id = next });
            _shell.Window.DispatcherQueue.TryEnqueue(() => Apply(detail));
        });
    }

    private async Task AttachSigned()
    {
        if (_id is not long id)
            return;
        string? path;
        try { path = await FileDialogs.PickFile(_shell.Window.Handle, ".pdf", ".png", ".jpg", ".jpeg"); }
        catch (Exception ex) { _shell.Problem(ex.Message); return; }
        if (path == null)
            return;
        var filename = Path.GetFileName(path);
        var data = Convert.ToBase64String(await File.ReadAllBytesAsync(path));
        _ = _shell.Run("Attaching the signed copy…", () => _shell.Call("doc_attach", new { doc_id = id, kind = "signed_copy", filename, base64_data = data }));
    }

    private async Task MarkSigned()
    {
        if (_id is not long id || !_shell.CanIssue)
            return;
        if (!await Flow.Confirm(this, "Mark this agreement as signed?", "The signed copy must already be attached. The agreement then stays on record.", "Mark signed"))
            return;
        _ = _shell.Run("Marking signed…", () => _shell.Call("doc_mark_signed", new { id }))
            .ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (!task.Result)
                    return;
                _dirty = false;
                ShowList();
                Reload();
            }));
    }

    private async Task CancelDocument()
    {
        if (_id is not long id || !_shell.CanIssue)
            return;
        var reason = _cancelReason.Text.Trim();
        if (reason.Length == 0)
        {
            _shell.Problem("Give a reason.");
            return;
        }
        if (!await Flow.Confirm(this, "Cancel this document?", "The number stays used. A signed agreement stays on record.", "Cancel document"))
            return;
        _ = _shell.Run("Cancelling document…", () => _shell.Call("doc_cancel", new { id, reason }))
            .ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (!task.Result)
                    return;
                _dirty = false;
                ShowList();
                Reload();
            }));
    }

    private void LoadProjects(long? selected)
    {
        var rows = Flow.Try(_shell, "project_list", new { client_id = (long?)null, include_archived = false });
        _project.Items.Clear();
        _project.Items.Add(Item("No project", 0L));
        if (rows is JsonElement list && list.ValueKind == JsonValueKind.Array)
            foreach (var row in list.EnumerateArray())
                _project.Items.Add(Item($"{Kit.Str(row, "client_name")} — {Kit.Str(row, "name")}", Kit.Id(row) ?? 0));
        Select(_project, selected ?? 0);
    }

    private void Paint()
    {
        var needle = _search.Text.Trim().ToLowerInvariant();
        _list.Items.Clear();
        _header.Content = Flow.Header(["Date", "Subject", "Recipient", "Status"], LetterWidths);
        var count = 0;
        foreach (var row in _rows)
        {
            var blob = $"{Kit.Str(row, "number")} {Kit.Str(row, "subject")} {Kit.Str(row, "party_name")} {Kit.Str(row, "reference")}".ToLowerInvariant();
            if (needle.Length > 0 && !blob.Contains(needle)) continue;
            var subject = Kit.Str(row, "subject");
            if (subject.Length == 0) subject = Kit.Str(row, "number");
            var cells = new[] { Kit.Str(row, "doc_date"), subject, Kit.Str(row, "party_name"), Kit.Str(row, "status") };
            var item = new ListViewItem { Content = Flow.Line(Flow.Columns(cells, LetterWidths), Flow.Edit(() => Open(row))), Tag = row };
            AutomationProperties.SetName(item, $"{cells[1]}, {cells[0]}, recipient {cells[2]}, {cells[3]}");
            _list.Items.Add(item);
            count++;
        }
        if (count == 0 && needle.Length == 0)
        {
            _summary.Text = "This company has no letters yet.";
            _empty.Text = "Create a letter or an agreement.";
        }
        else if (count == 0)
        {
            _summary.Text = "No letters match this search.";
            _empty.Text = "Change the search.";
        }
        else
        {
            _summary.Text = count == 1 ? "1 letter." : $"{count} letters.";
            _empty.Text = "";
        }
        _empty.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Reload() => _ = _shell.Run("Loading documents…", () =>
    {
        var rows = _shell.Call("doc_list", new { kind = (string?)null, q = (string?)null });
        _shell.Window.DispatcherQueue.TryEnqueue(() =>
        {
            _rows.Clear();
            if (rows.ValueKind == JsonValueKind.Array)
                foreach (var row in rows.EnumerateArray()) _rows.Add(row.Clone());
            Paint();
        });
    });

    private object Input()
    {
        var projectId = ((ComboBoxItem?)_project.SelectedItem)?.Tag is long id && id > 0 ? id : (long?)null;
        return new
        {
            id = _id,
            kind = ((ComboBoxItem?)_kind.SelectedItem)?.Tag as string ?? "letter_out",
            project_id = projectId,
            party_name = _party.Text,
            party_address = _address.Text,
            subject = _subject.Text,
            reference = _reference.Text,
            doc_date = _date.Text,
            body = _body.Text,
            template_version_id = (long?)null,
            fee = "0",
            milestones = Array.Empty<object>(),
        };
    }

    private void SaveDraft()
    {
        if (_saving || _status != "draft") return;
        if (_subject.Text.Trim().Length == 0) { Flow.Fault(_subject, _error, "Subject is required."); return; }
        var input = Input();
        _saving = true;
        _ = _shell.Run("Saving draft…", () =>
        {
            var saved = _shell.Call("doc_save_draft", new { input });
            if (saved.ValueKind == JsonValueKind.Number) _id = saved.GetInt64();
        }).ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() =>
        {
            _saving = false;
            if (task.Result) { _dirty = false; _error.IsOpen = false; _state.Text = "Draft saved."; }
        }));
    }

    private void Preview()
    {
        if (_id is not long id) { _error.Message = "Save the draft before previewing it."; _error.IsOpen = true; return; }
        _ = _shell.Run("Preparing PDF…", () =>
        {
            var encoded = _shell.Call("doc_pdf", new { id }).GetString() ?? "";
            _shell.Window.DispatcherQueue.TryEnqueue(async () => await Kit.PreviewPdf(_shell, encoded, "document"));
        });
    }

    private async Task Issue()
    {
        if (_saving || _status != "draft" || !_shell.CanIssue) return;
        if (!await Flow.Confirm(this, "Issue this document?", "Issue gives the document a number. The content can no longer be edited. This is not the same as saving a draft.", "Issue"))
            return;
        if (_subject.Text.Trim().Length == 0) { Flow.Fault(_subject, _error, "Subject is required."); return; }
        var input = Input();
        _saving = true;
        _issue.IsEnabled = false;
        _ = _shell.Run("Issuing document…", () =>
        {
            var saved = _shell.Call("doc_save_draft", new { input });
            var id = saved.ValueKind == JsonValueKind.Number ? saved.GetInt64() : _id ?? throw new AdmsException(1, "Save the draft before issuing it.");
            _shell.Call("doc_issue", new { id });
        }).ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() =>
        {
            _saving = false;
            _issue.IsEnabled = true;
            if (!task.Result) return;
            _dirty = false;
            _status = "issued";
            ShowList();
            Reload();
        }));
    }

    public async void NewItem() => await BeginNew("letter_out");
    public void Save() => SaveDraft();
    public void Print() => Preview();
    public void FocusSearch() => _search.Focus(FocusState.Programmatic);
    public async Task<bool> CanLeave() => await Flow.Discard(this, _dirty);

    private static ComboBoxItem Item(string label, object tag) => new() { Content = label, Tag = tag };
    private static void Select(ComboBox box, object tag)
    {
        foreach (ComboBoxItem item in box.Items)
            if (Equals(item.Tag, tag) || item.Tag?.ToString() == tag.ToString()) { box.SelectedItem = item; return; }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }
}

internal sealed class BankWorkspace : UserControl, IPageCommands
{
    private readonly AppShell _shell;
    private readonly ListView _accounts = new();
    private readonly ListView _preview = new();
    private readonly ListView _transactions = new();
    private readonly ComboBox _importAccount = new() { Header = "Bank account" };
    private readonly ComboBox _mapDate = new() { Header = "Date", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _mapParticulars = new() { Header = "Particulars", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _mapCredit = new() { Header = "Credit", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _mapDebit = new() { Header = "Debit", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _dateOrder = new() { Header = "Date order", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _fileColumns = new() { Spacing = 4 };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.WrapWholeWords };
    private readonly InfoBar _notice = Flow.Notice();
    private readonly TextBox _accountName = Flow.Input("Account name");
    private readonly TextBox _bank = Flow.Input("Bank");
    private readonly TextBox _number = Flow.Input("Account number");
    private readonly TextBox _ifsc = Flow.Input("IFSC");
    private readonly Button _saveAccount = new() { Content = "Create" };
    private string? _csv;
    private string? _csvName;
    private bool _heading = true;
    private bool _mapping;
    private int _previewTicket;
    private bool _previewClean;
    private long? _accountId;
    private bool _dirty;
    private StackPanel? _accountForm;

    public BankWorkspace(AppShell shell)
    {
        _shell = shell;
        Flow.UseTable(_accounts);
        Flow.UseTable(_preview);
        Flow.UseTable(_transactions);
        _dateOrder.Items.Add(new ComboBoxItem { Content = "Day, month, year", Tag = "dmy" });
        _dateOrder.Items.Add(new ComboBoxItem { Content = "Month, day, year", Tag = "mdy" });
        _dateOrder.Items.Add(new ComboBoxItem { Content = "Year, month, day", Tag = "ymd" });
        _dateOrder.SelectedIndex = 0;
        foreach (var box in new ComboBox[] { _mapDate, _mapParticulars, _mapCredit, _mapDebit, _dateOrder })
            box.SelectionChanged += (_, _) => { if (!_mapping && _csv != null) CheckRows(); };
        var pivot = new Pivot { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        pivot.Items.Add(new PivotItem { Header = "Accounts", Content = AccountsPane() });
        pivot.Items.Add(new PivotItem { Header = "Import statement", Content = ImportPane() });
        pivot.Items.Add(new PivotItem { Header = "Reconciliation", Content = ReconciliationPane() });
        var column = new Grid { Margin = new Thickness(8) };
        column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        column.Children.Add(Flow.SheetMark("04.09", "Accounts", "Reconciliation"));
        Grid.SetRow(pivot, 1);
        column.Children.Add(pivot);
        Content = column;
        LoadAccounts();
        if (Flow.TakeCreate(_shell, "bank"))
            pivot.SelectedIndex = 1;
    }

    private UIElement AccountsPane()
    {
        var form = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        form.Children.Add(Flow.Heading("Bank account"));
        form.Children.Add(Flow.Pair(_accountName, _bank));
        form.Children.Add(Flow.Pair(_number, _ifsc));
        _saveAccount.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        _saveAccount.Click += (_, _) => SaveAccount();
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => { _dirty = false; form.Visibility = Visibility.Collapsed; };
        var actions = Flow.Bar(cancel, _saveAccount);
        form.Children.Add(actions);
        form.Visibility = Visibility.Collapsed;
        _accountForm = form;
        foreach (var box in new[] { _accountName, _bank, _number, _ifsc })
            box.TextChanged += (_, _) => _dirty = true;
        var add = new Button { Content = "New account", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        add.Click += (_, _) => BeginAccount();
        var pane = new Grid { RowSpacing = 8, Margin = new Thickness(8) };
        pane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        pane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pane.Children.Add(add);
        Grid.SetRow(_accounts, 1);
        pane.Children.Add(_accounts);
        Grid.SetRow(form, 2);
        pane.Children.Add(form);
        _accounts.SelectionChanged += (_, _) =>
        {
            if (_accounts.SelectedItem is ListViewItem item && item.Tag is JsonElement row)
                EditAccount(row, form);
        };
        return pane;
    }

    private UIElement ImportPane()
    {
        var pick = new Button { Content = "Choose statement" };
        pick.Click += async (_, _) => await ChooseStatement();
        var check = new Button { Content = "Check rows" };
        check.Click += (_, _) => CheckRows();
        var commit = new Button { Content = "Import", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        commit.Click += (_, _) => CommitImport();
        _importAccount.HorizontalAlignment = HorizontalAlignment.Stretch;
        var bar = Flow.Bar(_importAccount, pick, check, commit);
        var columns = new Grid();
        var pane = new StackPanel { Spacing = 8, Margin = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Stretch };
        pane.Children.Add(new TextBlock { Text = "Banks keep Date, Particulars, Credit and Debit in their own order. Choose a CSV or Excel statement, map each column, check the rows, then import. A row with no date is left out, including any other text or amount on that row.", TextWrapping = TextWrapping.WrapWholeWords, Style = Flow.Look("BodyStyle") });
        pane.Children.Add(bar);
        pane.Children.Add(_fileColumns);
        pane.Children.Add(Flow.Section("Map the columns"));
        pane.Children.Add(columns);
        pane.Children.Add(_notice);
        pane.Children.Add(_preview);
        pane.SizeChanged += (_, _) => Flow.Place(columns, [_mapDate, _mapParticulars, _mapCredit, _mapDebit, _dateOrder], 180, true);
        return Flow.Scroll(pane);
    }

    private UIElement ReconciliationPane()
    {
        var refresh = new Button { Content = "Refresh summary" };
        refresh.Click += (_, _) => LoadSummary();
        var pane = new Grid { RowSpacing = 8, Margin = new Thickness(8) };
        pane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        pane.Children.Add(_summary);
        Grid.SetRow(refresh, 1);
        pane.Children.Add(refresh);
        Grid.SetRow(_transactions, 2);
        pane.Children.Add(_transactions);
        LoadSummary();
        return pane;
    }

    private void BeginAccount()
    {
        _accountId = null;
        _accountName.Text = _bank.Text = _number.Text = _ifsc.Text = "";
        _saveAccount.Content = "Create";
        _dirty = false;
        if (_accountForm != null)
            _accountForm.Visibility = Visibility.Visible;
    }

    private void EditAccount(JsonElement row, StackPanel form)
    {
        _accountId = Kit.Id(row);
        _accountName.Text = Kit.Str(row, "name");
        _bank.Text = Kit.Str(row, "bank_name");
        _number.Text = Kit.Str(row, "account_number");
        _ifsc.Text = Kit.Str(row, "ifsc");
        _saveAccount.Content = "Save changes";
        _dirty = false;
        form.Visibility = Visibility.Visible;
    }

    private void SaveAccount()
    {
        var account = new { id = _accountId, name = _accountName.Text.Trim(), bank_name = _bank.Text.Trim(), account_holder = "", account_number = _number.Text.Trim(), ifsc = _ifsc.Text.Trim(), is_primary = false, active = true };
        _ = _shell.Run(_accountId == null ? "Creating account…" : "Saving account…", () => _shell.Call("bank_account_save", new { account }))
            .ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() => { if (task.Result) { _dirty = false; LoadAccounts(); } }));
    }

    private void LoadAccounts() => _ = _shell.Run("Loading accounts…", () =>
    {
        var rows = _shell.Call("bank_account_list");
        _shell.Window.DispatcherQueue.TryEnqueue(() =>
        {
            _accounts.Items.Clear();
            _importAccount.Items.Clear();
            var widths = new GridLength[] { new(1.4, GridUnitType.Star), new(1.2, GridUnitType.Star), new(1, GridUnitType.Star), new(120) };
            _accounts.Items.Add(Flow.Header(["Account", "Bank", "Number", "IFSC"], widths));
            if (rows.ValueKind != JsonValueKind.Array) return;
            foreach (var row in rows.EnumerateArray())
            {
                var clone = row.Clone();
                var edit = Flow.Edit(() => { if (_accountForm != null) EditAccount(clone, _accountForm); });
                _accounts.Items.Add(new ListViewItem
                {
                    Content = Flow.Line(Flow.Columns([Kit.Str(clone, "name"), Kit.Str(clone, "bank_name"), Mask(Kit.Str(clone, "account_number")), Kit.Str(clone, "ifsc")], widths), edit),
                    Tag = clone,
                });
                _importAccount.Items.Add(new ComboBoxItem { Content = Kit.Str(clone, "name"), Tag = Kit.Id(clone) ?? 0 });
            }
            if (_importAccount.Items.Count > 0 && _importAccount.SelectedIndex < 0)
                _importAccount.SelectedIndex = 0;
        });
    });

    private async Task ChooseStatement()
    {
        string? path;
        try { path = await FileDialogs.PickFile(_shell.Window.Handle, ".csv", ".txt", ".xlsx"); }
        catch (Exception ex) { _shell.Problem(ex.Message); return; }
        if (path == null)
            return;
        StatementFile file;
        try { file = await Task.Run(() => BankStatement.Open(path)); }
        catch (Exception ex) { _shell.Problem(ex.Message); return; }
        _csv = file.Text;
        _csvName = Path.GetFileName(path);
        _heading = file.Heading;
        _previewClean = false;
        ShowColumns(file);
        CheckRows();
    }

    private void ShowColumns(StatementFile file)
    {
        _mapping = true;
        _fileColumns.Children.Clear();
        _fileColumns.Children.Add(Flow.Secondary(file.Heading
            ? "Columns in this file, in the bank's order."
            : "This file has no headings. The sample shows each column."));
        if (file.Headers.Length > 0)
            _fileColumns.Children.Add(Flow.Header(file.Headers));
        foreach (var row in file.Sample)
            _fileColumns.Children.Add(Flow.Columns(Pad(row, file.Headers.Length)));
        foreach (var box in new[] { _mapDate, _mapParticulars, _mapCredit, _mapDebit })
            box.Items.Clear();
        for (var i = 0; i < file.Labels.Length; i++)
        {
            foreach (var box in new[] { _mapDate, _mapParticulars, _mapCredit, _mapDebit })
                box.Items.Add(new ComboBoxItem { Content = file.Labels[i], Tag = i });
        }
        var used = new HashSet<int>();
        Pick(_mapDate, Take(used, file.Labels.Length, BankStatement.Suggest(file.Headers, "date"), 0));
        Pick(_mapParticulars, Take(used, file.Labels.Length, BankStatement.Suggest(file.Headers, "particulars", "narration", "description", "remarks", "details"), 1));
        Pick(_mapCredit, Take(used, file.Labels.Length, BankStatement.Suggest(file.Headers, "credit", "deposit", "cr"), 2));
        Pick(_mapDebit, Take(used, file.Labels.Length, BankStatement.Suggest(file.Headers, "debit", "withdrawal", "withdraw", "dr"), 3));
        _mapping = false;
    }

    private static int Take(HashSet<int> used, int count, int? found, int fallback)
    {
        if (count <= 0)
            return 0;
        if (found is int match && match >= 0 && match < count && used.Add(match))
            return match;
        for (var index = Math.Clamp(fallback, 0, count - 1); index < count; index++)
        {
            if (used.Add(index))
                return index;
        }
        for (var index = 0; index < count; index++)
        {
            if (used.Add(index))
                return index;
        }
        return 0;
    }

    private static void Pick(ComboBox box, int index)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            if (item.Tag is int value && value == index)
            {
                box.SelectedItem = item;
                return;
            }
        }
        if (box.Items.Count > 0)
            box.SelectedIndex = 0;
    }

    private static string[] Pad(string[] row, int width)
    {
        var cells = new string[width];
        for (var i = 0; i < width; i++)
            cells[i] = i < row.Length ? row[i] : "";
        return cells;
    }

    private void CheckRows()
    {
        if (_csv == null)
        {
            _shell.Problem("Choose a statement first.");
            return;
        }
        var ticket = ++_previewTicket;
        var text = _csv;
        var mapping = Mapping();
        _previewClean = false;
        _ = _shell.Run("Reading statement…", () =>
        {
            var parsed = _shell.Call("bank_csv_preview", new { text, mapping });
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (ticket == _previewTicket)
                    ShowPreview(parsed);
            });
        });
    }

    private void ShowPreview(JsonElement parsed)
    {
        _preview.Items.Clear();
        _previewClean = parsed.ValueKind == JsonValueKind.Array && parsed.GetArrayLength() > 0;
        if (parsed.ValueKind != JsonValueKind.Array)
        {
            _notice.Severity = InfoBarSeverity.Error;
            _notice.Message = "The file could not be read.";
            _notice.IsOpen = true;
            return;
        }
        var widths = new GridLength[] { new(120), new(2, GridUnitType.Star), new(140), new(140) };
        var money = new[] { false, false, true, true };
        _preview.Items.Add(Flow.Header(["Date", "Particulars", "Credit", "Debit"], widths, money));
        var errors = 0;
        foreach (var row in parsed.EnumerateArray())
        {
            var error = Kit.Str(row, "error");
            if (error.Length > 0) errors++;
            var particulars = Kit.Str(row, "narration");
            if (error.Length > 0)
                particulars = particulars.Length == 0 ? error : $"{particulars} — {error}";
            _preview.Items.Add(Flow.Columns([Kit.Str(row, "txn_date"), particulars, Paise(row, "credit_paise"), Paise(row, "debit_paise")], widths, money));
        }
        _previewClean = errors == 0 && parsed.GetArrayLength() > 0;
        _notice.Severity = _previewClean ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        _notice.Message = _previewClean ? $"{parsed.GetArrayLength()} rows are ready to import." : $"{errors} rows need attention. Nothing has been imported.";
        _notice.IsOpen = true;
    }

    private void CommitImport()
    {
        if (!_previewClean || _csv == null || _csvName == null)
        {
            _shell.Problem("Preview the statement and resolve row errors before importing.");
            return;
        }
        if (_importAccount.SelectedItem is not ComboBoxItem item || item.Tag is not long accountId || accountId == 0)
        {
            _shell.Problem("Select the bank account for this statement.");
            return;
        }
        var text = _csv;
        var filename = _csvName;
        var mapping = Mapping();
        _ = _shell.Run("Importing statement…", () => _shell.Call("bank_import_commit", new { account_id = accountId, filename, text, mapping }))
            .ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (!task.Result) return;
                _csv = null;
                _previewClean = false;
                _notice.Severity = InfoBarSeverity.Success;
                _notice.Message = "The statement was imported.";
                _notice.IsOpen = true;
                _preview.Items.Clear();
                LoadSummary();
            }));
    }

    private void LoadSummary() => _ = _shell.Run("Loading reconciliation…", () =>
    {
        var summary = _shell.Call("bank_summary", new { account_id = (long?)null });
        var txns = _shell.Call("bank_txn_list", new { status = (string?)null, account_id = (long?)null });
        _shell.Window.DispatcherQueue.TryEnqueue(() =>
        {
            _summary.Text = $"{Kit.Str(summary, "unmatched")} unmatched · {Kit.Str(summary, "partial")} partly matched · {Kit.Str(summary, "matched")} matched · {Kit.Str(summary, "rejected_rows")} rejected rows";
            _transactions.Items.Clear();
            var widths = new GridLength[] { new(120), new(2, GridUnitType.Star), new(140), new(120), new(130) };
            var money = new[] { false, false, true, false, false };
            _transactions.Items.Add(Flow.Header(["Date", "Particulars", "Amount", "Status", "Match"], widths, money));
            if (txns.ValueKind != JsonValueKind.Array) return;
            foreach (var row in txns.EnumerateArray())
            {
                var credit = PaiseValue(row, "credit_paise");
                var amount = credit > 0 ? Kit.Str(row, "credit_display") : Kit.Str(row, "debit_display");
                var line = Flow.Columns([Kit.Str(row, "txn_date"), Kit.Str(row, "narration"), amount, Kit.Str(row, "status"), ""], widths, money);
                var status = Kit.Str(row, "status");
                if (_shell.CanPost && Kit.Id(row) is long txnId && credit > 0 && status is "unmatched" or "partial")
                {
                    var unallocated = PaiseValue(row, "unallocated_paise");
                    if (unallocated <= 0)
                        unallocated = credit;
                    var narration = Kit.Str(row, "narration");
                    var button = new Button { Content = "Review match", MinWidth = 108 };
                    button.Click += (_, _) => ReviewMatch(txnId, credit, unallocated, narration);
                    if (line.Children.Count > 0 && line.Children[line.Children.Count - 1] is Border cell)
                        cell.Child = button;
                }
                _transactions.Items.Add(line);
            }
        });
    });

    private void ReviewMatch(long txnId, long credit, long unallocated, string narration)
    {
        _ = _shell.Run("Looking for invoices…", () =>
        {
            var suggestions = _shell.Call("bank_txn_suggest", new { txn_id = txnId });
            _shell.Window.DispatcherQueue.TryEnqueue(() => _ = ConfirmMatch(txnId, credit, unallocated, narration, suggestions));
        });
    }

    private async Task ConfirmMatch(long txnId, long credit, long unallocated, string narration, JsonElement suggestions)
    {
        if (suggestions.ValueKind != JsonValueKind.Array || suggestions.GetArrayLength() == 0)
        {
            _shell.Problem("There is no open tax invoice to match. A bill that will not be collected is written off on the invoice.");
            return;
        }
        var rows = new List<MatchRow>();
        var list = new StackPanel { Spacing = 10 };
        foreach (var row in suggestions.EnumerateArray())
        {
            if (!row.TryGetProperty("invoice_id", out var invoice) || !invoice.TryGetInt64(out var invoiceId))
                continue;
            var outstanding = row.TryGetProperty("balance_paise", out var balanceValue) && balanceValue.TryGetInt64(out var owed) ? owed : 0;
            if (outstanding <= 0)
                continue;
            var match = new MatchRow(invoiceId, outstanding, Kit.Str(row, "tds_amount"), Kit.Str(row, "tds_section"));
            var title = $"{Kit.Str(row, "number")} · {Kit.Str(row, "client_name")} · outstanding {Kit.Str(row, "balance_display")}";
            match.Check.Content = title;
            match.Amount.PlaceholderText = "Amount from this credit";
            match.Amount.IsEnabled = false;
            match.Tds.PlaceholderText = "TDS, if deducted";
            match.Tds.IsEnabled = false;
            var captured = match;
            match.Check.Checked += (_, _) =>
            {
                captured.Amount.IsEnabled = true;
                captured.Tds.IsEnabled = true;
                captured.Section.IsEnabled = true;
                if (captured.Amount.Text.Trim().Length == 0)
                    captured.Amount.Text = Rupees(Math.Min(captured.Outstanding, Room(rows, captured, unallocated)));
                if (rows.Count(item => item.Check.IsChecked == true) == 1 && unallocated == credit && captured.SuggestedTds.Length > 0 && captured.Amount.Text == Rupees(unallocated))
                {
                    captured.Tds.Text = captured.SuggestedTds;
                    SelectTds(captured.Section, captured.SuggestedSection);
                }
            };
            match.Check.Unchecked += (_, _) =>
            {
                captured.Amount.Text = "";
                captured.Amount.IsEnabled = false;
                captured.Tds.Text = "";
                captured.Tds.IsEnabled = false;
                captured.Section.SelectedIndex = 0;
                captured.Section.IsEnabled = false;
            };
            var fields = new Grid { ColumnSpacing = 8 };
            fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            fields.Children.Add(match.Amount);
            Grid.SetColumn(match.Tds, 1);
            fields.Children.Add(match.Tds);
            Grid.SetColumn(match.Section, 2);
            fields.Children.Add(match.Section);
            list.Children.Add(new StackPanel { Spacing = 4, Children = { match.Check, fields } });
            rows.Add(match);
            if (rows.Count == 24)
                break;
        }
        if (rows.Count == 0)
        {
            _shell.Problem("There is no open tax invoice to match. A bill that will not be collected is written off on the invoice.");
            return;
        }
        var note = new TextBlock
        {
            Text = $"Unmatched on this credit: {Rupees(unallocated)} of {Rupees(credit)}. Tick one bill and enter a smaller amount when the bill is paid in instalments. Tick two or more bills when this one credit pays them together. Anything you leave unmatched stays open for the next credit. A bill that will not be collected is written off on the invoice, not here. “{narration}”",
            TextWrapping = TextWrapping.WrapWholeWords,
        };
        var body = new ScrollViewer
        {
            MaxHeight = 420,
            Content = new StackPanel { Spacing = 12, Children = { note, list } },
        };
        if (_transactions.XamlRoot == null)
            return;
        var dialog = new ContentDialog
        {
            Title = "Match this credit to bills",
            Content = body,
            PrimaryButtonText = "Record allocation",
            CloseButtonText = "Leave unmatched",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = _transactions.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        var allocations = new List<object>();
        foreach (var match in rows)
        {
            if (match.Check.IsChecked != true)
                continue;
            var amount = match.Amount.Text.Trim();
            if (amount.Length == 0)
            {
                _shell.Problem("Enter the amount of this credit that pays each selected bill.");
                return;
            }
            var tds = match.Tds.Text.Trim();
            var section = ((ComboBoxItem?)match.Section.SelectedItem)?.Tag as string ?? "";
            if (tds.Length > 0 && tds != "0" && tds != "0.00" && section.Length == 0)
            {
                _shell.Problem("Choose the TDS section for the bill that has TDS.");
                return;
            }
            allocations.Add(new { invoice_id = match.InvoiceId, amount, tds, tds_section = section });
        }
        if (allocations.Count == 0)
        {
            _shell.Problem("Select at least one bill.");
            return;
        }
        _ = _shell.Run("Recording the receipt…", () => _shell.Call("bank_reconcile", new { txn_id = txnId, allocations }))
            .ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (task.Result)
                    LoadSummary();
            }));
    }

    private static long Room(List<MatchRow> rows, MatchRow current, long unallocated)
    {
        long used = 0;
        foreach (var row in rows)
        {
            if (row == current || row.Check.IsChecked != true)
                continue;
            used += PaiseText(row.Amount.Text);
        }
        var room = unallocated - used;
        return room < 0 ? 0 : room;
    }

    private static long PaiseText(string text)
    {
        var parts = text.Trim().Replace(",", "").Split('.');
        if (!long.TryParse(parts[0], out var rupees))
            return 0;
        var paise = 0L;
        if (parts.Length > 1 && parts[1].Length > 0)
        {
            var fraction = parts[1].Length >= 2 ? parts[1][..2] : parts[1].PadRight(2, '0');
            long.TryParse(fraction, out paise);
        }
        return rupees * 100 + paise;
    }

    private static string Rupees(long paise) => $"{paise / 100}.{paise % 100:00}";

    private static void SelectTds(ComboBox box, string section)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            if (item.Tag as string == section)
            {
                box.SelectedItem = item;
                return;
            }
        }
    }

    private sealed class MatchRow
    {
        public MatchRow(long invoiceId, long outstanding, string suggestedTds, string suggestedSection)
        {
            InvoiceId = invoiceId;
            Outstanding = outstanding;
            SuggestedTds = suggestedTds;
            SuggestedSection = suggestedSection;
            foreach (var (label, tag) in new[] { ("No TDS", ""), ("194J", "194J"), ("194C", "194C"), ("194C individual", "194C-1"), ("194C firm", "194C-2"), ("194M", "194M"), ("194-I", "194-I"), ("194H", "194H") })
                Section.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
            Section.SelectedIndex = 0;
            Section.IsEnabled = false;
        }

        public long InvoiceId { get; }
        public long Outstanding { get; }
        public string SuggestedTds { get; }
        public string SuggestedSection { get; }
        public CheckBox Check { get; } = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
        public TextBox Amount { get; } = new();
        public TextBox Tds { get; } = new();
        public ComboBox Section { get; } = new();
    }

    private static long PaiseValue(JsonElement row, string key)
    {
        if (!row.TryGetProperty(key, out var value) || !value.TryGetInt64(out var paise))
            return 0;
        return paise;
    }

    private static string Paise(JsonElement row, string key)
    {
        if (!row.TryGetProperty(key, out var value) || !value.TryGetInt64(out var paise) || paise == 0)
            return "";
        return Flow.Money(paise);
    }

    private object Mapping() => new
    {
        delimiter = ",",
        has_header = _heading,
        date_order = _dateOrder.SelectedItem is ComboBoxItem order && order.Tag is string tag ? tag : "dmy",
        date = SelectedColumn(_mapDate, 0),
        narration = SelectedColumn(_mapParticulars, 1),
        reference = (int?)null,
        debit = SelectedColumn(_mapDebit, 3),
        credit = SelectedColumn(_mapCredit, 2),
        amount = (int?)null,
        balance = (int?)null,
    };

    private static int SelectedColumn(ComboBox box, int fallback) =>
        box.SelectedItem is ComboBoxItem item && item.Tag is int index ? index : fallback;

    private static string Mask(string number)
    {
        var digits = number.Replace(" ", "");
        return digits.Length <= 4 ? digits : "ending " + digits[^4..];
    }

    public void NewItem() => BeginAccount();
    public void Save() => SaveAccount();
    public void Print() { }
    public void FocusSearch() { }
    public async Task<bool> CanLeave() => await Flow.Discard(this, _dirty);
}
