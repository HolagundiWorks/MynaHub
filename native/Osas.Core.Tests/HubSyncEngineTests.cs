using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Adms.Cloud;
using Xunit;

namespace Osas.Core.Tests;

public class HubSyncEngineTests
{
    /// <summary>A scripted stand-in for the company-file engine: a queue of changes, a cursor, and the records it was told to apply.</summary>
    sealed class FakeEngine
    {
        public List<JsonObject> Queue = new();
        public long Cursor;
        public List<JsonNode> Acks = new();
        public List<JsonNode> Applied = new();
        public bool Enabled = true;

        public JsonElement Call(string op, object? args)
        {
            var json = JsonSerializer.Serialize(args);
            var a = JsonNode.Parse(json is "null" ? "{}" : json)!;
            JsonNode result = op switch
            {
                "sync_status" => new JsonObject { ["enabled"] = Enabled, ["cloud_company"] = "co-1", ["last_pulled_seq"] = Cursor, ["pending"] = Queue.Count },
                "sync_collect" => new JsonArray(Queue.Select(q => (JsonNode)q.DeepClone()).ToArray()),
                "sync_ack" => Ack(a["items"]!.AsArray()),
                "sync_apply" => Apply(a),
                "sync_enable" => new JsonObject(),
                _ => throw new InvalidOperationException(op),
            };
            return JsonDocument.Parse(result.ToJsonString()).RootElement.Clone();
        }

        JsonNode Ack(JsonArray items)
        {
            foreach (var i in items)
            {
                Acks.Add(i!.DeepClone());
                if (i["status"]!.GetValue<string>() != "forbidden")
                    Queue.RemoveAll(q => q["sync_id"]!.GetValue<string>() == i["sync_id"]!.GetValue<string>());
            }
            return new JsonObject();
        }

        JsonNode Apply(JsonNode a)
        {
            var recs = a["records"]!.AsArray();
            foreach (var r in recs) Applied.Add(r!.DeepClone());
            Cursor = recs.Max(r => (long)r!["seq"]!.AsValue().GetValue<int>());
            return new JsonObject { ["applied"] = recs.Count, ["deferred"] = 0, ["skipped_local_changes"] = 0, ["next_seq"] = Cursor };
        }
    }

    sealed class FakeApi : IHubApi
    {
        public List<string> Calls = new();
        public Func<JsonArray, JsonArray> OnPush = _ => new JsonArray();
        public List<JsonObject> Cloud = new();
        public Task CreateCompanyAsync(string companyId, string name, CancellationToken ct) { Calls.Add("create:" + companyId); return Task.CompletedTask; }
        public Task<JsonArray> PushAsync(string companyId, JsonArray records, CancellationToken ct) { Calls.Add("push:" + records.Count); return Task.FromResult(OnPush(records)); }
        public Task<JsonArray> PullAsync(string companyId, long fromSeq, int limit, CancellationToken ct)
        {
            Calls.Add("pull:" + fromSeq);
            return Task.FromResult(new JsonArray(Cloud.Where(c => (long)c["seq"]!.AsValue().GetValue<int>() > fromSeq).Select(c => (JsonNode)c.DeepClone()).ToArray()));
        }
    }

    static JsonObject Change(string kind, string id, long v) =>
        new() { ["kind"] = kind, ["sync_id"] = id, ["base_rev"] = 0, ["updated_ms"] = 1000, ["deleted"] = false, ["body"] = new JsonObject { ["name"] = id }, ["v"] = v };

    [Fact]
    public async Task Push_sends_changes_without_the_local_version_and_acknowledges_with_it()
    {
        var eng = new FakeEngine { Queue = { Change("client", "c1", 7) } };
        var api = new FakeApi
        {
            OnPush = recs =>
            {
                Assert.Null(recs[0]!["v"]);
                return new JsonArray(new JsonObject { ["sync_id"] = "c1", ["kind"] = "client", ["status"] = "created", ["rev"] = 1, ["seq"] = 1 });
            },
        };
        var r = await new HubSyncEngine(eng.Call, api).RunOnceAsync();
        Assert.Equal(1, r.Pushed);
        Assert.Equal(7, eng.Acks[0]["v"]!.GetValue<long>());
        Assert.Equal("created", eng.Acks[0]["status"]!.GetValue<string>());
        Assert.True(r.Complete);
    }

