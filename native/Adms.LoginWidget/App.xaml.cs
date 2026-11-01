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

    /// <summary>
    /// The launcher hosts the suite's AI Core (Olmo 3 7B on 127.0.0.1:8742) for as long as it runs, so the assistant is there whether or not
    /// O-Studio is open. The model itself loads on the first question. When another process already serves the port, the launcher leaves it alone.
    /// </summary>
    static void StartAiCore()
    {
        Environment.SetEnvironmentVariable(Osas.Core.SuiteWire.EnvAiRole, "host");
        Office.Ai.SuiteAiCoordinator.StartHostIfNeeded();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Office.Ai.SuiteAiCoordinator.StopHost();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var request = LaunchRequest.FromArguments(Environment.GetCommandLineArgs());
        if (request != null)
            LaunchRequest.Write(request);
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
        ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => queue.TryEnqueue(() =>
        {
            CompanyLoginWidget.ShowOrFocus();
            _ = SuiteStatus.Instance.RefreshAsync();
            _ = SuiteLauncherHost.Instance.RunPendingLaunchAsync(queue);
        }), null, Timeout.Infinite, false);
        SuiteLauncherHost.Instance.Changed += () => queue.TryEnqueue(async () => await SuiteLauncherHost.Instance.RunPendingLaunchAsync(queue));

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
        StartAiCore();
        new SuiteLauncherWindow().Activate();
        if (request != null)
        {
            // Started on behalf of an app: show the sign-in window; the app starts when a project is locked.
            queue.TryEnqueue(() =>
            {
                CompanyLoginWidget.ShowOrFocus();
                _ = SuiteLauncherHost.Instance.RunPendingLaunchAsync(queue);
            });
        }
    }
}
