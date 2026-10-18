using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Adms.Desktop;

/// <summary>One input on a record page. Choice fields store the first value of each (value, label) pair.</summary>
internal sealed record RecordField(string Key, string Label, RecordKind Kind = RecordKind.Text, (string Value, string Label)[]? Choices = null, string Default = "");

internal enum RecordKind { Text, Date, Choice, Project, Staff }

/// <summary>
/// A list and an editing form for one kind of plain record: site records, permits and enquiries. Choose Edit on a line to
/// change it; Save adds a new record or updates the one being edited. Nothing here posts to the books.
/// </summary>
internal sealed class RecordPage : UserControl
{
    private readonly AppShell _shell;
    private readonly string _listOp;
    private readonly string _saveOp;
    private readonly RecordField[] _fields;
    private readonly string[] _columns;
    private readonly Func<JsonElement, string[]> _cells;
    private readonly GridLength[] _widths;
    private readonly Dictionary<string, FrameworkElement> _inputs = new();
    private readonly StackPanel _list = new() { Spacing = 0 };
    private readonly InfoBar _notice = new() { IsOpen = false, IsClosable = true };
    private readonly Button _save = new() { Content = "Save", MinHeight = 32, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
    private readonly TextBlock _mode = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Func<RecordPage, JsonElement, Button?>? _extra;
    private long? _editing;

    public RecordPage(AppShell shell, string sheet, string group, string title, string intro, string listOp, string saveOp, RecordField[] fields,
        string[] columns, GridLength[] widths, Func<JsonElement, string[]> cells, Func<RecordPage, JsonElement, Button?>? extra = null)
    {
        _shell = shell;
        _listOp = listOp;
        _saveOp = saveOp;
        _fields = fields;
        _columns = columns;
        _widths = widths;
        _cells = cells;
        _extra = extra;
        var page = new StackPanel { Spacing = 12, Margin = new Thickness(8) };
        page.Children.Add(Flow.SheetMark(sheet, group, title));
        page.Children.Add(Flow.Heading(title));
        page.Children.Add(Flow.Secondary(intro));
        page.Children.Add(_notice);
        if (shell.CanEdit)
        {
            page.Children.Add(_mode);
            var pending = new List<FrameworkElement>();
            foreach (var field in fields)
            {
                var input = Make(field);
                _inputs[field.Key] = input;
                pending.Add(input);
                if (pending.Count == 2)
                {
                    page.Children.Add(Flow.Pair(pending[0], pending[1]));
                    pending.Clear();
                }
            }
            if (pending.Count == 1)
                page.Children.Add(pending[0]);
            var clear = new Button { Content = "New record", MinHeight = 32 };
            clear.Click += (_, _) => Reset();
            _save.Click += (_, _) => Save();
            page.Children.Add(Flow.Bar(_save, clear));
        }
        page.Children.Add(Flow.Section("Records"));
        page.Children.Add(_list);
        Content = new ScrollViewer { Content = page };
        Reset();
        if (fields.Any(f => f.Kind == RecordKind.Project))
            LoadProjects();
        if (fields.Any(f => f.Kind == RecordKind.Staff))
            LoadStaff();
        Load();
    }

    private FrameworkElement Make(RecordField field)
    {
        if (field.Kind is RecordKind.Choice or RecordKind.Project or RecordKind.Staff)
        {
            var box = new ComboBox { Header = field.Label, HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = field.Kind == RecordKind.Project ? "Choose a project" : field.Kind == RecordKind.Staff ? "Choose a person" : "" };
            foreach (var (value, label) in field.Choices ?? [])
                box.Items.Add(new ComboBoxItem { Content = label, Tag = value });
            return box;
        }
        var multiline = field.Key is "details" or "brief" or "notes" or "body";
        return new TextBox
        {
            Header = field.Label,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = field.Kind == RecordKind.Date ? "yyyy-MM-dd" : "",
            AcceptsReturn = multiline,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = multiline ? 72 : 0,
        };
    }

    private void LoadProjects()
    {
        _ = _shell.Run("Loading projects…", () =>
        {
            var rows = _shell.Call("project_list", new { client_id = (long?)null, include_archived = false });
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (rows.ValueKind != JsonValueKind.Array)
                    return;
                foreach (var field in _fields.Where(f => f.Kind == RecordKind.Project))
                {
                    if (!_inputs.TryGetValue(field.Key, out var element) || element is not ComboBox box)
                        continue;
                    box.Items.Clear();
                    foreach (var row in rows.EnumerateArray())
                    {
                        if (Kit.Id(row) is not long id)
                            continue;
                        var client = Kit.Str(row, "client_name");
                        var name = Kit.Str(row, "name");
                        box.Items.Add(new ComboBoxItem { Content = client.Length == 0 ? name : $"{client} — {name}", Tag = id });
                    }
                    if (box.SelectedItem == null && box.Items.Count > 0)
                        box.SelectedIndex = 0;
                }
            });
        });
    }

    private void LoadStaff()
    {
        _ = _shell.Run("Loading the roster…", () =>
        {
            var rows = _shell.Call("staff_list", new { });
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (rows.ValueKind != JsonValueKind.Array)
                    return;
                foreach (var field in _fields.Where(f => f.Kind == RecordKind.Staff))
                {
                    if (!_inputs.TryGetValue(field.Key, out var element) || element is not ComboBox box)
                        continue;
                    box.Items.Clear();
                    foreach (var row in rows.EnumerateArray())
                        if (Kit.Id(row) is long id && Kit.Str(row, "active") is "true" or "True")
                            box.Items.Add(new ComboBoxItem { Content = Kit.Str(row, "name"), Tag = id });
                    if (box.SelectedItem == null && box.Items.Count > 0)
                        box.SelectedIndex = 0;
                }
            });
        });
    }

    public void Reload() => Load();

    public void Notice(string message)
    {
        _notice.Severity = InfoBarSeverity.Success;
        _notice.Message = message;
        _notice.IsOpen = true;
    }

    public AppShell Shell => _shell;

    private void Reset()
    {
        _editing = null;
        _mode.Text = "New record";
        foreach (var field in _fields)
        {
            if (!_inputs.TryGetValue(field.Key, out var element))
                continue;
            var initial = field.Kind == RecordKind.Date && field.Default == "today" ? Kit.Today() : field.Default;
            if (element is TextBox text)
                text.Text = initial;
            else if (element is ComboBox box && field.Kind == RecordKind.Choice)
                box.SelectedIndex = box.Items.Count > 0 ? 0 : -1;
        }
    }

    private void Fill(JsonElement row)
    {
        _editing = Kit.Id(row);
        _mode.Text = "Editing a record. Save to keep the changes, or choose New record to start again.";
        foreach (var field in _fields)
        {
            if (!_inputs.TryGetValue(field.Key, out var element))
                continue;
            var value = Kit.Str(row, field.Key);
            if (element is TextBox text)
                text.Text = value;
            else if (element is ComboBox box)
            {
                foreach (var item in box.Items.OfType<ComboBoxItem>())
                    if (Convert.ToString(item.Tag, System.Globalization.CultureInfo.InvariantCulture) == value)
                        box.SelectedItem = item;
            }
        }
    }

    private void Save()
    {
        var input = new Dictionary<string, object?> { ["id"] = _editing };
        foreach (var field in _fields)
        {
            var element = _inputs[field.Key];
            if (element is ComboBox box)
            {
                if (box.SelectedItem is not ComboBoxItem item)
                {
                    _shell.Problem($"Choose the {field.Label.ToLowerInvariant()}.");
                    return;
                }
                input[field.Key] = item.Tag;
            }
            else if (element is TextBox text)
                input[field.Key] = text.Text.Trim();
        }
        _ = _shell.Run("Saving…", () => _shell.Call(_saveOp, new { input })).ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() =>
        {
            if (!task.Result)
                return;
            Reset();
            Notice("Saved.");
            Load();
        }));
    }

    private void Load()
    {
        _ = _shell.Run("Loading…", () =>
        {
            var rows = _listOp switch
            {
                "site_record_list" => _shell.Call(_listOp, new { project_id = (long?)null, kind = (string?)null }),
                "permit_list" => _shell.Call(_listOp, new { project_id = (long?)null }),
                "library_list" => _shell.Call(_listOp, new { kind = (string?)null, search = (string?)null }),
                _ => _shell.Call(_listOp, new { }),
            };
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                _list.Children.Clear();
                if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() == 0)
                {
                    _list.Children.Add(Flow.Secondary("Nothing recorded yet."));
                    return;
                }
                var widths = _widths.Append(new GridLength(200)).ToArray();
                _list.Children.Add(Flow.Header(_columns.Append("").ToArray(), widths));
                foreach (var source in rows.EnumerateArray())
                {
                    var row = source.Clone();
                    var line = Flow.Columns(_cells(row).Append("").ToArray(), widths);
                    if (_shell.CanEdit && line.Children.Count > 0 && line.Children[line.Children.Count - 1] is Border cell)
                    {
                        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                        var edit = new Button { Content = "Edit", MinHeight = 28 };
                        edit.Click += (_, _) => Fill(row);
                        buttons.Children.Add(edit);
                        if (_extra?.Invoke(this, row) is { } more)
                            buttons.Children.Add(more);
                        cell.Child = buttons;
                    }
                    _list.Children.Add(line);
                }
            });
        });
    }
}