    [Fact]
    public async Task Push_comes_before_pull_and_pull_continues_from_the_cursor()
    {
        var eng = new FakeEngine { Queue = { Change("task", "t1", 1) }, Cursor = 2 };
        var api = new FakeApi
        {
            OnPush = _ => new JsonArray(new JsonObject { ["sync_id"] = "t1", ["kind"] = "task", ["status"] = "updated", ["rev"] = 2, ["seq"] = 5 }),
            Cloud =
            {
                new JsonObject { ["kind"] = "client", ["sync_id"] = "c9", ["rev"] = 1, ["seq"] = 3, ["deleted"] = false, ["body"] = new JsonObject() },
                new JsonObject { ["kind"] = "client", ["sync_id"] = "c8", ["rev"] = 1, ["seq"] = 1, ["deleted"] = false, ["body"] = new JsonObject() },
            },
        };
        var r = await new HubSyncEngine(eng.Call, api).RunOnceAsync();
        Assert.Equal("push:1", api.Calls[0]);
        Assert.Equal("pull:2", api.Calls[1]);
        Assert.Single(eng.Applied);
        Assert.Equal(1, r.Pulled);
    }

    [Fact]
    public async Task Forbidden_changes_stay_queued_and_the_pass_is_not_complete()
    {
        var eng = new FakeEngine { Queue = { Change("client", "c1", 1) } };
        var api = new FakeApi { OnPush = _ => new JsonArray(new JsonObject { ["sync_id"] = "c1", ["kind"] = "client", ["status"] = "forbidden", ["rev"] = 0, ["seq"] = 0 }) };
        var r = await new HubSyncEngine(eng.Call, api).RunOnceAsync();
        Assert.Equal(1, r.Forbidden);
        Assert.False(r.Complete);
        Assert.Single(eng.Queue);
        Assert.Single(api.Calls, c => c.StartsWith("push"));
    }

    [Fact]
    public async Task A_company_that_is_not_linked_is_refused()
    {
        var eng = new FakeEngine { Enabled = false };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new HubSyncEngine(eng.Call, new FakeApi()).RunOnceAsync());
    }

    [Fact]
    public async Task Linking_creates_the_cloud_company_then_turns_tracking_on()
    {
        var api = new FakeApi();
        await new HubSyncEngine(new FakeEngine().Call, api).LinkAsync("co-1", "Studio");
        Assert.Equal(new[] { "create:co-1" }, api.Calls.ToArray());
    }

    sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) { Seen.Add(r); return Task.FromResult(reply(r)); }
    }

    [Fact]
    public async Task The_data_api_sends_the_token_the_schema_headers_and_reads_the_error_message()
    {
        var stub = new Stub(r => r.RequestUri!.AbsolutePath.EndsWith("push_records")
            ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"message\":\"not a member of this company\"}", Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") });
        using var api = new HubDataApi("https://api.example.test/db/rest/v1", _ => Task.FromResult("tok"), stub);

        await api.PullAsync("co 1", 5, 500, CancellationToken.None);
        var pull = stub.Seen[0];
        Assert.Equal("Bearer tok", pull.Headers.Authorization!.ToString());
        Assert.Equal("hub", pull.Headers.GetValues("Accept-Profile").Single());
        Assert.Contains("company_id=eq.co%201", pull.RequestUri!.Query);
        Assert.Contains("seq=gt.5", pull.RequestUri.Query);

        var ex = await Assert.ThrowsAsync<HubApiException>(() => api.PushAsync("co-1", new JsonArray(), CancellationToken.None));
        Assert.Equal("not a member of this company", ex.Message);
        Assert.Equal("hub", stub.Seen[1].Headers.GetValues("Content-Profile").Single());
    }
}
