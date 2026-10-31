using System.Text.Json;

namespace Adms.Cloud;

/// <summary>Process-wide Neon sync (started when O-Studio opens a company).</summary>
public static class CloudSyncHub
{
    static OspSyncCoordinator? _coordinator;
    static CloudSyncStore? _store;

    public static CloudSyncStore Store => _store ??= CloudSyncStore.Load();

    public static event Action<string>? LogLine;

    public static IReadOnlyList<string> RecentLog { get; private set; } = Array.Empty<string>();

    public static bool IsRunning => _coordinator is not null;

    public static async Task<bool> StartAsync(CloudSyncHub.AppShellBridge shell)
    {
        await StopAsync().ConfigureAwait(false);
        var settings = NeonSettings.Load();
        if (!settings.IsComplete)
        {
            Emit("Neon not configured — add %LocalAppData%\\O-Studio\\neon.env (see docs/CLOUD-NEON.md).");
            return false;
        }

        _store = CloudSyncStore.Load();
        _coordinator = new OspSyncCoordinator(
            settings,
            _store,
            ownerId: shell.CompanyUuid,
            pushedBy: shell.UserName,
            targets: shell.ListSyncTargets,
            Emit);
        if (!await _coordinator.ConnectAsync().ConfigureAwait(false))
            return false;
        _coordinator.Start();
        Emit("Cloud sync running (push every 2s, pull on Postgres NOTIFY).");
        return true;
    }

    public static async Task StopAsync()
    {
        if (_coordinator is not null)
        {
            await _coordinator.DisposeAsync().ConfigureAwait(false);
            _coordinator = null;
        }
    }

    public static async Task<(bool Ok, string Message)> TestConnectionAsync()
    {
        var settings = NeonSettings.Load();
        if (!settings.IsComplete)
            return (false, "Missing DATABASE_URL or AWS_* variables.");
        await using var client = new OspCloudClient(settings);
        await client.EnsureSchemaAsync().ConfigureAwait(false);
        if (!await client.PingAsync().ConfigureAwait(false))
            return (false, "Postgres ping failed.");
        return (true, "Postgres schema ready.");
    }

    public static async Task<PushResult> PushNowAsync(long studioProjectId, string ospPath) =>
        _coordinator is null
            ? new PushResult(false, "Start cloud sync first.", null)
            : await _coordinator.PushNowAsync(studioProjectId, ospPath).ConfigureAwait(false);

    public static async Task<PullResult> PullNowAsync(long studioProjectId, string ospPath) =>
        _coordinator is null
            ? new PullResult(false, "Start cloud sync first.", null)
            : await _coordinator.PullNowAsync(studioProjectId, ospPath).ConfigureAwait(false);

    static void Emit(string line)
    {
        var list = RecentLog.ToList();
        list.Add($"{DateTime.Now:HH:mm:ss} {line}");
        if (list.Count > 80)
            list.RemoveRange(0, list.Count - 80);
        RecentLog = list;
        LogLine?.Invoke(line);
    }

    /// <summary>Bridge so Adms.Cloud does not reference WinUI.</summary>
    public sealed class AppShellBridge
    {
        public required string CompanyUuid { get; init; }
        public required string UserName { get; init; }
        public required Func<IReadOnlyList<OspSyncCoordinator.SyncTarget>> ListSyncTargets { get; init; }
    }
}
