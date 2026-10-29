using Hcw.Ui.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Osas.Core.Suite;

namespace Adms.Desktop;

/// <summary>
/// ShilpiDB, the suite's spatial store for drawing geometry. Shows whether the server is running and how many records it holds, and starts
/// or stops a server on this computer for the chosen company. Project business data stays in the company file; ShilpiDB records point at it.
/// </summary>
public sealed class ShilpiPanel : StackPanel
{
    private readonly ShilpiLink _link;
    private readonly Func<string?> _company;
    private readonly TextBlock _state = new() { Style = Resource<Style>("MetricValueStyle") };
    private readonly TextBlock _detail = new() { Style = Resource<Style>("CaptionStyle"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly InfoBar _notice = new() { IsOpen = false, IsClosable = true };
    private readonly Button _start = new() { Content = "Start ShilpiDB" };
    private readonly Button _stop = new() { Content = "Stop ShilpiDB" };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _busy;

    public ShilpiPanel(SuiteSettings settings, Func<string?> company)
    {
        _link = new ShilpiLink(settings);
        _company = company;
        Spacing = 8;
        var heading = new TextBlock { Text = "ShilpiDB", Style = Resource<Style>("SectionTitleStyle") };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        Children.Add(heading);
        Children.Add(new TextBlock { Text = "The spatial store for drawing data, started only on this computer.", Style = Resource<Style>("SecondaryStyle"), TextWrapping = TextWrapping.Wrap });
        Children.Add(_state);
        Children.Add(_detail);
        Children.Add(_notice);
        _start.Click += async (_, _) => await Start();
        _stop.Click += async (_, _) =>
        {
            _link.Stop();
            await Look();
        };
        Children.Add(new FlexBar { Children = { _start, _stop } });
        _timer.Tick += async (_, _) => await Look();
        Loaded += (_, _) =>
        {
            _timer.Start();
            _ = Look();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    private static T Resource<T>(string key) => (T)Microsoft.UI.Xaml.Application.Current.Resources[key];

    /// <summary>O-Studio closes only a server it started itself.</summary>
    public void Shutdown() => _link.Stop();

    private async Task Start()
    {
        var company = _company();
        if (company == null)
        {
            Tell(InfoBarSeverity.Warning, "Choose a company first. Its spatial records are kept beside its file.");
            return;
        }
        var why = _link.Start(ShilpiLink.DataFileFor(company));
        if (why != null)
            Tell(InfoBarSeverity.Error, why);
        else
        {
            _notice.IsOpen = false;
            await Task.Delay(800);
        }
        await Look();
    }

    private void Tell(InfoBarSeverity severity, string message)
    {
        _notice.Severity = severity;
        _notice.Message = message;
        _notice.IsOpen = true;
    }

    private async Task Look()
    {
        if (_busy)
            return;
        _busy = true;
        try
        {
            var s = await _link.Look();
            _state.Text = !s.ServerUp ? "Not running" : s.Records is { } n ? $"Running · {n:N0} records" : "Running";
            var lines = new List<string> { $"Server {s.Host}:{s.Port} · web gateway {s.HttpPort} {(s.GatewayUp ? "answering" : "not answering")}" };
            if (s.CellSize is { } cell)
                lines.Add($"Search grid cell size {cell:0.##}");
            if (_company() is { } company)
                lines.Add($"Data file for this company: {ShilpiLink.DataFileFor(company)}");
            if (s.Problem != null)
                lines.Add(s.Problem);
            _detail.Text = string.Join("\n", lines);
            _start.IsEnabled = !s.ServerUp;
            _stop.IsEnabled = _link.StartedHere;
        }
        finally
        {
            _busy = false;
        }
    }
}
