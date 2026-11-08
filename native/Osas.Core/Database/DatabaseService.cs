using System.Text.Json;
using System.Text.Json.Serialization;

namespace Osas.Core.Database;

/// <summary>An error the HTTP layer sends as <c>{"ok":false,"error":code,"message":...}</c> with <see cref="Status"/>.</summary>
public sealed class ServiceException(int status, string code, string message, IReadOnlyDictionary<string, object?>? extra = null) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public IReadOnlyDictionary<string, object?>? Extra { get; } = extra;
}

/// <summary>The Kivo session behind a request token (what the broker's <c>validate</c> answered).</summary>
public sealed record AuthInfo(string User, string CompanyId, IReadOnlyList<string> Roles);

/// <summary>Something that happened: <c>revision</c> (a project's remote revision moved) or <c>state</c> (the service or a project changed state).</summary>
public sealed record ServiceEvent(string Name, string? ProjectId, object Data);

public sealed record DbHealth(bool Ok, string State, string Sync, long Pending);

public sealed record DbConnectResult(bool Connected, long? RemoteRevision, string Action);

public sealed record DbProjectStatus(long? LocalRevision, long? RemoteRevision, string? LastSyncUtc, string State, long Pending);

public sealed record DbPushResult(long? RemoteRevision, bool LocalOnly = false);

public sealed record DbPullResult(long Revision, string? Sha256, bool Incoming);

/// <summary>
/// The suite's database service (contracts/session-broker.md section 2), without any UI or transport: it keeps a register of the connected <c>.osp</c> files,
/// holds a Kivo session for itself (<c>dbservice</c>, purpose <c>sync</c>) and syncs the registered files to the remote store only while that session is valid.
/// The local file is always the working copy; a pull goes to <c>&lt;name&gt;.osp.incoming</c> and never over the open file.
/// </summary>
public sealed class DatabaseService : IDisposable
{
    public static readonly TimeSpan ValidateCacheLife = TimeSpan.FromSeconds(30);

    sealed class Entry
    {
        public string Id = "";
        public string Name = "";
        public string Path = "";
        /// <summary>The last revision known to be the same on both sides (set by a push, a connect that found them equal, or an adopted pull). Null until known.</summary>
        public long? Base;
        public long RemoteRevision;
        public long IncomingRevision;
        public DateTime? LastSyncUtc;
        public bool Conflict;
        public bool Missing;
        public readonly SemaphoreSlim Gate = new(1, 1);
    }

    sealed class Registry
    {
        public List<RegistryEntry> Projects { get; set; } = [];
    }

