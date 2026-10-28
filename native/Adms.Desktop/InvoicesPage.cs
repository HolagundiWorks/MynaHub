using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Adms.Desktop;

internal sealed class InvoicesPage : UserControl, IPageCommands
{
    private readonly AppShell _shell;
    private readonly List<JsonElement> _rows = [];
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly ContentControl _header = new();
    private readonly TextBlock _summary = Flow.Secondary("Loading invoices…");
    private readonly TextBlock _empty = Flow.Secondary("");
    private readonly TextBox _search = new() { PlaceholderText = "Number, client or project", Header = "Search", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _status = new() { Header = "Status", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _editor = new() { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _project = new() { Header = "Project" };
    private readonly ComboBox _kind = new() { Header = "Kind" };
    private readonly ComboBox _gst = new() { Header = "GST mode" };
    private readonly TextBox _issue = Flow.Input("Issue date");
    private readonly TextBox _due = Flow.Input("Due date");
    private readonly TextBox _notes = Flow.Input("Notes", true);
    private readonly StackPanel _lines = new() { Spacing = 8 };
    private readonly TextBlock _title = Flow.Heading("Invoices and receipts");
    private readonly TextBlock _state = Flow.Secondary("");
    private readonly Button _save = new();
    private readonly Button _issueButton = new() { Content = "Issue" };
    private readonly Button _preview = new() { Content = "Preview PDF" };
    private readonly Button _convert = new() { Content = "Convert to tax invoice", Visibility = Visibility.Collapsed };
    private readonly TextBox _cancelReason = Flow.Input("Reason for cancelling");
    private readonly Button _cancelInvoice = new() { Content = "Cancel invoice", Visibility = Visibility.Collapsed };
    private readonly TextBox _receiptAmount = Flow.Input("Receipt amount");
    private readonly TextBox _receiptDate = Flow.Input("Received");
    private readonly ComboBox _receiptMode = new() { Header = "Mode" };
    private readonly TextBox _receiptTds = Flow.Input("TDS deducted");
    private readonly ComboBox _receiptSection = new() { Header = "TDS section" };
    private readonly TextBox _receiptReference = Flow.Input("Reference");
    private readonly TextBlock _receiptBalance = Flow.Secondary("");
    private readonly StackPanel _receiptRows = new() { Spacing = 8 };
    private readonly Button _recordReceipt = new() { Content = "Record receipt" };
    private readonly TextBox _writeoffAmount = Flow.Input("Amount to write off");
    private readonly TextBox _writeoffDate = Flow.Input("Write-off date");
    private readonly TextBox _writeoffReason = Flow.Input("Reason");
    private readonly Button _writeoff = new() { Content = "Write off" };
    private readonly StackPanel _writeoffs = new() { Spacing = 8 };
    private readonly StackPanel _receipt = new() { Spacing = 8 };
    private readonly InfoBar _error = new() { IsOpen = false, IsClosable = false, Severity = InfoBarSeverity.Error };
    private readonly List<(TextBox Description, TextBox Qty, TextBox Rate)> _lineBoxes = [];
    private static readonly GridLength[] Widths = [new(150), new(1.6, GridUnitType.Star), new(1.4, GridUnitType.Star), new(110), new(140), new(140)];
    private static readonly bool[] Money = [false, false, false, false, true, true];
    private long? _id;
    private string _docStatus = "draft";
    private bool _dirty;
    private bool _filling;
    private bool _saving;

    public InvoicesPage(AppShell shell)
    {
        _shell = shell;
        Flow.UseTable(_list);
        Flow.Surface(_header);
        AutomationProperties.SetName(_list, "Invoices");
        AutomationProperties.SetName(_search, "Search invoices");
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);
        _empty.Visibility = Visibility.Collapsed;
        foreach (var (label, tag) in new[] { ("All statuses", ""), ("Draft", "draft"), ("Issued", "issued"), ("Part paid", "partial"), ("Overdue", "overdue"), ("Paid", "paid"), ("Cancelled", "cancelled") })
            _status.Items.Add(Item(label, tag));
        _status.SelectedIndex = 0;
        _kind.Items.Add(Item("Tax invoice", "tax_invoice"));
        _kind.Items.Add(Item("Pro forma", "proforma"));
        _gst.Items.Add(Item("Within the state", "intra"));
        _gst.Items.Add(Item("Another state", "inter"));
        foreach (var (label, tag) in new[] { ("Bank", "bank"), ("UPI", "upi"), ("Cheque", "cheque"), ("Cash", "cash") })
            _receiptMode.Items.Add(Item(label, tag));
        _receiptMode.SelectedIndex = 0;
        foreach (var (label, tag) in new[] { ("No TDS", ""), ("194J Professional fees", "194J"), ("194C Contract", "194C"), ("194-I Rent", "194-I"), ("194H Commission", "194H") })
            _receiptSection.Items.Add(Item(label, tag));
        _receiptSection.SelectedIndex = 0;
        foreach (var box in new[] { _issue, _due, _notes, _receiptAmount, _receiptDate })
            box.TextChanged += (_, _) => { if (!_filling) _dirty = true; };
        _project.SelectionChanged += (_, _) => { if (!_filling) _dirty = true; };
        _kind.SelectionChanged += (_, _) => { if (!_filling) _dirty = true; };
        _gst.SelectionChanged += (_, _) => { if (!_filling) _dirty = true; };
        _search.TextChanged += (_, _) => Paint();
        _status.SelectionChanged += (_, _) => Paint();
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is ListViewItem item && item.Tag is JsonElement row)
                Open(row);
        };

        var create = Flow.Command("New invoice", () => _ = BeginNew(), true);
        AutomationProperties.SetName(create, "New invoice");
        if (!shell.CanEdit)
            create.Visibility = Visibility.Collapsed;
        var titleRow = new Grid { ColumnSpacing = 12 };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel { Spacing = 4 };
        titles.Children.Add(Flow.SheetMark("04.01", "Accounts", "Invoices"));
        titles.Children.Add(Flow.Heading("Invoices and receipts"));
        titles.Children.Add(Flow.Secondary("Fees billed and recovered on time."));
        titleRow.Children.Add(titles);
        Grid.SetColumn(create, 1);
        create.VerticalAlignment = VerticalAlignment.Top;
        titleRow.Children.Add(create);

        var filters = new Grid();
        var listPane = new Grid { RowSpacing = 12 };
        listPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        listPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        listPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        listPane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        listPane.Children.Add(titleRow);
        Grid.SetRow(filters, 1);
        listPane.Children.Add(filters);
        Grid.SetRow(_summary, 2);
        listPane.Children.Add(_summary);
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
        listPane.Children.Add(table);
        SizeChanged += (_, _) => Flow.Place(filters, [_search, _status], 280, true);

        _save.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        _save.Click += (_, _) => SaveDraft();
        _issueButton.Click += async (_, _) => await Issue();
        _preview.Click += (_, _) => Preview();
        var cancel = new Button { Content = "Back" };
        cancel.Click += async (_, _) => { if (await CanLeave()) ShowList(); };
        _convert.Click += (_, _) => ConvertProforma();
        _cancelInvoice.Click += async (_, _) => await CancelInvoice();
        _cancelReason.Visibility = Visibility.Collapsed;
        var addLine = new Button { Content = "Add line" };
        AutomationProperties.SetName(addLine, "Add invoice line");
        addLine.Click += (_, _) => AddLine("", "1", "0");
        _recordReceipt.Click += (_, _) => RecordReceipt();
        var suggestTen = new Button { Content = "TDS at 10%" };
        var suggestTwo = new Button { Content = "TDS at 2%" };
        AutomationProperties.SetName(suggestTen, "Suggest TDS at 10 percent of the fee");
        AutomationProperties.SetName(suggestTwo, "Suggest TDS at 2 percent of the fee");
        suggestTen.Click += (_, _) => SuggestTds("10");
        suggestTwo.Click += (_, _) => SuggestTds("2");
        _receipt.Children.Add(Flow.Section("Receipt and TDS"));
        _receipt.Children.Add(Flow.Secondary("Record what the client paid, and the TDS they deducted from the fee. The receipt prints on its own. A pro forma is converted to a tax invoice before it is paid."));
        _receipt.Children.Add(_receiptBalance);
        _receipt.Children.Add(Flow.Pair(_receiptDate, _receiptAmount));
        _receipt.Children.Add(Flow.Pair(_receiptTds, _receiptSection));
        _receipt.Children.Add(Flow.Bar(suggestTen, suggestTwo));
        _receipt.Children.Add(Flow.Pair(_receiptMode, _receiptReference));
        _receipt.Children.Add(_recordReceipt);
        _receipt.Children.Add(_receiptRows);
        _writeoffDate.Text = Kit.Today();
        AutomationProperties.SetName(_writeoff, "Write off the outstanding balance");
        _writeoff.Click += (_, _) => WriteOff();
        if (!_shell.CanPost)
            _writeoff.Visibility = Visibility.Collapsed;
        _receipt.Children.Add(Flow.Section("Not collected"));
        _receipt.Children.Add(Flow.Secondary("A write-off removes the balance from the client's ledger. It is not money received, and it does not change the bank. Use it when the bill, or part of it, will not be paid."));
        _receipt.Children.Add(Flow.Pair(_writeoffDate, _writeoffAmount));
        _receipt.Children.Add(_writeoffReason);
        _receipt.Children.Add(_writeoff);
        _receipt.Children.Add(_writeoffs);
        var actions = Flow.Bar(cancel, _save, _preview, _issueButton, _convert, _cancelInvoice);
        _editor.Children.Add(_title);
        _editor.Children.Add(_state);
        _editor.Children.Add(Flow.Section("Invoice"));
        _editor.Children.Add(_project);
        _editor.Children.Add(Flow.Pair(_kind, _gst));
        _editor.Children.Add(Flow.Secondary("A regular registration charges 18 percent GST. Within the state that is CGST and SGST. Another state is IGST. Architectural fees print under SAC 9983. A pro forma uses the same figures and is not a tax invoice."));
        _editor.Children.Add(Flow.Pair(_issue, _due));
        _editor.Children.Add(_cancelReason);
        _editor.Children.Add(Flow.Section("Lines"));
        _editor.Children.Add(_lines);
        _editor.Children.Add(addLine);
        _editor.Children.Add(_notes);
        _editor.Children.Add(_error);
        _editor.Children.Add(actions);
        _editor.Children.Add(_receipt);

        var host = new Grid { Margin = (Thickness)Application.Current.Resources["PageMargin"] };
        host.Children.Add(listPane);
        var editorScroll = Flow.Scroll(_editor);
        editorScroll.Visibility = Visibility.Collapsed;
        host.Children.Add(editorScroll);
        if (_shell.CanIssue)
            Flow.Pin(host, ("New invoice", () => _ = BeginNew()));
        Content = host;
        Reload();
    }

