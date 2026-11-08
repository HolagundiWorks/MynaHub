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
    /// Kivo is open. The model itself loads on the first question. When another process already serves the port, the launcher leaves it alone.
    /// </summary>
    static void StartAiCore()
    {
        Environment.SetEnvironmentVariable(Osas.Core.SuiteWire.EnvAiRole, "host");
        Office.Ai.SuiteAiCoordinator.StartHostIfNeeded();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Office.Ai.SuiteAiCoordinator.StopHost();
    }

    /// <summary>
    /// The launcher also hosts the suite's database service (loopback 127.0.0.1:8743): a local database that syncs to Neon in the background, only while
    /// Kivo has a session. It never opens a window and never asks for a password.
    /// </summary>
    static void StartDatabaseService() => DatabaseServiceHost.Instance.Start();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var request = LaunchRequest.FromArguments(Environment.GetCommandLineArgs());
        if (request != null)
            LaunchRequest.Write(request);
        _single = new Mutex(true, "O-Studio.Suite.Launcher", out var first);
        _show = new EventWaitHandle(false, EventResetMode.AutoReset, "O-Studio.Suite.Launcher.Show");
        if (!first)
        {
            // Another launcher is running: ask it to bring its window forward (and start the app this request names), then leave.
            _show.Set();
            Exit();
            return;
        }
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => queue.TryEnqueue(() =>
        {
            MynaWindow.ShowOrFocus();
            _ = SuiteStatus.Instance.RefreshAsync();
            SuiteLauncherHost.Instance.RunPendingLaunch();
        }), null, Timeout.Infinite, false);

        O.Ui.SuiteMotion.RespectSystemPreference();
        StartAiCore();
        StartDatabaseService();
        new SuiteLauncherWindow().Activate();
        if (request != null)
        {
            // Started on behalf of an app: start that app now, nothing has to be signed in here first.
            queue.TryEnqueue(SuiteLauncherHost.Instance.RunPendingLaunch);
        }
    }
}
