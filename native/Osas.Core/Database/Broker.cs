using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Osas.Core.Database;

/// <summary>An error of the session broker (Kivo). <see cref="Code"/> is one of the codes in contracts/session-broker.md section 1.</summary>
public sealed class BrokerException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record BrokerStatus(bool SignedIn, string User, string CompanyId, string CompanyName, DateTime? ExpiresUtc);

public sealed record BrokerSession(string Token, DateTime? ExpiresUtc, string User, string CompanyId, IReadOnlyList<string> Roles);

public sealed record BrokerValidation(bool Valid, string User, string CompanyId, IReadOnlyList<string> Roles, DateTime? ExpiresUtc);

/// <summary>The client of the session broker that Kivo serves (contracts/session-broker.md section 1). Tests use a fake.</summary>
public interface IBrokerClient
{
    Task<BrokerStatus> StatusAsync(CancellationToken ct = default);

    /// <summary>Throws <see cref="BrokerException"/> (<c>signed_out</c> when not interactive and Kivo has no session, <c>kivo_unavailable</c> when the pipe is not there).</summary>
    Task<BrokerSession> AskSessionAsync(string app, string purpose, string? projectId, bool interactive, CancellationToken ct = default);

    Task<BrokerValidation> ValidateAsync(string token, CancellationToken ct = default);
}

/// <summary>Talks to Kivo over the named pipe <c>\\.\pipe\osas-session-broker</c>: one JSON message out, one back, then closes.</summary>
public sealed class PipeBrokerClient : IBrokerClient
{
    public const string PipeName = "osas-session-broker";

    readonly string _pipe;

    public PipeBrokerClient(string pipeName = PipeName) => _pipe = pipeName;

    public async Task<BrokerStatus> StatusAsync(CancellationToken ct = default)
    {
        var r = await CallAsync(new { v = 1, op = "status" }, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        return new BrokerStatus(Bool(r, "signed_in"), Str(r, "user"), Str(r, "company_id"), Str(r, "company_name"), Utc(r, "expires_utc"));
    }

    public async Task<BrokerSession> AskSessionAsync(string app, string purpose, string? projectId, bool interactive, CancellationToken ct = default)
    {
        var request = new Dictionary<string, object?> { ["v"] = 1, ["op"] = "ask_session", ["app"] = app, ["purpose"] = purpose, ["interactive"] = interactive };
        if (!string.IsNullOrEmpty(projectId))
            request["project_id"] = projectId;
        var r = await CallAsync(request, interactive ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        return new BrokerSession(Str(r, "token"), Utc(r, "expires_utc"), Str(r, "user"), Str(r, "company_id"), Roles(r));
    }

    public async Task<BrokerValidation> ValidateAsync(string token, CancellationToken ct = default)
    {
        var r = await CallAsync(new { v = 1, op = "validate", token }, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        return new BrokerValidation(Bool(r, "valid"), Str(r, "user"), Str(r, "company_id"), Roles(r), Utc(r, "expires_utc"));
    }

    async Task<JsonElement> CallAsync(object request, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(2000, cts.Token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new BrokerException("kivo_unavailable", "Kivo is not running (the session pipe is not there).");
            }

            pipe.ReadMode = PipeTransmissionMode.Message;
            await pipe.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(request), cts.Token).ConfigureAwait(false);
            await pipe.FlushAsync(cts.Token).ConfigureAwait(false);

            using var ms = new MemoryStream();
            var buffer = new byte[4096];
            do
            {
                var n = await pipe.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
                if (n == 0)
                    break;
                ms.Write(buffer, 0, n);
                if (ms.Length > 1 << 20)
                    throw new BrokerException("bad_request", "The broker answered with too much data.");
            }
            while (!pipe.IsMessageComplete);

            return Parse(Encoding.UTF8.GetString(ms.ToArray()));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new BrokerException("kivo_unavailable", "Kivo did not answer in time.");
        }
        catch (IOException ex)
        {
            throw new BrokerException("kivo_unavailable", "The session pipe closed: " + ex.Message);
        }
    }

    /// <summary>Reads one response object; <c>ok:false</c> becomes a <see cref="BrokerException"/>.</summary>
    internal static JsonElement Parse(string json)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(json).RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new BrokerException("bad_request", "The broker answer was not JSON.");
        }

        if (root.ValueKind != JsonValueKind.Object)
            throw new BrokerException("bad_request", "The broker answer was not an object.");
        if (!Bool(root, "ok"))
            throw new BrokerException(Str(root, "error") is { Length: > 0 } code ? code : "bad_request", Str(root, "message"));
        return root;
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    static DateTime? Utc(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && DateTime.TryParse(v.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
            ? d
            : null;

    static IReadOnlyList<string> Roles(JsonElement e) =>
        e.TryGetProperty("roles", out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").ToList()
            : [];
}