internal static class RecordPages
{
    private static readonly (string, string)[] SiteKinds = [("snag", "Snag"), ("instruction", "Site instruction"), ("inspection", "Inspection")];
    private static readonly (string, string)[] SiteStatus = [("open", "Open"), ("resolved", "Resolved"), ("closed", "Closed")];
    private static readonly (string, string)[] PermitStatus = [("planned", "Planned"), ("applied", "Applied"), ("queried", "Queried"), ("approved", "Approved"), ("rejected", "Rejected")];
    private static readonly (string, string)[] EnquiryStatus = [("new", "New"), ("meeting", "Meeting held"), ("proposal", "Proposal sent"), ("won", "Won"), ("lost", "Lost")];

    private static readonly (string, string)[] LeaveKinds = [("casual", "Casual"), ("sick", "Sick"), ("earned", "Earned"), ("unpaid", "Unpaid")];
    private static readonly (string, string)[] LeaveStatus = [("requested", "Requested"), ("approved", "Approved"), ("rejected", "Rejected")];
    private static readonly (string, string)[] LibraryKinds = [("specification", "Specification"), ("standard", "Standard"), ("compliance", "Compliance"), ("lesson", "Lesson learned")];

    private static string Label((string Value, string Label)[] choices, string value) => choices.FirstOrDefault(c => c.Value == value).Label ?? value;

