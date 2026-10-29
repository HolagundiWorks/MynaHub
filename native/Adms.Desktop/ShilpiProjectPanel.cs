using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Adms.Desktop;

/// <summary>
/// A project's figures from its ShilpiDB file: running billing, the estimate, tasks, quantities and purchase orders. They are read from
/// the file every time the project is opened and are never copied into the company file. The file is chosen here; it is only read.
/// </summary>
internal sealed class ShilpiProjectPanel : StackPanel
{
    private readonly AppShell _shell;
    private readonly TextBlock _file = Flow.Secondary("No ShilpiDB file chosen for this project.");
    private readonly Button _choose = new() { Content = "Choose ShilpiDB file" };
    private readonly Button _clear = new() { Content = "Unlink" };
    private readonly TextBox _typed = new() { Header = "Or paste the full path of the file", PlaceholderText = "Full path to the .vdb file", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _use = new() { Content = "Use this path" };
    private readonly Button _reload = new() { Content = "Reload" };
    private readonly InfoBar _problem = new() { IsOpen = false, IsClosable = false, Severity = InfoBarSeverity.Error };
    private readonly StackPanel _figures = new() { Spacing = 8 };
    private long? _project;

    public ShilpiProjectPanel(AppShell shell)
    {
        _shell = shell;
        Spacing = 8;
        Children.Add(Flow.Section("ShilpiDB project data"));
        Children.Add(Flow.Secondary("Running billing, the estimate, tasks, quantities and purchase orders are read from the project's ShilpiDB file. Nothing here is typed in or kept in the company file."));
        Children.Add(_file);
        AutomationProperties.SetName(_choose, "Choose the ShilpiDB file for this project");
        _choose.Click += async (_, _) => await Choose();
        _clear.Click += (_, _) => Link("");
        _reload.Click += (_, _) => Load();
        _use.Click += (_, _) =>
        {
            if (_typed.Text.Trim().Trim('"').Length > 0)
                Link(_typed.Text.Trim().Trim('"'));
        };
        Children.Add(Flow.Bar(_choose, _reload, _clear));
        Children.Add(_typed);
        Children.Add(_use);
        Children.Add(_problem);
        Children.Add(_figures);
    }

    /// <summary>Shows the data of this project; a null id (a project not yet saved) hides the panel.</summary>
    public void Show(long? project)
    {
        _project = project;
        Visibility = project == null ? Visibility.Collapsed : Visibility.Visible;
        _choose.IsEnabled = _clear.IsEnabled = _use.IsEnabled = _typed.IsEnabled = _shell.CanEdit;
        _typed.Text = "";
        _figures.Children.Clear();
        _problem.IsOpen = false;
        _file.Text = "No ShilpiDB file chosen for this project.";
        _reload.Visibility = _clear.Visibility = Visibility.Collapsed;
        if (project != null)
            Load();
    }

    private async Task Choose()
    {
        string? path;
        try { path = await FileDialogs.PickFile(_shell.Window.Handle, ".vdb", ".shilpi", ".shilpidb"); }
        catch (Exception e) { Problem("The file chooser could not open: " + e.Message); return; }
        if (!string.IsNullOrEmpty(path))
            Link(path);
    }

    private void Link(string path)
    {
        if (_project is not { } id)
            return;
        _ = _shell.Run("Linking the ShilpiDB file…", () =>
        {
            try
            {
                _shell.Call("project_shilpi_set", new { project_id = id, path });
            }
            catch (Exception e)
            {
                _shell.Window.DispatcherQueue.TryEnqueue(() => Problem(e.Message));
                return;
            }
            _shell.Window.DispatcherQueue.TryEnqueue(Load);
        });
    }

    private void Problem(string message)
    {
        _problem.Message = message;
        _problem.IsOpen = true;
    }

    private void Load()
    {
        if (_project is not { } id)
            return;
        _ = _shell.Run("Reading the ShilpiDB file…", () =>
        {
            var data = _shell.Call("project_shilpi_read", new { project_id = id });
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (_project == id)
                    Paint(data);
            });
        });
    }

    private static string Rupees(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? Flow.Money((long)Math.Round(v.GetDouble() * 100)) : "";

    private static string Number(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble().ToString("0.##") : "";

    private static string Word(string value) => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private void Paint(JsonElement data)
    {
        _figures.Children.Clear();
        _problem.IsOpen = false;
        if (data.ValueKind != JsonValueKind.Object || !(data.TryGetProperty("linked", out var linked) && linked.GetBoolean()))
        {
            _file.Text = "No ShilpiDB file chosen for this project.";
            _reload.Visibility = _clear.Visibility = Visibility.Collapsed;
            return;
        }
        _file.Text = Kit.Str(data, "path");
        _reload.Visibility = Visibility.Visible;
        _clear.Visibility = _shell.CanEdit ? Visibility.Visible : Visibility.Collapsed;
        if (data.TryGetProperty("problem", out var problem) && problem.ValueKind == JsonValueKind.String)
        {
            Problem(problem.GetString() ?? "");
            return;
        }
        var t = data.GetProperty("totals");
        _figures.Children.Add(Flow.Secondary(
            $"Estimate {Rupees(t, "estimate")} · Billing {Rupees(t, "billing")} · Billed {Rupees(t, "billed")} · Paid {Rupees(t, "paid")} · Purchase orders {Rupees(t, "orders")} · {Number(t, "open_tasks")} open tasks"));
        Add(data, "billing", "Running billing", ["Stage", "Description", "Share %", "Amount", "Status", "Date"], [false, false, true, true, false, false],
            r => [Kit.Str(r, "stage"), Kit.Str(r, "description"), Number(r, "percent"), Rupees(r, "amount"), Word(Kit.Str(r, "status")), Kit.Str(r, "date")]);
        Add(data, "estimate", "Estimate", ["Code", "Description", "Unit", "Quantity", "Rate", "Amount"], [false, false, false, true, true, true],
            r => [Kit.Str(r, "code"), Kit.Str(r, "description"), Kit.Str(r, "unit"), Number(r, "quantity"), Rupees(r, "rate"), Rupees(r, "amount")]);
        Add(data, "tasks", "Tasks", ["Task", "Assigned to", "Due", "Status", "Done %"], [false, false, false, false, true],
            r => [Kit.Str(r, "title"), Kit.Str(r, "assignee"), Kit.Str(r, "due"), Word(Kit.Str(r, "status")), Number(r, "percent")]);
        Add(data, "quantities", "Quantities", ["Item", "Description", "Unit", "Quantity", "Measured from"], [false, false, false, true, false],
            r => [Kit.Str(r, "item"), Kit.Str(r, "description"), Kit.Str(r, "unit"), Number(r, "quantity"), Kit.Str(r, "source")]);
        Add(data, "orders", "Purchase orders", ["Number", "Vendor", "Date", "Description", "Amount", "Status"], [false, false, false, false, true, false],
            r => [Kit.Str(r, "number"), Kit.Str(r, "vendor"), Kit.Str(r, "date"), Kit.Str(r, "description"), Rupees(r, "amount"), Word(Kit.Str(r, "status"))]);
    }

    private void Add(JsonElement data, string key, string title, string[] headers, bool[] alignEnd, Func<JsonElement, string[]> cells)
    {
        _figures.Children.Add(Flow.Section(title));
        var rows = new List<string[]>();
        if (data.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var row in list.EnumerateArray())
                rows.Add(cells(row));
        if (rows.Count == 0)
            _figures.Children.Add(Flow.Secondary("None in this file."));
        else
            _figures.Children.Add(Flow.Table(title, headers, rows, alignEnd));
    }
}
