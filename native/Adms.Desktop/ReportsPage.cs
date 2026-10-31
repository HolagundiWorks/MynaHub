using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Adms.Desktop;

internal sealed class ReportsPage : UserControl, IPageCommands
{
    private readonly AppShell _shell;
    private readonly TextBox _from = new() { Header = "From", Text = Kit.YearStart(), HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "yyyy-MM-dd" };
    private readonly TextBox _to = new() { Header = "To", Text = Kit.Today(), HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "yyyy-MM-dd" };
    private readonly ComboBox _client = new() { Header = "Client", HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Choose a client" };
    private readonly ComboBox _project = new() { Header = "Project", HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "All projects" };
    private readonly TextBlock _period = Flow.Secondary("");
    private readonly TextBlock _heading = Flow.Section("Trial balance");
    private readonly InfoBar _error = new() { IsOpen = false, IsClosable = true, Severity = InfoBarSeverity.Error };
    private readonly StackPanel _body = new() { Spacing = 12 };
    private readonly Grid _dates = new();
    private readonly Grid _commands = new();
    private readonly List<UIElement> _commandItems = [];
    private readonly List<ReportBlock> _blocks = [];
    private bool _running;

    public ReportsPage(AppShell shell)
    {
        _shell = shell;
        AutomationProperties.SetName(_from, "From");
        AutomationProperties.SetHelpText(_from, "Period start, year month day. Example 2026-04-01.");
        AutomationProperties.SetName(_to, "To");
        AutomationProperties.SetHelpText(_to, "Period end, or the as-of date for trial balance and the balance sheet.");
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);

        _commandItems.Add(ReportButton("Trial balance", () => RunAsOf("Trial balance", "report_trial_balance", ShowTrial)));
        _commandItems.Add(ReportButton("Profit and loss", () => RunRange("Profit and loss", "report_pnl", ShowProfit)));
        _commandItems.Add(ReportButton("Balance sheet", () => RunAsOf("Balance sheet", "report_balance_sheet", ShowBalance)));
        _commandItems.Add(ReportButton("GST and TDS", () => RunRange("GST and TDS", "report_tax_summary", ShowTax)));
        _commandItems.Add(ReportButton("GST pack", () => RunRange("GST pack", "report_gst_pack", ShowGstPack)));
        _commandItems.Add(ReportButton("Client statement", ShowStatement));
        _commandItems.Add(ReportButton("Client ledger", ShowLedger));
        _commandItems.Add(ReportButton("Fee position", () => Run("Fee position", "Current position", "project_fee_position", new { }, ShowFees)));
        _commandItems.Add(ReportButton("Export PDF", ExportReport));
        _commandItems.Add(ReportButton("Print", PrintReport));

        var page = new StackPanel { Spacing = 16, Margin = (Thickness)Application.Current.Resources["PageMargin"] };
        page.Children.Add(Flow.SheetMark("04", "Accounts", "Financial reports"));
        page.Children.Add(Flow.Heading("Financial reports"));
        page.Children.Add(Flow.Secondary("Amounts are calculated by the company file. Each client has a ledger. When a client has more than one project, choose the project to see that project's ledger. The total rolls into Accounts receivable."));
        page.Children.Add(_dates);
        page.Children.Add(_commands);
        page.Children.Add(_error);
        page.Children.Add(_heading);
        page.Children.Add(_period);
        page.Children.Add(_body);
        Content = Flow.Scroll(page);
        SizeChanged += (_, _) =>
        {
            Flow.Place(_dates, [_from, _to, _client, _project], 200, true);
            Flow.Place(_commands, _commandItems, 160, true);
        };
        _client.SelectionChanged += (_, _) => LoadProjects();
        RunAsOf("Trial balance", "report_trial_balance", ShowTrial);
        LoadClients();
    }

