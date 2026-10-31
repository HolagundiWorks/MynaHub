using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Adms.LoginWidget;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        RequestedTheme = O.Ui.SuiteAppearance.IsDark ? ApplicationTheme.Dark : ApplicationTheme.Light;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var enginePath = Path.Combine(AppContext.BaseDirectory, "architect_office_desktop_lib.dll");
        if (File.Exists(enginePath))
        {
            try
            {
                NativeLibrary.Load(enginePath);
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
            {
            }
        }

        Osas.Core.Suite.Engine.LookIn(AppContext.BaseDirectory, "architect_office_desktop_lib.dll");
        O.Ui.SuiteMotion.RespectSystemPreference();
        new SuiteLauncherWindow().Activate();
    }
}
