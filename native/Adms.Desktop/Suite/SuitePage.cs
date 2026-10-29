using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Osas.Core.Suite;

namespace Adms.Desktop;

/// <summary>
/// The suite's tools for the open company: the company file monitor and ShilpiDB. The company itself is the Firm page and the main
/// menu, the assistant floats over every page, and the other apps open from the bar along the bottom.
/// </summary>
internal sealed class SuitePage : UserControl
{
    private readonly ShilpiPanel _shilpi;

    public SuitePage(AppShell shell)
    {
        var page = new StackPanel { Padding = new Thickness(32, 24, 32, 24), Spacing = 24 };
        var title = new TextBlock { Text = "Suite", Style = (Style)Application.Current.Resources["PageTitleStyle"] };
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
        page.Children.Add(title);
        var monitor = new MonitorPanel(shell.Window);
        monitor.Follow(shell.CompanyPath);
        _shilpi = new ShilpiPanel(new SuiteSettings(), () => shell.CompanyPath);
        page.Children.Add(monitor);
        page.Children.Add(_shilpi);
        Content = new ScrollViewer { Content = page };
        Unloaded += (_, _) => _shilpi.Shutdown();
    }
}
