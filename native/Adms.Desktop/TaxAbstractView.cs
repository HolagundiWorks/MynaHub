using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Adms.Desktop;

/// <summary>
/// The GST and TDS abstract for a period, shown on the tax payments page: what was owed at the start, charged, paid to the
/// government, and still owed, for each tax head, the TDS clients deducted by section, and the payments made. The same figures
/// can be saved as a CSV for a spreadsheet or the accountant. It reads posted records and changes nothing.
/// </summary>
internal static class TaxAbstractView
{
    private static readonly GridLength[] HeadWidths = [new(1.6, GridUnitType.Star), new(1, GridUnitType.Star), new(1, GridUnitType.Star), new(1, GridUnitType.Star), new(1, GridUnitType.Star)];
    private static readonly bool[] HeadMoney = [false, true, true, true, true];
    private static readonly GridLength[] SectionWidths = [new(1.6, GridUnitType.Star), new(70), new(1.2, GridUnitType.Star), new(1.2, GridUnitType.Star)];
    private static readonly bool[] SectionMoney = [false, true, true, true];
    private static readonly GridLength[] PaymentWidths = [new(96), new(1.4, GridUnitType.Star), new(1, GridUnitType.Star), new(1.2, GridUnitType.Star)];
    private static readonly bool[] PaymentMoney = [false, false, true, false];

    public static void Add(AppShell shell, Book book)
    {
        var from = Flow.Input("From");
        from.Text = Kit.YearStart();
        var to = Flow.Input("To");
        to.Text = Kit.Today();
        var result = new StackPanel { Spacing = 8 };
        var note = Flow.Secondary("Choose a period, then show the abstract. It reads posted journals and receipts and changes nothing.");
        var show = new Button { Content = "Show abstract" };
        var export = new Button { Content = "Export CSV" };
        AutomationProperties.SetName(show, "Show GST and TDS abstract");
        AutomationProperties.SetName(export, "Export GST and TDS abstract as CSV");
        show.Click += (_, _) => Show(shell, from.Text.Trim(), to.Text.Trim(), result);
        export.Click += async (_, _) => await Export(shell, from.Text.Trim(), to.Text.Trim());
        book.Form.Children.Add(Flow.Section("GST and TDS abstract"));
        book.Form.Children.Add(note);
        book.Form.Children.Add(Flow.Pair(from, to));
        book.Form.Children.Add(Flow.Bar(show, export));
        book.Form.Children.Add(result);
    }

    private static string? Check(string from, string to)
    {
        if (from.Length == 0 || to.Length == 0)
            return "Enter both the start date and the end date.";
        if (string.CompareOrdinal(from, to) > 0)
            return "The start date is after the end date.";
        return null;
    }

    private static void Show(AppShell shell, string from, string to, StackPanel host)
    {
        if (Check(from, to) is { } problem)
        {
            shell.Problem(problem);
            return;
        }
        _ = shell.Run("Preparing the abstract…", () =>
        {
            var abstractRows = shell.Call("report_tax_abstract", new { from, to });
            shell.Window.DispatcherQueue.TryEnqueue(() => Paint(host, abstractRows));
        });
    }

