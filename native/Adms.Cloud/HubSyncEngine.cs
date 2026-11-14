using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Adms.Cloud;

/// <summary>The cloud side of the company sync (docs/hub-sync-design.md): the Neon Data API, signed in as a person. Faked in tests.</summary>
public interface IHubApi
{
    Task CreateCompanyAsync(string companyId, string name, CancellationToken ct);
    /// <summary>Pushes records; the answer is one {sync_id, kind, status, rev, seq} per record.</summary>
    Task<JsonArray> PushAsync(string companyId, JsonArray records, CancellationToken ct);
    /// <summary>Records with seq above <paramref name="fromSeq"/>, oldest first.</summary>
    Task<JsonArray> PullAsync(string companyId, long fromSeq, int limit, CancellationToken ct);
}

public sealed class HubApiException : Exception
{
    public HubApiException(string message, HttpStatusCode? status = null) : base(message) => Status = status;
    public HttpStatusCode? Status { get; }
    public bool Unauthorized => Status == HttpStatusCode.Unauthorized;
}

/// <summary>Neon Data API over HTTPS with a Neon Auth access token (a public endpoint; no database credential).</summary>
public sealed class HubDataApi : IHubApi, IDisposable
{
    readonly HttpClient _http;
    readonly Func<CancellationToken, Task<string>> _token;

