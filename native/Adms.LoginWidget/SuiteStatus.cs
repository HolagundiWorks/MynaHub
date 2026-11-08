using Microsoft.UI.Dispatching;
using Osas.Core;
using Osas.Core.Agent;

namespace Adms.LoginWidget;

/// <summary>
/// What the launcher shows: the AI Core (the Olmo host on loopback, hosted by the launcher). It looks again every 5 seconds and on request.
/// </summary>
internal sealed class SuiteStatus
{
    internal static SuiteStatus Instance { get; } = new();

    internal string Ai { get; private set; } = "checking…";

    internal event Action? Changed;

    DispatcherQueueTimer? _timer;
    int _running;
    int _round;

    internal void Start(DispatcherQueue queue)
    {
        if (_timer != null)
            return;
        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(5);
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    internal async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
            return;
        try
        {
            // The AI check is heavy, so a ready AI is looked at about every 20 seconds.
            if (!Ai.StartsWith("ready", StringComparison.Ordinal) || _round++ % 4 == 0)
                Ai = await ProbeAiAsync().ConfigureAwait(true);
            WriteLog();
            Changed?.Invoke();
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>
    /// The latest status as plain text, <c>launcher-status.txt</c> in the suite's local folder, so it can be read without opening the menu. It is overwritten
    /// each time. The <c>Cloud sync:</c> line is kept for Kivo's Cloud sync page, which shows it.
    /// </summary>
    void WriteLog()
    {
        try
        {
            var path = SuiteWire.LocalPath("launcher-status.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}AI agent: {Ai}");
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
}