    private void LoadClients()
    {
        _ = _shell.Run("Loading clients�", () =>
        {
            var rows = _shell.Call("client_list", new { include_archived = false });
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                _client.Items.Clear();
                if (rows.ValueKind != JsonValueKind.Array)
                    return;
                foreach (var row in rows.EnumerateArray())
                {
                    if (Kit.Id(row) is not long id)
                        continue;
                    _client.Items.Add(new ComboBoxItem { Content = Kit.Str(row, "name"), Tag = id });
                }
                if (_client.Items.Count > 0 && _client.SelectedIndex < 0)
                    _client.SelectedIndex = 0;
                LoadProjects();
            });
        });
    }

    private void LoadProjects()
    {
        if (_client.SelectedItem is not ComboBoxItem item || item.Tag is not long clientId)
        {
            _project.Items.Clear();
            _project.Items.Add(new ComboBoxItem { Content = "All projects", Tag = 0L });
            _project.SelectedIndex = 0;
            return;
        }
        _ = _shell.Run("Loading projects�", () =>
        {
            var rows = _shell.Call("project_list", new { client_id = clientId, include_archived = false });
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                _project.Items.Clear();
                _project.Items.Add(new ComboBoxItem { Content = "All projects", Tag = 0L });
                if (rows.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in rows.EnumerateArray())
                    {
                        if (Kit.Id(row) is not long id)
                            continue;
                        _project.Items.Add(new ComboBoxItem { Content = Kit.Str(row, "name"), Tag = id });
                    }
                }
                _project.SelectedIndex = 0;
            });
        });
    }

    private void ShowStatement()
    {
        if (_client.SelectedItem is not ComboBoxItem item || item.Tag is not long clientId)
        {
            Fail("Choose a client.", "Client statement");
            return;
        }
        var to = _to.Text.Trim();
        if (to.Length == 0)
        {
            Fail("Enter the as-of date.");
            return;
        }
        Run("Client statement", $"As of {to}", "client_statement_report", new { client_id = clientId, as_of = to }, ShowClientStatement);
    }

    private void ShowLedger()
    {
        if (_client.SelectedItem is not ComboBoxItem item || item.Tag is not long clientId)
        {
            Fail("Choose a client.", "Client ledger");
            return;
        }
        long? projectId = null;
        if (_project.SelectedItem is ComboBoxItem project && project.Tag is long id && id != 0)
            projectId = id;
        var from = _from.Text.Trim();
        var to = _to.Text.Trim();
        if (from.Length == 0 || to.Length == 0)
        {
            Fail("Enter the period.");
            return;
        }
        Run("Client ledger", $"{from} to {to}", "party_ledger_report", new { client_id = clientId, project_id = projectId, from, to }, ShowPartyLedger);
    }

    private void ExportReport()
    {
        if (_blocks.Count == 0)
        {
            _shell.Problem("Prepare a report first.");
            return;
        }
        var pdf = ReportPaper.Pdf(_heading.Text, _period.Text, _blocks);
        var name = string.Join("_", _heading.Text.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        _ = _shell.Window.ShowPdf(pdf, name.Length == 0 ? "Report" : name);
    }

    private void PrintReport()
    {
        if (_blocks.Count == 0)
        {
            _shell.Problem("Prepare a report first.");
            return;
        }
        _ = _shell.Window.PrintPagesAsync(_heading.Text, ReportPaper.Pages(_heading.Text, _period.Text, _blocks));
    }

    private Button ReportButton(string label, Action action)
    {
        var button = Flow.Command(label, action);
        button.MinHeight = O.Ui.SuiteScale.ControlMinHeight;
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(button, label);
        return button;
    }

    private void RunAsOf(string title, string operation, Action<JsonElement> show)
    {
        var to = _to.Text.Trim();
        if (to.Length == 0)
        {
            Fail("Enter the as-of date.");
            return;
        }
        Run(title, $"As of {to}", operation, new { as_of = to }, show);
    }

    private void RunRange(string title, string operation, Action<JsonElement> show)
    {
        var from = _from.Text.Trim();
        var to = _to.Text.Trim();
        if (from.Length == 0 || to.Length == 0)
        {
            Fail("Enter both the start date and the end date.");
            return;
        }
        if (string.CompareOrdinal(from, to) > 0)
        {
            Fail("The start date is after the end date.");
            return;
        }
        Run(title, $"{from} to {to}", operation, new { from, to }, show);
    }

    private void Run(string title, string period, string operation, object args, Action<JsonElement> show)
    {
        if (_running)
            return;
        _running = true;
        _error.IsOpen = false;
        _heading.Text = title;
        _period.Text = period;
        _body.Children.Clear();
        _body.Children.Add(Flow.Secondary("Preparing the report�"));
        _ = _shell.Run("Preparing report�", () =>
        {
            var result = _shell.Call(operation, args);
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                _running = false;
                _body.Children.Clear();
                _blocks.Clear();
                show(result);
            });
        }).ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() =>
        {
            if (task.IsCompletedSuccessfully && task.Result)
                return;
            _running = false;
            if (_body.Children.Count == 1)
                _body.Children.Clear();
        }));
    }

    private void ShowTrial(JsonElement result)
    {
        var balanced = result.TryGetProperty("balanced", out var flag) && flag.ValueKind == JsonValueKind.True;
        _body.Children.Add(Flow.Secondary(balanced ? "Debits equal credits." : "Debits and credits do not match."));
        var rows = new List<string[]>();
        var count = 0;
        if (result.TryGetProperty("rows", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in list.EnumerateArray())
            {
                rows.Add([Account(row), Kind(Kit.Str(row, "kind")), Kit.Str(row, "debit_display"), Kit.Str(row, "credit_display")]);
                count++;
            }
        }
        rows.Add(["Total", "", Kit.Str(result, "total_debit"), Kit.Str(result, "total_credit")]);
        var note = balanced ? "Debits equal credits." : "Debits and credits do not match.";
        _blocks.Add(new ReportBlock { Heading = "Trial balance", Note = note, Headers = ["Account", "Kind", "Debit", "Credit"], Rows = rows });
        _body.Children.Add(Flow.Table("Trial balance", ["Account", "Kind", "Debit", "Credit"], rows, [false, false, true, true], rows.Count - 1));
        if (count == 0)
            _body.Children.Add(Flow.Secondary("No account balances on this date."));
    }

    private void ShowProfit(JsonElement result)
    {
        _body.Children.Add(SectionTable("Income", "income", result, "Total income", Kit.Str(result, "total_income")));
        _body.Children.Add(SectionTable("Expenses", "expenses", result, "Total expenses", Kit.Str(result, "total_expenses")));
        var profit = result.TryGetProperty("profit_paise", out var paise) && paise.TryGetInt64(out var value) ? value : 0;
        var label = profit < 0 ? "Loss" : profit > 0 ? "Profit" : "No profit or loss";
        _blocks.Add(new ReportBlock { Heading = label, Rows = [[label, Kit.Str(result, "profit_display")]], Headers = ["Result", "Amount"] });
        _body.Children.Add(Flow.Section(label));
        _body.Children.Add(new TextBlock { Text = Kit.Str(result, "profit_display"), Style = Flow.Look("MetricValueStyle") });
        AutomationProperties.SetName(_body.Children[^1], $"{label} {Kit.Str(result, "profit_display")}");
    }

    private void ShowBalance(JsonElement result)
    {
        var balanced = result.TryGetProperty("balanced", out var flag) && flag.ValueKind == JsonValueKind.True;
        _body.Children.Add(Flow.Secondary(balanced ? "Assets equal liabilities and equity." : "Assets do not equal liabilities and equity."));
        _body.Children.Add(SectionTable("Assets", "assets", result, "Total assets", Kit.Str(result, "total_assets")));
        _body.Children.Add(SectionTable("Liabilities", "liabilities", result, "", ""));
        _body.Children.Add(SectionTable("Equity", "equity", result, "", ""));
        _blocks.Add(new ReportBlock
        {
            Heading = "Total liabilities and equity",
            Note = balanced ? "Assets equal liabilities and equity." : "Assets do not equal liabilities and equity.",
            Headers = ["Item", "Amount"],
            Rows = [["Total liabilities and equity", Kit.Str(result, "total_liabilities_equity")]],
        });
        _body.Children.Add(Flow.Section("Total liabilities and equity"));
        var total = new TextBlock { Text = Kit.Str(result, "total_liabilities_equity"), Style = Flow.Look("MetricValueStyle") };
        AutomationProperties.SetName(total, $"Total liabilities and equity {Kit.Str(result, "total_liabilities_equity")}");
        _body.Children.Add(total);
    }

    private void ShowClientStatement(JsonElement result)
    {
        var client = Kit.Str(result, "client_name");
        _heading.Text = client.Length == 0 ? "Client statement" : $"Statement � {client}";
        _body.Children.Add(Flow.Secondary($"Balance due {Kit.Str(result, "balance_display")}"));
        var invoices = Rows(result, "invoices", row =>
        [
            Kit.Str(row, "date"), Kit.Str(row, "number"), Kit.Str(row, "project"), Kit.Str(row, "total_display"),
            Kit.Str(row, "received_display"), Kit.Str(row, "tds_display"), Kit.Str(row, "written_off_display"), Kit.Str(row, "balance_display"), Kit.Str(row, "bucket"),
        ]);
        invoices.Add(["", "", "Total", Kit.Str(result, "total_display"), Kit.Str(result, "received_display"), Kit.Str(result, "tds_display"), "", Kit.Str(result, "balance_display"), ""]);
        var headers = new[] { "Date", "Number", "Project", "Invoice", "Received", "TDS", "Written off", "Balance", "Age" };
        _blocks.Add(new ReportBlock { Heading = "Invoices", Headers = headers, Rows = invoices });
        _body.Children.Add(Flow.Section("Invoices"));
        _body.Children.Add(Flow.Table("Invoices", headers, invoices, [false, false, false, true, true, true, true, true, false], invoices.Count - 1));
        var receipts = Rows(result, "receipts", row =>
            [Kit.Str(row, "date"), Kit.Str(row, "number"), Kit.Str(row, "invoice"), Kit.Str(row, "amount_display"), Kit.Str(row, "tds_display"), Kit.Str(row, "section")]);
        _blocks.Add(new ReportBlock { Heading = "Receipts", Headers = ["Date", "Receipt", "Invoice", "Amount", "TDS", "Section"], Rows = receipts });
        _body.Children.Add(Flow.Section("Receipts"));
        if (receipts.Count == 0)
            _body.Children.Add(Flow.Secondary("No receipts on these invoices."));
        else
            _body.Children.Add(Flow.Table("Receipts", ["Date", "Receipt", "Invoice", "Amount", "TDS", "Section"], receipts, [false, false, false, true, true, false]));
        var ageing = Rows(result, "ageing", row => [Kit.Str(row, "bucket"), Kit.Str(row, "balance_display")]);
        _blocks.Add(new ReportBlock { Heading = "Ageing of the open balance", Headers = ["Bucket", "Balance"], Rows = ageing });
        _body.Children.Add(Flow.Section("Ageing of the open balance"));
        _body.Children.Add(Flow.Table("Ageing", ["Bucket", "Balance"], ageing, [false, true]));
    }

    private void ShowPartyLedger(JsonElement result)
    {
        var client = Kit.Str(result, "client_name");
        var project = Kit.Str(result, "project_name");
        _heading.Text = project.Length == 0 || project == "All projects" ? $"Ledger � {client}" : $"Ledger � {client} � {project}";
        _body.Children.Add(Flow.Secondary(Kit.Str(result, "note")));
        _body.Children.Add(Flow.Secondary($"Opening {Kit.Str(result, "opening_display")} � Closing {Kit.Str(result, "closing_display")}"));
        var rows = Rows(result, "lines", row =>
        [
            Kit.Str(row, "date"), Kit.Str(row, "particular"), Kit.Str(row, "project"),
            Kit.Str(row, "debit_display"), Kit.Str(row, "credit_display"), Kit.Str(row, "balance_display"),
        ]);
        rows.Insert(0, ["", "Opening balance", "", "", "", Kit.Str(result, "opening_display")]);
        rows.Add(["", "Closing balance", "", "", "", Kit.Str(result, "closing_display")]);
        var headers = new[] { "Date", "Particulars", "Project", "Debit", "Credit", "Balance" };
        _blocks.Add(new ReportBlock { Heading = _heading.Text, Note = Kit.Str(result, "note"), Headers = headers, Rows = rows });
        _body.Children.Add(Flow.Table("Ledger", headers, rows, [false, false, false, true, true, true], rows.Count - 1));
    }

    private void ShowFees(JsonElement result)
    {
        var rows = Rows(result, "rows", row =>
        [
            Kit.Str(row, "client"), Kit.Str(row, "project"), Consultancy.Label(Kit.Str(row, "consultancy")),
            Kit.Str(row, "fee_display"), Kit.Str(row, "billed_display"), Kit.Str(row, "received_display"),
            Kit.Str(row, "outstanding_display"), Kit.Str(row, "next_stage"), Kit.Str(row, "next_amount_display"),
        ]);
        rows.Add(["", "Total", "", Kit.Str(result, "fee_display"), Kit.Str(result, "billed_display"), Kit.Str(result, "received_display"), Kit.Str(result, "outstanding_display"), "", ""]);
        var headers = new[] { "Client", "Project", "Consultancy", "Agreed fee", "Billed", "Received", "Outstanding", "Next stage", "Stage amount" };
        _blocks.Add(new ReportBlock { Heading = "Fee position", Note = "The agreed fee is the latest issued agreement. Received includes TDS.", Headers = headers, Rows = rows });
        _body.Children.Add(Flow.Secondary("The agreed fee is the latest issued agreement. Received includes the amount paid and the TDS deducted."));
        _body.Children.Add(Flow.Table("Fee position", headers, rows, [false, false, false, true, true, true, true, false, true], rows.Count - 1));
    }

    private static List<string[]> Rows(JsonElement result, string property, Func<JsonElement, string[]> map)
    {
        var rows = new List<string[]>();
        if (result.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in list.EnumerateArray())
                rows.Add(map(row));
        }
        return rows;
    }

    private void ShowGstPack(JsonElement result)
    {
        ShowTax(result);
        _body.Children.Add(Lines("TDS deducted by clients", [Line("TDS deducted by clients", result, "tds_deducted")], -1));
        SupplyTable("Outward supplies", "supplies", result);
        SupplyTable("Credit notes", "credit_notes", result);
        var tds = Rows(result, "tds_lines", row =>
        [
            Kit.Str(row, "date"), Kit.Str(row, "receipt"), Kit.Str(row, "invoice"), Kit.Str(row, "client"),
            Kit.Str(row, "section"), Kit.Str(row, "received_display"), Kit.Str(row, "tds_display"),
        ]);
        var tdsHeaders = new[] { "Date", "Receipt", "Invoice", "Client", "Section", "Received", "TDS" };
        _blocks.Add(new ReportBlock
        {
            Heading = "TDS lines",
            Note = tds.Count == 0 ? "No client deducted TDS in this period." : "",
            Headers = tds.Count == 0 ? [] : tdsHeaders,
            Rows = tds,
        });
        _body.Children.Add(Flow.Section("TDS lines"));
        if (tds.Count == 0)
            _body.Children.Add(Flow.Secondary("No client deducted TDS in this period."));
        else
            _body.Children.Add(Flow.Table("TDS lines", tdsHeaders, tds, [false, false, false, false, false, true, true]));
    }

    private void SupplyTable(string title, string property, JsonElement result)
    {
        var rows = Rows(result, property, row =>
        [
            Kit.Str(row, "date"), Kit.Str(row, "number"), Kit.Str(row, "client"), Kit.Str(row, "gstin"), Kit.Str(row, "place"),
            Kit.Str(row, "taxable_display"), Kit.Str(row, "cgst_display"), Kit.Str(row, "sgst_display"), Kit.Str(row, "igst_display"), Kit.Str(row, "total_display"),
        ]);
        var headers = new[] { "Date", "Number", "Client", "GSTIN", "Place", "Taxable", "CGST", "SGST", "IGST", "Total" };
        _blocks.Add(new ReportBlock
        {
            Heading = title,
            Note = rows.Count == 0 ? "None in this period." : "",
            Headers = rows.Count == 0 ? [] : headers,
            Rows = rows,
        });
        _body.Children.Add(Flow.Section(title));
        if (rows.Count == 0)
            _body.Children.Add(Flow.Secondary("None in this period."));
        else
            _body.Children.Add(Flow.Table(title, headers, rows, [false, false, false, false, false, true, true, true, true, true]));
    }

    private void ShowTax(JsonElement result)
    {
        _body.Children.Add(Flow.Section("GST"));
        _body.Children.Add(Lines("GST", [
            Line("Output CGST", result, "output_cgst"),
            Line("Output SGST", result, "output_sgst"),
            Line("Output IGST", result, "output_igst"),
            Line("Input credit", result, "input_credit"),
            Line("Net GST payable", result, "net_gst_payable"),
            Line("Composition tax", result, "composition_tax"),
        ], 4));
        _body.Children.Add(Flow.Section("TDS"));
        _body.Children.Add(Lines("TDS", [
            Line("TDS receivable", result, "tds_receivable"),
            Line("TDS payable", result, "tds_payable"),
        ], -1));
    }

    private UIElement SectionTable(string title, string property, JsonElement result, string totalLabel, string total)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Flow.Section(title));
        var rows = new List<string[]>();
        var count = 0;
        if (result.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in list.EnumerateArray())
            {
                rows.Add([Kit.Str(row, "code"), Kit.Str(row, "name"), Kit.Str(row, "amount_display")]);
                count++;
            }
        }
        if (total.Length > 0)
            rows.Add(["", totalLabel, total]);
        if (rows.Count > 0)
            _blocks.Add(new ReportBlock { Heading = title, Headers = ["Code", "Account", "Amount"], Rows = rows });
        if (rows.Count == 0)
            panel.Children.Add(Flow.Secondary($"No {title.ToLowerInvariant()} lines in this report."));
        else
            panel.Children.Add(Flow.Table(title, ["Code", "Account", "Amount"], rows, [false, false, true], total.Length > 0 ? rows.Count - 1 : -1));
        if (count == 0 && total.Length > 0)
            panel.Children.Add(Flow.Secondary($"No {title.ToLowerInvariant()} lines. The total is the figure returned for this report."));
        return panel;
    }

    private UIElement Lines(string name, List<string[]> rows, int emphasizeFrom)
    {
        _blocks.Add(new ReportBlock { Heading = name, Headers = ["Description", "Amount"], Rows = rows });
        return Flow.Table(name, ["Description", "Amount"], rows, [false, true], emphasizeFrom);
    }

    private static string[] Line(string label, JsonElement result, string key) => [label, Kit.Str(result, key)];

    private static string Account(JsonElement row)
    {
        var code = Kit.Str(row, "code");
        var name = Kit.Str(row, "name");
        return code.Length == 0 ? name : $"{code}  {name}";
    }

    private static string Kind(string kind) => kind switch
    {
        "asset" => "Asset",
        "liability" => "Liability",
        "equity" => "Equity",
        "income" => "Income",
        "expense" => "Expense",
        _ => kind,
    };

    private void Fail(string message, string title = "Check the dates")
    {
        _error.Title = title;
        _error.Message = message;
        _error.IsOpen = true;
    }

    public void NewItem() { }
    public void Save() { }
    public void Print() => PrintReport();
    public void FocusSearch() => _from.Focus(FocusState.Programmatic);
}
