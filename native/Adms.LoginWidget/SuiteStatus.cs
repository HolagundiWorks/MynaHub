using Microsoft.UI.Dispatching;
using Osas.Core;
using Osas.Core.Agent;

namespace Adms.LoginWidget;

/// <summary>
/// What the launcher shows besides the company session: the AI agent (the Olmo host on loopback, started by O-Studio), the company database
/// (the open company file) and the cloud sync setup. It looks again every 20 seconds, when the company session changes, and on request.
/// </summary>
internal sealed class SuiteStatus
{
    internal static SuiteStatus Instance { get; } = new();

    internal string Ai { get; private set; } = "checking…";
    internal string Database { get; private set; } = "checking…";
    internal string Cloud { get; private set; } = "checking…";
    internal string Project => SuiteLauncherHost.Instance.ProjectLine();

    internal event Action? Changed;

    DispatcherQueueTimer? _timer;
    int _running;

    internal void Start(DispatcherQueue queue)
    {
        if (_timer != null)
            return;
        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(20);
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        SuiteLauncherHost.Instance.Changed += () => queue.TryEnqueue(async () => await RefreshAsync());
        _ = RefreshAsync();
    }

    internal async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
            return;
        try
        {
            var ai = await ProbeAiAsync().ConfigureAwait(true);
            Ai = ai;
            Database = DescribeDatabase();
            Cloud = DescribeCloud();
            WriteLog();
            Changed?.Invoke();
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>The latest status as plain text, <c>launcher-status.txt</c> in the suite's local folder, so it can be read without opening the menu. It is overwritten each time.</summary>
    void WriteLog()
    {
        try
        {
            var path = SuiteWire.LocalPath("launcher-status.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}AI agent: {Ai}{Environment.NewLine}Database: {Database}{Environment.NewLine}Cloud sync: {Cloud}{Environment.NewLine}Project: {Project}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the log is a convenience; never let it disturb the launcher
        }
    }

    static async Task<string> ProbeAiAsync()
    {
        try
        {
            var (ready, problem) = await OlmoAiCoreApi.VerifyAsync().ConfigureAwait(false);
            if (ready)
                return "ready (Olmo 3 7B on port 8742)";
            var text = problem ?? "";
            if (text.Contains("not installed", StringComparison.OrdinalIgnoreCase))
                return "model or runtime not installed";
            if (text.Contains("token", StringComparison.OrdinalIgnoreCase))
                return "token mismatch: close the suite apps and restart the launcher";
            return Office.Ai.SuiteAiCoordinator.IsHost ? "starting (the launcher hosts it)" : "not running (port 8742 is taken by another program)";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return Office.Ai.SuiteAiCoordinator.IsHost ? "starting (the launcher hosts it)" : "not running (port 8742 is taken by another program)";
        }
    }

    static string DescribeDatabase()
    {
        var host = SuiteLauncherHost.Instance;
        var path = host.CompanyOpen ? host.CompanyPath : SuiteJsonHub.CompanyPath() ?? "";
        if (string.IsNullOrWhiteSpace(path))
            return "no company file chosen";
        if (!File.Exists(path))
            return $"{Path.GetFileName(path)} not found";
        var size = new FileInfo(path).Length;
        var file = $"{Path.GetFileName(path)} ({FormatSize(size)})";
        if (!host.CompanyOpen)
            return $"{file}, closed";
        if (host.Locked)
            return $"{file}, open, session locked";
        return host.SignedIn ? $"{file}, open, signed in as {host.Username}" : $"{file}, open, sign-in needed";
    }

    /// <summary>The Neon credentials O-Studio syncs with are in <c>neon.env</c>; this only says whether they are set up, not whether the network answers.</summary>
    static string DescribeCloud()
    {
        try
        {
            var path = Environment.GetEnvironmentVariable(SuiteWire.EnvNeonEnvFile);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                path = SuiteWire.LocalPath("neon.env");
            if (!File.Exists(path))
                return "not set up (no neon.env)";
            var set = File.ReadLines(path).Any(l => l.TrimStart().StartsWith("DATABASE_URL", StringComparison.Ordinal) && l.Contains('=') && l[(l.IndexOf('=') + 1)..].Trim().Length > 0);
            return set ? "credentials found (sync runs inside O-Studio)" : "neon.env has no database address";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "neon.env could not be read";
        }
    }

    static string FormatSize(long bytes) =>
        bytes >= 1 << 20 ? $"{bytes / (1024.0 * 1024.0):0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";
}
