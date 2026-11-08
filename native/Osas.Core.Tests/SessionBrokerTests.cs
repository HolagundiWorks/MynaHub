using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using Osas.Core.Broker;
using Xunit;

namespace Osas.Core.Tests;

public sealed class SessionBrokerTests
{
    private sealed class FakeHost : IBrokerHost
    {
        public BrokerIdentity? CurrentIdentity { get; set; }
        public DateTime SignedInUtc { get; set; } = DateTime.UtcNow;
        public BrokerIdentity? SignInResult { get; set; }
        public int SignInAsked { get; private set; }

        public Task<BrokerIdentity?> RequestSignInAsync(CancellationToken cancel)
        {
            SignInAsked++;
            CurrentIdentity = SignInResult;
            return Task.FromResult(SignInResult);
        }
    }

    private static readonly BrokerIdentity Asha = new("asha", "co-1", "Studio One", ["owner"]);

    private static async Task<JsonObject> Ask(SessionBroker broker, object request) =>
        (JsonObject)JsonNode.Parse(await broker.HandleAsync(System.Text.Json.JsonSerializer.Serialize(request)))!;

    [Fact]
    public async Task Status_signed_out_shows_no_user()
    {
        var broker = new SessionBroker(new FakeHost());
        var r = await Ask(broker, new { v = 1, op = "status" });
        Assert.True((bool)r["ok"]!);
        Assert.False((bool)r["signed_in"]!);
        Assert.Null(r["user"]);
    }

    [Fact]
    public async Task Status_signed_in_shows_person_and_company()
    {
        var broker = new SessionBroker(new FakeHost { CurrentIdentity = Asha });
        var r = await Ask(broker, new { v = 1, op = "status" });
        Assert.True((bool)r["signed_in"]!);
        Assert.Equal("asha", (string)r["user"]!);
        Assert.Equal("co-1", (string)r["company_id"]!);
        Assert.Equal("Studio One", (string)r["company_name"]!);
        Assert.NotNull(r["expires_utc"]);
    }

    [Fact]
    public async Task Ask_session_non_interactive_while_signed_out_is_signed_out()
    {
        var host = new FakeHost { SignInResult = Asha };
        var broker = new SessionBroker(host);
        var r = await Ask(broker, new { v = 1, op = "ask_session", app = "tren", purpose = "db_connect", interactive = false });
        Assert.False((bool)r["ok"]!);
        Assert.Equal("signed_out", (string)r["error"]!);
        Assert.Equal(0, host.SignInAsked);
    }

    [Fact]
    public async Task Ask_session_interactive_waits_for_sign_in_then_gives_a_token()
    {
        var host = new FakeHost { SignInResult = Asha };
        var broker = new SessionBroker(host);
        var r = await Ask(broker, new { v = 1, op = "ask_session", app = "falcon", purpose = "import", project_id = "p1" });
        Assert.True((bool)r["ok"]!);
        Assert.Equal(1, host.SignInAsked);
        var token = (string)r["token"]!;
        Assert.Equal(43, token.Length); // 32 bytes in base64url, no padding
        Assert.DoesNotContain('=', token);
        Assert.Equal("owner", (string)r["roles"]![0]!);
    }

    [Fact]
    public async Task Ask_session_closed_sign_in_is_cancelled()
    {
        var broker = new SessionBroker(new FakeHost { SignInResult = null });
        var r = await Ask(broker, new { v = 1, op = "ask_session", app = "tren", purpose = "db_connect" });
        Assert.Equal("cancelled", (string)r["error"]!);
    }

    [Fact]
    public async Task Ask_session_by_an_auditor_is_denied_for_writing_but_not_connecting()
    {
        var auditor = new BrokerIdentity("ravi", "co-1", "Studio One", ["auditor"]);
        var broker = new SessionBroker(new FakeHost { CurrentIdentity = auditor });
        var save = await Ask(broker, new { v = 1, op = "ask_session", app = "tren", purpose = "save_to_office" });
        Assert.Equal("denied", (string)save["error"]!);
        var connect = await Ask(broker, new { v = 1, op = "ask_session", app = "tren", purpose = "db_connect" });
        Assert.True((bool)connect["ok"]!);
    }

    [Fact]
    public async Task Ask_session_gives_the_same_token_for_the_same_app_purpose_and_project()
    {
        var broker = new SessionBroker(new FakeHost { CurrentIdentity = Asha });
        var a = await Ask(broker, new { v = 1, op = "ask_session", app = "tren", purpose = "db_connect", project_id = "p1" });
        var b = await Ask(broker, new { v = 1, op = "ask_session", app = "tren", purpose = "db_connect", project_id = "p1" });
        var c = await Ask(broker, new { v = 1, op = "ask_session", app = "tren", purpose = "db_connect", project_id = "p2" });
        Assert.Equal((string)a["token"]!, (string)b["token"]!);
        Assert.NotEqual((string)a["token"]!, (string)c["token"]!);
    }