    sealed class RegistryEntry
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public long? Base { get; set; }
        public long RemoteRevision { get; set; }
        public DateTime? LastSyncUtc { get; set; }
    }

    readonly IBrokerClient _broker;
    readonly IRemoteStore _remote;
    readonly string? _registryPath;
    readonly Func<DateTime> _now;
    readonly object _gate = new();
    readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (AuthInfo Auth, DateTime At)> _authCache = new();

    BrokerSession? _session;
    bool _starting = true;
    bool _remoteDown;
    int _active;
    string _activity = "idle";
    string _lastState = "starting";
    string _lastBrokerProblem = "";
    CancellationTokenSource? _loop;
    Task? _loopTask;

    /// <param name="registryPath">Where the register of connected projects is kept between runs; null keeps it in memory only.</param>
    public DatabaseService(IBrokerClient broker, IRemoteStore remote, string? registryPath = null, Func<DateTime>? now = null)
    {
        _broker = broker;
        _remote = remote;
        _registryPath = registryPath;
        _now = now ?? (() => DateTime.UtcNow);
        LoadRegistry();
    }

    public event Action<ServiceEvent>? Event;

    public Action<string>? Log { get; set; }

    /// <summary>How long to wait before looking again while signed out (poll <c>status</c>).</summary>
    public TimeSpan SignedOutInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long to wait between sync passes while signed in.</summary>
    public TimeSpan SyncInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Why the last session attempt failed, for display (empty when it did not).</summary>
    public string SessionProblem => _lastBrokerProblem;

    // ---- state ----

    public string State
    {
        get
        {
            if (_starting)
                return "starting";
            if (_session == null)
                return "signed_out";
            if (_remote.CloudDisabled)
                return "local";
            return _remoteDown || !_remote.IsConfigured ? "offline" : "ready";
        }
    }

    public string Sync => State == "offline" ? "offline" : _activity;

    public DbHealth Health() => new(true, State, Sync, PendingTotal());

    long PendingTotal()
    {
        lock (_gate)
            return _entries.Values.Sum(PendingOf);
    }

    long PendingOf(Entry e)
    {
        if (e.Missing)
            return 0;
        try
        {
            var local = OspLocalFile.ReadMeta(e.Path).Revision;
            return Math.Max(0, local - (e.Base ?? e.RemoteRevision));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    void RaiseStateIfChanged()
    {
        var now = State;
        string old;
        lock (_gate)
        {
            old = _lastState;
            _lastState = now;
        }

        if (old != now)
        {
            if (now == "signed_out")
                lock (_gate)
                    _authCache.Clear();
            Event?.Invoke(new ServiceEvent("state", null, new { state = now, sync = Sync }));
        }
    }

    // ---- session of the service itself ----

    /// <summary>Check the service's own session, and ask Kivo for one (non-interactive) when it has none and Kivo is signed in.</summary>
    public async Task RefreshSessionAsync(CancellationToken ct = default)
    {
        try
        {
            if (_session != null)
            {
                BrokerValidation v;
                try
                {
                    v = await _broker.ValidateAsync(_session.Token, ct).ConfigureAwait(false);
                }
                catch (BrokerException)
                {
                    v = new BrokerValidation(false, "", "", [], null);
                }

                if (v.Valid)
                {
                    _lastBrokerProblem = "";
                    return;
                }

                _session = null;
                Log?.Invoke("Kivo session ended; sync paused.");
            }

            var status = await _broker.StatusAsync(ct).ConfigureAwait(false);
            if (!status.SignedIn)
            {
                _lastBrokerProblem = "Kivo is not signed in.";
                return;
            }

            _session = await _broker.AskSessionAsync("dbservice", "sync", null, interactive: false, ct).ConfigureAwait(false);
            _lastBrokerProblem = "";
            Log?.Invoke($"Kivo session for {_session.User}; sync can run.");
        }
        catch (BrokerException ex)
        {
            _session = null;
            _lastBrokerProblem = ex.Code == "kivo_unavailable" ? "Kivo is not running." : ex.Message;
        }
        finally
        {
            _starting = false;
            RaiseStateIfChanged();
        }
    }

    // ---- request tokens ----

    /// <summary>Validate a request token with the broker (a good answer is kept for 30 s). Throws 401 <see cref="ServiceException"/>.</summary>
    public async Task<AuthInfo> AuthenticateAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ServiceException(401, "bad_token", "A Bearer session token is required. Ask Kivo for one.");
        lock (_gate)
        {
            if (_authCache.TryGetValue(token, out var hit) && _now() - hit.At < ValidateCacheLife)
                return hit.Auth;
        }

        BrokerValidation v;
        try
        {
            v = await _broker.ValidateAsync(token, ct).ConfigureAwait(false);
        }
        catch (BrokerException ex)
        {
            lock (_gate)
                _authCache.Remove(token);
            throw ex.Code == "kivo_unavailable"
                ? new ServiceException(401, "signed_out", "Kivo is not running, so the session cannot be checked.")
                : new ServiceException(401, ex.Code is "expired" ? "expired" : "bad_token", ex.Message);
        }

        if (!v.Valid)
        {
            lock (_gate)
                _authCache.Remove(token);
            throw new ServiceException(401, "bad_token", "The session token is not valid. Ask Kivo for a new one.");
        }

        var auth = new AuthInfo(v.User, v.CompanyId, v.Roles);
        lock (_gate)
        {
            var cutoff = _now() - ValidateCacheLife;
            foreach (var stale in _authCache.Where(kv => kv.Value.At < cutoff).Select(kv => kv.Key).ToList())
                _authCache.Remove(stale);
            _authCache[token] = (auth, _now());
        }

        // A valid Kivo session exists, so pick it up now instead of at the next poll.
        if (_session == null)
            await RefreshSessionAsync(ct).ConfigureAwait(false);
        return auth;
    }

    // ---- operations ----

    public async Task<DbConnectResult> ConnectAsync(AuthInfo auth, string projectId, string? name, string ospPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(ospPath))
            throw new ServiceException(400, "bad_request", "project_id and osp_path are required.");
        string full;
        OspLocalFile.Meta meta;
        try
        {
            full = Path.GetFullPath(ospPath);
            meta = OspLocalFile.ReadMeta(full);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or NotSupportedException or UnauthorizedAccessException)
        {
            throw new ServiceException(400, "bad_request", ex.Message);
        }

        if (meta.ProjectId.Length > 0 && !string.Equals(meta.ProjectId, projectId, StringComparison.OrdinalIgnoreCase))
            throw new ServiceException(400, "bad_request", $"The file holds project {meta.ProjectId}, not {projectId}.");

        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(projectId, out entry!))
                _entries[projectId] = entry = new Entry { Id = projectId };
            entry.Name = string.IsNullOrWhiteSpace(name) ? (meta.Name.Length > 0 ? meta.Name : Path.GetFileNameWithoutExtension(full)) : name!;
            entry.Path = full;
            entry.Missing = false;
        }

        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RemoteHead? head = null;
            var reachable = false;
            if (_remote.IsConfigured)
            {
                try
                {
                    head = await _remote.GetHeadAsync(projectId, ct).ConfigureAwait(false);
                    reachable = true;
                    _remoteDown = false;
                }
                catch (RemoteUnavailableException ex)
                {
                    _remoteDown = true;
                    Log?.Invoke("Remote not reachable: " + ex.Message);
                }
            }
            else
            {
                _remoteDown = true;
            }

            string action;
            long? remoteRev = null;
            if (!reachable)
            {
                action = "none";
            }
            else
            {
                var remote = head?.Revision ?? 0;
                remoteRev = remote;
                entry.RemoteRevision = remote;
                action = Decide(entry, meta.Revision, remote);
            }

            entry.Conflict = action == "conflict";
            SaveRegistry();
            RaiseStateIfChanged();
            return new DbConnectResult(true, remoteRev, action);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>What a connect or a sync pass should do for a project at <paramref name="local"/> against <paramref name="remote"/>. Sets the base when it was not known.</summary>
    static string Decide(Entry e, long local, long remote)
    {
        if (local == remote)
        {
            e.Base = local;
            return "none";
        }

        e.Base ??= remote < local ? remote : local;
        var localMoved = local > e.Base;
        var remoteMoved = remote > e.Base;
        if (localMoved && remoteMoved)
            return "conflict";
        if (localMoved)
            return "push";
        if (remoteMoved)
            return "pull";
        return "none";
    }

    public DbProjectStatus GetProject(string projectId)
    {
        var entry = Find(projectId);
        long? local = null;
        var missing = false;
        try
        {
            local = OspLocalFile.ReadMeta(entry.Path).Revision;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            missing = true;
        }

        var pending = local is long l ? Math.Max(0, l - (entry.Base ?? entry.RemoteRevision)) : 0;
        string state;
        if (missing)
            state = "missing";
        else if (entry.Conflict)
            state = "conflict";
        else if (entry.IncomingRevision > (local ?? 0) && File.Exists(IncomingPath(entry)))
            state = "incoming";
        else if (State == "offline")
            state = "offline";
        else
            state = pending > 0 ? "pending" : "synced";
        return new DbProjectStatus(local, entry.RemoteRevision, entry.LastSyncUtc?.ToString("o"), state, pending);
    }

    public async Task<DbPushResult> PushAsync(AuthInfo auth, string projectId, long? revision, string? sha256, CancellationToken ct = default)
    {
        var entry = Find(projectId);
        // Project files stay on this PC: the local file is the shared database, so there is nothing to send.
        if (_remote.CloudDisabled)
            return new DbPushResult(null, true);
        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var r = await PushCoreAsync(entry, new RemoteContext(auth.CompanyId, auth.User), revision, sha256, ct).ConfigureAwait(false);
            return new DbPushResult(r);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    async Task<long> PushCoreAsync(Entry entry, RemoteContext who, long? expectedLocalRevision, string? sha256, CancellationToken ct)
    {
        OspLocalFile.Meta meta;
        try
        {
            meta = OspLocalFile.ReadMeta(entry.Path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new ServiceException(404, "not_found", "The project file cannot be read: " + ex.Message);
        }

        if (expectedLocalRevision is long want && want != meta.Revision)
            throw new ServiceException(409, "revision_conflict", $"The file is at revision {meta.Revision}, not {want}. Save again and retry.", new Dictionary<string, object?> { ["local_revision"] = meta.Revision });
        if (!string.IsNullOrWhiteSpace(sha256) && !string.Equals(OspLocalFile.Sha256(entry.Path), sha256, StringComparison.OrdinalIgnoreCase))
            throw new ServiceException(409, "revision_conflict", "The file changed after the app computed sha256. Save again and retry.", new Dictionary<string, object?> { ["local_revision"] = meta.Revision });
        if (!_remote.IsConfigured)
            throw new ServiceException(503, "offline", "The cloud database is not set up on this PC (neon.env).");
        if (_session == null)
            throw new ServiceException(401, "signed_out", "Kivo is not signed in; sync is paused.");

        Begin("pushing");
        try
        {
            RemotePushResult result;
            try
            {
                result = await _remote.PushAsync(who, entry.Path, entry.RemoteRevision, ct).ConfigureAwait(false);
                _remoteDown = false;
            }
            catch (RemoteUnavailableException ex)
            {
                _remoteDown = true;
                throw new ServiceException(503, "offline", ex.Message);
            }

            if (result.Conflict)
            {
                entry.RemoteRevision = result.Revision;
                entry.Conflict = meta.Revision > (entry.Base ?? 0);
                Raise(new ServiceEvent("revision", entry.Id, new { project_id = entry.Id, remote_revision = result.Revision }));
                throw new ServiceException(409, "revision_conflict", $"The cloud moved on to revision {result.Revision}. Pull first.", new Dictionary<string, object?> { ["remote_revision"] = result.Revision });
            }

            if (!result.Ok)
                throw new ServiceException(502, "bad_request", result.Message);
            entry.RemoteRevision = result.Revision;
            entry.Base = result.Revision;
            entry.Conflict = false;
            entry.LastSyncUtc = _now();
            SaveRegistry();
            return result.Revision;
        }
        finally
        {
            End();
        }
    }

    public async Task<DbPullResult> PullAsync(AuthInfo auth, string projectId, CancellationToken ct = default)
    {
        var entry = Find(projectId);
        if (_remote.CloudDisabled)
            return new DbPullResult(0, null, false);
        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await PullCoreAsync(entry, ct).ConfigureAwait(false);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    async Task<DbPullResult> PullCoreAsync(Entry entry, CancellationToken ct)
    {
        if (!_remote.IsConfigured)
            throw new ServiceException(503, "offline", "The cloud database is not set up on this PC (neon.env).");
        if (_session == null)
            throw new ServiceException(401, "signed_out", "Kivo is not signed in; sync is paused.");
        long local;
        try
        {
            local = OspLocalFile.ReadMeta(entry.Path).Revision;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new ServiceException(404, "not_found", "The project file cannot be read: " + ex.Message);
        }

        Begin("pulling");
        try
        {
            RemoteHead? head;
            try
            {
                head = await _remote.GetHeadAsync(entry.Id, ct).ConfigureAwait(false);
                _remoteDown = false;
            }
            catch (RemoteUnavailableException ex)
            {
                _remoteDown = true;
                throw new ServiceException(503, "offline", ex.Message);
            }

            if (head == null)
                throw new ServiceException(404, "not_found", "This project was never pushed to the cloud.");
            entry.RemoteRevision = head.Revision;
            if (head.Revision <= local)
                return new DbPullResult(local, null, false);

            var incoming = IncomingPath(entry);
            var part = incoming + ".part";
            try
            {
                var pulled = await _remote.PullToAsync(entry.Id, part, ct).ConfigureAwait(false);
                if (!pulled.Ok)
                    throw new ServiceException(502, "bad_request", pulled.Message);
                File.Move(part, incoming, overwrite: true);
                var sha = OspLocalFile.Sha256(incoming);
                entry.IncomingRevision = pulled.Revision;
                entry.RemoteRevision = pulled.Revision;
                entry.LastSyncUtc = _now();
                SaveRegistry();
                Raise(new ServiceEvent("revision", entry.Id, new { project_id = entry.Id, remote_revision = pulled.Revision }));
                return new DbPullResult(pulled.Revision, sha, true);
            }
            catch (RemoteUnavailableException ex)
            {
                _remoteDown = true;
                throw new ServiceException(503, "offline", ex.Message);
            }
            finally
            {
                try { if (File.Exists(part)) File.Delete(part); } catch (IOException) { }
            }
        }
        finally
        {
            End();
        }
    }

    // ---- background pass ----

    /// <summary>One pass: look after the service's session and, while it is valid, push, fetch or flag every registered project.</summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        await RefreshSessionAsync(ct).ConfigureAwait(false);
        var session = _session;
        if (session == null || !_remote.IsConfigured)
        {
            RaiseStateIfChanged();
            return;
        }

        List<Entry> list;
        lock (_gate)
            list = _entries.Values.ToList();
        var who = new RemoteContext(session.CompanyId, session.User);
        foreach (var entry in list)
        {
            if (ct.IsCancellationRequested || _session == null)
                break;
            if (!await entry.Gate.WaitAsync(0, ct).ConfigureAwait(false))
                continue;
            try
            {
                await SyncEntryAsync(entry, who, ct).ConfigureAwait(false);
            }
            catch (ServiceException ex)
            {
                Log?.Invoke($"{entry.Name}: {ex.Message}");
            }
            catch (RemoteUnavailableException)
            {
                _remoteDown = true;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Log?.Invoke($"{entry.Name}: {ex.Message}");
            }
            finally
            {
                entry.Gate.Release();
            }
        }

        RaiseStateIfChanged();
    }

    async Task SyncEntryAsync(Entry entry, RemoteContext who, CancellationToken ct)
    {
        OspLocalFile.Meta meta;
        try
        {
            meta = OspLocalFile.ReadMeta(entry.Path);
            if (entry.Missing)
            {
                entry.Missing = false;
                Raise(new ServiceEvent("state", entry.Id, new { project_id = entry.Id, state = "synced" }));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            if (!entry.Missing)
            {
                entry.Missing = true;
                Raise(new ServiceEvent("state", entry.Id, new { project_id = entry.Id, state = "missing" }));
            }

            return;
        }

        RemoteHead? head;
        try
        {
            head = await _remote.GetHeadAsync(entry.Id, ct).ConfigureAwait(false);
            _remoteDown = false;
        }
        catch (RemoteUnavailableException)
        {
            _remoteDown = true;
            return;
        }

        var remote = head?.Revision ?? 0;
        if (remote != entry.RemoteRevision)
        {
            entry.RemoteRevision = remote;
            Raise(new ServiceEvent("revision", entry.Id, new { project_id = entry.Id, remote_revision = remote }));
        }

        var wasConflict = entry.Conflict;
        var action = Decide(entry, meta.Revision, remote);
        entry.Conflict = action == "conflict";
        if (entry.Conflict != wasConflict)
            Raise(new ServiceEvent("state", entry.Id, new { project_id = entry.Id, state = entry.Conflict ? "conflict" : "synced" }));

        switch (action)
        {
            case "push":
                await PushCoreAsync(entry, who, meta.Revision, null, ct).ConfigureAwait(false);
                break;
            case "pull" when entry.IncomingRevision < remote:
                await PullCoreAsync(entry, ct).ConfigureAwait(false);
                break;
            case "none":
                entry.LastSyncUtc ??= _now();
                break;
        }
    }

    // ---- events and loop ----

    void Raise(ServiceEvent e) => Event?.Invoke(e);

    void Begin(string activity)
    {
        Interlocked.Increment(ref _active);
        _activity = activity;
        Event?.Invoke(new ServiceEvent("state", null, new { state = State, sync = activity }));
    }

    void End()
    {
        if (Interlocked.Decrement(ref _active) <= 0)
        {
            _activity = "idle";
            Event?.Invoke(new ServiceEvent("state", null, new { state = State, sync = Sync }));
        }
    }

    /// <summary>Start the background loop (session poll and sync passes).</summary>
    public void Start()
    {
        if (_loopTask != null)
            return;
        _loop = new CancellationTokenSource();
        var ct = _loop.Token;
        _loopTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await TickAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log?.Invoke("Database service: " + ex.Message);
                }

                try
                {
                    await Task.Delay(_session == null ? SignedOutInterval : SyncInterval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, ct);
    }

    public void Dispose()
    {
        _loop?.Cancel();
        try { _loopTask?.Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { /* shutting down */ }
        _loop?.Dispose();
        _loop = null;
        _loopTask = null;
    }

    // ---- helpers ----

    Entry Find(string projectId)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(projectId, out var e))
                return e;
        }

        throw new ServiceException(404, "not_found", $"Project {projectId} is not connected. Call connect first.");
    }

    static string IncomingPath(Entry e) => e.Path + ".incoming";

    static readonly JsonSerializerOptions RegistryJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    void LoadRegistry()
    {
        if (_registryPath == null || !File.Exists(_registryPath))
            return;
        try
        {
            var reg = JsonSerializer.Deserialize<Registry>(File.ReadAllText(_registryPath), RegistryJson);
            foreach (var p in reg?.Projects ?? [])
            {
                if (p.Id.Length == 0 || p.Path.Length == 0)
                    continue;
                _entries[p.Id] = new Entry { Id = p.Id, Name = p.Name, Path = p.Path, Base = p.Base, RemoteRevision = p.RemoteRevision, LastSyncUtc = p.LastSyncUtc };
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log?.Invoke("Project register could not be read: " + ex.Message);
        }
    }

    void SaveRegistry()
    {
        if (_registryPath == null)
            return;
        try
        {
            Registry reg;
            lock (_gate)
            {
                reg = new Registry
                {
                    Projects = _entries.Values.Select(e => new RegistryEntry { Id = e.Id, Name = e.Name, Path = e.Path, Base = e.Base, RemoteRevision = e.RemoteRevision, LastSyncUtc = e.LastSyncUtc }).ToList(),
                };
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_registryPath)!);
            var tmp = _registryPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(reg, RegistryJson));
            File.Move(tmp, _registryPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log?.Invoke("Project register could not be saved: " + ex.Message);
        }
    }
}