    private Grid Host => (Grid)Content;
    private void ShowList()
    {
        _dirty = false;
        Host.Children[1].Visibility = Visibility.Collapsed;
        Host.Children[0].Visibility = Visibility.Visible;
        _list.SelectedItem = null;
    }

    private async Task BeginNew()
    {
        if (!await CanLeave())
            return;
        if (_project.Items.Count <= 1)
        {
            _shell.Problem("Add a project before creating an invoice.");
            return;
        }
        _filling = true;
        _id = null;
        _docStatus = "draft";
        _issue.Text = Kit.Today();
        _due.Text = "";
        _notes.Text = "";
        _receiptAmount.Text = "";
        _receiptDate.Text = Kit.Today();
        _project.SelectedIndex = 0;
        _kind.SelectedIndex = 0;
        _gst.SelectedIndex = 0;
        _lines.Children.Clear();
        _lineBoxes.Clear();
        AddLine("", "1", "0");
        _title.Text = "New invoice";
        _state.Text = "A draft can be changed. Issue gives it a number and freezes it.";
        ApplyLock(true);
        _error.IsOpen = false;
        _filling = false;
        _dirty = false;
        Host.Children[0].Visibility = Visibility.Collapsed;
        Host.Children[1].Visibility = Visibility.Visible;
        _project.Focus(FocusState.Programmatic);
    }

