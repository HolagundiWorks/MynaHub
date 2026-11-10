using O.Ui.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Office.Ai;
using Osas.Core.Agent;

namespace Adms.Desktop;

/// <summary>
/// The Suite dashboard for the open company: file health, the local assistant and launcher cards for sibling apps.
/// </summary>
internal sealed class SuitePage : UserControl
{
    private readonly TextBlock _assistantState = new() { Style = Resource("BodyStyle"), TextWrapping = TextWrapping.Wrap };

    public SuitePage(AppShell shell)
    {
        var page = new StackPanel { Padding = O.Ui.SuiteScale.PageMargin, Spacing = O.Ui.SuiteScale.CardGap };
        var title = new TextBlock { Text = "Suite", Style = Resource("PageTitleStyle") };
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
        page.Children.Add(title);
        page.Children.Add(new TextBlock { Text = $"{shell.CompanyName} · {shell.CompanyPath}", Style = Resource("CaptionStyle"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });

        var monitor = new MonitorPanel(shell.Window);
        monitor.Follow(shell.CompanyPath);
        page.Children.Add(monitor);

        var cards = new FlexBar
        {
            Children =
            {
                monitor.Details,
                Card(AssistantCard(shell)),
                Card(AppsCard()),
            },
        };
        page.Children.Add(cards);
        Content = new ScrollViewer { Content = page };
        _ = CheckAssistant();
    }

    private static Style Resource(string key) => (Style)Application.Current.Resources[key];

    private static Border Card(UIElement child) => new() { Style = Resource("SuiteCardStyle"), Width = 380, Margin = new Thickness(0, 0, O.Ui.SuiteScale.CardGap, O.Ui.SuiteScale.CardGap), Child = child };

    private UIElement AssistantCard(AppShell shell)
    {
        var stack = new StackPanel { Spacing = 8 };
        var heading = new TextBlock { Text = "Assistant", Style = Resource("SectionTitleStyle") };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        stack.Children.Add(heading);
        stack.Children.Add(new TextBlock { Text = "The glowing logo at the bottom of every page. Olmo 3 7B runs on this computer from MynaHub; MynaOps and MynaCAD connect to the same model. Read-only, nothing sent anywhere.", Style = Resource("SecondaryStyle"), TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(_assistantState);
        stack.Children.Add(new TextBlock { Text = $"Works as {shell.User} ({shell.Role})", Style = Resource("CaptionStyle") });
        return stack;
    }

    private async Task CheckAssistant()
    {
        if (!Office.Ai.LocalLlmHost.Installed)
        {
            _assistantState.Text = $"Olmo is not installed (see docs/LOCAL-MODEL.md in MynaOps).";
            return;
        }
        var (ready, problem) = await OlmoAiCoreApi.VerifyAsync();
        _assistantState.Text = problem ?? (ready ? "Ready · loads on first question" : "Not ready");
    }

    private static UIElement AppsCard()
    {
        var stack = new StackPanel { Spacing = 8 };
        var heading = new TextBlock { Text = "Other apps", Style = Resource("SectionTitleStyle") };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        stack.Children.Add(heading);
        stack.Children.Add(new TextBlock { Text = "Open MynaOps, MynaCAD and MynaMod from the bar at the bottom of the window.", Style = Resource("SecondaryStyle"), TextWrapping = TextWrapping.Wrap });
        return stack;
    }
}