    public static UIElement Site(AppShell shell) => new RecordPage(
        shell, "01.05", "Practice", "Site records",
        "Snags, site instructions and inspections on a project. A snag stays open, and shows as overdue after its due date, until it is marked resolved.",
        "site_record_list", "site_record_save",
        [
            new("project_id", "Project", RecordKind.Project),
            new("kind", "Type", RecordKind.Choice, SiteKinds),
            new("record_date", "Date", RecordKind.Date, null, "today"),
            new("due_date", "Due by", RecordKind.Date),
            new("title", "Title"),
            new("location", "Location"),
            new("assigned_to", "Responsible"),
            new("status", "Status", RecordKind.Choice, SiteStatus),
            new("details", "Details"),
        ],
        ["Date", "Project", "Type", "Title", "Status"],
        [new(96), new(1.2, GridUnitType.Star), new(1, GridUnitType.Star), new(1.6, GridUnitType.Star), new(1, GridUnitType.Star)],
        r => [Kit.Str(r, "record_date"), Kit.Str(r, "project"), Label(SiteKinds, Kit.Str(r, "kind")), Kit.Str(r, "title"),
              Kit.Str(r, "overdue") is "true" or "True" ? "Overdue" : Label(SiteStatus, Kit.Str(r, "status"))]);

    public static UIElement Permits(AppShell shell) => new RecordPage(
        shell, "01.06", "Practice", "Permits and approvals",
        "Statutory approvals for each project: the authority, where the application stands, and when an approval lapses.",
        "permit_list", "permit_save",
        [
            new("project_id", "Project", RecordKind.Project),
            new("authority", "Authority"),
            new("kind", "Permit or approval"),
            new("reference", "Reference"),
            new("status", "Status", RecordKind.Choice, PermitStatus),
            new("applied_date", "Applied on", RecordKind.Date),
            new("decision_date", "Decision on", RecordKind.Date),
            new("valid_until", "Valid until", RecordKind.Date),
            new("notes", "Notes"),
        ],
        ["Project", "Authority", "Approval", "Status", "Valid until"],
        [new(1.2, GridUnitType.Star), new(1.2, GridUnitType.Star), new(1.4, GridUnitType.Star), new(100), new(130)],
        r => [Kit.Str(r, "project"), Kit.Str(r, "authority"), Kit.Str(r, "kind"), Label(PermitStatus, Kit.Str(r, "status")),
              Kit.Str(r, "watch") switch { "lapsed" => Kit.Str(r, "valid_until") + " (lapsed)", "lapsing" => Kit.Str(r, "valid_until") + " (soon)", _ => Kit.Str(r, "valid_until") }]);

