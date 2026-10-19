using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Adms.Desktop;

/// <summary>One input on a record page. Choice fields store the first value of each (value, label) pair.</summary>
internal sealed record RecordField(string Key, string Label, RecordKind Kind = RecordKind.Text, (string Value, string Label)[]? Choices = null, string Default = "");

/// <summary>Money is typed in rupees and sent as paise, Percent is typed as a percentage and sent as basis points, Quantity is typed as a number and sent in thousandths.</summary>
internal enum RecordKind { Text, Date, Choice, Project, Staff, Client, OptionalProject, OptionalClient, Money, Percent, Quantity, YesNo }

/// <summary>
/// A list and an editing form for one kind of plain record: site records, permits and enquiries. Choose Edit on a line to
/// change it; Save adds a new record or updates the one being edited. Nothing here posts to the books.
/// </summary>
internal sealed class RecordPage : UserControl, ILiveRefresh
{
    /// <summary>Another app may have added to the list; reload it unless the person is typing in the form.</summary>
    public bool CanRefreshLive => !_inputs.Values.Any(i => i is Control c && c.FocusState != FocusState.Unfocused);

    public void RefreshLive() => Load();

    private readonly AppShell _shell;
    private readonly string _listOp;
    private readonly string _saveOp;
    private readonly RecordField[] _fields;
    private readonly string[] _columns;
    private readonly Func<JsonElement, string[]> _cells;
    private readonly GridLength[] _widths;
    private readonly Dictionary<string, FrameworkElement> _inputs = new();
    private readonly StackPanel _list = new() { Spacing = 0 };
    private readonly InfoBar _notice = Flow.Notice();
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
        if (fields.Any(f => f.Kind is RecordKind.Project or RecordKind.OptionalProject))
            LoadProjects();
        if (fields.Any(f => f.Kind is RecordKind.Client or RecordKind.OptionalClient))
            LoadClients();
        if (fields.Any(f => f.Kind == RecordKind.Staff))
            LoadStaff();
        Load();
    }

    private FrameworkElement Make(RecordField field)
    {
        if (field.Kind is RecordKind.Choice or RecordKind.Project or RecordKind.Staff or RecordKind.Client or RecordKind.OptionalProject or RecordKind.OptionalClient or RecordKind.YesNo)
        {
            var box = new ComboBox
            {
                Header = field.Label,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                PlaceholderText = field.Kind is RecordKind.Project or RecordKind.OptionalProject ? "Choose a project" : field.Kind == RecordKind.Staff ? "Choose a person" : field.Kind is RecordKind.Client or RecordKind.OptionalClient ? "Choose a client" : "",
            };
            foreach (var (value, label) in field.Kind == RecordKind.YesNo ? [("false", "No"), ("true", "Yes")] : field.Choices ?? [])
                box.Items.Add(new ComboBoxItem { Content = label, Tag = value });
            return box;
        }
        var multiline = field.Key is "details" or "brief" or "notes" or "body" or "decisions";
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
                foreach (var field in _fields.Where(f => f.Kind is RecordKind.Project or RecordKind.OptionalProject))
                {
                    if (!_inputs.TryGetValue(field.Key, out var element) || element is not ComboBox box)
                        continue;
                    box.Items.Clear();
                    if (field.Kind == RecordKind.OptionalProject)
                        box.Items.Add(new ComboBoxItem { Content = "(No project)", Tag = null });
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

    private void LoadClients()
    {
        _ = _shell.Run("Loading clients…", () =>
        {
            var rows = _shell.Call("client_list", new { include_archived = false });
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (rows.ValueKind != JsonValueKind.Array)
                    return;
                foreach (var field in _fields.Where(f => f.Kind is RecordKind.Client or RecordKind.OptionalClient))
                {
                    if (!_inputs.TryGetValue(field.Key, out var element) || element is not ComboBox box)
                        continue;
                    box.Items.Clear();
                    if (field.Kind == RecordKind.OptionalClient)
                        box.Items.Add(new ComboBoxItem { Content = "(No client)", Tag = null });
                    foreach (var row in rows.EnumerateArray())
                        if (Kit.Id(row) is long id)
                            box.Items.Add(new ComboBoxItem { Content = Kit.Str(row, "name"), Tag = id });
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
            else if (element is ComboBox box && field.Kind is RecordKind.Choice or RecordKind.YesNo or RecordKind.OptionalProject or RecordKind.OptionalClient)
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
                text.Text = field.Kind switch
                {
                    RecordKind.Money => value.Length == 0 ? "" : (long.Parse(value, System.Globalization.CultureInfo.InvariantCulture) / 100m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                    RecordKind.Percent => value.Length == 0 ? "" : (long.Parse(value, System.Globalization.CultureInfo.InvariantCulture) / 100m).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                    RecordKind.Quantity => value.Length == 0 ? "" : (long.Parse(value, System.Globalization.CultureInfo.InvariantCulture) / 1000m).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    _ => value,
                };
            else if (element is ComboBox box)
            {
                box.SelectedItem = null;
                foreach (var item in box.Items.OfType<ComboBoxItem>())
                    if (string.Equals(Convert.ToString(item.Tag, System.Globalization.CultureInfo.InvariantCulture), value, StringComparison.OrdinalIgnoreCase))
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
                    Flow.Fault(box, _notice, $"Choose the {field.Label.ToLowerInvariant()}.");
                    return;
                }
                input[field.Key] = field.Kind == RecordKind.YesNo ? item.Tag as string == "true" : item.Tag;
            }
            else if (element is TextBox text)
            {
                var typed = text.Text.Trim();
                if (field.Kind is RecordKind.Money or RecordKind.Percent or RecordKind.Quantity)
                {
                    var scale = field.Kind == RecordKind.Quantity ? 1000m : 100m;
                    if (typed.Length == 0)
                        input[field.Key] = 0L;
                    else if (decimal.TryParse(typed.Replace(",", ""), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var number) && number >= 0 && number < 1_000_000_000_000m)
                        input[field.Key] = (long)Math.Round(number * scale, MidpointRounding.AwayFromZero);
                    else
                    {
                        Flow.Fault(text, _notice, $"Enter {field.Label.ToLowerInvariant()} as a number that is not negative.");
                        return;
                    }
                }
                else
                    input[field.Key] = typed;
            }
        }
        _ = _shell.Run("Saving…", () => _shell.Call(_saveOp, new { input })).ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() =>
        {
            if (!task.Result)
            {
                // The engine's message usually names the field it is about; mark that one.
                var problem = _shell.LastProblem;
                var about = _fields.FirstOrDefault(f => problem.Contains(f.Label, StringComparison.OrdinalIgnoreCase));
                if (about != null && _inputs[about.Key] is Control control)
                    Flow.Mark(control, problem);
                return;
            }
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

    private static readonly (string, string)[] ProposalBasis = [("percent", "A percentage of the cost of the work"), ("per_area", "A rate per unit of area"), ("lump_sum", "A lump sum")];
    private static readonly (string, string)[] ProposalStatus = [("draft", "Draft"), ("sent", "Sent"), ("accepted", "Accepted"), ("declined", "Declined")];

    public static UIElement Proposals(AppShell shell) => new RecordPage(
        shell, "01.08", "Practice", "Fee proposals",
        "What the practice proposes to charge a client. Choose how the fee is worked out and fill in only the figures for that basis. A proposal is drafted, sent, then accepted or declined; once the client has decided it cannot change.",
        "proposal_list", "proposal_save",
        [
            new("client_id", "Client", RecordKind.Client),
            new("project_id", "Project", RecordKind.OptionalProject),
            new("title", "Title"),
            new("basis", "Fee is", RecordKind.Choice, ProposalBasis),
            new("cost_paise", "Estimated cost of the work (₹)", RecordKind.Money),
            new("rate_bp", "Fee percentage", RecordKind.Percent),
            new("area_milli", "Built-up area", RecordKind.Quantity),
            new("area_rate_paise", "Rate per unit of area (₹)", RecordKind.Money),
            new("lump_paise", "Lump-sum fee (₹)", RecordKind.Money),
            new("gst_bp", "GST (%)", RecordKind.Percent, null, "18"),
            new("issued_date", "Issued on", RecordKind.Date, null, "today"),
            new("valid_until", "Valid until", RecordKind.Date),
            new("notes", "Notes"),
        ],
        ["Number", "Client", "Title", "Fee with GST (₹)", "Status"],
        [new(80), new(1.2, GridUnitType.Star), new(1.6, GridUnitType.Star), new(130), new(110)],
        r => [Kit.Str(r, "number"), Kit.Str(r, "client"), Kit.Str(r, "title"), Flow.Money(long.TryParse(Kit.Str(r, "total_paise"), out var total) ? total : 0).Replace("₹", "").Trim(),
              Kit.Str(r, "lapsed") is "true" or "True" ? "Sent (lapsed)" : Label(ProposalStatus, Kit.Str(r, "status"))],
        (page, r) =>
        {
            var status = Kit.Str(r, "status");
            if (Kit.Id(r) is not long id || status is "accepted" or "declined")
                return null;
            var next = new Button { Content = status == "draft" ? "Mark sent" : "Client decided…", MinHeight = 28 };
            void Move(string to) => _ = page.Shell.Run("Updating the proposal…", () => page.Shell.Call("proposal_set_status", new { id, status = to }))
                .ContinueWith(task => page.Shell.Window.DispatcherQueue.TryEnqueue(() => { if (task.Result) page.Reload(); }));
            if (status == "draft")
                next.Click += (_, _) => Move("sent");
            else
            {
                var menu = new MenuFlyout();
                var accepted = new MenuFlyoutItem { Text = "Accepted" };
                accepted.Click += (_, _) => Move("accepted");
                var declined = new MenuFlyoutItem { Text = "Declined" };
                declined.Click += (_, _) => Move("declined");
                menu.Items.Add(accepted);
                menu.Items.Add(declined);
                next.Flyout = menu;
            }
            return next;
        });

    public static UIElement Meetings(AppShell shell) => new RecordPage(
        shell, "01.09", "Practice", "Meetings and decisions",
        "A record of what was discussed and decided with a client, and what has to be followed up. A follow-up shows as due once its date has come and until it is marked done.",
        "meeting_list", "meeting_save",
        [
            new("meeting_date", "Date", RecordKind.Date, null, "today"),
            new("title", "Title"),
            new("client_id", "Client", RecordKind.OptionalClient),
            new("project_id", "Project", RecordKind.OptionalProject),
            new("attendees", "Who was there"),
            new("follow_up_date", "Follow up on", RecordKind.Date),
            new("follow_up_done", "Follow-up done", RecordKind.YesNo),
            new("notes", "Discussion"),
            new("decisions", "Decisions"),
        ],
        ["Date", "Title", "Client", "Follow up"],
        [new(96), new(1.8, GridUnitType.Star), new(1.2, GridUnitType.Star), new(130)],
        r => [Kit.Str(r, "meeting_date"), Kit.Str(r, "title"), Kit.Str(r, "client"),
              Kit.Str(r, "follow_up_due") is "true" or "True" ? Kit.Str(r, "follow_up_date") + " (due)" : Kit.Str(r, "follow_up_done") is "true" or "True" ? "Done" : Kit.Str(r, "follow_up_date")]);

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
