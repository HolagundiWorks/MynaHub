using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Adms.Desktop;

/// <summary>Consultancy payouts / subcontracts for a freelance practice — not staff salaries.</summary>
internal sealed class SubcontractsPage : UserControl, ILiveRefresh
{
    private static readonly (string Value, string Label)[] Statuses =
    [
        ("quoted", "Quoted"),
        ("agreed", "Agreed"),
        ("paid", "Paid"),
        ("cancelled", "Cancelled"),
    ];

    private readonly AppShell _shell;
    private readonly StackPanel _list = new() { Spacing = Hcw.Ui.SuiteScale.StackGap };
    private readonly ComboBox _project = new() { Header = "Project", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _consultant = Flow.Input("Consultant");
    private readonly TextBox _work = Flow.Input("Work");
    private readonly TextBox _amount = Flow.Input("Amount (₹)");
    private readonly ComboBox _status = new() { Header = "Status", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _due = Flow.Input("Due (YYYY-MM-DD)");
    private readonly TextBox _paid = Flow.Input("Paid on (YYYY-MM-DD)");
    private readonly TextBox _notes = Flow.Input("Notes", true);
    private readonly InfoBar _notice = Flow.Notice();
    private readonly ComboBox _filter = new() { Header = "Show project", HorizontalAlignment = HorizontalAlignment.Stretch };
    private long? _filterProject;
    private long? _editing;

    public bool CanRefreshLive => true;
    public void RefreshLive() => Load();

    public SubcontractsPage(AppShell shell)
    {
        _shell = shell;
        foreach (var (value, label) in Statuses)
            _status.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        _status.SelectedIndex = 0;

        var page = new StackPanel
        {
            Spacing = Hcw.Ui.SuiteScale.SectionGap,
            Margin = Hcw.Ui.SuiteScale.PageMargin,
        };
        page.Children.Add(Flow.SheetMark("03", "Billing", "Subcontracts"));
        page.Children.Add(Flow.Heading("Consultancy payouts"));
        page.Children.Add(Flow.Secondary("Money owed to other consultants on a project. This is a schedule of work and pay — not a salary run."));
        page.Children.Add(_notice);

        if (shell.CanEdit)
        {
            var form = new StackPanel { Spacing = Hcw.Ui.SuiteScale.StackGap };
            form.Children.Add(Flow.Section("Record"));
            form.Children.Add(Flow.Pair(_project, _consultant));
            form.Children.Add(Flow.Pair(_work, _amount));
            form.Children.Add(Flow.Pair(_status, _due));
            form.Children.Add(Flow.Pair(_paid, _notes));
            var save = Flow.Command("Save", Save, primary: true);
            var clear = Flow.Command("New record", Reset);
            form.Children.Add(Flow.Bar(save, clear));
            page.Children.Add(form);
        }

        _filter.SelectionChanged += (_, _) =>
        {
            _filterProject = (_filter.SelectedItem as ComboBoxItem)?.Tag as long?;
            Load();
        };
        page.Children.Add(_filter);
        page.Children.Add(Flow.Section("Schedule"));
        page.Children.Add(_list);
        Content = new ScrollViewer { Content = page };
        LoadProjects();
        Load();
    }

    private void LoadProjects()
    {
        _ = _shell.Run("Loading projects…", () =>
        {
            var rows = _shell.Call("project_list", new { client_id = (long?)null, include_archived = false });
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                _project.Items.Clear();
                _filter.Items.Clear();
                _filter.Items.Add(new ComboBoxItem { Content = "All projects", Tag = null });
                if (rows.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in rows.EnumerateArray())
                    {
                        if (Kit.Id(row) is not long id)
                            continue;
                        var code = Kit.Str(row, "code");
                        var name = Kit.Str(row, "name");
                        var label = string.IsNullOrEmpty(code) ? name : $"{code} — {name}";
                        _project.Items.Add(new ComboBoxItem { Content = label, Tag = id });
                        _filter.Items.Add(new ComboBoxItem { Content = label, Tag = id });
                    }
                }
                if (_project.Items.Count > 0)
                    _project.SelectedIndex = 0;
                _filter.SelectedIndex = 0;
            });
        });
    }

    private void Load()
    {
        _ = _shell.Run("Loading subcontracts…", () =>
        {
            var rows = _shell.Call("subcontract_list", new { project_id = _filterProject });
            _shell.Window.DispatcherQueue.TryEnqueue(() => Paint(rows));
        });
    }

    private void Paint(JsonElement rows)
    {
        _list.Children.Clear();
        if (rows.ValueKind != JsonValueKind.Array)
            return;
        foreach (var row in rows.EnumerateArray())
        {
            var line = new Grid { ColumnSpacing = Hcw.Ui.SuiteScale.FieldGap };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var project = Kit.Str(row, "project_code");
            if (project.Length == 0)
                project = Kit.Str(row, "project");
            var left = new StackPanel { Spacing = 2 };
            left.Children.Add(new TextBlock { Text = Kit.Str(row, "consultant"), Style = Flow.Look("BodyStyle") });
            left.Children.Add(new TextBlock { Text = project, Style = Flow.Look("CaptionStyle") });
            line.Children.Add(left);
            var mid = new TextBlock
            {
                Text = Kit.Str(row, "work"),
                Style = Flow.Look("SecondaryStyle"),
                TextWrapping = TextWrapping.WrapWholeWords,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(mid, 1);
            line.Children.Add(mid);
            var amount = new TextBlock
            {
                Text = "₹ " + Kit.Str(row, "amount_display"),
                Style = Flow.Look("BodyStyle"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(amount, 2);
            line.Children.Add(amount);
            var status = new TextBlock
            {
                Text = Label(Kit.Str(row, "status")),
                Style = Flow.Look("CaptionStyle"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(status, 3);
            line.Children.Add(status);
            if (_shell.CanEdit && Kit.Id(row) is long id)
            {
                var edit = new Button { Content = "Edit", MinHeight = Hcw.Ui.SuiteScale.ControlMinHeight };
                edit.Click += (_, _) => Fill(row);
                Grid.SetColumn(edit, 4);
                line.Children.Add(edit);
            }
            _list.Children.Add(new Border
            {
                Style = (Style)Application.Current.Resources["SuiteCardStyle"],
                Padding = new Thickness(16, 12, 16, 12),
                Child = line,
            });
        }
        if (_list.Children.Count == 0)
            _list.Children.Add(Flow.Secondary("No consultancy payouts yet."));
    }

    private static string Label(string status) => Statuses.FirstOrDefault(s => s.Value == status).Label ?? status;

    private void Fill(JsonElement row)
    {
        _editing = Kit.Id(row);
        _consultant.Text = Kit.Str(row, "consultant");
        _work.Text = Kit.Str(row, "work");
        if (long.TryParse(Kit.Str(row, "amount_paise"), out var paise))
            _amount.Text = (paise / 100.0).ToString("0.##");
        else
            _amount.Text = Kit.Str(row, "amount_display");
        _due.Text = Kit.Str(row, "due_date");
        _paid.Text = Kit.Str(row, "paid_date");
        _notes.Text = Kit.Str(row, "notes");
        var projectId = Desk.Id(row, "project_id");
        foreach (ComboBoxItem item in _project.Items)
        {
            if (item.Tag is long id && id == projectId)
            {
                _project.SelectedItem = item;
                break;
            }
        }
        var status = Kit.Str(row, "status");
        foreach (ComboBoxItem item in _status.Items)
        {
            if (item.Tag as string == status)
            {
                _status.SelectedItem = item;
                break;
            }
        }
    }

    private void Reset()
    {
        _editing = null;
        _consultant.Text = "";
        _work.Text = "";
        _amount.Text = "";
        _due.Text = "";
        _paid.Text = "";
        _notes.Text = "";
        _status.SelectedIndex = 0;
        if (_project.Items.Count > 0)
            _project.SelectedIndex = 0;
        _notice.IsOpen = false;
    }

    private void Save()
    {
        if ((_project.SelectedItem as ComboBoxItem)?.Tag is not long projectId)
        {
            _notice.Title = "Choose a project.";
            _notice.IsOpen = true;
            return;
        }
        if (_consultant.Text.Trim().Length == 0)
        {
            _notice.Title = "Enter the consultant's name.";
            _notice.IsOpen = true;
            return;
        }
        if (!decimal.TryParse(_amount.Text.Trim(), out var rupees) || rupees < 0)
        {
            _notice.Title = "Enter the amount in rupees.";
            _notice.IsOpen = true;
            return;
        }
        var input = new
        {
            id = _editing,
            project_id = projectId,
            consultant = _consultant.Text.Trim(),
            work = _work.Text.Trim(),
            amount_paise = (long)Math.Round(rupees * 100m),
            status = (_status.SelectedItem as ComboBoxItem)?.Tag as string ?? "quoted",
            due_date = _due.Text.Trim(),
            paid_date = _paid.Text.Trim(),
            notes = _notes.Text.Trim(),
        };
        _ = _shell.Run("Saving subcontract…", () => _shell.Call("subcontract_save", new { input }))
            .ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (!task.Result)
                    return;
                Reset();
                Load();
            }));
    }
}
