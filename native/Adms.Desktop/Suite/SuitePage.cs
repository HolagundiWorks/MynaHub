using O.Ui.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Adms.Desktop;

/// <summary>
/// The Suite dashboard for the open company: file health and where the other apps open. MynaHub has no AI agent: it lives in MynaOps and MynaCAD.
/// </summary>
internal sealed class SuitePage : UserControl
{
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
                Card(AppsCard()),
            },
        };
        page.Children.Add(cards);
        Content = new ScrollViewer { Content = page };
    }

    private static Style Resource(string key) => (Style)Application.Current.Resources[key];

    private static Border Card(UIElement child) => new() { Style = Resource("SuiteCardStyle"), Width = 380, Margin = new Thickness(0, 0, O.Ui.SuiteScale.CardGap, O.Ui.SuiteScale.CardGap), Child = child };

    private static UIElement AppsCard()
    {
        var stack = new StackPanel { Spacing = 8 };
        var heading = new TextBlock { Text = "Other apps", Style = Resource("SectionTitleStyle") };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        stack.Children.Add(heading);
        stack.Children.Add(new TextBlock { Text = "Open MynaOps, MynaCAD and Myna3D from the bar at the bottom of the window. They do not sign in here; the AI agent runs in MynaOps and serves MynaCAD too.", Style = Resource("SecondaryStyle"), TextWrapping = TextWrapping.Wrap });
        return stack;
    }
}