    public static UIElement Staff(AppShell shell) => new RecordPage(
        shell, "06.03", "Office", "Staff",
        "The office roster. Leave days taken this financial year (April to March) are counted from approved leave. Enter a leaving date when someone leaves; they stay on the roster for past records.",
        "staff_list", "staff_save",
        [
            new("name", "Name"),
            new("role", "Role"),
            new("specialisation", "Specialisation"),
            new("phone", "Phone"),
            new("email", "Email"),
            new("joined_date", "Joined", RecordKind.Date),
            new("left_date", "Left", RecordKind.Date),
        ],
        ["Name", "Role", "Joined", "Leave this year"],
        [new(1.6, GridUnitType.Star), new(1.2, GridUnitType.Star), new(110), new(110)],
        r => [Kit.Str(r, "name") + (Kit.Str(r, "active") is "true" or "True" ? "" : " (left)"), Kit.Str(r, "role"), Kit.Str(r, "joined_date"), Kit.Str(r, "leave_days_this_year") + " days"]);

    public static UIElement Leave(AppShell shell) => new RecordPage(
        shell, "06.04", "Office", "Leave",
        "Leave requests and approvals. Days are counted from the first day to the last day, both included. A person cannot have two leaves on the same day.",
        "leave_list", "leave_save",
        [
            new("staff_id", "Person", RecordKind.Staff),
            new("kind", "Kind of leave", RecordKind.Choice, LeaveKinds),
            new("from_date", "First day", RecordKind.Date, null, "today"),
            new("to_date", "Last day", RecordKind.Date, null, "today"),
            new("status", "Status", RecordKind.Choice, LeaveStatus),
            new("reason", "Reason"),
        ],
        ["Person", "Kind", "From", "To", "Days", "Status"],
        [new(1.4, GridUnitType.Star), new(90), new(96), new(96), new(50), new(100)],
        r => [Kit.Str(r, "staff"), Label(LeaveKinds, Kit.Str(r, "kind")), Kit.Str(r, "from_date"), Kit.Str(r, "to_date"), Kit.Str(r, "days"), Label(LeaveStatus, Kit.Str(r, "status"))]);

    public static UIElement Library(AppShell shell) => new RecordPage(
        shell, "06.05", "Office", "Library",
        "The office's own reference: specifications, design standards, compliance notes and lessons learned from projects.",
        "library_list", "library_save",
        [
            new("kind", "Type", RecordKind.Choice, LibraryKinds),
            new("title", "Title"),
            new("category", "Category"),
            new("reference", "Reference"),
            new("body", "Text"),
        ],
        ["Type", "Title", "Category", "Reference"],
        [new(120), new(2, GridUnitType.Star), new(1, GridUnitType.Star), new(1, GridUnitType.Star)],
        r => [Label(LibraryKinds, Kit.Str(r, "kind")), Kit.Str(r, "title"), Kit.Str(r, "category"), Kit.Str(r, "reference")]);

    public static UIElement Enquiries(AppShell shell) => new RecordPage(
        shell, "01.07", "Practice", "Enquiries",
        "People who have asked about work, with the next follow-up date. When an enquiry is won, Make client adds them to Clients.",
        "enquiry_list", "enquiry_save",
        [
            new("received_date", "Received", RecordKind.Date, null, "today"),
            new("name", "Name"),
            new("contact", "Phone or email"),
            new("source", "Heard about us through"),
            new("status", "Status", RecordKind.Choice, EnquiryStatus),
            new("follow_up", "Follow up on", RecordKind.Date),
            new("brief", "What they want"),
            new("notes", "Notes"),
        ],
        ["Received", "Name", "Status", "Follow up"],
        [new(96), new(1.6, GridUnitType.Star), new(1.2, GridUnitType.Star), new(130)],
        r => [Kit.Str(r, "received_date"), Kit.Str(r, "name"), Label(EnquiryStatus, Kit.Str(r, "status")),
              Kit.Str(r, "follow_up_due") is "true" or "True" ? Kit.Str(r, "follow_up") + " (due)" : Kit.Str(r, "follow_up")],
        (page, r) =>
        {
            if (Kit.Str(r, "client_id").Length > 0 || Kit.Id(r) is not long id)
                return null;
            var make = new Button { Content = "Make client", MinHeight = 28 };
            make.Click += (_, _) => _ = page.Shell.Run("Adding the client…", () => page.Shell.Call("enquiry_convert", new { id }))
                .ContinueWith(task => page.Shell.Window.DispatcherQueue.TryEnqueue(() =>
                {
                    if (!task.Result)
                        return;
                    page.Notice("Added to Clients.");
                    page.Reload();
                }));
            return make;
        });
}