    [Fact]
    public async Task Bad_requests_are_refused()
    {
        var broker = new SessionBroker(new FakeHost { CurrentIdentity = Asha });
        Assert.Equal("bad_request", (string)(await Ask(broker, new { v = 2, op = "status" }))["error"]!);
        Assert.Equal("bad_request", (string)(await Ask(broker, new { v = 1, op = "nope" }))["error"]!);
        Assert.Equal("bad_request", (string)(await Ask(broker, new { v = 1, op = "ask_session", app = "x", purpose = "db_connect" }))["error"]!);
        var raw = JsonNode.Parse(await broker.HandleAsync("not json"))!;
        Assert.Equal("bad_request", (string)raw["error"]!);
    }

    [Fact]
    public async Task Validate_of_a_wrong_token_is_not_valid()
    {
        var broker = new SessionBroker(new FakeHost { CurrentIdentity = Asha });
        await Ask(broker, new { v = 1, op = "ask_session", app = "tren", purpose = "db_connect" });
        var r = await Ask(broker, new { v = 1, op = "validate", token = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" });
        Assert.True((bool)r["ok"]!);
        Assert.False((bool)r["valid"]!);
        var none = await Ask(broker, new { v = 1, op = "validate" });
        Assert.False((bool)none["valid"]!);
    }

    [Fact]
    public async Task Validate_of_the_right_token_names_the_person()
    {
        var broker = new SessionBroker(new FakeHost { CurrentIdentity = Asha });
        var given = await Ask(broker, new { v = 1, op = "ask_session", app = "dbservice", purpose = "db_connect" });
        var r = await Ask(broker, new { v = 1, op = "validate", token = (string)given["token"]! });
        Assert.True((bool)r["valid"]!);
        Assert.Equal("asha", (string)r["user"]!);
        Assert.Equal("co-1", (string)r["company_id"]!);
    }

    [Fact]
    public async Task End_session_with_a_token_ends_only_that_token()
    {
        var broker = new SessionBroker(new FakeHost { CurrentIdentity = Asha });
        var a = (string)(await Ask(broker, new { v = 1, op = "ask_session", app = "tren", purpose = "db_connect" }))["token"]!;
        var b = (string)(await Ask(broker, new { v = 1, op = "ask_session", app = "falcon", purpose = "db_connect" }))["token"]!;
        Assert.True((bool)(await Ask(broker, new { v = 1, op = "end_session", token = a }))["ok"]!);
        Assert.False((bool)(await Ask(broker, new { v = 1, op = "validate", token = a }))["valid"]!);
        Assert.True((bool)(await Ask(broker, new { v = 1, op = "validate", token = b }))["valid"]!);
    }

    [Fact]
    public async Task End_session_without_a_token_ends_every_token()
    {
        var broker = new SessionBroker(new FakeHost { CurrentIdentity = Asha });
        var a = (string)(await Ask(broker, new { v = 1, op = "ask_session", app = "tren", purpose = "db_connect" }))["token"]!;
        var b = (string)(await Ask(broker, new { v = 1, op = "ask_session", app = "falcon", purpose = "sync" }))["token"]!;
        await Ask(broker, new { v = 1, op = "end_session" });
        Assert.False((bool)(await Ask(broker, new { v = 1, op = "validate", token = a }))["valid"]!);
        Assert.False((bool)(await Ask(broker, new { v = 1, op = "validate", token = b }))["valid"]!);
        Assert.Equal(0, broker.Tokens.Count);
    }

    [Fact]
    public async Task Sign_out_ending_all_sessions_invalidates_tokens()
    {
        var broker = new SessionBroker(new FakeHost { CurrentIdentity = Asha });
        var a = (string)(await Ask(broker, new { v = 1, op = "ask_session", app = "tren", purpose = "db_connect" }))["token"]!;
        broker.EndAllSessions();
        Assert.False((bool)(await Ask(broker, new { v = 1, op = "validate", token = a }))["valid"]!);
    }

    [Fact]
    public void Tokens_end_after_eight_idle_hours_and_twenty_four_absolute()
    {
        var now = new DateTime(2026, 11, 8, 9, 0, 0, DateTimeKind.Utc);
        var table = new SessionTokenTable(() => now);
        var (token, _) = table.Issue(new TokenKey("tren", "db_connect", ""), Asha);

        now += TimeSpan.FromHours(7);
        Assert.NotNull(table.Validate(token)); // use slides the idle window
        now += TimeSpan.FromHours(7);
        Assert.NotNull(table.Validate(token));
        now += TimeSpan.FromHours(7);
        Assert.NotNull(table.Validate(token)); // 21 h in
        now += TimeSpan.FromHours(4); // 25 h in: past the absolute limit although used 4 h ago
        Assert.Null(table.Validate(token));

        var (idle, _) = table.Issue(new TokenKey("tren", "sync", ""), Asha);
        now += TimeSpan.FromHours(8) + TimeSpan.FromSeconds(1);
        Assert.Null(table.Validate(idle));
    }

    [Fact]
    public void Renew_slides_the_window_but_never_past_the_absolute_limit()
    {
        var now = new DateTime(2026, 11, 8, 9, 0, 0, DateTimeKind.Utc);
        var table = new SessionTokenTable(() => now);
        var (token, first) = table.Issue(new TokenKey("tren", "db_connect", ""), Asha);
        now += TimeSpan.FromHours(5);
        Assert.Equal(SessionTokenTable.RenewResult.Renewed, table.Renew(token, out var renewed));
        Assert.True(renewed > first);
        now += TimeSpan.FromHours(7);
        Assert.Equal(SessionTokenTable.RenewResult.Renewed, table.Renew(token, out _));
        now += TimeSpan.FromHours(7);
        Assert.Equal(SessionTokenTable.RenewResult.Renewed, table.Renew(token, out var last));
        Assert.Equal(new DateTime(2026, 11, 8, 9, 0, 0, DateTimeKind.Utc) + SessionTokenTable.AbsoluteLifetime, last);
        now += TimeSpan.FromHours(6);
        Assert.Equal(SessionTokenTable.RenewResult.Expired, table.Renew(token, out _));
        Assert.Equal(SessionTokenTable.RenewResult.BadToken, table.Renew("nope", out _));
    }

    // ---- the real pipe ----

    private static string UniquePipe() => "osas-session-broker-test-" + Guid.NewGuid().ToString("N");

    private static async Task<JsonObject> OverPipe(string name, object request)
    {
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        client.ReadMode = PipeTransmissionMode.Message;
        await client.WriteAsync(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(request)));
        using var all = new MemoryStream();
        var chunk = new byte[4096];
        do
        {
            var n = await client.ReadAsync(chunk);
            if (n == 0)
                break;
            all.Write(chunk, 0, n);
        }
        while (!client.IsMessageComplete);
        return (JsonObject)JsonNode.Parse(Encoding.UTF8.GetString(all.ToArray()))!;
    }

    [Fact]
    public async Task The_pipe_answers_a_status_request_and_serves_callers_one_after_another()
    {
        var name = UniquePipe();
        using var pipe = new SessionBrokerPipe(new SessionBroker(new FakeHost { CurrentIdentity = Asha }), name);
        Assert.True(pipe.Start());
        for (var i = 0; i < 3; i++)
        {
            var r = await OverPipe(name, new { v = 1, op = "status" });
            Assert.True((bool)r["signed_in"]!);
        }
    }

    [Fact]
    public async Task A_second_server_on_the_same_pipe_does_not_start()
    {
        var name = UniquePipe();
        var broker = new SessionBroker(new FakeHost());
        using var first = new SessionBrokerPipe(broker, name);
        using var second = new SessionBrokerPipe(broker, name);
        Assert.True(first.Start());
        Assert.False(second.Start());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task An_interactive_ask_can_wait_while_other_callers_are_still_served()
    {
        var host = new WaitingHost();
        var name = UniquePipe();
        using var pipe = new SessionBrokerPipe(new SessionBroker(host), name);
        Assert.True(pipe.Start());

        var waiting = OverPipe(name, new { v = 1, op = "ask_session", app = "tren", purpose = "db_connect" });
        await host.Asked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var status = await OverPipe(name, new { v = 1, op = "status" });
        Assert.False((bool)status["signed_in"]!);
        Assert.False(waiting.IsCompleted);

        host.Finish(Asha);
        var answer = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True((bool)answer["ok"]!);
    }

    private sealed class WaitingHost : IBrokerHost
    {
        private readonly TaskCompletionSource<BrokerIdentity?> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public BrokerIdentity? CurrentIdentity { get; private set; }
        public DateTime SignedInUtc => DateTime.UtcNow;

        public Task<BrokerIdentity?> RequestSignInAsync(CancellationToken cancel)
        {
            Asked.TrySetResult();
            return _done.Task;
        }

        public void Finish(BrokerIdentity who)
        {
            CurrentIdentity = who;
            _done.TrySetResult(who);
        }
    }

    [Fact]
    public async Task The_pipe_allows_only_the_current_user()
    {
        // A second Windows account cannot be signed in from a test, so the access list is read instead: it must name the current user
        // and nobody broader (not Everyone, Users or Authenticated Users).
        var name = UniquePipe();
        using var pipe = new SessionBrokerPipe(new SessionBroker(new FakeHost()), name);
        Assert.True(pipe.Start());
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);

        var rules = client.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        var me = WindowsIdentity.GetCurrent().User!;
        var broad = new[] { WellKnownSidType.WorldSid, WellKnownSidType.BuiltinUsersSid, WellKnownSidType.AuthenticatedUserSid, WellKnownSidType.NetworkSid };
        foreach (var rule in rules.Where(r => r.AccessControlType == AccessControlType.Allow))
        {
            var sid = (SecurityIdentifier)rule.IdentityReference;
            Assert.DoesNotContain(broad, w => sid.IsWellKnown(w));
        }
        Assert.Contains(rules, r => r.AccessControlType == AccessControlType.Allow && ((SecurityIdentifier)r.IdentityReference).Equals(me));
    }
}