    private static async Task Export(AppShell shell, string from, string to)
    {
        if (Check(from, to) is { } problem)
        {
            shell.Problem(problem);
            return;
        }
        var csv = "";
        var ok = await shell.Run("Preparing the CSV…", () =>
        {
            var text = shell.Call("report_tax_abstract_csv", new { from, to });
            csv = text.ValueKind == JsonValueKind.String ? text.GetString() ?? "" : "";
        });
        if (!ok || csv.Length == 0)
            return;
        var file = await FileDialogs.PickCsvDestination(shell.Window.Handle, $"GST-TDS-abstract-{from}-to-{to}");
        if (file == null)
            return;
        try
        {
            // a byte-order mark so Excel reads rupee amounts and Indian-language names correctly
            await File.WriteAllTextAsync(file.Path, csv, new UTF8Encoding(true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            shell.Problem("The file could not be saved. Choose another folder, or close it if it is open in another program.");
        }
    }

    private static long Paise(JsonElement row, string key) =>
        row.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    private static string Money(long paise) => paise == 0 ? "0.00" : Flow.Money(paise).Replace("?", "").Trim();

    private static IEnumerable<JsonElement> List(JsonElement parent, string key) =>
        parent.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Select(r => r.Clone()) : [];

    private static string[] HeadCells(JsonElement row) =>
        [Kit.Str(row, "head"), Money(Paise(row, "opening")), Money(Paise(row, "charged")), Money(Paise(row, "paid")), Money(Paise(row, "closing"))];

    private static void Heads(StackPanel host, string title, string[] labels, IEnumerable<JsonElement> rows, JsonElement? total = null)
    {
        host.Children.Add(Flow.Section(title));
        var list = rows.ToList();
        if (list.Count == 0)
        {
            host.Children.Add(Flow.Secondary("No figures in or before this period."));
            return;
        }
        host.Children.Add(Flow.Header(["Head", labels[0], labels[1], labels[2], labels[3]], HeadWidths, HeadMoney));
        foreach (var row in list)
            host.Children.Add(Flow.Columns(HeadCells(row), HeadWidths, HeadMoney));
        if (total is JsonElement t)
            host.Children.Add(Flow.Columns(HeadCells(t), HeadWidths, HeadMoney, emphasize: true));
    }

    private static void Paint(StackPanel host, JsonElement a)
    {
        host.Children.Clear();
        host.Children.Add(Flow.Secondary($"{Kit.Str(a, "from")} to {Kit.Str(a, "to")}. Amounts in rupees."));
        var gst = List(a, "gst").ToList();
        Heads(host, "GST", ["Payable at start", "Charged", "Paid", "Payable at end"], gst, gst.Count > 0 && a.TryGetProperty("gst_total", out var total) ? total.Clone() : null);
        if (a.TryGetProperty("input_credit", out var credit))
            Heads(host, "Input tax credit", ["Available at start", "Added", "Used", "Available at end"], [credit.Clone()]);

        host.Children.Add(Flow.Section("TDS deducted by clients"));
        host.Children.Add(Flow.Secondary("Credit the firm claims for tax its clients deducted from fees."));
        var sections = List(a, "tds_by_clients").ToList();
        if (sections.Count == 0)
            host.Children.Add(Flow.Secondary("No TDS was deducted on receipts in this period."));
        else
        {
            host.Children.Add(Flow.Header(["Section", "Receipts", "Received", "TDS deducted"], SectionWidths, SectionMoney));
            foreach (var row in sections)
                host.Children.Add(Flow.Columns([Kit.Str(row, "section"), Kit.Str(row, "receipts"), Money(Paise(row, "received")), Money(Paise(row, "deducted"))], SectionWidths, SectionMoney));
            if (a.TryGetProperty("tds_by_clients_total", out var sum))
                host.Children.Add(Flow.Columns(["Total", Kit.Str(sum, "receipts"), Money(Paise(sum, "received")), Money(Paise(sum, "deducted"))], SectionWidths, SectionMoney, emphasize: true));
        }

        if (a.TryGetProperty("tds_payable", out var payable))
            Heads(host, "TDS payable by the firm", ["Payable at start", "Deducted", "Paid", "Payable at end"], [payable.Clone()]);
        var other = List(a, "other").ToList();
        if (other.Count > 0)
            Heads(host, "Other statutory dues", ["Payable at start", "Charged", "Paid", "Payable at end"], other);

        host.Children.Add(Flow.Section("Tax payments in this period"));
        var payments = List(a, "payments").ToList();
        if (payments.Count == 0)
            host.Children.Add(Flow.Secondary("No tax payments were recorded in this period."));
        else
        {
            host.Children.Add(Flow.Header(["Date", "Tax", "Amount", "Paid via"], PaymentWidths, PaymentMoney));
            foreach (var row in payments)
            {
                var reversed = row.TryGetProperty("reversed", out var r) && r.ValueKind == JsonValueKind.True;
                host.Children.Add(Flow.Columns([Kit.Str(row, "date"), Kit.Str(row, "head") + (reversed ? " (reversed)" : ""), Money(Paise(row, "amount")), Kit.Str(row, "via")], PaymentWidths, PaymentMoney));
            }
        }
    }
}
