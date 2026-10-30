using Npgsql;
using Office.Shared;

namespace Adms.Cloud;

/// <summary>
/// Background push when local <c>meta.revision</c> rises; pull when Postgres NOTIFY reports a newer revision.
/// </summary>
public sealed class OspSyncCoordinator : IAsyncDisposable
{
    readonly NeonSettings _settings;
    readonly CloudSyncStore _store;
    readonly string _ownerId;
    readonly string _pushedBy;
    readonly Func<IReadOnlyList<SyncTarget>> _targets;
    readonly Action<string> _log;

    OspCloudClient? _client;
    CancellationTokenSource? _cts;
    Task? _listenTask;
    Task? _pollTask;

    public sealed record SyncTarget(long StudioProjectId, string Name, string OspPath);

    public OspSyncCoordinator(
        NeonSettings settings,
        CloudSyncStore store,
        string ownerId,
        string pushedBy,
        Func<IReadOnlyList<SyncTarget>> targets,
        Action<string> log)
    {
        _settings = settings;
        _store = store;
        _ownerId = ownerId;
        _pushedBy = pushedBy;
        _targets = targets;
        _log = log;
    }

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        if (!_settings.IsComplete)
        {
            _log("Neon is not configured. Copy docs/CLOUD-NEON.md settings into %LocalAppData%\\OSAS\\neon.env");
            return false;
        }
        await DisposeClientAsync().ConfigureAwait(false);
        _client = new OspCloudClient(_settings);
        await _client.EnsureSchemaAsync(ct).ConfigureAwait(false);
        if (!await _client.PingAsync(ct).ConfigureAwait(false))
        {
            _log("Could not reach Lakebase Postgres.");
            await DisposeClientAsync().ConfigureAwait(false);
            return false;
        }
        _log("Connected to Neon (schema ready).");
        return true;
    }

    public void Start()
    {
        Stop();
        if (_client is null)
            return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _pollTask = Task.Run(() => PollLoopAsync(token), token);
        _listenTask = Task.Run(() => ListenLoopAsync(token), token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listenTask?.Wait(TimeSpan.FromSeconds(2)); } catch { /* shutting down */ }
        try { _pollTask?.Wait(TimeSpan.FromSeconds(2)); } catch { /* shutting down */ }
        _cts?.Dispose();
        _cts = null;
        _listenTask = null;
        _pollTask = null;
    }

    async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickPushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log("Push check: " + ex.Message);
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    async Task ListenLoopAsync(CancellationToken ct)
    {
        var url = _settings.DatabaseUrlUnpooled ?? _settings.DatabaseUrl;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var conn = new NpgsqlConnection(url);
                await conn.OpenAsync(ct).ConfigureAwait(false);
                conn.Notification += (_, e) =>
                {
                    if (e.Channel != "osp_revision" || string.IsNullOrEmpty(e.Payload))
                        return;
                    var parts = e.Payload.Split(':', 2);
                    if (parts.Length != 2 || !long.TryParse(parts[1], out var rev))
                        return;
                    _ = Task.Run(async () =>
                    {
                        try { await OnRemoteRevisionAsync(parts[0], rev, ct).ConfigureAwait(false); }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _log("Pull: " + ex.Message);
                        }
                    }, ct);
                };
                await using (var listen = new NpgsqlCommand("LISTEN osp_revision", conn))
                    await listen.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                while (!ct.IsCancellationRequested)
                    await conn.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log("Realtime listen paused: " + ex.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    async Task TickPushAsync(CancellationToken ct)
    {
        if (_client is null)
            return;
        foreach (var t in _targets())
        {
            var state = _store.Get(t.StudioProjectId);
            if (!state.Enabled || string.IsNullOrWhiteSpace(t.OspPath) || !File.Exists(t.OspPath))
                continue;

            long localRev;
            string projectId;
            using (var odp = OdpFile.Open(t.OspPath))
            {
                var meta = odp.Meta();
                localRev = meta.Revision;
                projectId = meta.ProjectId;
            }

            state.OspPath = t.OspPath;
            state.OspProjectId = projectId;
            if (localRev <= state.LastLocalRevision)
            {
                _store.Projects[t.StudioProjectId] = state;
                continue;
            }

            var push = await _client.PushAsync(
                t.OspPath,
                _ownerId,
                _pushedBy,
                state.LastKnownCloudRevision,
                ct).ConfigureAwait(false);
            if (push.Ok && push.NewRevision is long nr)
            {
                state.LastLocalRevision = nr;
                state.LastKnownCloudRevision = nr;
                _log($"{t.Name}: {push.Message}");
            }
            else if (!push.Ok)
                _log($"{t.Name}: {push.Message}");

            _store.Projects[t.StudioProjectId] = state;
            _store.Save();
        }
    }

    async Task OnRemoteRevisionAsync(string ospProjectId, long cloudRev, CancellationToken ct)
    {
        if (_client is null)
            return;
        foreach (var t in _targets())
        {
            var state = _store.Get(t.StudioProjectId);
            if (!state.Enabled || string.IsNullOrWhiteSpace(t.OspPath))
                continue;
            if (state.OspProjectId != ospProjectId && !string.IsNullOrEmpty(state.OspProjectId))
                continue;
            if (cloudRev <= state.LastKnownCloudRevision)
                continue;

            long localRev = 0;
            if (File.Exists(t.OspPath))
            {
                using var odp = OdpFile.Open(t.OspPath);
                localRev = odp.Meta().Revision;
            }
            if (cloudRev <= localRev)
            {
                state.LastKnownCloudRevision = cloudRev;
                _store.Projects[t.StudioProjectId] = state;
                _store.Save();
                continue;
            }

            var pull = await _client.PullAsync(t.OspPath, localRev, ct).ConfigureAwait(false);
            if (pull.Ok && pull.NewRevision is long nr)
            {
                state.LastKnownCloudRevision = nr;
                state.LastLocalRevision = nr;
                _log($"{t.Name}: {pull.Message}");
            }
            else if (!pull.Ok)
                _log($"{t.Name}: {pull.Message}");
            _store.Projects[t.StudioProjectId] = state;
            _store.Save();
        }
    }

    public async Task<PushResult> PushNowAsync(long studioProjectId, string ospPath, CancellationToken ct = default)
    {
        if (_client is null)
            return new PushResult(false, "Not connected.", null);
        var state = _store.Get(studioProjectId);
        return await _client.PushAsync(ospPath, _ownerId, _pushedBy, state.LastKnownCloudRevision, ct).ConfigureAwait(false);
    }

    public async Task<PullResult> PullNowAsync(long studioProjectId, string ospPath, CancellationToken ct = default)
    {
        if (_client is null)
            return new PullResult(false, "Not connected.", null);
        long localRev = 0;
        if (File.Exists(ospPath))
        {
            using var odp = OdpFile.Open(ospPath);
            localRev = odp.Meta().Revision;
        }
        return await _client.PullAsync(ospPath, localRev, ct).ConfigureAwait(false);
    }

    async Task DisposeClientAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        await DisposeClientAsync().ConfigureAwait(false);
    }
}
