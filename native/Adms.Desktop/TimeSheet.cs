using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Adms.Desktop;

internal sealed class TimeSheet : UserControl
{
    private readonly AppShell _shell;
    private readonly ComboBox _project = new() { Header = "Project", HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Choose a project" };
    private readonly TextBox _person = new() { Header = "Person", PlaceholderText = "Leave blank to use your sign-in name", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _date = new() { Header = "Date", Text = Kit.Today(), PlaceholderText = "yyyy-MM-dd", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _hours = new() { Header = "Hours", PlaceholderText = "1.5", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _note = new() { Header = "Note", PlaceholderText = "What the time was for", HorizontalAlignment = HorizontalAlignment.Stretch, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64 };
    private readonly StackPanel _lines = new() { Spacing = 0 };
    private readonly StackPanel _use = new() { Spacing = 8 };
    private readonly InfoBar _notice = Flow.Notice();

    private TimeSheet(AppShell shell)
    {
        _shell = shell;
        var page = new StackPanel { Spacing = 12, Margin = new Thickness(8) };
        page.Children.Add(Flow.SheetMark("01.04", "Practice", "Time"));
        page.Children.Add(Flow.Heading("Time"));
        page.Children.Add(Flow.Secondary("Hours against a project. Recording time does not post a journal or an invoice."));
        page.Children.Add(_notice);
        page.Children.Add(Flow.Pair(_project, _person));
        page.Children.Add(Flow.Pair(_date, _hours));
        page.Children.Add(_note);
        var record = new Button { Content = "Record time", MinHeight = 32, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        record.Visibility = shell.CanEdit ? Visibility.Visible : Visibility.Collapsed;
        record.Click += (_, _) => Record();
        page.Children.Add(record);
        page.Children.Add(_lines);
        page.Children.Add(Flow.Section("Fee against hours"));
        page.Children.Add(Flow.Secondary("Bar length is that project's share of recorded hours. Fee per hour is the latest issued agreement fee divided by those hours."));
        page.Children.Add(_use);
        Content = new ScrollViewer { Content = page };
        LoadProjects();
        LoadLines();
        LoadUse();
    }

    public static UIElement Page(AppShell shell) => new TimeSheet(shell);

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

    private void LoadLines()
    {
        _ = _shell.Run("Loading time…", () =>
        {
            var rows = _shell.Call("time_entry_list", new { project_id = (long?)null });
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                _lines.Children.Clear();
                var widths = new GridLength[] { new(110), new(2, GridUnitType.Star), new(140), new(80), new(2, GridUnitType.Star), new(90) };
                _lines.Children.Add(Flow.Header(["Date", "Project", "Person", "Hours", "Note", ""], widths));
                if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() == 0)
                {
                    _lines.Children.Add(Flow.Secondary("No time recorded yet."));
                    return;
                }
                foreach (var row in rows.EnumerateArray())
                {
                    var line = Flow.Columns(
                        [Kit.Str(row, "work_date"), $"{Kit.Str(row, "client")} — {Kit.Str(row, "project")}", Kit.Str(row, "person"), Kit.Str(row, "hours_display"), Kit.Str(row, "note"), ""],
                        widths);
                    if (_shell.CanEdit && Kit.Id(row) is long id && line.Children.Count > 0 && line.Children[line.Children.Count - 1] is Border cell)
                    {
                        var remove = new Button { Content = "Remove", MinWidth = 72 };
                        remove.Click += (_, _) => Remove(id);
                        cell.Child = remove;
                    }
                    _lines.Children.Add(line);
                }
            });
        });
    }

    private void Record()
    {
        if (_project.SelectedItem is not ComboBoxItem item || item.Tag is not long projectId)
        {
            Flow.Fault(_project, _notice, "Choose a project.");
            return;
        }
        var person = _person.Text;
        var workDate = _date.Text.Trim();
        var hours = _hours.Text.Trim();
        var note = _note.Text;
        _ = _shell.Run("Recording time…", () => _shell.Call("time_entry_record", new { project_id = projectId, person, work_date = workDate, hours, note }))
            .ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (!task.Result)
                    return;
                _hours.Text = "";
                _note.Text = "";
                _notice.Severity = InfoBarSeverity.Success;
                _notice.Message = "The hours are recorded. They are not posted to the accounts.";
                _notice.IsOpen = true;
                LoadLines();
                LoadUse();
            }));
    }

    private void Remove(long id)
    {
        _ = _shell.Run("Removing the time line…", () => _shell.Call("time_entry_cancel", new { id }))
            .ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (!task.Result)
                    return;
                LoadLines();
                LoadUse();
            }));
    }

    private void LoadUse()
    {
        _ = _shell.Run("Loading hours against the fee…", () =>
        {
            var result = _shell.Call("time_utilization");
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                _use.Children.Clear();
                if (!result.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() == 0)
                {
                    _use.Children.Add(Flow.Secondary("Record hours, or issue an agreement, to see the fee against the time."));
                    return;
                }
                _use.Children.Add(Flow.Secondary($"{Kit.Str(result, "hours_display")} hours recorded. Issued agreement fees {Kit.Str(result, "fee_display")}."));
                foreach (var row in rows.EnumerateArray())
                {
                    var share = 0L;
                    if (row.TryGetProperty("share_bp", out var shareValue) && shareValue.TryGetInt64(out var parsed))
                        share = parsed;
                    var block = new StackPanel { Spacing = 4 };
                    block.Children.Add(new TextBlock { Text = $"{Kit.Str(row, "client")} — {Kit.Str(row, "project")}" });
                    block.Children.Add(new TextBlock { Text = $"{Kit.Str(row, "hours_display")} hours · fee {Kit.Str(row, "fee_display")} · {Kit.Str(row, "per_hour_display")} per hour" });
                    block.Children.Add(HourBar(share));
                    _use.Children.Add(block);
                }
            });
        });
    }

    private static Grid HourBar(long shareBp)
    {
        var track = new Grid { Height = 8, HorizontalAlignment = HorizontalAlignment.Stretch, MaxWidth = 420 };
        if (shareBp <= 0)
        {
            track.Background = Flow.Ink("BorderSubtleBrush");
            return track;
        }
        var remain = Math.Max(0, 10_000 - shareBp);
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(shareBp, GridUnitType.Star) });
        if (remain > 0)
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(remain, GridUnitType.Star) });
        track.Children.Add(new Border { Height = 8, Background = Flow.Ink("BrandAccentBrush") });
        if (remain > 0)
        {
            var rest = new Border { Height = 8, Background = Flow.Ink("BorderSubtleBrush") };
            Grid.SetColumn(rest, 1);
            track.Children.Add(rest);
        }
        return track;
    }
}
