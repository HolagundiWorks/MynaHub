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

    // One launcher per Windows session. The names are per session (no Global prefix), so each signed-in user has their own.
    static Mutex? _single;
    static EventWaitHandle? _show;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _single = new Mutex(true, "O-Studio.Suite.Launcher", out var first);
        _show = new EventWaitHandle(false, EventResetMode.AutoReset, "O-Studio.Suite.Launcher.Show");
        if (!first)
        {
            // Another launcher is running: ask it to bring its sign-in window forward, then leave.
            _show.Set();
            Exit();
            return;
        }
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => queue.TryEnqueue(CompanyLoginWidget.ShowOrFocus), null, Timeout.Infinite, false);

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
