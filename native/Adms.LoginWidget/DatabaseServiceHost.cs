using System.Text.Json;
using Adms.Cloud;
using Osas.Core;
using Osas.Core.Database;

namespace Adms.LoginWidget;

/// <summary>
/// Runs the suite's database service inside the launcher, as the AI Core runs here: loopback HTTP on 127.0.0.1:8743, started with the launcher, no window of
/// its own. The logic is <see cref="DatabaseService"/> in Osas.Core; this class only wires the real Kivo pipe and the Neon store to it. The launcher never
/// signs in: it asks Kivo for a session (non-interactive) and the service syncs only while that session is valid.
/// </summary>
internal sealed class DatabaseServiceHost
{
    internal static DatabaseServiceHost Instance { get; } = new();

    static readonly HttpClient Probe = new() { Timeout = TimeSpan.FromSeconds(2) };

    DatabaseService? _service;
    DatabaseHttpServer? _server;
    IRemoteStore? _remote;

    /// <summary>Raised when the service's state or sync activity changes (any thread).</summary>
    internal event Action? Changed;

    internal bool IsRunning => _server?.IsRunning == true;

    /// <summary>Why the service did not start (for example the port is taken); empty when it runs.</summary>
    internal string Problem { get; private set; } = "";

    internal void Start()
    {
        if (_service != null)
            return;
        _remote = Osas.Core.ProjectFilePolicy.PushToCloud ? new NeonRemoteStore() : new LocalOnlyRemoteStore();
        _service = new DatabaseService(new PipeBrokerClient(), _remote, SuiteWire.LocalPath("dbservice-projects.json"))
        {
            Log = line => System.Diagnostics.Debug.WriteLine("[dbservice] " + line),
        };
        _server = new DatabaseHttpServer(_service, SuiteWire.DbPort);
        if (!_server.Start())
        {
            Problem = _server.Problem;
            Changed?.Invoke();
            return;
        }

        _service.Event += e =>
        {
            if (e.Name == "state")
                Changed?.Invoke();
        };
        _service.Start();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
    }

    internal void Stop()
    {
        _server?.Dispose();
        _server = null;
        _service?.Dispose();
        _service = null;
        if (_remote is IAsyncDisposable disposable)
            _ = disposable.DisposeAsync();
        _remote = null;
    }

    /// <summary>The words the window and the tray show: <c>connected</c>, <c>offline</c>, <c>signed out</c>, <c>starting</c> or <c>stopped (reason)</c>. Read from <c>/v1/health</c> like any client would.</summary>
    internal async Task<(string Word, string Detail, int Pending)> DescribeAsync()
    {
        if (!IsRunning)
            return ($"stopped ({(Problem.Length > 0 ? Problem : "not started")})", "", 0);
        try
        {
            using var response = await Probe.GetAsync(SuiteWire.DbUrl + "/v1/health").ConfigureAwait(false);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var root = doc.RootElement;
            var state = root.TryGetProperty("state", out var s) ? s.GetString() ?? "" : "";
            var sync = root.TryGetProperty("sync", out var y) ? y.GetString() ?? "" : "";
            var pending = root.TryGetProperty("pending", out var p) && p.TryGetInt32(out var n) ? n : 0;
            var word = state switch
            {
                "ready" => "connected",
                "local" => "on this PC",
                "signed_out" => "signed out",
                "offline" => "offline",
                _ => "starting",
            };
            var detail = state switch
            {
                "ready" => sync is "pushing" or "pulling" ? sync : pending > 0 ? $"{pending} revision(s) waiting" : "in step",
                "local" => "project files stay on this PC; only Kivo is synced",
                "signed_out" => "sign in to Kivo to sync; the local project files still work",
                "offline" => new NeonSettingsProbe().Configured ? "cloud not reachable; changes wait on this PC" : "no neon.env; changes stay on this PC",
                _ => "",
            };
            return (word, detail, pending);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return ("stopped (does not answer on port " + SuiteWire.DbPort + ")", "", 0);
        }
    }

    /// <summary>Whether the cloud credentials exist, for the offline hint only.</summary>
    sealed class NeonSettingsProbe
    {
        internal bool Configured => NeonSettings.Load().IsComplete;
    }
}
