using System.Text.Json;
using Npgsql;
using Office.Shared;

namespace Adms.Cloud;

/// <summary>Process-wide Neon sync. The launcher runs it from sign-in on; O-Studio only configures it and pushes or pulls a project on request.</summary>
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
        bool connected;
        try
        {
            connected = await _coordinator.ConnectAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Emit(DescribeConnectionFailure(ex));
            connected = false;
        }

        if (!connected)
        {
            // A coordinator that never connected must not count as running.
            await _coordinator.DisposeAsync().ConfigureAwait(false);
            _coordinator = null;
            return false;
        }

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
        try
        {
            var settings = NeonSettings.Load();
            if (!settings.IsComplete)
                return (false, "Missing DATABASE_URL or AWS_* variables. See docs/CLOUD-NEON.md and %LocalAppData%\\O-Studio\\neon.env.");

            await using var client = new OspCloudClient(settings);
            await client.EnsureSchemaAsync().ConfigureAwait(false);
            if (!await client.PingAsync().ConfigureAwait(false))
                return (false, "Postgres ping failed.");
            var (s3Ok, s3Msg) = await client.TestStorageAsync().ConfigureAwait(false);
            if (!s3Ok)
                return (false, s3Msg);
            return (true, "Postgres schema ready. " + s3Msg);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, DescribeConnectionFailure(ex));
        }
    }

    static string DescribeConnectionFailure(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is PostgresException pg)
                return "Postgres: " + pg.MessageText;
            if (cur is NpgsqlException or TimeoutException)
                return "Postgres: " + cur.Message;
        }

        return "Cloud connection failed. " + ex.Message;
    }

    /// <summary>Cloud head for an open <c>.osp</c> (does not require background sync to be running).</summary>
    public static async Task<CloudHead?> GetCloudHeadForOspAsync(string ospPath)
    {
        try
        {
            if (!File.Exists(ospPath) || !OdpFile.LooksLikeOdp(ospPath))
                return null;
            string projectId;
            using (var odp = OdpFile.Open(ospPath))
            {
                projectId = odp.Meta().ProjectId;
            }
            if (string.IsNullOrWhiteSpace(projectId))
                return null;
            var settings = NeonSettings.Load();
            if (!settings.IsComplete)
                return null;
            await using var client = new OspCloudClient(settings);
            return await client.GetHeadAsync(projectId).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Refresh cached cloud revision from Postgres after a failed push or on page load.</summary>
    public static async Task<long?> RefreshCloudRevisionAsync(long studioProjectId, string ospPath)
    {
        var head = await GetCloudHeadForOspAsync(ospPath).ConfigureAwait(false);
        if (head is null)
            return null;
        var store = Store;
        var state = store.Get(studioProjectId);
        state.LastKnownCloudRevision = head.LatestRevision;
        if (!string.IsNullOrEmpty(ospPath))
            state.OspPath = ospPath;
        store.Projects[studioProjectId] = state;
        store.Save();
        return head.LatestRevision;
    }

    /// <summary>
    /// Push one project now. The background sync runs in the launcher, so a window that asks for this (O-Studio's Cloud sync page) usually has no
    /// running coordinator of its own: it then pushes through a short-lived connection, as the signed-in company and user in <paramref name="shell"/>.
    /// </summary>
    public static async Task<PushResult> PushNowAsync(long studioProjectId, string ospPath, AppShellBridge? shell = null)
    {
        if (_coordinator is not null)
            return await _coordinator.PushNowAsync(studioProjectId, ospPath).ConfigureAwait(false);
        var settings = NeonSettings.Load();
        if (!settings.IsComplete)
            return new PushResult(false, "Neon is not configured. See docs/CLOUD-NEON.md.", null);
        if (shell is null)
            return new PushResult(false, "Not connected.", null);
        await using var client = new OspCloudClient(settings);
        await client.EnsureSchemaAsync().ConfigureAwait(false);
        return await client.PushAsync(ospPath, shell.CompanyUuid, shell.UserName, Store.Get(studioProjectId).LastKnownCloudRevision).ConfigureAwait(false);
    }

    public static async Task<PullResult> PullNowAsync(long studioProjectId, string ospPath)
    {
        if (_coordinator is not null)
            return await _coordinator.PullNowAsync(studioProjectId, ospPath).ConfigureAwait(false);
        var settings = NeonSettings.Load();
        if (!settings.IsComplete)
            return new PullResult(false, "Neon is not configured. See docs/CLOUD-NEON.md.", null);
        long localRev = 0;
        if (File.Exists(ospPath))
        {
            using var odp = OdpFile.Open(ospPath);
            localRev = odp.Meta().Revision;
        }

        await using var client = new OspCloudClient(settings);
        await client.EnsureSchemaAsync().ConfigureAwait(false);
        return await client.PullAsync(ospPath, localRev).ConfigureAwait(false);
    }

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
