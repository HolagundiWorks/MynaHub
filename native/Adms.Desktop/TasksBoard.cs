using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Adms.Desktop;

/// <summary>Solo practice Kanban: Todo / Doing / Done, each card optionally tagged with a project.</summary>
internal sealed class TasksBoardPage : UserControl, ILiveRefresh
{
    private static readonly (string Key, string Label)[] Columns =
    [
        ("todo", "To do"),
        ("doing", "Doing"),
        ("done", "Done"),
    ];

    private readonly AppShell _shell;
    private readonly Dictionary<string, StackPanel> _columns = new();
    private readonly ComboBox _projectFilter = new() { Header = "Project tag", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _title = Flow.Input("Task");
    private readonly TextBox _notes = Flow.Input("Notes", true);
    private readonly TextBox _due = Flow.Input("Due (YYYY-MM-DD)");
    private readonly ComboBox _project = new() { Header = "Tag with project", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly InfoBar _notice = Flow.Notice();
    private readonly List<JsonElement> _projects = [];
    private long? _filterProject;

    public bool CanRefreshLive => true;
    public void RefreshLive() => Load();

    public TasksBoardPage(AppShell shell, bool widget = false)
    {
        _shell = shell;
        var gap = Hcw.Ui.SuiteScale.StackGap;
        var page = new StackPanel
        {
            Spacing = widget ? gap : Hcw.Ui.SuiteScale.SectionGap,
            Margin = widget ? new Thickness(0) : Hcw.Ui.SuiteScale.PageMargin,
        };
        if (!widget)
        {
            page.Children.Add(Flow.SheetMark("01", "Practice", "Tasks"));
            page.Children.Add(Flow.Heading("Tasks"));
            page.Children.Add(Flow.Secondary("A personal board for the practice. Tag a card with a project so it shows next to that job everywhere."));
        }
        page.Children.Add(_notice);

        if (shell.CanEdit)
        {
            var form = new StackPanel { Spacing = gap };
            if (!widget)
                form.Children.Add(Flow.Section("New task"));
            form.Children.Add(Flow.Pair(_title, _due));
            form.Children.Add(Flow.Pair(_project, _notes));
            var add = Flow.Command("Add task", Add, primary: true);
            form.Children.Add(Flow.Bar(add));
            page.Children.Add(form);
        }

        _projectFilter.SelectionChanged += (_, _) =>
        {
            _filterProject = (_projectFilter.SelectedItem as ComboBoxItem)?.Tag as long?;
            Load();
        };
        page.Children.Add(_projectFilter);

        var board = new Grid { ColumnSpacing = Hcw.Ui.SuiteScale.CardGap };
        for (var i = 0; i < Columns.Length; i++)
        {
            board.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var (key, label) = Columns[i];
            var stack = new StackPanel { Spacing = gap };
            _columns[key] = stack;
            var card = new Border
            {
                Style = (Style)Application.Current.Resources["SuiteCardStyle"],
                Child = new StackPanel
                {
                    Spacing = gap,
                    Children =
                    {
                        new TextBlock { Text = label, Style = Flow.Look("SectionTitleStyle") },
                        stack,
                    },
                },
            };
            Grid.SetColumn(card, i);
            board.Children.Add(card);
        }
        page.Children.Add(board);
        Content = new ScrollViewer
        {
            Content = page,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = widget ? 520 : double.PositiveInfinity,
        };
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
                _projects.Clear();
                _project.Items.Clear();
                _projectFilter.Items.Clear();
                _project.Items.Add(new ComboBoxItem { Content = "(No project)", Tag = null });
                _projectFilter.Items.Add(new ComboBoxItem { Content = "All projects", Tag = null });
                if (rows.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in rows.EnumerateArray())
                    {
                        if (Kit.Id(row) is not long id)
                            continue;
                        _projects.Add(row.Clone());
                        var label = ProjectLabel(row);
                        _project.Items.Add(new ComboBoxItem { Content = label, Tag = id });
                        _projectFilter.Items.Add(new ComboBoxItem { Content = label, Tag = id });
                    }
                }
                _project.SelectedIndex = 0;
                _projectFilter.SelectedIndex = 0;
            });
        });
    }

    private static string ProjectLabel(JsonElement row)
    {
        var code = Kit.Str(row, "code");
        var name = Kit.Str(row, "name");
        return string.IsNullOrEmpty(code) ? name : $"{code} — {name}";
    }

    private void Load()
    {
        _ = _shell.Run("Loading tasks…", () =>
        {
            var rows = _shell.Call("task_list", new { project_id = _filterProject });
            _shell.Window.DispatcherQueue.TryEnqueue(() => Paint(rows));
        });
    }

    private void Paint(JsonElement rows)
    {
        foreach (var col in _columns.Values)
            col.Children.Clear();
        if (rows.ValueKind != JsonValueKind.Array)
            return;
        foreach (var row in rows.EnumerateArray())
        {
            var status = Kit.Str(row, "status");
            if (!_columns.TryGetValue(status, out var col))
                continue;
            col.Children.Add(Card(row));
        }
    }

    private UIElement Card(JsonElement row)
    {
        var id = Kit.Id(row) ?? 0;
        var title = Kit.Str(row, "title");
        var project = Kit.Str(row, "project");
        var code = Kit.Str(row, "project_code");
        var due = Kit.Str(row, "due_date");
        var status = Kit.Str(row, "status");
        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(new TextBlock
        {
            Text = title,
            Style = Flow.Look("BodyStyle"),
            TextWrapping = TextWrapping.WrapWholeWords,
        });
        if (project.Length > 0)
        {
            var tag = string.IsNullOrEmpty(code) ? project : $"{code} · {project}";
            body.Children.Add(new Border
            {
                Style = (Style)Application.Current.Resources["SuiteChipStyle"],
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = tag, Style = Flow.Look("CaptionStyle"), VerticalAlignment = VerticalAlignment.Center },
            });
        }
        if (due.Length > 0)
            body.Children.Add(new TextBlock { Text = $"Due {due}", Style = Flow.Look("CaptionStyle") });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Hcw.Ui.SuiteScale.StackGap };
        foreach (var (key, label) in Columns)
        {
            if (key == status)
                continue;
            var move = new Button { Content = label, MinHeight = Hcw.Ui.SuiteScale.ControlMinHeight };
            move.Click += (_, _) => Move(id, key);
            actions.Children.Add(move);
        }
        if (_shell.CanEdit)
        {
            var del = new Button { Content = "Delete", MinHeight = Hcw.Ui.SuiteScale.ControlMinHeight };
            del.Click += (_, _) => Delete(id);
            actions.Children.Add(del);
        }
        body.Children.Add(actions);

        var card = new Border
        {
            Background = (Brush)Application.Current.Resources["AppBackgroundBrush"],
            CornerRadius = Hcw.Ui.SuiteScale.CornerControl,
            Padding = new Thickness(12),
            Child = body,
        };
        AutomationProperties.SetName(card, title);
        return card;
    }

    private void Add()
    {
        var title = _title.Text.Trim();
        if (title.Length == 0)
        {
            _notice.Title = "Enter a task title.";
            _notice.IsOpen = true;
            return;
        }
        var input = new
        {
            id = (long?)null,
            project_id = (_project.SelectedItem as ComboBoxItem)?.Tag as long?,
            title,
            notes = _notes.Text.Trim(),
            status = "todo",
            position = 0L,
            due_date = _due.Text.Trim(),
        };
        _ = _shell.Run("Adding task…", () => _shell.Call("task_save", new { input }))
            .ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (!task.Result)
                    return;
                _title.Text = "";
                _notes.Text = "";
                _due.Text = "";
                _project.SelectedIndex = 0;
                _notice.IsOpen = false;
                Load();
            }));
    }

    private void Move(long id, string status) =>
        _ = _shell.Run("Moving task…", () => _shell.Call("task_set_status", new { id, status, position = (long?)null }))
            .ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() => { if (task.Result) Load(); }));

    private void Delete(long id) =>
        _ = _shell.Run("Deleting task…", () => _shell.Call("task_delete", new { id }))
            .ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() => { if (task.Result) Load(); }));
}
