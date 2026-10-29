using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Adms.Desktop;

/// <summary>
/// The attendance register. Choose a day and mark each person on the roster, then see the month: days present, half days, absent, on
/// leave and holidays. A person is on the register only for the days between joining and leaving.
/// </summary>
internal sealed class AttendancePage : UserControl
{
    private static readonly (string Value, string Label)[] States = [("", "Not marked"), ("present", "Present"), ("half", "Half day"), ("absent", "Absent"), ("leave", "On leave"), ("holiday", "Holiday")];

    private readonly AppShell _shell;
    private readonly TextBox _day = Desk.Typed("Day", Kit.Today());
    private readonly TextBox _month = Desk.Typed("Month", Kit.Today()[..7]);
    private readonly StackPanel _register = new() { Spacing = 4 };
    private readonly StackPanel _summary = new();
    private readonly InfoBar _notice = Flow.Notice();
    private readonly List<(long Staff, ComboBox State, TextBox Note, string Was)> _rows = [];

    public AttendancePage(AppShell shell)
    {
        _shell = shell;
        var page = new StackPanel { Spacing = 12, Margin = (Thickness)Application.Current.Resources["PageMargin"] };
        page.Children.Add(Flow.SheetMark("06.06", "Office", "Attendance"));
        page.Children.Add(Flow.Heading("Attendance"));
        page.Children.Add(Flow.Secondary("Mark who was in on a day. A day that has not come cannot be marked. Marking a day again replaces the earlier mark."));
        page.Children.Add(_notice);
        page.Children.Add(Flow.Pair(_day, new Border()));
        var save = Flow.Command("Save the day", SaveDay, primary: true);
        save.Visibility = shell.CanEdit ? Visibility.Visible : Visibility.Collapsed;
        page.Children.Add(Flow.Bar(Flow.Command("Show the day", LoadDay), save));
        page.Children.Add(_register);
        page.Children.Add(Flow.Section("The month"));
        page.Children.Add(Flow.Pair(_month, new Border()));
        page.Children.Add(Flow.Bar(Flow.Command("Show the month", LoadMonth)));
        page.Children.Add(_summary);
        Content = new ScrollViewer { Content = page };
        LoadDay();
        LoadMonth();
    }

