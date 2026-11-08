using Xunit;
using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Osas.Core.Database;

namespace Osas.Core.Tests;

/// <summary>The launcher's database service (contracts/session-broker.md sections 2 and 4), against a fake Kivo broker and a fake remote store.</summary>
public sealed class DatabaseServiceTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "osas-dbsvc-" + Guid.NewGuid().ToString("N"));
    readonly FakeBroker _broker = new();
    readonly FakeRemote _remote = new();
    readonly DatabaseService _service;
    readonly DatabaseHttpServer _server;
    readonly HttpClient _http = new();

    public DatabaseServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _service = new DatabaseService(_broker, _remote, Path.Combine(_dir, "projects.json"));
        _server = new DatabaseHttpServer(_service, 0);
        Assert.True(_server.Start(), _server.Problem);
    }

    public void Dispose()
    {
        _server.Dispose();
        _service.Dispose();
        _http.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ---- health ----

    [Fact]
    public async Task Health_needs_no_token_and_follows_the_kivo_session()
    {
        var starting = await GetJson("/v1/health");
        Assert.Equal("starting", starting.GetProperty("state").GetString());

        await _service.RefreshSessionAsync();
        var signedOut = await GetJson("/v1/health");
        Assert.True(signedOut.GetProperty("ok").GetBoolean());
        Assert.Equal("signed_out", signedOut.GetProperty("state").GetString());

        _broker.SignedIn = true;
        await _service.RefreshSessionAsync();
        var ready = await GetJson("/v1/health");
        Assert.Equal("ready", ready.GetProperty("state").GetString());
        Assert.Equal("idle", ready.GetProperty("sync").GetString());
        Assert.Equal(0, ready.GetProperty("pending").GetInt64());
        Assert.Equal(("dbservice", "sync", false), _broker.LastAsk);
    }

    [Fact]
    public async Task Health_says_offline_when_the_remote_is_not_reachable()
    {
        _broker.SignedIn = true;
        await _service.RefreshSessionAsync();
        _remote.Down = true;
        var (osp, id) = NewProject("p1", 1);
        _broker.Tokens.Add("tok");
        var (code, _) = await Post("/v1/projects/connect", "tok", new { project_id = id, name = "p1", osp_path = osp, revision = 1 });
        Assert.Equal(HttpStatusCode.OK, code); // offline first: the file is registered anyway
        Assert.Equal("offline", (await GetJson("/v1/health")).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Signing_out_of_kivo_pauses_the_service_and_signing_in_again_resumes_it()
    {
        _broker.SignedIn = true;
        await _service.RefreshSessionAsync();
        Assert.Equal("ready", _service.State);
        _broker.SignedIn = false;
        _broker.Tokens.Clear();
        await _service.RefreshSessionAsync();
        Assert.Equal("signed_out", _service.State);
        _broker.SignedIn = true;
        await _service.RefreshSessionAsync();
        Assert.Equal("ready", _service.State);
    }

    // ---- tokens ----

    [Fact]
    public async Task No_token_and_bad_token_are_401()
    {
        var (osp, id) = NewProject("p1", 1);
        var body = new { project_id = id, name = "p1", osp_path = osp, revision = 1 };

        var (none, noneBody) = await Post("/v1/projects/connect", null, body);
        Assert.Equal(HttpStatusCode.Unauthorized, none);
        Assert.False(noneBody.GetProperty("ok").GetBoolean());
        Assert.Equal("bad_token", noneBody.GetProperty("error").GetString());

        var (bad, badBody) = await Post("/v1/projects/connect", "nope", body);
        Assert.Equal(HttpStatusCode.Unauthorized, bad);
        Assert.Equal("bad_token", badBody.GetProperty("error").GetString());

        var (get, _) = await Send(HttpMethod.Get, "/v1/projects/" + id, null, null);
        Assert.Equal(HttpStatusCode.Unauthorized, get);
        var (push, _) = await Post($"/v1/projects/{id}/push", "nope", new { revision = 1 });
        Assert.Equal(HttpStatusCode.Unauthorized, push);
    }

    [Fact]
    public async Task A_broker_that_is_not_there_is_401_signed_out()
    {
        _broker.Unavailable = true;
        var (osp, id) = NewProject("p1", 1);
        var (code, body) = await Post("/v1/projects/connect", "tok", new { project_id = id, name = "p1", osp_path = osp, revision = 1 });
        Assert.Equal(HttpStatusCode.Unauthorized, code);
        Assert.Equal("signed_out", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_good_answer_from_the_broker_is_kept_for_30_seconds()
    {
        _broker.SignedIn = true;
        _broker.Tokens.Add("tok");
        var (osp, id) = NewProject("p1", 1);
        var body = new { project_id = id, name = "p1", osp_path = osp, revision = 1 };
        await Post("/v1/projects/connect", "tok", body);
        var after1 = _broker.ValidateCalls;
        await Post("/v1/projects/connect", "tok", body);
        await Send(HttpMethod.Get, "/v1/projects/" + id, "tok", null);
        Assert.Equal(after1, _broker.ValidateCalls);

        var clock = DateTime.UtcNow;
        var timed = new DatabaseService(_broker, _remote, null, () => clock);
        await timed.AuthenticateAsync("tok");
        var n = _broker.ValidateCalls;
        await timed.AuthenticateAsync("tok");
        Assert.Equal(n, _broker.ValidateCalls);
        clock = clock.AddSeconds(31);
        await timed.AuthenticateAsync("tok");
        Assert.Equal(n + 1, _broker.ValidateCalls);
        timed.Dispose();
    }

    [Fact]
    public async Task Browser_requests_are_refused()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _server.BaseUrl + "/v1/health");
        req.Headers.Add("Origin", "https://evil.example");
        using var res = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ---- connect, push, pull ----

    [Fact]
    public async Task Connect_then_push_sends_the_file_and_pull_leaves_the_open_file_alone()
    {
        await SignIn();
        var (osp, id) = NewProject("p1", 3);

        var (c, connect) = await Post("/v1/projects/connect", "tok", new { project_id = id, name = "p1", osp_path = osp, revision = 3 });
        Assert.Equal(HttpStatusCode.OK, c);
        Assert.True(connect.GetProperty("connected").GetBoolean());
        Assert.Equal(0, connect.GetProperty("remote_revision").GetInt64());
        Assert.Equal("push", connect.GetProperty("action").GetString());

        var (p, push) = await Post($"/v1/projects/{id}/push", "tok", new { revision = 3, sha256 = OspLocalFile.Sha256(osp) });
        Assert.Equal(HttpStatusCode.OK, p);
        Assert.Equal(3, push.GetProperty("remote_revision").GetInt64());
        Assert.Equal(3, _remote.Heads[id].Revision);

        var (_, status) = await Send(HttpMethod.Get, "/v1/projects/" + id, "tok", null);
        Assert.Equal(3, status.GetProperty("local_revision").GetInt64());
        Assert.Equal(3, status.GetProperty("remote_revision").GetInt64());
        Assert.Equal("synced", status.GetProperty("state").GetString());
        Assert.Equal(0, status.GetProperty("pending").GetInt64());

        // Another device pushes revision 5; our file is untouched at 3.
        _remote.Seed(id, MakeOsp(Path.Combine(_dir, "other.osp"), id, "p1", 5));
        var before = File.ReadAllBytes(osp);
        var (q, pull) = await Post($"/v1/projects/{id}/pull", "tok", new { });
        Assert.Equal(HttpStatusCode.OK, q);
        Assert.Equal(5, pull.GetProperty("revision").GetInt64());
        Assert.True(pull.GetProperty("incoming").GetBoolean());
        var incoming = osp + ".incoming";
        Assert.True(File.Exists(incoming));
        Assert.Equal(OspLocalFile.Sha256(incoming), pull.GetProperty("sha256").GetString());
        Assert.Equal(before, File.ReadAllBytes(osp));
        Assert.Equal(3, OspLocalFile.ReadMeta(osp).Revision);
        Assert.Equal(5, OspLocalFile.ReadMeta(incoming).Revision);
        Assert.False(File.Exists(incoming + ".part"));

        var (_, after) = await Send(HttpMethod.Get, "/v1/projects/" + id, "tok", null);
        Assert.Equal("incoming", after.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Connect_tells_a_file_that_is_behind_to_pull_and_a_diverged_one_to_conflict()
    {
        await SignIn();
        var (osp, id) = NewProject("p1", 2);
        _remote.Seed(id, MakeOsp(Path.Combine(_dir, "other.osp"), id, "p1", 4));
        var (_, behind) = await Post("/v1/projects/connect", "tok", new { project_id = id, name = "p1", osp_path = osp, revision = 2 });
        Assert.Equal("pull", behind.GetProperty("action").GetString());
        Assert.Equal(4, behind.GetProperty("remote_revision").GetInt64());

        // The file and the cloud were equal at 4; both move on.
        SetRevision(osp, 4);
        await Post("/v1/projects/connect", "tok", new { project_id = id, name = "p1", osp_path = osp, revision = 4 });
        SetRevision(osp, 5);
        _remote.Seed(id, MakeOsp(Path.Combine(_dir, "other2.osp"), id, "p1", 6));
        var (_, diverged) = await Post("/v1/projects/connect", "tok", new { project_id = id, name = "p1", osp_path = osp, revision = 5 });
        Assert.Equal("conflict", diverged.GetProperty("action").GetString());
    }

    [Fact]
    public async Task Push_is_409_when_the_remote_moved()
    {
        await SignIn();
        var (osp, id) = NewProject("p1", 2);
        await Post("/v1/projects/connect", "tok", new { project_id = id, name = "p1", osp_path = osp, revision = 2 });
        await Post($"/v1/projects/{id}/push", "tok", new { revision = 2 });

        // Another device pushes revision 4 while this one saves revision 3.
        _remote.Seed(id, MakeOsp(Path.Combine(_dir, "other.osp"), id, "p1", 4));
        SetRevision(osp, 3);
        var (code, body) = await Post($"/v1/projects/{id}/push", "tok", new { revision = 3 });
        Assert.Equal(HttpStatusCode.Conflict, code);
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Equal("revision_conflict", body.GetProperty("error").GetString());
        Assert.Equal(4, body.GetProperty("remote_revision").GetInt64());
        Assert.Equal(4, _remote.Heads[id].Revision); // not overwritten
    }

    [Fact]
    public async Task Push_is_409_when_the_file_is_not_at_the_revision_the_app_named()
    {
        await SignIn();
        var (osp, id) = NewProject("p1", 2);
        await Post("/v1/projects/connect", "tok", new { project_id = id, name = "p1", osp_path = osp, revision = 2 });
        var (code, body) = await Post($"/v1/projects/{id}/push", "tok", new { revision = 1 });
        Assert.Equal(HttpStatusCode.Conflict, code);
        Assert.Equal("revision_conflict", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Unknown_project_is_404_and_a_wrong_project_id_is_400()
    {
        await SignIn();
        var (code, _) = await Send(HttpMethod.Get, "/v1/projects/nope", "tok", null);
        Assert.Equal(HttpStatusCode.NotFound, code);
        var (osp, _) = NewProject("p1", 1);
        var (bad, _) = await Post("/v1/projects/connect", "tok", new { project_id = "someone-else", name = "x", osp_path = osp, revision = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, bad);
    }

    // ---- the background pass ----

    [Fact]
    public async Task The_background_pass_pushes_new_revisions_only_while_a_session_exists()
    {
        await SignIn();
        var (osp, id) = NewProject("p1", 1);
        await Post("/v1/projects/connect", "tok", new { project_id = id, name = "p1", osp_path = osp, revision = 1 });
        await _service.TickAsync();
        Assert.Equal(1, _remote.Heads[id].Revision);

        // Signed out: a new revision waits.
        _broker.SignedIn = false;
        _broker.Tokens.Clear();
        SetRevision(osp, 2);
        await _service.TickAsync();
        Assert.Equal("signed_out", _service.State);
        Assert.Equal(1, _remote.Heads[id].Revision);
        Assert.Equal(1, _service.Health().Pending);

        // Signed in again: it goes.
        _broker.SignedIn = true;
        await _service.TickAsync();
        Assert.Equal(2, _remote.Heads[id].Revision);
        Assert.Equal(0, _service.Health().Pending);
    }

    [Fact]
    public async Task The_background_pass_fetches_a_newer_remote_revision_to_the_sidecar()
    {
        await SignIn();
        var (osp, id) = NewProject("p1", 1);
        await Post("/v1/projects/connect", "tok", new { project_id = id, name = "p1", osp_path = osp, revision = 1 });
        await _service.TickAsync(); // pushes 1, base 1
        _remote.Seed(id, MakeOsp(Path.Combine(_dir, "other.osp"), id, "p1", 3));
        await _service.TickAsync();
        Assert.True(File.Exists(osp + ".incoming"));
        Assert.Equal(1, OspLocalFile.ReadMeta(osp).Revision);
    }

    [Fact]
    public async Task The_register_survives_a_restart()
    {
        await SignIn();
        var (osp, id) = NewProject("p1", 2);
        await Post("/v1/projects/connect", "tok", new { project_id = id, name = "p1", osp_path = osp, revision = 2 });
        using var again = new DatabaseService(_broker, _remote, Path.Combine(_dir, "projects.json"));
        Assert.Equal(2, again.GetProject(id).LocalRevision);
    }

    // ---- events ----

    [Fact]
    public async Task Events_stream_tells_a_new_remote_revision()
    {
        await SignIn();
        var (osp, id) = NewProject("p1", 1);
        await Post("/v1/projects/connect", "tok", new { project_id = id, name = "p1", osp_path = osp, revision = 1 });
        await _service.TickAsync();

        using var req = new HttpRequestMessage(HttpMethod.Get, _server.BaseUrl + $"/v1/projects/{id}/events");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "tok");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal("text/event-stream", res.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(cts.Token));

        _remote.Seed(id, MakeOsp(Path.Combine(_dir, "other.osp"), id, "p1", 7));
        await _service.TickAsync();

        var seen = new StringBuilder();
        while (!seen.ToString().Contains("\"remote_revision\":7"))
        {
            var line = await reader.ReadLineAsync(cts.Token);
            Assert.NotNull(line);
            seen.AppendLine(line);
        }

        Assert.Contains("event: revision", seen.ToString());
    }

    [Fact]
    public async Task Events_need_a_token()
    {
        var (code, _) = await Send(HttpMethod.Get, "/v1/projects/x/events", null, null);
        Assert.Equal(HttpStatusCode.Unauthorized, code);
    }

    // ---- the pipe client ----

    [Fact]
    public async Task The_pipe_client_speaks_the_contract_in_message_mode()
    {
        var name = "osas-test-" + Guid.NewGuid().ToString("N");
        var requests = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = Task.Run(async () =>
        {
            for (var i = 0; i < 3; i++)
            {
                await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(cts.Token);
                var buf = new byte[4096];
                var n = await pipe.ReadAsync(buf, cts.Token);
                var text = Encoding.UTF8.GetString(buf, 0, n);
                requests.Add(text);
                var op = JsonDocument.Parse(text).RootElement.GetProperty("op").GetString();
                var reply = op switch
                {
                    "status" => "{\"v\":1,\"ok\":true,\"signed_in\":true,\"user\":\"asha\",\"company_id\":\"c1\",\"company_name\":\"Acme\",\"expires_utc\":\"2026-11-09T10:00:00Z\"}",
                    "validate" => "{\"v\":1,\"ok\":true,\"valid\":false}",
                    _ => "{\"v\":1,\"ok\":false,\"error\":\"signed_out\",\"message\":\"Kivo is not signed in.\"}",
                };
                await pipe.WriteAsync(Encoding.UTF8.GetBytes(reply), cts.Token);
                pipe.WaitForPipeDrain();
            }
        }, cts.Token);

        var client = new PipeBrokerClient(name);
        var status = await client.StatusAsync(cts.Token);
        Assert.True(status.SignedIn);
        Assert.Equal("c1", status.CompanyId);
        Assert.False((await client.ValidateAsync("abc", cts.Token)).Valid);
        var ex = await Assert.ThrowsAsync<BrokerException>(() => client.AskSessionAsync("dbservice", "sync", null, false, cts.Token));
        Assert.Equal("signed_out", ex.Code);
        await serverTask;

        Assert.Contains("\"op\":\"ask_session\"", requests[2]);
        Assert.Contains("\"app\":\"dbservice\"", requests[2]);
        Assert.Contains("\"interactive\":false", requests[2]);
        Assert.Contains("\"v\":1", requests[2]);
    }

    [Fact]
    public async Task The_pipe_client_reports_kivo_unavailable_when_there_is_no_pipe()
    {
        var client = new PipeBrokerClient("osas-test-missing-" + Guid.NewGuid().ToString("N"));
        var ex = await Assert.ThrowsAsync<BrokerException>(() => client.StatusAsync());
        Assert.Equal("kivo_unavailable", ex.Code);
    }

    // ---- helpers ----

    async Task SignIn()
    {
        _broker.SignedIn = true;
        _broker.Tokens.Add("tok");
        await _service.RefreshSessionAsync();
    }

    (string Path, string Id) NewProject(string name, long revision)
    {
        var id = "proj-" + name;
        return (MakeOsp(Path.Combine(_dir, name + ".osp"), id, name, revision), id);
    }

    static string MakeOsp(string path, string id, string name, long revision)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;"
            + "CREATE TABLE IF NOT EXISTS change (revision INTEGER NOT NULL, tbl TEXT NOT NULL, key TEXT NOT NULL, op TEXT NOT NULL);"
            + $"INSERT OR REPLACE INTO meta VALUES ('format','osas-project'),('project_id','{id}'),('name','{name}'),('revision','{revision}');";
        cmd.ExecuteNonQuery();
        return path;
    }

    static void SetRevision(string path, long revision)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE meta SET value = '{revision}' WHERE key = 'revision'; INSERT INTO change VALUES ({revision}, 'takeoff_row', 'k', 'put');";
        cmd.ExecuteNonQuery();
    }

    async Task<JsonElement> GetJson(string path)
    {
        var (_, body) = await Send(HttpMethod.Get, path, null, null);
        return body;
    }

    Task<(HttpStatusCode, JsonElement)> Post(string path, string? token, object body) => Send(HttpMethod.Post, path, token, body);

    async Task<(HttpStatusCode, JsonElement)> Send(HttpMethod method, string path, string? token, object? body)
    {
        using var req = new HttpRequestMessage(method, _server.BaseUrl + path);
        if (token != null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null)
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var res = await _http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    // ---- fakes ----

    sealed class FakeBroker : IBrokerClient
    {
        public bool SignedIn;
        public bool Unavailable;
        public HashSet<string> Tokens = [];
        public int ValidateCalls;
        public (string App, string Purpose, bool Interactive)? LastAsk;

        public Task<BrokerStatus> StatusAsync(CancellationToken ct = default)
        {
            if (Unavailable)
                throw new BrokerException("kivo_unavailable", "no pipe");
            return Task.FromResult(new BrokerStatus(SignedIn, SignedIn ? "asha" : "", SignedIn ? "c1" : "", "Acme", null));
        }

        public Task<BrokerSession> AskSessionAsync(string app, string purpose, string? projectId, bool interactive, CancellationToken ct = default)
        {
            if (Unavailable)
                throw new BrokerException("kivo_unavailable", "no pipe");
            LastAsk = (app, purpose, interactive);
            if (!SignedIn)
                throw new BrokerException("signed_out", "Kivo is not signed in.");
            Tokens.Add("svc-token");
            return Task.FromResult(new BrokerSession("svc-token", null, "asha", "c1", ["estimator"]));
        }

        public Task<BrokerValidation> ValidateAsync(string token, CancellationToken ct = default)
        {
            ValidateCalls++;
            if (Unavailable)
                throw new BrokerException("kivo_unavailable", "no pipe");
            var valid = SignedIn && Tokens.Contains(token);
            return Task.FromResult(new BrokerValidation(valid, valid ? "asha" : "", valid ? "c1" : "", valid ? ["estimator"] : [], null));
        }
    }

    sealed class FakeRemote : IRemoteStore
    {
        public readonly Dictionary<string, (long Revision, byte[] Bytes)> Heads = [];
        public bool Down;

        public bool IsConfigured => true;

        /// <summary>Another device pushed this file.</summary>
        public void Seed(string projectId, string ospPath) =>
            Heads[projectId] = (OspLocalFile.ReadMeta(ospPath).Revision, File.ReadAllBytes(ospPath));

        public Task<RemoteHead?> GetHeadAsync(string projectId, CancellationToken ct = default)
        {
            if (Down)
                throw new RemoteUnavailableException("down");
            return Task.FromResult(Heads.TryGetValue(projectId, out var h) ? new RemoteHead(h.Revision, null) : null);
        }

        public Task<RemotePushResult> PushAsync(RemoteContext who, string ospPath, long expectedRemoteRevision, CancellationToken ct = default)
        {
            if (Down)
                throw new RemoteUnavailableException("down");
            var meta = OspLocalFile.ReadMeta(ospPath);
            var current = Heads.TryGetValue(meta.ProjectId, out var h) ? h.Revision : 0;
            if (current != expectedRemoteRevision)
                return Task.FromResult(new RemotePushResult(false, true, current, "moved"));
            Heads[meta.ProjectId] = (meta.Revision, File.ReadAllBytes(ospPath));
            return Task.FromResult(new RemotePushResult(true, false, meta.Revision, "pushed"));
        }

        public Task<RemotePullResult> PullToAsync(string projectId, string destPath, CancellationToken ct = default)
        {
            if (Down)
                throw new RemoteUnavailableException("down");
            var h = Heads[projectId];
            File.WriteAllBytes(destPath, h.Bytes);
            return Task.FromResult(new RemotePullResult(true, h.Revision, null, "pulled"));
        }
    }
}
