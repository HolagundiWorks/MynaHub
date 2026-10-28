using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Osas.Core.Agent;
using Osas.Core.Suite;

namespace Adms.Desktop;

/// <summary>
/// The tools the assistant may use, run through the session this person is signed in to in O-Studio. The engine checks their role for
/// every call, writes the audit entry, and only reads: the assistant sees what this person can see and changes nothing.
/// </summary>
internal sealed class StudioToolHost(AppShell shell) : IToolHost
{
    private IReadOnlyList<ToolSpec>? _tools;

    public IReadOnlyList<ToolSpec> Tools() => _tools ??= shell.Call("agent_tools").EnumerateArray()
        .Select(t => new ToolSpec(t.GetProperty("name").GetString()!, t.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "", JsonNode.Parse(t.GetProperty("input_schema").GetRawText())!))
        .ToList();

    public string Call(string name, JsonObject arguments)
    {
        try
        {
            return shell.Call("agent_call", new { name, arguments = JsonNode.Parse(arguments.ToJsonString()) }).GetRawText();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new AssistantException(ex.Message);
        }
    }
}

/// <summary>The AI assistant as a page of O-Studio, working as the person signed in here.</summary>
internal sealed class AssistantHostPage : UserControl
{
    public AssistantHostPage(AppShell shell)
    {
        var settings = new SuiteSettings();
        var tools = new StudioToolHost(shell);
        var agent = new AgentPanel(settings, () => new AssistantContext(shell.CompanyPath, shell.CompanyName, shell.User, shell.Role, tools), () => shell.CompanyPath);
        var page = new StackPanel { Padding = new Thickness(32, 24, 32, 24), Spacing = 16 };
        page.Children.Add(new TextBlock { Text = "Assistant", Style = (Style)Application.Current.Resources["PageTitleStyle"] });
        page.Children.Add(agent);
        Content = new ScrollViewer { Content = page };
    }
}