    private void LoadDay()
    {
        var day = _day.Text.Trim();
        _ = _shell.Run("Loading the register…", () =>
        {
            var rows = _shell.Call("attendance_day", new { day });
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                _register.Children.Clear();
                _rows.Clear();
                if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() == 0)
                {
                    _register.Children.Add(Flow.Secondary("No one is on the roster for this day. Add people under Staff."));
                    return;
                }
                foreach (var row in rows.EnumerateArray())
                {
                    if (Desk.Id(row, "staff_id") is not long staff)
                        continue;
                    var state = new ComboBox { MinWidth = 150, IsEnabled = _shell.CanEdit };
                    foreach (var (value, label) in States)
                        state.Items.Add(new ComboBoxItem { Content = label, Tag = value });
                    var was = Kit.Str(row, "status");
                    state.SelectedIndex = Math.Max(0, Array.FindIndex(States, s => s.Value == was));
                    var note = new TextBox { PlaceholderText = "Note", Text = Kit.Str(row, "note"), IsEnabled = _shell.CanEdit, MinWidth = 220 };
                    var line = new Grid { ColumnSpacing = 12 };
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
                    var name = new TextBlock { Text = Kit.Str(row, "name"), VerticalAlignment = VerticalAlignment.Center, Style = Flow.Look("BodyStyle") };
                    line.Children.Add(name);
                    Grid.SetColumn(state, 1);
                    line.Children.Add(state);
                    Grid.SetColumn(note, 2);
                    line.Children.Add(note);
                    _register.Children.Add(line);
                    _rows.Add((staff, state, note, was));
                }
            });
        });
    }

    private void SaveDay()
    {
        var day = _day.Text.Trim();
        var marks = _rows
            .Select(r => (r.Staff, State: (r.State.SelectedItem as ComboBoxItem)?.Tag as string ?? "", Note: r.Note.Text.Trim()))
            .Where(m => m.State.Length > 0)
            .ToList();
        if (marks.Count == 0)
        {
            _shell.Problem("Choose a mark for at least one person.");
            return;
        }
        _ = _shell.Run("Saving the day…", () =>
        {
            foreach (var m in marks)
                _shell.Call("attendance_mark", new { staff_id = m.Staff, day, status = m.State, note = m.Note });
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                _notice.Severity = InfoBarSeverity.Success;
                _notice.Message = $"Attendance for {day} is saved.";
                _notice.IsOpen = true;
                LoadMonth();
            });
        });
    }

    private void LoadMonth()
    {
        var month = _month.Text.Trim();
        _ = _shell.Run("Loading the month…", () =>
        {
            var rows = _shell.Call("attendance_month", new { month });
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                _summary.Children.Clear();
                if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() == 0)
                {
                    _summary.Children.Add(Flow.Secondary("No one is on the roster for this month."));
                    return;
                }
                var widths = new GridLength[] { new(2, GridUnitType.Star), new(80), new(80), new(80), new(80), new(80), new(110) };
                bool[] right = [false, true, true, true, true, true, true];
                _summary.Children.Add(Flow.Header(["Name", "Present", "Half days", "Absent", "On leave", "Holidays", "Days worked"], widths, right));
                foreach (var row in rows.EnumerateArray())
                {
                    var halves = long.TryParse(Kit.Str(row, "worked_half_days"), out var h) ? h : 0;
                    var worked = halves % 2 == 0 ? (halves / 2).ToString() : $"{halves / 2}.5";
                    _summary.Children.Add(Flow.Columns([Kit.Str(row, "name"), Kit.Str(row, "present"), Kit.Str(row, "half"), Kit.Str(row, "absent"), Kit.Str(row, "leave"), Kit.Str(row, "holiday"), worked], widths, right));
                }
            });
        });
    }
}

/// <summary>
/// Checks the TDS the office booked on receipts against what clients' deductors reported in Form 26AS. Import the statement as a CSV,
/// then reconcile a period. It reads records and changes none of the books.
/// </summary>
internal sealed class TdsReconPage : UserControl
{
    private readonly AppShell _shell;
    private readonly TextBox _from = Desk.Typed("From", Kit.YearStart());
    private readonly TextBox _to = Desk.Typed("To", Kit.Today());
    private readonly StackPanel _result = new() { Spacing = 8 };
    private readonly InfoBar _notice = Flow.Notice();

    public TdsReconPage(AppShell shell)
    {
        _shell = shell;
        var page = new StackPanel { Spacing = 12, Margin = (Thickness)Application.Current.Resources["PageMargin"] };
        page.Children.Add(Flow.SheetMark("05.04", "Reports and tax", "26AS check"));
        page.Children.Add(Flow.Heading("26AS check"));
        page.Children.Add(Flow.Secondary("Import the statement you saved from TRACES as a CSV. It needs columns for the deductor's name and TAN, the date, the amount paid and the tax deducted. Entries already imported are skipped, so importing again is safe. A deduction is matched when the client's TAN and the tax amount agree and the dates are within 120 days."));
        page.Children.Add(_notice);
        var import = Flow.Command("Import a 26AS CSV…", Import);
        import.Visibility = shell.CanFinance ? Visibility.Visible : Visibility.Collapsed;
        page.Children.Add(Flow.Bar(import));
        page.Children.Add(Flow.Section("Check a period"));
        page.Children.Add(Flow.Pair(_from, _to));
        page.Children.Add(Flow.Bar(Flow.Command("Check", Reconcile, primary: true)));
        page.Children.Add(_result);
        Content = new ScrollViewer { Content = page };
    }

    private void Say(InfoBarSeverity severity, string message)
    {
        _notice.Severity = severity;
        _notice.Message = message;
        _notice.IsOpen = true;
    }

