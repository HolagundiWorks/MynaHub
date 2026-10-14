using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Adms.Desktop;

internal sealed class ReportsPage : UserControl, IPageCommands
{
    private readonly AppShell _shell;
    private readonly TextBox _from = new() { Header = "From", Text = Kit.YearStart(), MaxWidth = 180, PlaceholderText = "yyyy-MM-dd" };
    private readonly TextBox _to = new() { Header = "To", Text = Kit.Today(), MaxWidth = 180, PlaceholderText = "yyyy-MM-dd" };
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
        _commandItems.Add(ReportButton("Export PDF", ExportReport));
        _commandItems.Add(ReportButton("Print", PrintReport));

        var page = new StackPanel { Spacing = 16, Margin = (Thickness)Application.Current.Resources["PageMargin"] };
        page.Children.Add(Flow.SheetMark("04", "Accounts", "Financial reports"));
        page.Children.Add(Flow.Heading("Financial reports"));
        page.Children.Add(Flow.Secondary("Amounts are calculated by the company file for the period you choose."));
        page.Children.Add(_dates);
        page.Children.Add(_commands);
        page.Children.Add(_error);
        page.Children.Add(_heading);
        page.Children.Add(_period);
        page.Children.Add(_body);
        Content = new ScrollViewer { Content = page };
        SizeChanged += (_, _) =>
        {
            Flow.Place(_dates, [_from, _to], 200, false);
            Flow.Place(_commands, _commandItems, 160, false);
        };
        RunAsOf("Trial balance", "report_trial_balance", ShowTrial);
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
        button.MinHeight = 32;
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
        _body.Children.Add(Flow.Secondary("Preparing the report…"));
        _ = _shell.Run("Preparing report…", () =>
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

    private void Fail(string message)
    {
        _error.Title = "Check the dates";
        _error.Message = message;
        _error.IsOpen = true;
    }

    public void NewItem() { }
    public void Save() { }
    public void Print() => PrintReport();
    public void FocusSearch() => _from.Focus(FocusState.Programmatic);
}
