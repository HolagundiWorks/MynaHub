using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Adms.Desktop;

internal sealed class Book : UserControl, IPageCommands
{
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly TextBox _search = new() { Header = "Search", PlaceholderText = "Search this list", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _summary = Flow.Secondary("");
    private readonly ContentControl _tableHead = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0) };
    private readonly Grid _split = new();
    private readonly Grid _listPane = new() { RowSpacing = 12 };
    private readonly ScrollViewer _formScroll;
    private readonly ScrollViewer _pageScroll = new();
    private readonly List<JsonElement> _rows = [];
    private string[] _headers = [];
    private string[] _keys = [];
    private bool[] _money = [];
    private bool _dirty;
    private bool _filling = true;
    private bool? _stacked;
    private AppShell? _loadedBy;
    private string _loadStatus = "";
    private string _loadOperation = "";
    private object? _loadArgs;

    public StackPanel Form { get; } = new() { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
    public StackPanel Detail { get; } = new() { Spacing = 8 };
    public Action? SaveAction { get; set; }
    public Action<JsonElement>? Chosen { get; set; }

    public Book(string sheet, string section, string title, string result)
    {
        Flow.UseTable(_list);
        Flow.Surface(_tableHead);
        AutomationProperties.SetName(_list, title);
        AutomationProperties.SetName(_search, $"Search {title}");
        _search.TextChanged += (_, _) => Paint();
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is ListViewItem item && item.Tag is JsonElement row)
                Chosen?.Invoke(row);
        };
        _listPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _listPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _listPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _listPane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var heading = new StackPanel { Spacing = 4 };
        heading.Children.Add(Flow.SheetMark(sheet, section, title));
        heading.Children.Add(Flow.Heading(title));
        heading.Children.Add(Flow.Secondary(result));
        _listPane.Children.Add(heading);
        var filters = new Grid { ColumnSpacing = 8 };
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filters.Children.Add(_search);
        var refresh = new Button { Content = "Refresh", VerticalAlignment = VerticalAlignment.Bottom };
        AutomationProperties.SetName(refresh, $"Refresh {title}");
        refresh.Click += (_, _) => Refresh();
        Grid.SetColumn(refresh, 1);
        filters.Children.Add(refresh);
        Grid.SetRow(filters, 1);
        _listPane.Children.Add(filters);
        Grid.SetRow(_summary, 2);
        _listPane.Children.Add(_summary);
        var table = new Grid();
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        table.Children.Add(_tableHead);
        Grid.SetRow(_list, 1);
        table.Children.Add(_list);
        Grid.SetRow(table, 3);
        _listPane.Children.Add(table);
        Form.Children.Add(Detail);
        _split.Margin = (Thickness)Application.Current.Resources["PageMargin"];
        _split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star), MinWidth = 280 });
        _split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 280 });
        _split.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _split.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });
        _split.Children.Add(_listPane);
        _formScroll = Flow.Scroll(Form);
        _formScroll.Margin = new Thickness(24, 0, 0, 0);
        Grid.SetColumn(_formScroll, 1);
        _split.Children.Add(_formScroll);
        _pageScroll.HorizontalScrollMode = ScrollMode.Disabled;
        _pageScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Content = _split;
        SizeChanged += (_, _) => Reflow();
        _filling = false;
    }

    private void Reflow()
    {
        var stack = ActualWidth > 0 && ActualWidth < 960;
        if (_stacked == stack)
            return;
        _stacked = stack;
        if (stack)
        {
            _split.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            _split.ColumnDefinitions[0].MinWidth = 0;
            _split.ColumnDefinitions[1].Width = new GridLength(0);
            _split.ColumnDefinitions[1].MinWidth = 0;
            _split.RowDefinitions[0].Height = GridLength.Auto;
            _split.RowDefinitions[1].Height = GridLength.Auto;
            _list.MaxHeight = 420;
            Grid.SetColumn(_listPane, 0);
            Grid.SetRow(_listPane, 0);
            Grid.SetColumn(_formScroll, 0);
            Grid.SetRow(_formScroll, 1);
            _formScroll.Margin = new Thickness(0, 16, 0, 0);
            Content = _pageScroll;
            _pageScroll.Content = _split;
        }
        else
        {
            _split.ColumnDefinitions[0].Width = new GridLength(1.4, GridUnitType.Star);
            _split.ColumnDefinitions[0].MinWidth = 280;
            _split.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            _split.ColumnDefinitions[1].MinWidth = 280;
            _split.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            _split.RowDefinitions[1].Height = new GridLength(0);
            _list.MaxHeight = double.PositiveInfinity;
            Grid.SetColumn(_listPane, 0);
            Grid.SetRow(_listPane, 0);
            Grid.SetColumn(_formScroll, 1);
            Grid.SetRow(_formScroll, 0);
            _formScroll.Margin = new Thickness(24, 0, 0, 0);
            _pageScroll.Content = null;
            Content = _split;
        }
    }

    public void Watch(params Control[] controls)
    {
        foreach (var control in controls)
        {
            if (control is TextBox box)
                box.TextChanged += (_, _) => { if (box.FocusState != FocusState.Unfocused) Touch(); };
            else if (control is ComboBox combo)
                combo.SelectionChanged += (_, _) => { if (combo.FocusState != FocusState.Unfocused) Touch(); };
            else if (control is PasswordBox password)
                password.PasswordChanged += (_, _) => { if (password.FocusState != FocusState.Unfocused) Touch(); };
        }
    }

    public void Touch()
    {
        if (!_filling)
            _dirty = true;
    }

    public void ClearDirty() => _dirty = false;

    public void Show(JsonElement rows, string[] headers, string[] keys, bool[] money)
    {
        _headers = headers;
        _keys = keys;
        _money = money;
        _rows.Clear();
        if (rows.ValueKind == JsonValueKind.Array)
            foreach (var row in rows.EnumerateArray())
                _rows.Add(row.Clone());
        Paint();
    }

    public void Reload(AppShell shell, string status, string operation, object? args, string[] headers, string[] keys, bool[] money)
    {
        _loadedBy = shell;
        _loadStatus = status;
        _loadOperation = operation;
        _loadArgs = args;
        _ = shell.Run(status, () =>
        {
            var rows = shell.Call(operation, args);
            shell.Window.DispatcherQueue.TryEnqueue(() => Show(rows, headers, keys, money));
        });
    }

    public void Refresh()
    {
        if (_loadedBy == null || _loadOperation.Length == 0)
            return;
        Reload(_loadedBy, _loadStatus, _loadOperation, _loadArgs, _headers, _keys, _money);
    }

    private void Paint()
    {
        var needle = _search.Text.Trim().ToLowerInvariant();
        _list.Items.Clear();
        if (_headers.Length > 0)
        {
            var header = Flow.Header(_headers, Widths(_headers.Length, _money), _money);
            AutomationProperties.SetName(header, string.Join(", ", _headers));
            _tableHead.Content = header;
        }
        var count = 0;
        foreach (var row in _rows)
        {
            var cells = new string[_keys.Length];
            for (var i = 0; i < _keys.Length; i++)
                cells[i] = Text(row, _keys[i]);
            if (needle.Length > 0 && !string.Join(' ', cells).ToLowerInvariant().Contains(needle))
                continue;
            var item = new ListViewItem { Content = Flow.Columns(cells, Widths(cells.Length, _money), _money), Tag = row };
            AutomationProperties.SetName(item, string.Join(", ", cells));
            _list.Items.Add(item);
            count++;
        }
        _summary.Text = count == 0 ? "Nothing to show." : count == 1 ? "1 record." : $"{count} records.";
    }

    public void NewItem() => Form.Children.OfType<Control>().FirstOrDefault()?.Focus(FocusState.Programmatic);
    public void Save() => SaveAction?.Invoke();
    public void Print() { }
    public void FocusSearch() => _search.Focus(FocusState.Programmatic);
    public async Task<bool> CanLeave() => await Flow.Discard(this, _dirty);

    public static ComboBox Choices(string header, params (string Label, string Tag)[] items)
    {
        var box = new ComboBox { Header = header, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (label, tag) in items)
            box.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
        if (box.Items.Count > 0)
            box.SelectedIndex = 0;
        return box;
    }

    public static ComboBox Accounts(JsonElement rows, string header, string? kind)
    {
        var box = new ComboBox { Header = header, HorizontalAlignment = HorizontalAlignment.Stretch };
        box.Items.Add(new ComboBoxItem { Content = "Select an account", Tag = 0L });
        if (rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray())
            {
                if (kind != null && Kit.Str(row, "kind") != kind)
                    continue;
                box.Items.Add(new ComboBoxItem { Content = $"{Kit.Str(row, "code")}  {Kit.Str(row, "name")}", Tag = Kit.Id(row) ?? 0 });
            }
        }
        box.SelectedIndex = 0;
        return box;
    }

    public static long SelectedId(ComboBox box) => box.SelectedItem is ComboBoxItem item && item.Tag is long id ? id : 0;

    public static string Selected(ComboBox box, string fallback) => box.SelectedItem is ComboBoxItem item && item.Tag is string tag ? tag : fallback;

    public static ComboBox PaidVia() => Choices("Paid via", ("Bank", "bank"), ("Cash", "cash"), ("Pay later", "payable"));

    private static string Text(JsonElement row, string key)
    {
        if (!row.TryGetProperty(key, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return "";
        var text = value.ValueKind switch
        {
            JsonValueKind.True => "Yes",
            JsonValueKind.False => "No",
            JsonValueKind.String => value.GetString() ?? "",
            _ => value.ToString(),
        };
        if (key != "paid_via")
            return text;
        return text switch
        {
            "payable" => "Pay later",
            "bank" => "Bank",
            "cash" => "Cash",
            _ when text.StartsWith("bank:", StringComparison.Ordinal) => "Bank",
            _ => text,
        };
    }

    private static GridLength[] Widths(int count, bool[] money)
    {
        var widths = new GridLength[count];
        for (var i = 0; i < count; i++)
            widths[i] = money.Length > i && money[i] ? new GridLength(140) : i == 0 ? new GridLength(140) : new GridLength(1, GridUnitType.Star);
        return widths;
    }
}

internal static class Ledgers
{
    public static UIElement Journals(AppShell shell)
    {
        var book = new Book("04.03", "Accounts", "Journals", "Posted journals stay as they were posted. A correction is a reversal.");
        var from = Flow.Input("From");
        from.Text = Kit.YearStart();
        var to = Flow.Input("To");
        to.Text = Kit.Today();
        object Period() => new { from = from.Text.Trim(), to = to.Text.Trim(), limit = 200 };
        var show = new Button { Content = "Show journals", MinHeight = 32 };
        show.Click += (_, _) => book.Reload(shell, "Loading journals…", "journal_list", Period(), JournalHeaders, JournalKeys, JournalMoney);
        book.Form.Children.Add(Flow.Section("Period"));
        book.Form.Children.Add(Flow.Pair(from, to));
        book.Form.Children.Add(show);
        var date = Flow.Input("Date");
        date.Text = Kit.Today();
        var narration = Flow.Input("Narration");
        ComboBox debit = Book.Choices("Debit account", ("Loading accounts…", ""));
        ComboBox credit = Book.Choices("Credit account", ("Loading accounts…", ""));
        var amount = Flow.Input("Amount");
        amount.Text = "0";
        if (shell.CanPost)
        {
            book.Form.Children.Add(Flow.Section("Post a journal"));
            book.Form.Children.Add(date);
            book.Form.Children.Add(narration);
            book.Form.Children.Add(debit);
            book.Form.Children.Add(credit);
            book.Form.Children.Add(amount);
            var post = new Button { Content = "Post journal", MinHeight = 32, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            post.Click += async (_, _) =>
            {
                if (!await Flow.Confirm(book, "Post this journal?", "A posted journal cannot be edited. A correction is a new reversal journal.", "Post"))
                    return;
                var debitId = Book.SelectedId(debit);
                var creditId = Book.SelectedId(credit);
                if (debitId == 0 || creditId == 0) { shell.Problem("Choose the debit and credit accounts."); return; }
                var posted = new
                {
                    date = date.Text.Trim(),
                    narration = narration.Text.Trim(),
                    lines = new[]
                    {
                        new { account_id = debitId, debit = amount.Text.Trim(), credit = "0", memo = "" },
                        new { account_id = creditId, debit = "0", credit = amount.Text.Trim(), memo = "" },
                    },
                };
                _ = shell.Run("Posting journal…", () => shell.Call("journal_post", posted)).ContinueWith(task =>
                    shell.Window.DispatcherQueue.TryEnqueue(() => { if (task.Result) book.Reload(shell, "Loading journals…", "journal_list", Period(), JournalHeaders, JournalKeys, JournalMoney); }));
            };
            book.Form.Children.Add(post);
            book.Watch(date, narration, debit, credit, amount);
        }
        book.Chosen = row =>
        {
            var id = Kit.Id(row);
            if (id is not long journalId) return;
            shell.Run("Loading journal…", () =>
            {
                var lines = shell.Call("journal_lines", new { id = journalId });
                shell.Window.DispatcherQueue.TryEnqueue(() =>
                {
                    book.Detail.Children.Clear();
                    book.Detail.Children.Add(Flow.Section("Journal lines"));
                    book.Detail.Children.Add(Kit.TableOf(lines, "Journal lines", ["Account", "Memo", "Debit", "Credit"], ["account", "memo", "debit_display", "credit_display"], [false, false, true, true]));
                });
            });
        };
        if (shell.CanPost)
            LoadAccounts(shell, rows =>
            {
                Replace(book.Form, debit, Book.Accounts(rows, "Debit account", null));
                Replace(book.Form, credit, Book.Accounts(rows, "Credit account", null));
                debit = Find(book.Form, "Debit account");
                credit = Find(book.Form, "Credit account");
            });
        book.Reload(shell, "Loading journals…", "journal_list", Period(), JournalHeaders, JournalKeys, JournalMoney);
        return book;
    }

    private static readonly string[] JournalHeaders = ["Number", "Date", "Narration", "Total"];
    private static readonly string[] JournalKeys = ["number", "date", "narration", "total_display"];
    private static readonly bool[] JournalMoney = [false, false, false, true];

    public static UIElement Expenses(AppShell shell)
    {
        var page = Entry(shell, "04.04", "Office expenses", "Record an office expense. Select a payable row to pay it, or void an entry that has not been paid.", "expense_list",
        ["Date", "Description", "Paid via", "Settled", "Voided", "Total"], ["date", "description", "paid_via", "settled", "voided", "total_display"], [false, false, false, false, false, true],
        (book, accounts) =>
        {
            var date = Flow.Input("Date");
            date.Text = Kit.Today();
            var description = Flow.Input("Description");
            var bill = Flow.Input("Bill reference");
            var account = Book.Accounts(accounts, "Expense account", "expense");
            var amount = Flow.Input("Amount");
            var gst = Flow.Input("GST");
            gst.Text = "0";
            var tds = Flow.Input("TDS");
            tds.Text = "0";
            var via = Book.PaidVia();
            book.Form.Children.Add(Flow.Section("Record an expense"));
            foreach (var control in new Control[] { date, description, bill, account, amount, gst, tds, via })
                book.Form.Children.Add(control);
            AddPost(shell, book, "Record expense", "Record this expense?", "This posts a journal in the company file.", () =>
            {
                var id = Book.SelectedId(account);
                if (id == 0) { shell.Problem("Choose an expense account."); return null; }
                return new { expense = new { date = date.Text.Trim(), vendor_id = (long?)null, bill_ref = bill.Text.Trim(), description = description.Text.Trim(), expense_account_id = id, amount = amount.Text.Trim(), gst = gst.Text.Trim(), tds = tds.Text.Trim(), paid_via = Book.Selected(via, "bank") } };
            }, "expense_record", "Recording expense…");
            book.Watch(date, description, bill, account, amount, gst, tds, via);
        });
        if (page is Book register)
            register.Chosen = row => ShowExpense(shell, register, row);
        return page;
    }

    private static void ShowExpense(AppShell shell, Book book, JsonElement row)
    {
        book.Detail.Children.Clear();
        var id = Kit.Id(row);
        if (id is not long expenseId)
            return;
        var settled = Flag(row, "settled");
        var voided = Flag(row, "voided");
        var payable = Kit.Str(row, "paid_via") == "payable";
        book.Detail.Children.Add(Flow.Section(Kit.Str(row, "description")));
        book.Detail.Children.Add(Flow.Secondary(voided
            ? "This expense is voided."
            : settled
                ? "This payable expense has been paid."
                : payable
                    ? "Recorded as payable. It can be paid or voided."
                    : "Paid when it was recorded. It can still be voided."));
        if (!shell.CanPost || voided)
            return;
        if (payable && !settled)
        {
            var date = Flow.Input("Payment date");
            date.Text = Kit.Today();
            var via = Book.Choices("Pay from", ("Bank", "bank"), ("Cash", "cash"));
            var settle = new Button { Content = "Pay this expense", MinHeight = 32 };
            settle.Click += async (_, _) =>
            {
                if (!await Flow.Confirm(book, "Pay this expense?", "This posts the payment in the company file. A paid expense cannot be voided.", "Pay"))
                    return;
                var when = date.Text.Trim();
                var how = Book.Selected(via, "bank");
                _ = shell.Run("Paying expense…", () => shell.Call("expense_settle", new { id = expenseId, date = when, paid_via = how })).ContinueWith(task =>
                    shell.Window.DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!task.Result)
                            return;
                        book.Detail.Children.Clear();
                        book.Refresh();
                    }));
            };
            book.Detail.Children.Add(date);
            book.Detail.Children.Add(via);
            book.Detail.Children.Add(settle);
        }
        if (settled)
            return;
        var reason = Flow.Input("Reason for voiding");
        var voidButton = new Button { Content = "Void this expense", MinHeight = 32 };
        voidButton.Click += async (_, _) =>
        {
            var why = reason.Text.Trim();
            if (why.Length == 0)
            {
                shell.Problem("Give a reason.");
                return;
            }
            if (!await Flow.Confirm(book, "Void this expense?", "Voiding posts a reversal. The reason is stored in the audit log.", "Void"))
                return;
            _ = shell.Run("Voiding expense…", () => shell.Call("expense_void", new { id = expenseId, reason = why })).ContinueWith(task =>
                shell.Window.DispatcherQueue.TryEnqueue(() =>
                {
                    if (!task.Result)
                        return;
                    book.Detail.Children.Clear();
                    book.Refresh();
                }));
        };
        book.Detail.Children.Add(reason);
        book.Detail.Children.Add(voidButton);
    }

    private static bool Flag(JsonElement row, string key) =>
        row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;

    public static UIElement Income(AppShell shell)
    {
        var book = new Book("04.05", "Accounts", "Rent and interest", "Rent, interest and other income received by the firm.");
        JsonElement properties = default;
        var date = Flow.Input("Date");
        date.Text = Kit.Today();
        var kind = Book.Choices("Kind", ("Other", "other"), ("Rent", "rent"), ("Interest", "interest"));
        var property = new ComboBox { Header = "Property", HorizontalAlignment = HorizontalAlignment.Stretch };
        var payer = Flow.Input("Payer");
        var description = Flow.Input("Description");
        var amount = Flow.Input("Amount");
        var gst = Flow.Input("GST");
        gst.Text = "0";
        var mode = Book.Choices("GST mode", ("Within the state", "intra"), ("Another state", "inter"));
        var tds = Flow.Input("TDS");
        tds.Text = "0";
        var via = Book.Choices("Received via", ("Bank", "bank"), ("Cash", "cash"));
        if (shell.CanPost)
        {
            book.Form.Children.Add(Flow.Section("Record income"));
            foreach (var control in new Control[] { date, kind, property, payer, description, amount, gst, mode, tds, via })
                book.Form.Children.Add(control);
            AddPost(shell, book, "Record income", "Record this income?", "This posts a journal in the company file.", () =>
            {
                long? propertyId = Book.SelectedId(property) is long id && id > 0 ? id : null;
                return new { income = new { date = date.Text.Trim(), kind = Book.Selected(kind, "other"), property_id = propertyId, payer = payer.Text.Trim(), description = description.Text.Trim(), amount = amount.Text.Trim(), gst = gst.Text.Trim(), gst_mode = Book.Selected(mode, "intra"), tds = tds.Text.Trim(), received_via = Book.Selected(via, "bank") } };
            }, "income_record", "Recording income…");
            var propertyName = Flow.Input("New property name");
            var propertyKind = Book.Choices("Property kind", ("Residential", "residential"), ("Commercial", "commercial"));
            var saveProperty = new Button { Content = "Save property", MinHeight = 32 };
            saveProperty.Click += (_, _) =>
            {
                var saved = new { property = new { id = (long?)null, name = propertyName.Text.Trim(), kind = Book.Selected(propertyKind, "residential"), address = "", active = true } };
                _ = shell.Run("Saving property…", () => shell.Call("property_save", saved)).ContinueWith(task => shell.Window.DispatcherQueue.TryEnqueue(() =>
                {
                    if (!task.Result)
                        return;
                    _ = shell.Run("Loading properties…", () =>
                    {
                        var list = shell.Call("property_list");
                        shell.Window.DispatcherQueue.TryEnqueue(() => FillProperties(property, list));
                    });
                }));
            };
            book.Form.Children.Add(propertyName);
            book.Form.Children.Add(propertyKind);
            book.Form.Children.Add(saveProperty);
            book.Watch(date, kind, property, payer, description, amount, gst, mode, tds, via, propertyName);
            _ = shell.Run("Loading properties…", () =>
            {
                properties = shell.Call("property_list");
                shell.Window.DispatcherQueue.TryEnqueue(() => FillProperties(property, properties));
            });
        }
        book.Reload(shell, "Loading income…", "income_list", null, ["Date", "Kind", "Payer", "Amount"], ["date", "kind", "payer", "amount_display"], [false, false, false, true]);
        return book;
    }

    public static UIElement Salaries(AppShell shell) => Entry(shell, "04.06", "Salaries", "Salary payments and the deductions the firm withholds.", "payroll_list",
        ["Date", "Employee", "Period", "Net"], ["date", "employee", "period_label", "net_display"], [false, false, false, true],
        (book, _) =>
        {
            var date = Flow.Input("Date");
            date.Text = Kit.Today();
            var employee = Flow.Input("Employee");
            var period = Flow.Input("Period");
            var gross = Flow.Input("Gross");
            var tds = Flow.Input("Income tax deducted");
            tds.Text = "0";
            var pfEmployee = Flow.Input("Employee provident fund");
            pfEmployee.Text = "0";
            var pfEmployer = Flow.Input("Employer provident fund");
            pfEmployer.Text = "0";
            var esiEmployee = Flow.Input("Employee ESI");
            esiEmployee.Text = "0";
            var esiEmployer = Flow.Input("Employer ESI");
            esiEmployer.Text = "0";
            var professional = Flow.Input("Professional tax");
            professional.Text = "0";
            var via = Book.PaidVia();
            book.Form.Children.Add(Flow.Section("Record salary"));
            foreach (var control in new Control[] { date, employee, period, gross, tds, pfEmployee, pfEmployer, esiEmployee, esiEmployer, professional, via })
                book.Form.Children.Add(control);
            AddPost(shell, book, "Record salary", "Record this salary?", "This posts a journal in the company file.", () => new
            {
                payroll = new
                {
                    date = date.Text.Trim(),
                    employee = employee.Text.Trim(),
                    period_label = period.Text.Trim(),
                    gross = gross.Text.Trim(),
                    tds = tds.Text.Trim(),
                    paid_via = Book.Selected(via, "bank"),
                    pf_employee = pfEmployee.Text.Trim(),
                    pf_employer = pfEmployer.Text.Trim(),
                    esi_employee = esiEmployee.Text.Trim(),
                    esi_employer = esiEmployer.Text.Trim(),
                    professional_tax = professional.Text.Trim(),
                }
            }, "payroll_record", "Recording salary…");
            book.Watch(date, employee, period, gross, tds, pfEmployee, pfEmployer, esiEmployee, esiEmployer, professional, via);
        });

    public static UIElement Assets(AppShell shell)
    {
        var book = new Book("04.07", "Accounts", "Fixed assets", "Assets the firm owns, and depreciation for the financial year.");
        if (shell.CanPost)
        {
            var name = Flow.Input("Name");
            var date = Flow.Input("Purchase date");
            date.Text = Kit.Today();
            var cost = Flow.Input("Cost");
            var rate = Flow.Input("Depreciation rate %");
            rate.Text = "10";
            var via = Book.PaidVia();
            book.Form.Children.Add(Flow.Section("Add an asset"));
            foreach (var control in new Control[] { name, date, cost, rate, via })
                book.Form.Children.Add(control);
            AddPost(shell, book, "Add asset", "Add this asset?", "This posts the purchase in the company file.", () => new
            {
                asset = new { name = name.Text.Trim(), purchase_date = date.Text.Trim(), cost = cost.Text.Trim(), rate_percent = rate.Text.Trim(), paid_via = Book.Selected(via, "bank") }
            }, "asset_add", "Adding asset…");
            var depreciate = new Button { Content = "Depreciate this year", MinHeight = 32 };
            depreciate.Click += async (_, _) =>
            {
                if (!await Flow.Confirm(book, "Post this year's depreciation?", "Each asset is depreciated once for the financial year. The company file calculates the amounts.", "Post depreciation"))
                    return;
                var fy = Kit.FinancialYear();
                _ = shell.Run("Posting depreciation…", () => shell.Call("asset_depreciate", new { fy })).ContinueWith(task =>
                    shell.Window.DispatcherQueue.TryEnqueue(() => { if (task.Result) book.Refresh(); }));
            };
            book.Form.Children.Add(depreciate);
            book.Watch(name, date, cost, rate, via);
        }
        book.Reload(shell, "Loading assets…", "asset_list", null, ["Name", "Purchased", "Cost", "Book value"], ["name", "purchase_date", "cost_display", "book_value_display"], [false, false, true, true]);
        return book;
    }

    public static UIElement Tax(AppShell shell)
    {
        var book = new Book("04.08", "Accounts", "Tax payments", "Payments of GST, TDS and other taxes the firm owes.");
        if (shell.CanPost)
        {
            var kind = Book.Choices("Tax", ("TDS", "tds"), ("CGST", "cgst"), ("SGST", "sgst"), ("IGST", "igst"), ("Composition", "composition"), ("Income tax", "income_tax"), ("Provident fund", "pf"), ("ESI", "esi"), ("Professional tax", "professional_tax"));
            var date = Flow.Input("Date");
            date.Text = Kit.Today();
            var amount = Flow.Input("Amount");
            var narration = Flow.Input("Narration");
            var via = Book.Choices("Paid via", ("Bank", "bank"), ("Cash", "cash"));
            book.Form.Children.Add(Flow.Section("Record a payment"));
            foreach (var control in new Control[] { kind, date, amount, narration, via })
                book.Form.Children.Add(control);
            var pay = new Button { Content = "Record payment", MinHeight = 32, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            pay.Click += async (_, _) =>
            {
                if (!await Flow.Confirm(book, "Record this tax payment?", "This posts a journal in the company file.", "Record payment"))
                    return;
                var payment = new { kind = Book.Selected(kind, "tds"), date = date.Text.Trim(), amount = amount.Text.Trim(), paid_via = Book.Selected(via, "bank"), narration = narration.Text.Trim() };
                _ = shell.Run("Recording tax payment…", () => shell.Call("tax_payment", payment)).ContinueWith(task =>
                    shell.Window.DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!task.Result)
                            return;
                        book.ClearDirty();
                        LoadTax(shell, book);
                    }));
            };
            book.Form.Children.Add(pay);
            book.Watch(kind, date, amount, narration, via);
        }
        TaxAbstractView.Add(shell, book);
        LoadTax(shell, book);
        return book;
    }

    private static void LoadTax(AppShell shell, Book book) =>
        _ = shell.Run("Loading tax payments…", () =>
        {
            var page = shell.Call("journal_page", new
            {
                query = Flow.PageQuery(new Dictionary<string, string>
                {
                    ["source"] = "tax_payment",
                    ["from"] = "0000-01-01",
                    ["to"] = "9999-12-31",
                }, 200),
            });
            var rows = page.TryGetProperty("rows", out var list) ? list : default;
            shell.Window.DispatcherQueue.TryEnqueue(() =>
                book.Show(rows, ["Number", "Date", "Narration", "Amount"], ["number", "date", "narration", "total_display"], [false, false, false, true]));
        });

    public static UIElement Accounts(AppShell shell)
    {
        var book = new Book("04.10", "Accounts", "Chart and financial years", "Accounts and the years the file is open for.");
        if (shell.CanPost)
        {
            var code = Flow.Input("Code");
            var name = Flow.Input("Name");
            var kind = Book.Choices("Kind", ("Expense", "expense"), ("Income", "income"), ("Asset", "asset"), ("Liability", "liability"), ("Equity", "equity"));
            book.Form.Children.Add(Flow.Section("New account"));
            book.Form.Children.Add(code);
            book.Form.Children.Add(name);
            book.Form.Children.Add(kind);
            var create = new Button { Content = "Create account", MinHeight = 32, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            create.Click += (_, _) =>
            {
                var created = new { code = code.Text.Trim(), name = name.Text.Trim(), kind = Book.Selected(kind, "expense") };
                _ = shell.Run("Creating account…", () => shell.Call("account_create", created)).ContinueWith(task =>
                    shell.Window.DispatcherQueue.TryEnqueue(() => { if (task.Result) ReloadAccounts(shell, book); }));
            };
            book.Form.Children.Add(create);
            book.Watch(code, name, kind);
        }
        var years = new StackPanel { Spacing = 8 };
        var yearBox = new ComboBox { Header = "Financial year to close", HorizontalAlignment = HorizontalAlignment.Stretch };
        book.Form.Children.Add(Flow.Section("Financial years"));
        book.Form.Children.Add(years);
        book.Form.Children.Add(yearBox);
        if (shell.CanManage)
        {
            var password = new PasswordBox { Header = "Your password" };
            var close = new Button { Content = "Close selected year", MinHeight = 32 };
            close.Click += async (_, _) =>
            {
                var fy = yearBox.SelectedItem is ComboBoxItem item && item.Tag is string tag ? tag : "";
                if (fy.Length == 0)
                {
                    shell.Problem("Select a financial year first.");
                    return;
                }
                if (!await Flow.Confirm(book, "Close this financial year?", "Closing a year blocks new postings in that year. Your password confirms it.", "Close year"))
                    return;
                var secret = password.Password;
                _ = shell.Run("Updating the year…", () => shell.Call("period_set", new { fy, closed = true, password = secret }));
            };
            book.Form.Children.Add(password);
            book.Form.Children.Add(close);
        }
        ReloadAccounts(shell, book);
        _ = shell.Run("Loading years…", () =>
        {
            var list = shell.Call("period_list");
            shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                years.Children.Clear();
                yearBox.Items.Clear();
                if (list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in list.EnumerateArray())
                    {
                        var fy = Kit.Str(row, "fy");
                        yearBox.Items.Add(new ComboBoxItem { Content = $"{fy} · {Kit.Str(row, "status")}", Tag = fy });
                    }
                }
                if (yearBox.Items.Count > 0)
                    yearBox.SelectedIndex = 0;
                years.Children.Add(Kit.TableOf(list, "Financial years", ["Year", "Start", "End", "Status", "Journals"], ["fy", "start_date", "end_date", "status", "journals"], [false, false, false, false, true]));
            });
        });
        return book;
    }

    public static UIElement Company(AppShell shell)
    {
        var form = new DraftForm();
        var page = form.Body;
        page.Children.Add(Flow.SheetMark("05.01", "Admin", "Firm settings"));
        page.Children.Add(Flow.Heading("Firm settings"));
        page.Children.Add(Flow.Secondary("The name, registration and tax scheme printed on letters and invoices. Studio owns these details for the suite; CoreOp, Draft and Form read them."));
        var fields = new Dictionary<string, TextBox>();
        foreach (var (key, label) in new[]
        {
            ("name", "Name"), ("address", "Address"), ("email", "Email"), ("phone", "Phone"), ("pan", "PAN"), ("gstin", "GSTIN"),
            ("coa_registration", "COA registration"), ("bank_details", "Bank details"), ("upi_id", "UPI id"), ("architect_name", "Architect"),
            ("accent_hex", "Document accent"),
        })
        {
            var box = Flow.Input(label);
            if (key is "address" or "bank_details")
            {
                box.AcceptsReturn = true;
                box.TextWrapping = TextWrapping.Wrap;
                box.MinHeight = 80;
            }
            fields[key] = box;
        }
        page.Children.Add(Flow.Pair(fields["name"], fields["architect_name"]));
        page.Children.Add(Flow.Pair(fields["email"], fields["phone"]));
        page.Children.Add(fields["address"]);
        page.Children.Add(Flow.Pair(fields["pan"], fields["gstin"]));
        page.Children.Add(Flow.Pair(fields["coa_registration"], fields["upi_id"]));
        page.Children.Add(fields["accent_hex"]);
        fields["accent_hex"].PlaceholderText = "#3E5240";
        page.Children.Add(Flow.Secondary("One colour for rules and labels on new documents, as six hex digits. Leave it blank for black ink. Issued documents stay as they were."));
        page.Children.Add(fields["bank_details"]);
        var scheme = Book.Choices("GST scheme", ("Not registered", "none"), ("Composition", "composition"), ("Regular", "regular"));
        var letterhead = Book.Choices("Letterhead", ("Classic", "classic"), ("Minimal", "minimal"));
        // Read-only, the two choices show as plain values; the choosers appear only while editing.
        var schemeView = new TextBox { Header = "GST scheme", IsReadOnly = true };
        var letterheadView = new TextBox { Header = "Letterhead", IsReadOnly = true };
        var choosers = Flow.Pair(scheme, letterhead);
        var views = Flow.Pair(schemeView, letterheadView);
        page.Children.Add(views);
        page.Children.Add(choosers);
        page.Children.Add(Flow.Secondary("The square logo is the mark in the side panel and on documents. The rectangle logo is the wide letterhead on letters, invoices, and receipts."));
        var squareImage = new Image { Width = 72, Height = 72, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
        var wideImage = new Image { Width = 200, Height = 56, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
        var squareClear = new Button { Content = "Remove", MinWidth = 88, Visibility = Visibility.Collapsed };
        var wideClear = new Button { Content = "Remove", MinWidth = 88, Visibility = Visibility.Collapsed };
        var logoButtons = new List<Button>();
        page.Children.Add(LogoSlot(shell, "square", "Square logo", squareImage, squareClear, logoButtons));
        page.Children.Add(LogoSlot(shell, "wide", "Rectangle logo", wideImage, wideClear, logoButtons));
        void LoadLogos()
        {
            _ = shell.Run("Loading logos…", () =>
            {
                var stored = shell.Call("company_logo");
                shell.Window.DispatcherQueue.TryEnqueue(() =>
                {
                    _ = ShowLogo(squareImage, squareClear, MarkPresent(stored, "square"), MarkBytes(stored, "square"), shell.CanManage);
                    _ = ShowLogo(wideImage, wideClear, MarkPresent(stored, "wide"), MarkBytes(stored, "wide"), shell.CanManage);
                });
            });
        }
        squareClear.Click += (_, _) => ClearLogo("square");
        wideClear.Click += (_, _) => ClearLogo("wide");
        void ClearLogo(string shape)
        {
            if (!shell.CanManage)
                return;
            _ = shell.Run("Removing logo…", () => shell.Call("company_logo_clear", new { shape })).ContinueWith(task =>
                shell.Window.DispatcherQueue.TryEnqueue(() =>
                {
                    if (!task.Result)
                        return;
                    LoadLogos();
                    shell.RefreshPaneLogo?.Invoke();
                }));
        }
        var save = new Button { Content = "Save profile", MinHeight = 32, Style = (Style)Application.Current.Resources["AccentButtonStyle"], Visibility = Visibility.Collapsed };
        var cancel = new Button { Content = "Cancel", MinHeight = 32, Visibility = Visibility.Collapsed };
        var edit = new Button { Content = "Edit", MinHeight = 32, MinWidth = 88, HorizontalAlignment = HorizontalAlignment.Left, Visibility = shell.CanManage ? Visibility.Visible : Visibility.Collapsed };
        var editing = false;
        // The profile is read-only until Edit is pressed: text cannot be typed, choices are locked, and the logo buttons are hidden.
        void SetEditing(bool on)
        {
            editing = on;
            // A field that cannot be edited is drawn as plain text, not as an input box, so it does not look editable.
            foreach (var box in fields.Values.Append(schemeView).Append(letterheadView))
            {
                box.IsReadOnly = !on;
                box.IsTabStop = on;
                if (on)
                {
                    // Back to the input box the theme draws.
                    box.ClearValue(Control.BackgroundProperty);
                    box.ClearValue(Control.BorderThicknessProperty);
                    box.ClearValue(Control.PaddingProperty);
                }
                else
                {
                    box.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    box.BorderThickness = new Thickness(0);
                    box.Padding = new Thickness(0, Hcw.Ui.SuiteScale.ControlPadding.Top, 0, Hcw.Ui.SuiteScale.ControlPadding.Bottom);
                }
                box.PlaceholderText = on ? "" : "Not set";
                box.IsHitTestVisible = on;
            }
            choosers.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            views.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            schemeView.Text = (scheme.SelectedItem as ComboBoxItem)?.Content as string ?? "";
            letterheadView.Text = (letterhead.SelectedItem as ComboBoxItem)?.Content as string ?? "";
            edit.Visibility = !on && shell.CanManage ? Visibility.Visible : Visibility.Collapsed;
            save.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            cancel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            squareClear.IsEnabled = wideClear.IsEnabled = on;
            foreach (var button in logoButtons)
                button.Visibility = on && shell.CanManage ? Visibility.Visible : Visibility.Collapsed;
        }
        void SaveProfile()
        {
            if (!shell.CanManage || !editing)
                return;
            var profile = fields.ToDictionary(pair => pair.Key, pair => pair.Value.Text);
            profile["gst_scheme"] = Book.Selected(scheme, "none");
            profile["letterhead_style"] = Book.Selected(letterhead, "classic");
            _ = shell.Run("Saving profile…", () => shell.Call("company_save_profile", new { profile })).ContinueWith(task =>
                shell.Window.DispatcherQueue.TryEnqueue(() => { if (task.Result) { form.ClearDirty(); SetEditing(false); } }));
        }
        save.Click += (_, _) => SaveProfile();
        void LoadProfile()
        {
            _ = shell.Run("Loading profile…", () =>
            {
                var profile = shell.Call("company_get_profile");
                shell.Window.DispatcherQueue.TryEnqueue(() =>
                {
                    foreach (var (key, box) in fields)
                        box.Text = Kit.Str(profile, key);
                    Select(scheme, Kit.Str(profile, "gst_scheme"));
                    Select(letterhead, Kit.Str(profile, "letterhead_style"));
                    schemeView.Text = (scheme.SelectedItem as ComboBoxItem)?.Content as string ?? "";
                    letterheadView.Text = (letterhead.SelectedItem as ComboBoxItem)?.Content as string ?? "";
                    form.Arm();
                });
            });
        }
        edit.Click += (_, _) => SetEditing(true);
        cancel.Click += (_, _) =>
        {
            SetEditing(false);
            form.ClearDirty();
            LoadProfile();
        };
        // Edit, Save and Cancel sit at the top, under the description, where the page is read.
        page.Children.Insert(3, new StackPanel { Orientation = Orientation.Horizontal, Spacing = Hcw.Ui.SuiteScale.StackGap, Margin = new Thickness(0, Hcw.Ui.SuiteScale.Space1, 0, Hcw.Ui.SuiteScale.Space2), Children = { edit, save, cancel } });
        form.SaveAction = SaveProfile;
        if (shell.CanManage)
            form.Watch([.. fields.Values, scheme, letterhead]);
        SetEditing(false);
        LoadProfile();
        LoadLogos();
        return form;
    }

    private static UIElement LogoSlot(AppShell shell, string shape, string title, Image preview, Button clear, List<Button> editOnly)
    {
        var choose = new Button { Content = "Choose", MinWidth = 88 };
        choose.Visibility = Visibility.Collapsed;
        editOnly.Add(choose);
        choose.Click += async (_, _) =>
        {
            if (!shell.CanManage)
                return;
            string? path;
            try { path = await FileDialogs.PickFile(shell.Window.Handle, ".png", ".jpg", ".jpeg"); }
            catch (Exception ex) { shell.Problem(ex.Message); return; }
            if (path == null)
                return;
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception ex) { shell.Problem(ex.Message); return; }
            if (bytes.Length > 1_500_000)
            {
                shell.Problem("The logo must be smaller than 1.5 MB.");
                return;
            }
            var data = Convert.ToBase64String(bytes);
            _ = shell.Run("Saving logo…", () => shell.Call("company_logo_set", new { shape, data_base64 = data })).ContinueWith(task =>
                shell.Window.DispatcherQueue.TryEnqueue(() =>
                {
                    if (!task.Result)
                        return;
                    shell.RefreshPaneLogo?.Invoke();
                    _ = ShowStored(preview, clear, data, shell.CanManage);
                }));
        };
        var label = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, MinWidth = 120 };
        return new StackPanel
        {
            Spacing = 8,
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { label, preview, choose, clear },
        };
    }

    private static bool MarkPresent(JsonElement stored, string shape) =>
        stored.ValueKind == JsonValueKind.Object && stored.TryGetProperty(shape, out var mark) && Kit.Str(mark, "present") is "true" or "True";

    private static string MarkBytes(JsonElement stored, string shape) =>
        stored.ValueKind == JsonValueKind.Object && stored.TryGetProperty(shape, out var mark) ? Kit.Str(mark, "data_base64") : "";

    private static async Task ShowStored(Image logo, Button clear, string data, bool canClear)
    {
        try
        {
            logo.Source = await BrandArt.Decode(data);
            clear.Visibility = canClear ? Visibility.Visible : Visibility.Collapsed;
        }
        catch
        {
            logo.Source = null;
            clear.Visibility = Visibility.Collapsed;
        }
    }

    private static async Task ShowLogo(Image logo, Button clear, bool present, string data, bool canClear)
    {
        if (!present || data.Length == 0)
        {
            logo.Source = null;
            clear.Visibility = Visibility.Collapsed;
            return;
        }
        await ShowStored(logo, clear, data, canClear);
    }

    public static UIElement Settings(AppShell shell)
    {
        var form = new DraftForm();
        var page = form.Body;
        page.Children.Add(Flow.SheetMark("05.02", "Admin", "Security"));
        page.Children.Add(Flow.Heading("Security and backup"));
        if (shell.CanAudit)
        {
            var activity = new StackPanel { Spacing = 0 };
            page.Children.Add(Flow.Section("Recent activity"));
            page.Children.Add(Flow.Secondary("Latest events recorded in this company file."));
            page.Children.Add(activity);
            var refreshActivity = new Button { Content = "Refresh activity", MinHeight = 32 };
            refreshActivity.Click += (_, _) => LoadActivity(shell, activity);
            page.Children.Add(refreshActivity);
            LoadActivity(shell, activity);
        }
        var output = new StackPanel { Spacing = 8 };
        page.Children.Add(output);
        if (shell.CanManage)
        {
            var yours = new PasswordBox { Header = "Your password" };
            page.Children.Add(Flow.Section("People"));
            page.Children.Add(Flow.Secondary("Who can sign in to the suite. Your password confirms each change. CoreOp, Draft and Form never ask for the admin password."));
            var person = Flow.Input("Username");
            var personPassword = new PasswordBox { Header = "Password (12 or more characters)" };
            var confirm = new PasswordBox { Header = "Confirm password" };
            var role = new ComboBox { Header = "Role", HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var (label, key) in new[] { ("Accounts", "accounts"), ("Principal", "principal"), ("Document editor", "document_editor"), ("Auditor", "auditor") })
                role.Items.Add(new ComboBoxItem { Content = label, Tag = key });
            role.SelectedIndex = 0;
            page.Children.Add(Flow.Pair(person, role));
            page.Children.Add(Flow.Pair(personPassword, confirm));
            var create = new Button { Content = "Create account", MinHeight = 32, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            create.Click += (_, _) =>
            {
                if (personPassword.Password != confirm.Password)
                {
                    shell.Problem("The new passwords do not match.");
                    Flow.Mark(confirm, "The new passwords do not match.");
                    return;
                }
                var chosen = (role.SelectedItem as ComboBoxItem)?.Tag as string ?? "accounts";
                var user = new { username = person.Text.Trim(), password = personPassword.Password, role = chosen, admin_password = yours.Password };
                _ = shell.Run("Creating account…", () => shell.Call("user_create", user)).ContinueWith(task =>
                    shell.Window.DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!task.IsCompletedSuccessfully || !task.Result)
                            return;
                        person.Text = "";
                        personPassword.Password = "";
                        confirm.Password = "";
                        yours.Password = "";
                        shell.Run("Loading people…", () => Pages.Show(shell, output, "People", shell.Call("user_list"), ["Id", "Username", "Role", "Disabled"], ["id", "username", "role", "disabled"], [true, false, false, false]));
                    }));
            };
            var people = new Button { Content = "Show people", MinHeight = 32 };
            people.Click += (_, _) => shell.Run("Loading people…", () => Pages.Show(shell, output, "People", shell.Call("user_list"), ["Id", "Username", "Role", "Disabled"], ["id", "username", "role", "disabled"], [true, false, false, false]));
            page.Children.Add(Flow.Bar(create, people));
            page.Children.Add(Flow.Secondary("To reset someone's password: enter their user id from Show people, a new password (12+), and your password, then Reset password."));
            var resetId = Flow.Input("User id");
            var resetPassword = new PasswordBox { Header = "New password for that person" };
            page.Children.Add(Flow.Pair(resetId, resetPassword));
            var reset = new Button { Content = "Reset password", MinHeight = 32 };
            reset.Click += (_, _) =>
            {
                if (!long.TryParse(resetId.Text.Trim(), out var id))
                {
                    shell.Problem("Enter the numeric user id from Show people.");
                    return;
                }
                var args = new { id, password = resetPassword.Password, admin_password = yours.Password };
                _ = shell.Run("Resetting password…", () => shell.Call("user_set_password", args)).ContinueWith(task =>
                    shell.Window.DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!task.IsCompletedSuccessfully || !task.Result)
                            return;
                        resetPassword.Password = "";
                        yours.Password = "";
                        shell.Problem("Password updated.");
                    }));
            };
            var stop = new Button { Content = "Stop sign-in for user id", MinHeight = 32 };
            stop.Click += (_, _) =>
            {
                if (!long.TryParse(resetId.Text.Trim(), out var id))
                {
                    shell.Problem("Enter the numeric user id from Show people.");
                    return;
                }
                var adminPassword = yours.Password;
                _ = shell.Run("Updating person…", () =>
                {
                    var list = shell.Call("user_list");
                    var roleName = "accounts";
                    if (list.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var row in list.EnumerateArray())
                        {
                            if (Kit.Id(row) == id)
                            {
                                roleName = Kit.Str(row, "role");
                                break;
                            }
                        }
                    }
                    shell.Call("user_update", new { id, role = roleName, disabled = true, admin_password = adminPassword });
                }).ContinueWith(task =>
                    shell.Window.DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!task.IsCompletedSuccessfully || !task.Result)
                            return;
                        yours.Password = "";
                        shell.Run("Loading people…", () => Pages.Show(shell, output, "People", shell.Call("user_list"), ["Id", "Username", "Role", "Disabled"], ["id", "username", "role", "disabled"], [true, false, false, false]));
                    }));
            };
            page.Children.Add(Flow.Bar(reset, stop));
            page.Children.Add(Flow.Section("Company file"));
            var protection = new TextBlock { TextWrapping = TextWrapping.Wrap };
            page.Children.Add(protection);
            var protect = new Button { Content = "Protect this company file", MinHeight = 32 };
            var rotate = new Button { Content = "New recovery code", MinHeight = 32 };
            protect.Click += async (_, _) =>
            {
                if (yours.Password.Length == 0)
                {
                    shell.Problem("Enter your password.");
                    Flow.Mark(yours, "Enter your password.");
                    return;
                }
                var ask = new ContentDialog
                {
                    Title = "Protect this company file?",
                    Content = new TextBlock
                    {
                        Text = "The file on disk is encrypted. Your password opens it. A new recovery code is shown once and replaces the current one. That code also opens the file. Write it down before you close this window.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    PrimaryButtonText = "Protect",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = shell.Window.Content.XamlRoot,
                };
                if (await ask.ShowAsync() != ContentDialogResult.Primary)
                    return;
                var adminPassword = yours.Password;
                string? code = null;
                var saved = await shell.Run("Protecting the company file…", () =>
                {
                    code = shell.Call("company_protect", new { password = adminPassword }).GetString();
                });
                if (!saved || string.IsNullOrWhiteSpace(code))
                    return;
                yours.Password = "";
                protect.Visibility = Visibility.Collapsed;
                rotate.Visibility = Visibility.Visible;
                protection.Text = "This company file is protected. Your password and the recovery code open it. A backup of a protected file is protected with the same secret.";
                var shown = new ContentDialog
                {
                    Title = "Save your recovery code",
                    Content = new TextBlock
                    {
                        Text = code + "\n\nThis code is shown once. It opens the company file and resets the owner password.",
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                    },
                    CloseButtonText = "I have saved it",
                    XamlRoot = shell.Window.Content.XamlRoot,
                };
                await shown.ShowAsync();
            };
            rotate.Click += async (_, _) =>
            {
                if (yours.Password.Length == 0)
                {
                    shell.Problem("Enter your password.");
                    Flow.Mark(yours, "Enter your password.");
                    return;
                }
                var adminPassword = yours.Password;
                string? code = null;
                var saved = await shell.Run("Writing a new recovery code…", () =>
                {
                    code = shell.Call("company_rotate_recovery", new { password = adminPassword }).GetString();
                });
                if (!saved || string.IsNullOrWhiteSpace(code))
                    return;
                yours.Password = "";
                var shown = new ContentDialog
                {
                    Title = "Save your recovery code",
                    Content = new TextBlock
                    {
                        Text = code + "\n\nThis code is shown once. The previous recovery code no longer opens the file.",
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                    },
                    CloseButtonText = "I have saved it",
                    XamlRoot = shell.Window.Content.XamlRoot,
                };
                await shown.ShowAsync();
            };
            page.Children.Add(Flow.Bar(protect, rotate));
            shell.Run("Loading file protection…", () =>
            {
                var status = shell.Call("company_protection");
                var on = Kit.Str(status, "protected") is "true" or "True";
                shell.Window.DispatcherQueue.TryEnqueue(() =>
                {
                    protection.Text = on
                        ? "This company file is protected. Your password and the recovery code open it. A backup of a protected file is protected with the same secret."
                        : "This company file can be protected. Until then it is a normal company file, and any existing file still opens the way it does today.";
                    protect.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
                    rotate.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                });
            });
            page.Children.Add(Flow.Section("Backup"));
            page.Children.Add(Flow.Secondary("Your password confirms people changes, file protection and backups."));
            page.Children.Add(yours);
            var backup = new Button { Content = "Create backup", MinHeight = 32 };
            backup.Click += (_, _) =>
            {
                var adminPassword = yours.Password;
                shell.Run("Creating backup…", () => Pages.Show(shell, output, "Backup", shell.Call("backup_create", new { password = adminPassword }), ["File", "SHA-256", "Bytes", "Folder"], ["filename", "sha256", "bytes", "folder"], [false, false, true, false]));
            };
            page.Children.Add(backup);
            form.Watch([person, role, yours]);
            form.Arm();
        }
        var audit = new Button { Content = "Show audit", MinHeight = 32 };
        audit.Click += (_, _) => shell.Run("Loading audit…", () => Pages.Show(shell, output, "Audit", shell.Call("audit_list", new { limit = 100 }), ["When", "Person", "Event", "Detail"], ["at", "user", "event", "detail"], [false, false, false, false]));
        var backups = new Button { Content = "Show backups", MinHeight = 32 };
        backups.Click += (_, _) => shell.Run("Loading backups…", () => Pages.Show(shell, output, "Backups", shell.Call("backup_list"), ["File", "Bytes"], ["filename", "bytes"], [false, true]));
        var commands = new StackPanel { Spacing = 8 };
        if (shell.CanAudit)
            commands.Children.Add(audit);
        if (shell.CanManage)
            commands.Children.Add(backups);
        page.Children.Add(commands);
        return form;
    }

    private static readonly GridLength[] ActivityWidths = [new(200), new(160), new(180), new(2, GridUnitType.Star)];

    private static void LoadActivity(AppShell shell, StackPanel activity)
    {
        _ = shell.Run("Loading recent activity…", () =>
        {
            var audit = Flow.Try(shell, "audit_page", new { query = Flow.PageQuery(pageSize: 20) });
            shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                activity.Children.Clear();
                if (audit is JsonElement auditPage && auditPage.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() > 0)
                {
                    activity.Children.Add(Flow.Header(["When", "Person", "Event", "Detail"], ActivityWidths));
                    foreach (var row in rows.EnumerateArray())
                    {
                        var when = Kit.Str(row, "at");
                        var who = Kit.Str(row, "user");
                        var ev = EventLabel(Kit.Str(row, "event"));
                        var detail = Kit.Str(row, "detail");
                        var line = Flow.Columns([when, who.Length == 0 ? "—" : who, ev, detail.Length == 0 ? "—" : detail], ActivityWidths);
                        AutomationProperties.SetName(line, $"{when}, {who}, {ev}. {detail}");
                        activity.Children.Add(line);
                    }
                }
                else
                    activity.Children.Add(Flow.Secondary("No audit events yet."));
            });
        });
    }

    private static string EventLabel(string value)
    {
        if (value.Length == 0)
            return "Event";
        var words = value.Replace('_', ' ');
        return char.ToUpperInvariant(words[0]) + words[1..];
    }

    internal sealed class DraftForm : UserControl, IPageCommands
    {
        private bool _dirty;
        private bool _filling = true;

        public StackPanel Body { get; } = new() { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        public Action? SaveAction { get; set; }

        public DraftForm()
        {
            Body.Margin = (Thickness)Application.Current.Resources["PageMargin"];
            Content = new ScrollViewer
            {
                Content = Body,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollMode = ScrollMode.Disabled,
            };
        }

        public void Watch(params Control[] controls)
        {
            foreach (var control in controls)
            {
                if (control is TextBox box)
                    box.TextChanged += (_, _) => { if (box.FocusState != FocusState.Unfocused) Touch(); };
                else if (control is ComboBox combo)
                    combo.SelectionChanged += (_, _) => { if (combo.FocusState != FocusState.Unfocused) Touch(); };
                else if (control is PasswordBox password)
                    password.PasswordChanged += (_, _) => { if (password.FocusState != FocusState.Unfocused) Touch(); };
            }
        }

        public void Arm() => _filling = false;

        public void ClearDirty() => _dirty = false;

        private void Touch()
        {
            if (!_filling)
                _dirty = true;
        }

        public void NewItem() => Body.Children.OfType<Control>().FirstOrDefault()?.Focus(FocusState.Programmatic);

        public void Save() => SaveAction?.Invoke();

        public void Print() { }

        public void FocusSearch() => NewItem();

        public Task<bool> CanLeave() => Flow.Discard(this, _dirty);
    }

    private static UIElement Entry(AppShell shell, string sheet, string title, string result, string operation, string[] headers, string[] keys, bool[] money, Action<Book, JsonElement> build)
    {
        var book = new Book(sheet, "Accounts", title, result);
        if (shell.CanPost)
        {
            _ = shell.Run("Loading accounts…", () =>
            {
                var accounts = shell.Call("account_list", new { as_of = Kit.Today() });
                shell.Window.DispatcherQueue.TryEnqueue(() => build(book, accounts));
            });
        }
        book.Reload(shell, "Loading…", operation, operation == "journal_list" ? new { from = Kit.YearStart(), to = Kit.Today(), limit = 200 } : null, headers, keys, money);
        return book;
    }

    private static void AddPost(AppShell shell, Book book, string label, string title, string message, Func<object?> capture, string operation, string status)
    {
        var button = new Button { Content = label, MinHeight = 32, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        button.Click += async (_, _) =>
        {
            if (!await Flow.Confirm(book, title, message, label))
                return;
            var args = capture();
            if (args == null)
                return;
            _ = shell.Run(status, () => shell.Call(operation, args)).ContinueWith(task =>
                shell.Window.DispatcherQueue.TryEnqueue(() =>
                {
                    if (!task.Result)
                        return;
                    book.ClearDirty();
                    book.Refresh();
                }));
        };
        book.Form.Children.Add(button);
    }

    private static void LoadAccounts(AppShell shell, Action<JsonElement> apply) =>
        _ = shell.Run("Loading accounts…", () =>
        {
            var rows = shell.Call("account_list", new { as_of = Kit.Today() });
            shell.Window.DispatcherQueue.TryEnqueue(() => apply(rows));
        });

    private static void ReloadAccounts(AppShell shell, Book book) =>
        book.Reload(shell, "Loading accounts…", "account_list", new { as_of = Kit.Today() }, ["Code", "Name", "Kind", "Balance"], ["code", "name", "kind", "balance_display"], [false, false, false, true]);

    private static void FillProperties(ComboBox box, JsonElement rows)
    {
        box.Items.Clear();
        box.Items.Add(new ComboBoxItem { Content = "No property", Tag = 0L });
        if (rows.ValueKind == JsonValueKind.Array)
            foreach (var row in rows.EnumerateArray())
                box.Items.Add(new ComboBoxItem { Content = Kit.Str(row, "name"), Tag = Kit.Id(row) ?? 0 });
        box.SelectedIndex = 0;
    }

    private static void Replace(Panel panel, ComboBox current, ComboBox next)
    {
        var index = panel.Children.IndexOf(current);
        if (index < 0)
            return;
        panel.Children.RemoveAt(index);
        panel.Children.Insert(index, next);
    }

    private static ComboBox Find(Panel panel, string header) =>
        panel.Children.OfType<ComboBox>().First(box => Equals(box.Header, header));

    private static void Select(ComboBox box, string tag)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            if (item.Tag as string == tag)
            {
                box.SelectedItem = item;
                return;
            }
        }
    }
}
