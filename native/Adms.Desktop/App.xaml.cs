using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Adms.Desktop;

public partial class App : Application
{
    private const string InstanceName = @"Local\Osas.Kivo.Instance";
    private const string ShowName = @"Local\Osas.Kivo.Show";

    // Held for the life of the process: the mutex marks this as the one Kivo, the event lets a second launch ask it to come forward.
    private static Mutex? _instance;
    private static EventWaitHandle? _show;

    private MainWindow? _window;

    /// <summary>Started with <c>--broker</c>: no window until a caller needs the person to sign in.</summary>
    internal static bool BrokerMode { get; private set; }

    internal static KivoBroker? Broker { get; private set; }

    public App()
    {
        InitializeComponent();
        RequestedTheme = O.Ui.SuiteAppearance.IsDark ? ApplicationTheme.Dark : ApplicationTheme.Light;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        BrokerMode = Environment.GetCommandLineArgs().Skip(1).Any(a => string.Equals(a, "--broker", StringComparison.OrdinalIgnoreCase));

        // One Kivo per person: it owns the session broker. A second start brings the first forward (or, for --broker, simply ends).
        _instance = new Mutex(true, InstanceName, out var first);
        _show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowName);
        if (!first)
        {
            if (!BrokerMode)
                _show.Set();
            Environment.Exit(0);
            return;
        }

        // The office interface lives inside the one engine library, so the suite pages load it from there.
        var enginePath = Path.Combine(AppContext.BaseDirectory, "architect_office_desktop_lib.dll");
        if (File.Exists(enginePath))
        {
            try
            {
                NativeLibrary.Load(enginePath);
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
            {
                // Session create will surface a clearer message.
            }
        }
        Osas.Core.Suite.Engine.LookIn(AppContext.BaseDirectory, "architect_office_desktop_lib.dll");

        Broker = new KivoBroker();
        Broker.Start();
        _window = new MainWindow();
        Broker.ShowSignIn = () => _window.DispatcherQueue.TryEnqueue(_window.ShowForBroker);
        WatchForSecondStart();
        if (!BrokerMode)
            _window.Activate();
    }

    private void WatchForSecondStart()
    {
        var thread = new Thread(() =>
        {
            while (_show!.WaitOne())
                _window!.DispatcherQueue.TryEnqueue(_window.ShowForUser);
        })
        { IsBackground = true, Name = "Kivo second-start watcher" };
        thread.Start();
    }
}
