using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Adms.LoginWidget;

/// <summary>Hidden host window so the process stays alive while the tray icon is shown.</summary>
internal sealed class SuiteLauncherWindow : Window
{
    public SuiteLauncherWindow()
    {
        Title = "O-Studio suite";
        Content = new Grid { Width = 1, Height = 1 };
        try
        {
            AppWindow.Hide();
        }
        catch
        {
        }

        Closed += (_, _) => SuiteTrayIcon.Stop();
        Activated += (_, _) =>
        {
            try
            {
                AppWindow.Hide();
            }
            catch
            {
            }
        };

        SuiteTrayIcon.Start(DispatcherQueue);
        SuiteStatus.Instance.Start(DispatcherQueue);
    }
}