    private void Open(JsonElement row)
    {
        if (Kit.Id(row) is long invoiceId)
            OpenById(invoiceId, focusReceipt: false);
    }

    /// <summary>Opens one invoice. From the project path "record payment", focus goes to the amount received.</summary>
    private void OpenById(long invoiceId, bool focusReceipt)
    {
        _ = _shell.Run("Opening invoice…", () =>
        {
            var detail = _shell.Call("invoice_get", new { id = invoiceId });
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                Apply(detail);
                if (focusReceipt && _receiptAmount.IsEnabled)
                    _receiptAmount.Focus(FocusState.Programmatic);
            });
        });
    }

    private void Apply(JsonElement detail)
    {
        _filling = true;
        var input = detail.TryGetProperty("input", out var body) ? body : detail;
        _id = Kit.Id(input) ?? Kit.Id(detail);
        _docStatus = Kit.Str(detail, "status");
        if (_docStatus.Length == 0)
            _docStatus = "draft";
        Select(_project, Kit.Long(Kit.Str(input, "project_id")));
        Select(_kind, Kit.Str(input, "kind"));
        Select(_gst, Kit.Str(input, "gst_mode"));
        _issue.Text = Kit.Str(input, "issue_date");
        _due.Text = Kit.Str(input, "due_date");
        _notes.Text = Kit.Str(input, "notes");
        _lines.Children.Clear();
        _lineBoxes.Clear();
        if (input.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
        {
            foreach (var line in lines.EnumerateArray())
                AddLine(Kit.Str(line, "description"), Kit.Str(line, "qty"), Kit.Str(line, "rate"));
        }
        if (_lineBoxes.Count == 0)
            AddLine("", "1", "0");
        var number = Kit.Str(detail, "number");
        var draft = _docStatus == "draft";
        var kind = Kit.Str(input, "kind");
        _title.Text = number.Length == 0 ? "Draft invoice" : number;
        _state.Text = draft
            ? "Draft. Issue gives this invoice a number. After that, the content cannot be edited."
            : kind == "proforma"
                ? "Pro forma. This asks for payment and is not a tax invoice. Convert it when the client accepts."
                : $"{StatusLabel(_docStatus)}. Issued invoices stay as they were issued. Record the receipt, including TDS the client deducted.";
        _receiptDate.Text = Kit.Today();
        _receiptAmount.Text = "";
        _receiptTds.Text = "";
        _receiptReference.Text = "";
        _receiptSection.SelectedIndex = 0;
        ApplyLock(draft && _shell.CanEdit);
        _error.IsOpen = false;
        _filling = false;
        _dirty = false;
        Host.Children[0].Visibility = Visibility.Collapsed;
        Host.Children[1].Visibility = Visibility.Visible;
        if (!draft && kind == "tax_invoice")
            LoadMoney();
    }

    private void ApplyLock(bool editable)
    {
        foreach (var box in new Control[] { _project, _kind, _gst, _issue, _due, _notes })
            box.IsEnabled = editable;
        foreach (var (description, qty, rate) in _lineBoxes)
        {
            description.IsEnabled = editable;
            qty.IsEnabled = editable;
            rate.IsEnabled = editable;
        }
        _save.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        _save.Content = _id == null ? "Create" : "Save draft";
        _issueButton.Visibility = editable && _shell.CanIssue ? Visibility.Visible : Visibility.Collapsed;
        _preview.Visibility = _id == null ? Visibility.Collapsed : Visibility.Visible;
        var issued = !editable && _docStatus == "issued";
        var kind = ((ComboBoxItem?)_kind.SelectedItem)?.Tag as string ?? "";
        _receipt.Visibility = issued && kind == "tax_invoice" ? Visibility.Visible : Visibility.Collapsed;
        _recordReceipt.Visibility = _shell.CanIssue ? Visibility.Visible : Visibility.Collapsed;
        _convert.Visibility = issued && kind == "proforma" && _shell.CanEdit ? Visibility.Visible : Visibility.Collapsed;
        _cancelInvoice.Visibility = issued && _shell.CanIssue ? Visibility.Visible : Visibility.Collapsed;
        _cancelReason.Visibility = _cancelInvoice.Visibility;
    }

    private void AddLine(string description, string qty, string rate)
    {
        var desc = Flow.Input("Description");
        var quantity = Flow.Input("Quantity");
        var price = Flow.Input("Rate");
        desc.Text = description;
        quantity.Text = qty;
        price.Text = rate;
        foreach (var box in new[] { desc, quantity, price })
            box.TextChanged += (_, _) => { if (!_filling) _dirty = true; };
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(desc);
        Grid.SetColumn(quantity, 1);
        row.Children.Add(quantity);
        Grid.SetColumn(price, 2);
        row.Children.Add(price);
        _lines.Children.Add(row);
        _lineBoxes.Add((desc, quantity, price));
    }

    private void Paint()
    {
        var needle = _search.Text.Trim().ToLowerInvariant();
        var status = ((ComboBoxItem?)_status.SelectedItem)?.Tag as string ?? "";
        _list.Items.Clear();
        _header.Content = Flow.Header(["Number", "Project", "Client", "Status", "Total", "Balance"], Widths, Money);
        var count = 0;
        foreach (var row in _rows)
        {
            var effective = Kit.Str(row, "effective_status");
            if (effective.Length == 0)
                effective = Kit.Str(row, "status");
            if (status.Length > 0 && effective != status)
                continue;
            var number = Kit.Str(row, "number");
            if (number.Length == 0)
                number = "Draft";
            var blob = $"{number} {Kit.Str(row, "client_name")} {Kit.Str(row, "project_name")}".ToLowerInvariant();
            if (needle.Length > 0 && !blob.Contains(needle))
                continue;
            var cells = new[] { number, Kit.Str(row, "project_name"), Kit.Str(row, "client_name"), StatusLabel(effective), Kit.Str(row, "total_display"), Kit.Str(row, "balance_display") };
            var item = new ListViewItem { Content = Flow.Line(Flow.Columns(cells, Widths, Money), Flow.Edit(() => Open(row))), Tag = row };
            AutomationProperties.SetName(item, $"{cells[0]}, {cells[1]}, {cells[2]}, {cells[3]}, total {cells[4]}, balance {cells[5]}");
            _list.Items.Add(item);
            count++;
        }
        if (count == 0 && needle.Length == 0 && status.Length == 0)
        {
            _summary.Text = "This company has no invoices yet.";
            _empty.Text = "Create an invoice for a project.";
        }
        else if (count == 0)
        {
            _summary.Text = "No invoices match this search.";
            _empty.Text = "Change the search or status filter.";
        }
        else
            _summary.Text = count == 1 ? "1 invoice." : $"{count} invoices.";
        _empty.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Reload() => _ = _shell.Run("Loading invoices…", () =>
    {
        var page = _shell.Call("invoice_page", new { query = Flow.PageQuery(pageSize: 200) });
        var projects = _shell.Call("project_list", new { client_id = (long?)null, include_archived = false });
        _shell.Window.DispatcherQueue.TryEnqueue(() =>
        {
            _rows.Clear();
            if (page.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
                foreach (var row in rows.EnumerateArray())
                    _rows.Add(row.Clone());
            _project.Items.Clear();
            _project.Items.Add(Item("Select a project", 0L));
            if (projects.ValueKind == JsonValueKind.Array)
                foreach (var row in projects.EnumerateArray())
                    _project.Items.Add(Item($"{Kit.Str(row, "client_name")} — {Kit.Str(row, "name")}", Kit.Id(row) ?? 0));
            Paint();
            if (Flow.TakeCreate(_shell, "invoices"))
                _ = BeginNew();
            else if (Flow.TakeOpen(_shell, "invoices") is { } target)
                OpenById(target.Id, focusReceipt: target.Kind == "pay");
        });
    });

    private void SaveDraft()
    {
        if (_saving || _docStatus != "draft" && _id != null)
            return;
        var input = Capture();
        if (input == null)
            return;
        _saving = true;
        _save.IsEnabled = false;
        _ = _shell.Run(_id == null ? "Creating invoice…" : "Saving draft…", () =>
        {
            var saved = _shell.Call("invoice_save_draft", new { input });
            if (saved.ValueKind == JsonValueKind.Number)
                _id = saved.GetInt64();
        }).ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() =>
        {
            _saving = false;
            _save.IsEnabled = true;
            if (!task.Result)
                return;
            _dirty = false;
            ShowList();
            Reload();
        }));
    }

    private async Task Issue()
    {
        if (_saving || !_shell.CanIssue)
            return;
        if (!await Flow.Confirm(this, "Issue this invoice?", "Issue gives the invoice a number. The content can no longer be edited. This is not the same as saving a draft.", "Issue"))
            return;
        var input = Capture();
        if (input == null)
            return;
        _saving = true;
        _issueButton.IsEnabled = false;
        _ = _shell.Run("Issuing invoice…", () =>
        {
            var saved = _shell.Call("invoice_save_draft", new { input });
            var id = saved.ValueKind == JsonValueKind.Number ? saved.GetInt64() : _id ?? throw new Adms.Interop.AdmsException(1, "Save the draft before issuing it.");
            _shell.Call("invoice_issue", new { id });
        }).ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() =>
        {
            _saving = false;
            _issueButton.IsEnabled = true;
            if (!task.Result)
                return;
            _dirty = false;
            ShowList();
            Reload();
        }));
    }

    private void Preview()
    {
        if (_id is not long id)
        {
            Fail("Save the draft before previewing it.");
            return;
        }
        _ = _shell.Run("Preparing PDF…", () =>
        {
            var encoded = _shell.Call("invoice_pdf", new { id }).GetString() ?? "";
            _shell.Window.DispatcherQueue.TryEnqueue(async () => await Kit.PreviewPdf(_shell, encoded, "invoice"));
        });
    }

    private void ConvertProforma()
    {
        if (_id is not long id)
            return;
        _ = _shell.Run("Converting pro forma…", () =>
        {
            var created = _shell.Call("invoice_convert_proforma", new { id, today = Kit.Today() });
            var next = created.ValueKind == JsonValueKind.Number ? created.GetInt64() : id;
            var detail = _shell.Call("invoice_get", new { id = next });
            _shell.Window.DispatcherQueue.TryEnqueue(() => Apply(detail));
        });
    }

    private async Task CancelInvoice()
    {
        if (_id is not long id || !_shell.CanIssue)
            return;
        var reason = _cancelReason.Text.Trim();
        if (reason.Length == 0)
        {
            Fail("Give a reason for the cancellation.");
            return;
        }
        if (!await Flow.Confirm(this, "Cancel this invoice?", "The number stays used. Cancel only when nothing has been received against it.", "Cancel invoice"))
            return;
        _ = _shell.Run("Cancelling invoice…", () => _shell.Call("invoice_cancel", new { id, reason }))
            .ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (!task.Result)
                    return;
                _dirty = false;
                ShowList();
                Reload();
            }));
    }

    private void LoadMoney()
    {
        if (_id is not long id)
            return;
        _ = _shell.Run("Loading receipts…", () =>
        {
            var list = _shell.Call("receipt_list", new { invoice_id = id });
            var balance = _shell.Call("receipt_balance", new { invoice_id = id });
            var writeoffs = _shell.Call("invoice_writeoff_list", new { invoice_id = id });
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                PaintReceipts(list, balance);
                PaintWriteoffs(writeoffs);
            });
        });
    }

    private void PaintReceipts(JsonElement list, JsonElement balance)
    {
        var written = Kit.Str(balance, "written_off_display");
        _receiptBalance.Text = written.Length == 0 || written == "?0.00"
            ? $"Outstanding {Kit.Str(balance, "balance_display")}. Received {Kit.Str(balance, "received_display")}. TDS {Kit.Str(balance, "tds_display")}."
            : $"Outstanding {Kit.Str(balance, "balance_display")}. Received {Kit.Str(balance, "received_display")}. TDS {Kit.Str(balance, "tds_display")}. Written off {written}.";
        _receiptRows.Children.Clear();
        if (list.ValueKind != JsonValueKind.Array)
            return;
        foreach (var row in list.EnumerateArray())
        {
            var id = Kit.Id(row);
            if (id is not long receiptId)
                continue;
            var tds = Kit.Str(row, "tds_display");
            var section = Kit.Str(row, "tds_section");
            var label = $"{Kit.Str(row, "number")} · {Kit.Str(row, "received_date")} · {Kit.Str(row, "amount_display")}";
            if (section.Length > 0)
                label += $" · TDS {tds} ({section})";
            if (row.TryGetProperty("reversed", out var reversed) && reversed.ValueKind == JsonValueKind.True)
                label += " · Reversed";
            var preview = new Button { Content = "Receipt PDF" };
            var number = Kit.Str(row, "number");
            preview.Click += (_, _) =>
            {
                _ = _shell.Run("Preparing receipt…", () =>
                {
                    var encoded = _shell.Call("receipt_pdf", new { receipt_id = receiptId }).GetString() ?? "";
                    _shell.Window.DispatcherQueue.TryEnqueue(async () => await Kit.PreviewPdf(_shell, encoded, number.Length == 0 ? "receipt" : number));
                });
            };
            var line = new Grid { ColumnSpacing = 12 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
            Grid.SetColumn(preview, 1);
            line.Children.Add(preview);
            _receiptRows.Children.Add(line);
        }
    }

    private void PaintWriteoffs(JsonElement list)
    {
        _writeoffs.Children.Clear();
        if (list.ValueKind != JsonValueKind.Array)
            return;
        foreach (var row in list.EnumerateArray())
        {
            if (Kit.Id(row) is not long writeoffId)
                continue;
            var reversed = row.TryGetProperty("reversed", out var flag) && flag.ValueKind == JsonValueKind.True;
            var label = $"Write-off · {Kit.Str(row, "date")} · {Kit.Str(row, "amount_display")} · {Kit.Str(row, "reason")}";
            if (reversed)
                label += " · Reversed";
            var line = new Grid { ColumnSpacing = 12 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
            if (!reversed && _shell.CanPost)
            {
                var reverse = new Button { Content = "Reverse" };
                reverse.Click += (_, _) => ReverseWriteoff(writeoffId);
                Grid.SetColumn(reverse, 1);
                line.Children.Add(reverse);
            }
            _writeoffs.Children.Add(line);
        }
    }

    private void WriteOff()
    {
        if (_id is not long id || _docStatus != "issued" || !_shell.CanPost)
            return;
        var amount = _writeoffAmount.Text.Trim();
        var date = _writeoffDate.Text.Trim();
        var reason = _writeoffReason.Text.Trim();
        if (amount.Length == 0 || reason.Length == 0 || date.Length == 0)
        {
            Fail("Enter the date, the amount, and the reason for the write-off.");
            return;
        }
        var input = new { invoice_id = id, writeoff_date = date, amount, reason };
        _ = _shell.Run("Writing off…", () => _shell.Call("invoice_writeoff", new { input }))
            .ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (!task.Result)
                    return;
                _writeoffAmount.Text = "";
                _writeoffReason.Text = "";
                LoadMoney();
                Reload();
            }));
    }

    private void ReverseWriteoff(long writeoffId)
    {
        var reason = _writeoffReason.Text.Trim();
        if (reason.Length == 0)
        {
            Fail("Enter the reason for reversing the write-off.");
            return;
        }
        _ = _shell.Run("Reversing write-off…", () => _shell.Call("invoice_writeoff_reverse", new { writeoff_id = writeoffId, reason }))
            .ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (!task.Result)
                    return;
                _writeoffReason.Text = "";
                LoadMoney();
                Reload();
            }));
    }

    private void SuggestTds(string rate)
    {
        if (_id is not long id)
            return;
        _ = _shell.Run("Suggesting TDS…", () =>
        {
            var value = _shell.Call("receipt_suggest_tds", new { invoice_id = id, rate }).GetString() ?? "";
            _shell.Window.DispatcherQueue.TryEnqueue(() => _receiptTds.Text = value);
        });
    }

    private void RecordReceipt()
    {
        if (_id is not long id || _docStatus != "issued" || !_shell.CanIssue)
            return;
        var amount = _receiptAmount.Text.Trim();
        var date = _receiptDate.Text.Trim();
        var tds = _receiptTds.Text.Trim();
        var section = ((ComboBoxItem?)_receiptSection.SelectedItem)?.Tag as string ?? "";
        var mode = ((ComboBoxItem?)_receiptMode.SelectedItem)?.Tag as string ?? "bank";
        var reference = _receiptReference.Text.Trim();
        if (amount.Length == 0 && tds.Length == 0)
        {
            Fail("Enter the amount received or the TDS deducted.");
            return;
        }
        if (tds.Length > 0 && tds != "0" && tds != "0.00" && section.Length == 0)
        {
            Fail("Choose the TDS section.");
            return;
        }
        var receipt = new { invoice_id = id, received_date = date, amount, tds, tds_section = section, mode, reference, notes = "", bank_account_id = (long?)null };
        _ = _shell.Run("Recording receipt…", () => _shell.Call("receipt_record", new { input = receipt }))
            .ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (!task.Result)
                    return;
                _receiptAmount.Text = "";
                _receiptTds.Text = "";
                _receiptReference.Text = "";
                _receiptSection.SelectedIndex = 0;
                LoadMoney();
                Reload();
            }));
    }

    private object? Capture()
    {
        var projectId = ((ComboBoxItem?)_project.SelectedItem)?.Tag is long id ? id : 0;
        if (projectId == 0)
        {
            Fail("Select a project.");
            return null;
        }
        var lines = new List<object>();
        foreach (var (description, qty, rate) in _lineBoxes)
        {
            if (description.Text.Trim().Length == 0 && qty.Text.Trim().Length == 0)
                continue;
            lines.Add(new { description = description.Text.Trim(), qty = qty.Text.Trim(), rate = rate.Text.Trim() });
        }
        if (lines.Count == 0)
        {
            Fail("Add a line with a description.");
            return null;
        }
        return new
        {
            id = _id,
            project_id = projectId,
            kind = ((ComboBoxItem?)_kind.SelectedItem)?.Tag as string ?? "tax_invoice",
            issue_date = _issue.Text.Trim(),
            due_date = _due.Text.Trim(),
            gst_mode = ((ComboBoxItem?)_gst.SelectedItem)?.Tag as string ?? "intra",
            gst_rate = "",
            notes = _notes.Text.Trim(),
            lines,
        };
    }

    private void Fail(string message)
    {
        _error.Message = message;
        _error.IsOpen = true;
    }

    public async void NewItem() => await BeginNew();
    public void Save() => SaveDraft();
    public void Print() => Preview();
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
        if (box.Items.Count > 0)
            box.SelectedIndex = 0;
    }

    private static string StatusLabel(string status) => status switch
    {
        "draft" => "Draft",
        "issued" => "Issued",
        "partial" => "Part paid",
        "overdue" => "Overdue",
        "paid" => "Paid",
        "cancelled" => "Cancelled",
        "completed" => "Completed",
        _ => status,
    };
}
