using Hcw.Ui.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Osas.Core.Agent;
using Osas.Core.Suite;

namespace Adms.Desktop;

/// <summary>
/// The Suite dashboard for the open company: the company file's health along the top, then cards for who has the file open, ShilpiDB,
/// the local assistant and the other apps. The company itself is the Firm page, the assistant floats over every page, and the other
/// apps open from the bar along the bottom.
/// </summary>
internal sealed class SuitePage : UserControl
{
    private readonly ShilpiPanel _shilpi;
    private readonly TextBlock _assistantState = new() { Style = Resource("BodyStyle"), TextWrapping = TextWrapping.Wrap };

    public SuitePage(AppShell shell)
    {
        var page = new StackPanel { Padding = new Thickness(32, 24, 32, 24), Spacing = 16 };
        var title = new TextBlock { Text = "Suite", Style = Resource("PageTitleStyle") };
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
        page.Children.Add(title);
        page.Children.Add(new TextBlock { Text = $"{shell.CompanyName} · {shell.CompanyPath}", Style = Resource("CaptionStyle"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });

        var monitor = new MonitorPanel(shell.Window);
        monitor.Follow(shell.CompanyPath);
        page.Children.Add(monitor);

        _shilpi = new ShilpiPanel(new SuiteSettings(), () => shell.CompanyPath);
        var cards = new FlexBar
        {
            Children =
            {
                monitor.Details,
                Card(_shilpi),
                Card(AssistantCard(shell)),
                Card(AppsCard()),
            },
        };
        page.Children.Add(cards);
        Content = new ScrollViewer { Content = page };
        Unloaded += (_, _) => _shilpi.Shutdown();
        _ = CheckAssistant();
    }

    private static Style Resource(string key) => (Style)Application.Current.Resources[key];

    private static Border Card(UIElement child) => new() { Style = Resource("SuiteCardStyle"), Width = 380, Margin = new Thickness(0, 0, 16, 16), Child = child };

    private UIElement AssistantCard(AppShell shell)
    {
        var stack = new StackPanel { Spacing = 8 };
        var heading = new TextBlock { Text = "Assistant", Style = Resource("SectionTitleStyle") };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        stack.Children.Add(heading);
        stack.Children.Add(new TextBlock { Text = "The glowing logo at the bottom of every page. It runs a model on this computer through Ollama: no key, nothing sent anywhere, read-only.", Style = Resource("SecondaryStyle"), TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(_assistantState);
        stack.Children.Add(new TextBlock { Text = $"Works as {shell.User} ({shell.Role})", Style = Resource("CaptionStyle") });
        return stack;
    }

    private async Task CheckAssistant()
    {
        using var ollama = new OllamaApi();
        var models = await ollama.InstalledModelsAsync();
        _assistantState.Text = models.Count == 0
            ? "Ollama is not running, or has no model yet. Install it and fetch a model that can use tools (for example ollama pull llama3.1)."
            : $"Ready · {models.Count} model{(models.Count == 1 ? "" : "s")}: {string.Join(", ", models.Take(4))}{(models.Count > 4 ? "…" : "")}";
    }

    private static UIElement AppsCard()
    {
        var stack = new StackPanel { Spacing = 8 };
        var heading = new TextBlock { Text = "Other apps", Style = Resource("SectionTitleStyle") };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        stack.Children.Add(heading);
        stack.Children.Add(new TextBlock { Text = "Open them from the bar along the bottom. They open on this company, already signed in.", Style = Resource("SecondaryStyle"), TextWrapping = TextWrapping.Wrap });
        foreach (var card in SuiteHub.Cards)
        {
            var found = SuiteHub.FindExecutable(card);
            var row = new StackPanel { Spacing = 0 };
            row.Children.Add(new TextBlock { Text = $"{card.Title} · {(found != null ? "installed" : "not installed")}", Style = Resource("BodyStyle") });
            row.Children.Add(new TextBlock { Text = card.Blurb, Style = Resource("CaptionStyle"), TextWrapping = TextWrapping.Wrap });
            stack.Children.Add(row);
        }
        return stack;
    }
}
