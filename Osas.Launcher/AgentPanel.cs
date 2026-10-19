using Hcw.Ui.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Osas.Core.Suite;
using Windows.ApplicationModel.DataTransfer;

namespace Osas.Launcher;

/// <summary>
/// The AI assistant. It runs outside OSAS (Claude Desktop, Claude Code or another MCP client) and reaches the company through office-mcp,
/// as one named user with that user's role, using fixed read-only tools. OSAS shows what it can do and makes the settings block to paste;
/// it never holds or writes a password.
/// </summary>
public sealed class AgentPanel : StackPanel
{
    private readonly AgentLink _link;
    private readonly Func<string?> _company;
    private readonly Func<string?> _user;
    private readonly TextBlock _state = new() { Style = Resource<Style>("BodyStyle"), TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _config = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
        MinHeight = 160,
        MaxWidth = 760,
        HorizontalAlignment = HorizontalAlignment.Left,
        Header = "Settings to paste into the assistant",
    };
    private readonly Button _copy = new() { Content = "Copy the settings" };

    public AgentPanel(SuiteSettings settings, Func<string?> company, Func<string?> user)
    {
        _link = new AgentLink(settings);
        _company = company;
        _user = user;
        Spacing = 8;
        var heading = new TextBlock { Text = "AI assistant", Style = Resource<Style>("SectionTitleStyle") };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        Children.Add(heading);
        Children.Add(new TextBlock
        {
            Text = "An assistant such as Claude can look things up in the company through a fixed set of read-only tools. It signs in as one company user and sees only what that user's role allows, so make a user with the lowest role that does the job (an Auditor can read but not change). It cannot change anything yet, and it cannot run SQL.",
            Style = Resource<Style>("SecondaryStyle"),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 760,
            HorizontalAlignment = HorizontalAlignment.Left,
        });
        Children.Add(_state);
        Children.Add(_config);
        _copy.Click += (_, _) =>
        {
            var data = new DataPackage();
            data.SetText(_config.Text);
            Clipboard.SetContent(data);
        };
        Children.Add(new FlexBar { Children = { _copy } });
        var tools = new StackPanel { Spacing = 2 };
        tools.Children.Add(new TextBlock { Text = "What it can use", Style = Resource<Style>("SectionTitleStyle"), FontSize = 16 });
        foreach (var tool in AgentLink.Tools)
            tools.Children.Add(new TextBlock { Text = $"{tool.Name} · {tool.Purpose}", Style = Resource<Style>("BodyStyle"), TextWrapping = TextWrapping.Wrap });
        Children.Add(tools);
        Refresh();
    }

    private static T Resource<T>(string key) => (T)Microsoft.UI.Xaml.Application.Current.Resources[key];

    /// <summary>Redraws the settings block for the company and person now chosen in OSAS.</summary>
    public void Refresh()
    {
        var company = _company();
        var program = _link.FindProgram();
        if (company == null)
        {
            _state.Text = "Choose a company first. The settings name the company file.";
            _config.Text = "";
            _copy.IsEnabled = false;
            return;
        }
        _state.Text = program == null
            ? "office-mcp.exe was not found beside OSAS. The settings below name it without a folder; install it, or put its folder on the PATH. Replace the password text with that user's password; it is kept only in the assistant's own settings. The settings use a user named auditor: add one in Users, or change the name."
            : $"office-mcp found: {program}. Replace the password text with that user's password; it is kept only in the assistant's own settings. The settings use a user named auditor: add one in Users, or change the name.";
        _config.Text = AgentLink.ClientConfig(program ?? "office-mcp.exe", company, _user() ?? "auditor");
        _copy.IsEnabled = true;
    }
}