    public HubDataApi(string baseUrl, Func<CancellationToken, Task<string>> accessToken, HttpMessageHandler? handler = null)
    {
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback))
            throw new ArgumentException("The data API address must be an https URL.", nameof(baseUrl));
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = uri;
        _http.Timeout = TimeSpan.FromSeconds(60);
        _token = accessToken;
    }

    public Task CreateCompanyAsync(string companyId, string name, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "rpc/create_company", new JsonObject { ["p_id"] = companyId, ["p_name"] = name }, ct);

    public async Task<JsonArray> PushAsync(string companyId, JsonArray records, CancellationToken ct) =>
        (await SendAsync(HttpMethod.Post, "rpc/push_records", new JsonObject { ["p_company"] = companyId, ["p_records"] = records }, ct))?.AsArray() ?? new JsonArray();

    public async Task<JsonArray> PullAsync(string companyId, long fromSeq, int limit, CancellationToken ct) =>
        (await SendAsync(HttpMethod.Get,
            $"record?company_id=eq.{Uri.EscapeDataString(companyId)}&seq=gt.{fromSeq}&order=seq.asc&limit={limit}&select=kind,sync_id,rev,seq,deleted,body", null, ct))?.AsArray()
        ?? new JsonArray();

    async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _token(ct));
        req.Headers.Add("Accept-Profile", "hub");
        if (body is not null)
        {
            req.Headers.Add("Content-Profile", "hub");
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }
        using var res = await _http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new HubApiException(ErrorText(text, res.StatusCode), res.StatusCode);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    static string ErrorText(string body, HttpStatusCode code)
    {
        try
        {
            var m = JsonNode.Parse(body)?["message"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(m))
                return m!;
        }
        catch (JsonException) { }
        return $"The cloud service refused the request ({(int)code}).";
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>What one sync pass did.</summary>
public sealed record HubSyncResult(int Pushed, int Merged, int RejectedOlder, int Forbidden, int Pulled, int Deferred, int SkippedLocalChanges, bool Complete);

/// <summary>
/// One company sync pass: push local changes, then pull the cloud's. <paramref name="engine"/> is the company-file engine (the sync_* operations);
/// it decides what changed and applies what arrives, this class only moves records. Push first, so the cloud settles conflicts before a pull
/// overwrites anything.
/// </summary>
public sealed class HubSyncEngine
{
    const int Batch = 200;
    const int MaxRounds = 50;

    readonly Func<string, object?, JsonElement> _engine;
    readonly IHubApi _api;

    public HubSyncEngine(Func<string, object?, JsonElement> engine, IHubApi api)
    {
        _engine = engine;
        _api = api;
    }

    /// <summary>Links the company file to the cloud company (creating it when new) and turns change tracking on. Owner only.</summary>
    public async Task LinkAsync(string companyId, string name, CancellationToken ct = default)
    {
        await _api.CreateCompanyAsync(companyId, name, ct);
        _engine("sync_enable", new { cloud_company = companyId });
    }

    public async Task<HubSyncResult> RunOnceAsync(CancellationToken ct = default)
    {
        var st = _engine("sync_status", null);
        if (!st.GetProperty("enabled").GetBoolean())
            throw new InvalidOperationException("Cloud sync is not linked for this company file.");
        var company = st.GetProperty("cloud_company").GetString()!;
        int pushed = 0, merged = 0, older = 0, forbidden = 0, pulled = 0, deferred = 0, skipped = 0;
        var complete = true;

        for (var round = 0; round < MaxRounds; round++)
        {
            var batch = JsonNode.Parse(_engine("sync_collect", new { limit = Batch }).GetRawText())!.AsArray();
            if (batch.Count == 0)
                break;
            var results = await _api.PushAsync(company, Strip(batch), ct);
            var acks = new JsonArray();
            var progressed = false;
            foreach (var res in results)
            {
                var sid = res!["sync_id"]!.GetValue<string>();
                var sent = batch.First(b => string.Equals(b!["sync_id"]!.GetValue<string>(), sid, StringComparison.OrdinalIgnoreCase))!;
                var status = res["status"]!.GetValue<string>();
                switch (status)
                {
                    case "created": case "updated": pushed++; progressed = true; break;
                    case "merged": merged++; progressed = true; break;
                    case "rejected_older": older++; progressed = true; break;
                    default: forbidden++; break;
                }
                acks.Add(new JsonObject
                {
                    ["kind"] = sent["kind"]!.DeepClone(),
                    ["sync_id"] = sent["sync_id"]!.DeepClone(),
                    ["v"] = sent["v"]!.DeepClone(),
                    ["status"] = status,
                    ["rev"] = res["rev"]?.DeepClone() ?? 0,
                });
            }
            _engine("sync_ack", new { items = acks });
            if (!progressed || batch.Count < Batch)
            {
                complete = progressed || batch.Count == 0;
                break;
            }
        }

        for (var round = 0; round < MaxRounds; round++)
        {
            var from = _engine("sync_status", null).GetProperty("last_pulled_seq").GetInt64();
            var rows = await _api.PullAsync(company, from, 500, ct);
            if (rows.Count == 0)
                break;
            var res = _engine("sync_apply", new { from_seq = from, records = rows });
            pulled += res.GetProperty("applied").GetInt32();
            deferred = res.GetProperty("deferred").GetInt32();
            skipped += res.GetProperty("skipped_local_changes").GetInt32();
            var next = res.GetProperty("next_seq").GetInt64();
            if (rows.Count < 500 || next <= from)
            {
                if (deferred > 0)
                    complete = false;
                break;
            }
        }
        return new HubSyncResult(pushed, merged, older, forbidden, pulled, deferred, skipped, complete && forbidden == 0);
    }

    /// <summary>The cloud function needs only kind, sync_id, base_rev, updated_ms, deleted, body; v stays here for the acknowledgement.</summary>
    static JsonArray Strip(JsonArray batch)
    {
        var a = new JsonArray();
        foreach (var r in batch)
        {
            var o = r!.AsObject();
            a.Add(new JsonObject
            {
                ["kind"] = o["kind"]!.DeepClone(),
                ["sync_id"] = o["sync_id"]!.DeepClone(),
                ["base_rev"] = o["base_rev"]!.DeepClone(),
                ["updated_ms"] = o["updated_ms"]!.DeepClone(),
                ["deleted"] = o["deleted"]!.DeepClone(),
                ["body"] = o["body"]!.DeepClone(),
            });
        }
        return a;
    }
}
