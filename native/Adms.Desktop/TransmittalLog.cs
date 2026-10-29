using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Adms.Desktop;

internal sealed class TransmittalLog : UserControl
{
    private readonly AppShell _shell;
    private readonly ComboBox _project = new() { Header = "Project", HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Choose a project" };
    private readonly TextBox _date = new() { Header = "Date sent", Text = Kit.Today(), PlaceholderText = "yyyy-MM-dd", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _recipient = new() { Header = "Sent to", PlaceholderText = "Person or office", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _purpose = new() { Header = "Purpose", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _medium = new() { Header = "Sent by", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _reference = new() { Header = "Reference", PlaceholderText = "TR-1", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _number = new() { Header = "Drawing number", PlaceholderText = "A-01", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _title = new() { Header = "Title", PlaceholderText = "Ground floor plan", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _revision = new() { Header = "Revision", PlaceholderText = "B", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _copies = new() { Header = "Copies", Text = "1", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _pending = new() { Spacing = 0 };
    private readonly StackPanel _log = new() { Spacing = 0 };
    private readonly List<PendingItem> _items = [];
    private readonly InfoBar _notice = Flow.Notice();

    private TransmittalLog(AppShell shell)
    {
        _shell = shell;
        foreach (var purpose in new[] { "For information", "For approval", "For tender", "For construction" })
            _purpose.Items.Add(new ComboBoxItem { Content = purpose, Tag = purpose });
        _purpose.SelectedIndex = 0;
        foreach (var medium in new[] { "Email", "Courier", "Hand" })
            _medium.Items.Add(new ComboBoxItem { Content = medium, Tag = medium });
        _medium.SelectedIndex = 0;
        var page = new StackPanel { Spacing = 12, Margin = (Thickness)Application.Current.Resources["PageMargin"] };
        page.Children.Add(Flow.SheetMark("02.03", "Documents", "Transmittals"));
        page.Children.Add(Flow.Heading("Transmittals"));
        page.Children.Add(Flow.Secondary("A log of drawings and documents sent on a project. Removing a line drops it from the log. It does not change an issued letter."));
        page.Children.Add(_notice);
        page.Children.Add(Flow.Pair(_project, _recipient));
        page.Children.Add(Flow.Pair(_date, _reference));
        page.Children.Add(Flow.Pair(_purpose, _medium));
        page.Children.Add(Flow.Section("Drawings in this transmittal"));
        page.Children.Add(Flow.Pair(_number, _title));
        page.Children.Add(Flow.Pair(_revision, _copies));
        var add = new Button { Content = "Add drawing" };
        add.Visibility = shell.CanEdit ? Visibility.Visible : Visibility.Collapsed;
        add.Click += (_, _) => AddDrawing();
        var record = new Button { Content = "Record transmittal", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        record.Visibility = shell.CanEdit ? Visibility.Visible : Visibility.Collapsed;
        record.Click += (_, _) => Record();
        page.Children.Add(Flow.Bar(add, record));
        page.Children.Add(_pending);
        page.Children.Add(Flow.Section("Log"));
        page.Children.Add(_log);
        Content = new ScrollViewer { Content = page };
        LoadProjects();
        LoadLog();
    }

    public static UIElement Page(AppShell shell) => new TransmittalLog(shell);

    private void LoadProjects()
    {
        _ = _shell.Run("Loading projects…", () =>
        {
            var rows = _shell.Call("project_list", new { client_id = (long?)null, include_archived = false });
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                _project.Items.Clear();
                if (rows.ValueKind != JsonValueKind.Array)
                    return;
                foreach (var row in rows.EnumerateArray())
                {
                    if (Kit.Id(row) is not long id)
                        continue;
                    var client = Kit.Str(row, "client_name");
                    var name = Kit.Str(row, "name");
                    _project.Items.Add(new ComboBoxItem { Content = client.Length == 0 ? name : $"{client} — {name}", Tag = id });
                }
                if (_project.Items.Count > 0)
                    _project.SelectedIndex = 0;
            });
        });
    }

    private void AddDrawing()
    {
        var title = _title.Text.Trim();
        if (title.Length == 0)
        {
            Flow.Fault(_title, _notice, "Enter the drawing title.");
            return;
        }
        if (!int.TryParse(_copies.Text.Trim(), out var copies) || copies < 1 || copies > 999)
        {
            Flow.Fault(_copies, _notice, "Enter the number of copies from 1 to 999.");
            return;
        }
        _items.Add(new PendingItem(_number.Text.Trim(), title, _revision.Text.Trim(), copies));
        _number.Text = "";
        _title.Text = "";
        _revision.Text = "";
        _copies.Text = "1";
        ShowPending();
    }

    private void ShowPending()
    {
        _pending.Children.Clear();
        if (_items.Count == 0)
        {
            _pending.Children.Add(Flow.Secondary("No drawings added yet."));
            return;
        }
        var widths = new GridLength[] { new(120), new(2, GridUnitType.Star), new(80), new(70), new(90) };
        _pending.Children.Add(Flow.Header(["Number", "Title", "Revision", "Copies", ""], widths));
        for (var index = 0; index < _items.Count; index++)
        {
            var item = _items[index];
            var line = Flow.Columns([item.Number, item.Title, item.Revision, item.Copies.ToString(), ""], widths);
            var at = index;
            if (line.Children.Count > 0 && line.Children[line.Children.Count - 1] is Border cell)
            {
                var remove = new Button { Content = "Remove", MinWidth = 72 };
                remove.Click += (_, _) =>
                {
                    _items.RemoveAt(at);
                    ShowPending();
                };
                cell.Child = remove;
            }
            _pending.Children.Add(line);
        }
    }

    private void Record()
    {
        if (_project.SelectedItem is not ComboBoxItem item || item.Tag is not long projectId)
        {
            _shell.Problem("Choose a project.");
            return;
        }
        if (_items.Count == 0)
        {
            _shell.Problem("Add at least one drawing.");
            return;
        }
        var sentDate = _date.Text.Trim();
        var recipient = _recipient.Text.Trim();
        var purpose = (_purpose.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        var medium = (_medium.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        var reference = _reference.Text.Trim();
        var items = _items.Select(row => new { drawing_number = row.Number, title = row.Title, revision = row.Revision, copies = row.Copies }).ToArray();
        _ = _shell.Run("Recording the transmittal…", () => _shell.Call("transmittal_record", new { project_id = projectId, sent_date = sentDate, recipient, purpose, medium, reference, items }))
            .ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (!task.Result)
                    return;
                _items.Clear();
                _recipient.Text = "";
                _reference.Text = "";
                ShowPending();
                _notice.Severity = InfoBarSeverity.Success;
                _notice.Message = "The transmittal is in the log.";
                _notice.IsOpen = true;
                LoadLog();
            }));
    }

    private void LoadLog()
    {
        _ = _shell.Run("Loading transmittals…", () =>
        {
            var rows = _shell.Call("transmittal_list", new { project_id = (long?)null });
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                _log.Children.Clear();
                if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() == 0)
                {
                    _log.Children.Add(Flow.Secondary("No transmittals recorded yet."));
                    return;
                }
                foreach (var row in rows.EnumerateArray())
                {
                    var drawings = Drawings(row);
                    var acknowledged = Kit.Str(row, "acknowledged") is "true" or "True";
                    var block = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 12) };
                    block.Children.Add(new TextBlock
                    {
                        Text = $"{Kit.Str(row, "sent_date")}  {Kit.Str(row, "client")} — {Kit.Str(row, "project")}  {Kit.Str(row, "reference")}",
                        TextWrapping = TextWrapping.Wrap,
                    });
                    block.Children.Add(Flow.Secondary($"{Kit.Str(row, "recipient")} · {Kit.Str(row, "purpose")} · {Kit.Str(row, "medium")} · {(acknowledged ? "Acknowledged" : "Waiting for acknowledgement")}"));
                    block.Children.Add(new TextBlock { Text = drawings, TextWrapping = TextWrapping.Wrap });
                    if (_shell.CanEdit && Kit.Id(row) is long id)
                    {
                        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                        if (!acknowledged)
                        {
                            var ack = new Button { Content = "Mark acknowledged" };
                            ack.Click += (_, _) => Acknowledge(id);
                            buttons.Children.Add(ack);
                        }
                        var remove = new Button { Content = "Remove" };
                        remove.Click += (_, _) => Remove(id);
                        buttons.Children.Add(remove);
                        block.Children.Add(buttons);
                    }
                    _log.Children.Add(block);
                }
            });
        });
    }

    private static string Drawings(JsonElement row)
    {
        if (!row.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return "";
        var lines = new List<string>();
        foreach (var item in items.EnumerateArray())
        {
            var number = Kit.Str(item, "drawing_number");
            var title = Kit.Str(item, "title");
            var revision = Kit.Str(item, "revision");
            var copies = Kit.Str(item, "copies");
            var name = number.Length == 0 ? title : $"{number} {title}";
            if (revision.Length > 0)
                name += $" rev {revision}";
            name += $" · {copies} copies";
            lines.Add(name);
        }
        return string.Join("\n", lines);
    }

    private void Acknowledge(long id)
    {
        _ = _shell.Run("Recording the acknowledgement…", () => _shell.Call("transmittal_acknowledge", new { id }))
            .ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() => { if (task.Result) LoadLog(); }));
    }

    private void Remove(long id)
    {
        _ = _shell.Run("Removing the transmittal…", () => _shell.Call("transmittal_cancel", new { id }))
            .ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() => { if (task.Result) LoadLog(); }));
    }

    private sealed record PendingItem(string Number, string Title, string Revision, int Copies);
}
