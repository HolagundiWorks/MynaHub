using O.Ui.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Osas.Core.Monitor;

namespace Adms.Desktop;

/// <summary>
/// Watches the company file that MynaHub and MynaOps share: space used, what is free inside it, who has it open and how busy they are,
/// backups, and how often it is being written. It opens the file read-only, never signs in, and shows counts and sizes only.
/// </summary>
public sealed class MonitorPanel : StackPanel
{
    private readonly Window _window;
    private readonly TextBlock _path = new() { Style = Resource<Style>("CaptionStyle"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly InfoBar _problem = new() { IsOpen = false, IsClosable = false, Severity = InfoBarSeverity.Warning };
    private readonly FlexBar _tiles = new();
    private readonly StackPanel _programs = new() { Spacing = 4 };
    private readonly StackPanel _tables = new() { Spacing = 4 };
    private readonly TextBlock _stamp = new() { Style = Resource<Style>("CaptionStyle") };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private CompanyFileMonitor? _monitor;
    private bool _busy;

    public MonitorPanel(Window window)
    {
        _window = window;
        Spacing = O.Ui.SuiteScale.CardGap;
        var refresh = new Button { Content = "Look now" };
        refresh.Click += async (_, _) => await Look();
        Children.Add(_problem);
        Children.Add(_tiles);
        var inside = new StackPanel { Spacing = 8 };
        var heading = new TextBlock { Text = "Company file monitor", Style = Resource<Style>("SectionTitleStyle") };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        inside.Children.Add(heading);
        inside.Children.Add(new TextBlock { Text = "Sizes and counts only, never names, amounts or documents. Nothing is changed.", Style = Resource<Style>("SecondaryStyle"), TextWrapping = TextWrapping.Wrap });
        inside.Children.Add(_path);
        inside.Children.Add(Section("Open in"));
        inside.Children.Add(_programs);
        inside.Children.Add(Section("Space used by table"));
        inside.Children.Add(_tables);
        inside.Children.Add(_stamp);
        inside.Children.Add(refresh);
        Details = new Border { Style = Resource<Style>("SuiteCardStyle"), Width = 380, Margin = new Thickness(0, 0, O.Ui.SuiteScale.CardGap, O.Ui.SuiteScale.CardGap), Child = inside };

        Say("No company file is open.");
        _timer.Tick += async (_, _) => await Look();
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    /// <summary>The card with who has the file open and where the space goes, for the dashboard to place.</summary>
    public UIElement Details { get; }

    private static T Resource<T>(string key) => (T)Microsoft.UI.Xaml.Application.Current.Resources[key];

    private static TextBlock Section(string text)
    {
        var block = new TextBlock { Text = text, Style = Resource<Style>("SectionTitleStyle") };
        AutomationProperties.SetHeadingLevel(block, AutomationHeadingLevel.Level3);
        return block;
    }

    /// <summary>Watches the company file the suite is working in (the one chosen in the Company section).</summary>
    public void Follow(string? path)
    {
        _monitor?.Dispose();
        _monitor = null;
        if (path == null)
        {
            _path.Text = "";
            Say("No company file is open.");
            return;
        }
        _monitor = new CompanyFileMonitor(path);
        _path.Text = path;
        _problem.IsOpen = false;
        _ = Look();
    }

    private void Say(string message)
    {
        _problem.Message = message;
        _problem.Severity = InfoBarSeverity.Informational;
        _problem.IsOpen = true;
    }

    private async Task Look()
    {
        var monitor = _monitor;
        if (monitor == null || _busy)
            return;
        _busy = true;
        try
        {
            var stats = await Task.Run(monitor.Sample);
            if (!ReferenceEquals(monitor, _monitor))
                return;
            Paint(stats);
        }
        finally
        {
            _busy = false;
        }
    }

    private void Paint(CompanyStats s)
    {
        if (s.Problem != null)
        {
            _problem.Severity = s.Protected ? InfoBarSeverity.Informational : InfoBarSeverity.Warning;
            _problem.Message = s.Problem;
            _problem.IsOpen = true;
        }
        else
        {
            _problem.IsOpen = false;
        }
        _tiles.Children.Clear();
        if (!s.Exists)
        {
            _programs.Children.Clear();
            _tables.Children.Clear();
            return;
        }
        var freePercent = s.TotalOnDiskBytes > 0 && s.MainBytes > 0 ? 100.0 * s.FreeBytes / s.MainBytes : 0;
        Tile("Size on disk", Bytes(s.TotalOnDiskBytes), $"{Bytes(s.MainBytes)} file + {Bytes(s.WalBytes)} write-ahead log");
        if (s.IsCompanyFile)
        {
            Tile("Free inside the file", Bytes(s.FreeBytes), $"{freePercent:0.#}% of the file; compacting gives this back");
            Tile("Project documents", Bytes(s.ProjectDocumentBytes), $"{Count(s, "project_documents")} saved by MynaOps");
            Tile("Stored files", Bytes(s.StoredFileBytes), $"{Count(s, "stored_files")} letters, drawings and other files");
            Tile("Records", $"{Count(s, "clients")} clients", $"{Count(s, "projects")} projects · {Count(s, "invoices")} invoices · {Count(s, "audit_log")} audit entries");
            var activity = s.ChangesPerMinute == 0 ? "Quiet" : $"{s.ChangesPerMinute:0} in the last minute";
            Tile("Writes by other programs", activity, "Looks that saw the other program write");
            Tile("File format", $"Version {s.SchemaVersion}", $"{s.JournalMode} mode · {s.PageCount:N0} pages of {Bytes(s.PageSize)}");
        }
        Tile("Backups", s.BackupCount == 0 ? "None found" : Bytes(s.BackupBytes), s.BackupCount == 0 ? "No backups folder beside the file" : $"{s.BackupCount} files · newest {s.NewestBackup:d MMM yyyy HH:mm}");
        Tile("Drive", Bytes(s.DriveFreeBytes) + " free", $"of {Bytes(s.DriveTotalBytes)}");

        _programs.Children.Clear();
        if (s.OpenIn.Count == 0)
        {
            _programs.Children.Add(new TextBlock { Text = "No app has this file open.", Style = Resource<Style>("SecondaryStyle") });
        }
        foreach (var program in s.OpenIn)
        {
            var row = new TextBlock { Text = $"{program.App} · {program.CpuPercent:0.#}% processor · {Bytes(program.MemoryBytes)} memory", Style = Resource<Style>("BodyStyle") };
            AutomationProperties.SetName(row, $"{program.App} has this file open, using {program.CpuPercent:0.#} percent of the processor and {Bytes(program.MemoryBytes)} of memory");
            _programs.Children.Add(row);
        }
        _tables.Children.Clear();
        if (s.Biggest.Count == 0)
            _tables.Children.Add(new TextBlock { Text = s.IsCompanyFile ? "This build of the database engine does not report space by table." : "", Style = Resource<Style>("SecondaryStyle") });
        foreach (var table in s.Biggest)
            _tables.Children.Add(new TextBlock { Text = $"{table.Table} · {Bytes(table.Bytes)}", Style = Resource<Style>("BodyStyle") });
        _stamp.Text = $"{s.Name} · looked at {s.Sampled:HH:mm:ss}" + (s.LastOpened != null ? $" · last opened {s.LastOpened}" : "");
    }

    private static string Count(CompanyStats s, string table) => s.Counts.TryGetValue(table, out var n) ? n.ToString("N0") : "?";

    private void Tile(string label, string value, string note)
    {
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new TextBlock { Text = label, Style = Resource<Style>("CaptionStyle") });
        stack.Children.Add(new TextBlock { Text = value, Style = Resource<Style>("MetricValueStyle") });
        stack.Children.Add(new TextBlock { Text = note, Style = Resource<Style>("CaptionStyle"), TextWrapping = TextWrapping.Wrap });
        var card = new Border { Style = Resource<Style>("SuiteCardStyle"), Width = 250, MinHeight = 92, Margin = new Thickness(0, 0, O.Ui.SuiteScale.CardGap, O.Ui.SuiteScale.CardGap), Child = stack };
        AutomationProperties.SetName(card, $"{label}: {value}. {note}");
        _tiles.Children.Add(card);
    }

    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}
