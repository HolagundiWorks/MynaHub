using Microsoft.UI.Xaml;

namespace Adms.Desktop;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        RequestedTheme = Hcw.Ui.SuiteAppearance.IsDark ? ApplicationTheme.Dark : ApplicationTheme.Light;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // The office interface lives inside the one engine library, so the suite pages load it from there.
        Osas.Core.Suite.Engine.LookIn(AppContext.BaseDirectory, "architect_office_desktop_lib.dll");
        _window = new MainWindow();
        _window.Activate();
    }
}