    private async void Import()
    {
        var path = await FileDialogs.PickFile(_shell.Window.Handle, ".csv");
        if (path == null)
            return;
        string csv;
        try
        {
            csv = await File.ReadAllTextAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _shell.Problem("The file could not be read. Close it if it is open in another program.");
            return;
        }
        _ = _shell.Run("Importing the statement…", () =>
        {
            var result = _shell.Call("tds26as_import", new { csv });
            _shell.Window.DispatcherQueue.TryEnqueue(() => Say(InfoBarSeverity.Success, $"{Kit.Str(result, "added")} entries added, {Kit.Str(result, "already_there")} already there."));
        });
    }

    private static long Paise(JsonElement row, string key) => row.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    private void Reconcile()
    {
        var from = _from.Text.Trim();
        var to = _to.Text.Trim();
        _ = _shell.Run("Checking…", () =>
        {
            var report = _shell.Call("tds26as_reconcile", new { from, to });
            _shell.Window.DispatcherQueue.TryEnqueue(() => Paint(report));
        });
    }

    private void Paint(JsonElement report)
    {
        _result.Children.Clear();
        _result.Children.Add(Desk.Body($"Matched: {Flow.Money(Paise(report, "matched_paise"))}   ·   Reported but not booked: {Flow.Money(Paise(report, "only_reported_paise"))}   ·   Booked but not reported: {Flow.Money(Paise(report, "only_booked_paise"))}"));
        var matched = Desk.Items(report, "matched");
        var reported = Desk.Items(report, "only_reported");
        var booked = Desk.Items(report, "only_booked");
        Section("Matched", matched, ["Client", "Receipt", "Receipt date", "26AS date", "TDS (₹)"], [new(2, GridUnitType.Star), new(90), new(100), new(100), new(110)],
            r => [Kit.Str(r, "client"), Kit.Str(r, "receipt_number"), Kit.Str(r, "receipt_date"), Kit.Str(r, "entry_date"), Rupees(r)], "Nothing matched in this period.");
        Section("Reported by the deductor, not booked", reported, ["Deductor", "TAN", "Section", "26AS date", "TDS (₹)"], [new(2, GridUnitType.Star), new(110), new(70), new(100), new(110)],
            r => [Kit.Str(r, "deductor"), Kit.Str(r, "tan"), Kit.Str(r, "section"), Kit.Str(r, "entry_date"), Rupees(r)], "Every reported deduction is booked.");
        Section("Booked, not found in 26AS", booked, ["Client", "Receipt", "Receipt date", "Why", "TDS (₹)"], [new(2, GridUnitType.Star), new(90), new(100), new(2, GridUnitType.Star), new(110)],
            r => [Kit.Str(r, "client"), Kit.Str(r, "receipt_number"), Kit.Str(r, "receipt_date"), Kit.Str(r, "why"), Rupees(r)], "Every booked deduction is reported.");
    }

    private static string Rupees(JsonElement row) => Flow.Money(Paise(row, "tds_paise")).Replace("₹", "").Trim();

    private void Section(string title, List<JsonElement> rows, string[] headers, GridLength[] widths, Func<JsonElement, string[]> cells, string empty)
    {
        _result.Children.Add(Flow.Section(title));
        if (rows.Count == 0)
        {
            _result.Children.Add(Flow.Secondary(empty));
            return;
        }
        bool[] right = [false, false, false, false, true];
        _result.Children.Add(Flow.Header(headers, widths, right));
        foreach (var row in rows)
            _result.Children.Add(Flow.Columns(cells(row), widths, right));
    }
}

/// <summary>Small readers for the engine's JSON, shared by the pages in this file.</summary>
internal static class Desk
{
    public static TextBox Typed(string header, string text) { var box = Flow.Input(header); box.Text = text; return box; }

    public static long? Id(JsonElement row, string key) => row.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;

    public static List<JsonElement> Items(JsonElement parent, string key) =>
        parent.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Select(r => r.Clone()).ToList() : [];

    public static TextBlock Body(string text) => new() { Text = text, Style = Flow.Look("BodyStyle"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
}
