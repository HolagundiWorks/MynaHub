using Hcw.Ui.Widgets;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Adms.Desktop;

/// <summary>
/// Floating tasks board with the suite widget chrome (stay on top, transparency).
/// </summary>
internal sealed class TasksWidget : WidgetWindow
{
    static TasksWidget? _open;

    readonly AppShell _shell;

    TasksWidget(AppShell shell) : base("Tasks")
    {
        _shell = shell;
        Closed += (_, _) =>
        {
            if (ReferenceEquals(_open, this))
                _open = null;
        };
        AttachChrome(BuildBody());
        AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 640));
        Activate();
    }

    public static void ShowOrFocus(AppShell shell)
    {
        if (_open != null)
        {
            _open.Activate();
            return;
        }
        _open = new TasksWidget(shell);
    }

    FrameworkElement BuildBody() => new Border
    {
        Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SurfaceBackgroundBrush"],
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(10),
        Child = new TasksBoardPage(_shell, widget: true),
    };
}
