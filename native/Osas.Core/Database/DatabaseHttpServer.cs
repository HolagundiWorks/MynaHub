using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace Osas.Core.Database;

/// <summary>
/// The loopback HTTP face of <see cref="DatabaseService"/> (contracts/session-broker.md section 2): <c>127.0.0.1:8743</c>, JSON, Bearer token on everything but
/// <c>/v1/health</c>. A small hand-written HTTP/1.1 server (one request per connection, plus the long-lived events stream), so it needs no URL reservation or
/// admin rights, and no web framework in the launcher.
/// </summary>
public sealed class DatabaseHttpServer : IDisposable
{
    public const int DefaultPort = 8743;
    public const string EnvDbUrl = "OSAS_DB_URL";

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    const int MaxBody = 64 * 1024;

    readonly DatabaseService _service;
    readonly int _requestedPort;
    TcpListener? _listener;
    CancellationTokenSource? _cts;

    public DatabaseHttpServer(DatabaseService service, int port = DefaultPort)
    {
        _service = service;
        _requestedPort = port;
    }

    /// <summary>The port actually bound (differs from the requested one only when 0 was asked for, as the tests do).</summary>
    public int Port { get; private set; }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>Why <see cref="Start"/> failed (for example the port is taken), or empty.</summary>
    public string Problem { get; private set; } = "";

    public bool IsRunning => _listener != null;

    /// <summary>Start listening on loopback only. Returns false (with <see cref="Problem"/>) when the port cannot be had; the launcher then shows the database as offline.</summary>
    public bool Start()
    {
        if (_listener != null)
            return true;
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, _requestedPort);
            listener.Server.ExclusiveAddressUse = true;
            listener.Start(32);
            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        catch (SocketException ex)
        {
            Problem = ex.SocketErrorCode == SocketError.AddressAlreadyInUse || ex.SocketErrorCode == SocketError.AccessDenied
                ? $"port {_requestedPort} is used by another program"
                : ex.Message;
            return false;
        }

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(_listener, _cts.Token));
        return true;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch (SocketException) { }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }

    async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(client, ct), ct);
        }
    }

    sealed record Request(string Method, string Path, Dictionary<string, string> Headers, byte[] Body);

    async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                var req = await ReadRequestAsync(stream, timeout.Token).ConfigureAwait(false);
                if (req == null)
                    return;
                var guard = Guard(req);
                if (guard != null)
                {
                    await WriteJsonAsync(stream, guard.Status, Error(guard), ct).ConfigureAwait(false);
                    return;
                }

                var path = req.Path;
                var query = path.IndexOf('?');
                if (query >= 0)
                    path = path[..query];
                var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

                if (segments is ["v1", "health"] && req.Method == "GET")
                {
                    await WriteJsonAsync(stream, 200, _service.Health(), ct).ConfigureAwait(false);
                    return;
                }

                var auth = await _service.AuthenticateAsync(BearerOf(req), ct).ConfigureAwait(false);

                if (segments is ["v1", "projects", "connect"] && req.Method == "POST")
                {
                    var body = ParseBody(req);
                    var result = await _service.ConnectAsync(auth, Str(body, "project_id"), Str(body, "name"), Str(body, "osp_path"), ct).ConfigureAwait(false);
                    await WriteJsonAsync(stream, 200, result, ct).ConfigureAwait(false);
                }
                else if (segments is ["v1", "projects", var id] && req.Method == "GET")
                {
                    await WriteJsonAsync(stream, 200, _service.GetProject(Uri.UnescapeDataString(id)), ct).ConfigureAwait(false);
                }
                else if (segments is ["v1", "projects", var pid, "push"] && req.Method == "POST")
                {
                    var body = ParseBody(req);
                    long? revision = body.TryGetProperty("revision", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt64(out var n) ? n : null;
                    var result = await _service.PushAsync(auth, Uri.UnescapeDataString(pid), revision, Str(body, "sha256"), ct).ConfigureAwait(false);
                    await WriteJsonAsync(stream, 200, result, ct).ConfigureAwait(false);
                }
                else if (segments is ["v1", "projects", var qid, "pull"] && req.Method == "POST")
                {
                    var result = await _service.PullAsync(auth, Uri.UnescapeDataString(qid), ct).ConfigureAwait(false);
                    await WriteJsonAsync(stream, 200, result, ct).ConfigureAwait(false);
                }
                else if (segments is ["v1", "projects", var eid, "events"] && req.Method == "GET")
                {
                    _service.GetProject(Uri.UnescapeDataString(eid)); // 404 when not connected
                    await StreamEventsAsync(stream, Uri.UnescapeDataString(eid), ct).ConfigureAwait(false);
                }
                else
                {
                    await WriteJsonAsync(stream, 404, new { ok = false, error = "not_found", message = "No such endpoint." }, ct).ConfigureAwait(false);
                }
            }
            catch (ServiceException ex)
            {
                await TryWriteAsync(stream, ex, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
                // the caller went away, or the service is stopping
            }
            catch (Exception ex)
            {
                _service.Log?.Invoke("Database service request failed: " + ex.Message);
                await TryWriteAsync(stream, new ServiceException(500, "bad_request", "The database service could not handle the request."), ct).ConfigureAwait(false);
            }
        }
    }

    static async Task TryWriteAsync(NetworkStream stream, ServiceException ex, CancellationToken ct)
    {
        try
        {
            await WriteJsonAsync(stream, ex.Status, Error(ex), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    static object Error(ServiceException ex)
    {
        var dict = new Dictionary<string, object?> { ["ok"] = false, ["error"] = ex.Code, ["message"] = ex.Message };
        if (ex.Extra != null)
            foreach (var kv in ex.Extra)
                dict[kv.Key] = kv.Value;
        return dict;
    }

    /// <summary>Refuse what a web page in a browser could send: another Host (DNS rebinding) or any Origin.</summary>
    static ServiceException? Guard(Request req)
    {
        if (req.Headers.ContainsKey("origin"))
            return new ServiceException(403, "denied", "Browser requests are not allowed.");
        if (req.Headers.TryGetValue("host", out var host))
        {
            var name = host.Contains(':') ? host[..host.LastIndexOf(':')] : host;
            if (!name.Equals("127.0.0.1", StringComparison.Ordinal) && !name.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                return new ServiceException(403, "denied", "Unexpected Host header.");
        }

        return null;
    }

    static string? BearerOf(Request req)
    {
        if (!req.Headers.TryGetValue("authorization", out var value))
            return null;
        const string prefix = "Bearer ";
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? value[prefix.Length..].Trim() : null;
    }

    static JsonElement ParseBody(Request req)
    {
        if (req.Body.Length == 0)
            return JsonDocument.Parse("{}").RootElement;
        try
        {
            var el = JsonDocument.Parse(req.Body).RootElement.Clone();
            return el.ValueKind == JsonValueKind.Object ? el : throw new ServiceException(400, "bad_request", "The body must be a JSON object.");
        }
        catch (JsonException)
        {
            throw new ServiceException(400, "bad_request", "The body is not valid JSON.");
        }
    }

    static string Str(JsonElement body, string name) =>
        body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static async Task<Request?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var head = new MemoryStream();
        var one = new byte[1];
        // Read up to the blank line that ends the header; the header is small.
        while (head.Length < 16 * 1024)
        {
            var n = await stream.ReadAsync(one, ct).ConfigureAwait(false);
            if (n == 0)
                return null;
            head.WriteByte(one[0]);
            var len = (int)head.Length;
            if (len >= 4)
            {
                var b = head.GetBuffer();
                if (b[len - 4] == '\r' && b[len - 3] == '\n' && b[len - 2] == '\r' && b[len - 1] == '\n')
                    break;
            }
        }

        var lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            return null;
        var first = lines[0].Split(' ');
        if (first.Length < 2)
            return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var i = line.IndexOf(':');
            if (i > 0)
                headers[line[..i].Trim().ToLowerInvariant()] = line[(i + 1)..].Trim();
        }

        var body = Array.Empty<byte>();
        if (headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out var length) && length > 0)
        {
            if (length > MaxBody)
                throw new ServiceException(413, "bad_request", "The body is too large.");
            body = new byte[length];
            var read = 0;
            while (read < length)
            {
                var n = await stream.ReadAsync(body.AsMemory(read, length - read), ct).ConfigureAwait(false);
                if (n == 0)
                    return null;
                read += n;
            }
        }

        return new Request(first[0].ToUpperInvariant(), first[1], headers, body);
    }

    static async Task WriteJsonAsync(NetworkStream stream, int status, object payload, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        var head = $"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    async Task StreamEventsAsync(NetworkStream stream, string projectId, CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<ServiceEvent>();
        void OnEvent(ServiceEvent e)
        {
            if (e.ProjectId == null || string.Equals(e.ProjectId, projectId, StringComparison.OrdinalIgnoreCase))
                channel.Writer.TryWrite(e);
        }

        _service.Event += OnEvent;
        try
        {
            var head = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream; charset=utf-8\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
            // The current state first, so a new listener does not wait for a change.
            var s = _service.GetProject(projectId);
            await WriteEventAsync(stream, "state", new { state = s.State, sync = _service.Sync, service = _service.State }, ct).ConfigureAwait(false);
            await WriteEventAsync(stream, "revision", new { remote_revision = s.RemoteRevision }, ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(TimeSpan.FromSeconds(20));
                try
                {
                    var e = await channel.Reader.ReadAsync(wait.Token).ConfigureAwait(false);
                    await WriteEventAsync(stream, e.Name, e.Data, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // keep-alive comment so a dead connection is noticed
                    await stream.WriteAsync(": keep-alive\n\n"u8.ToArray(), ct).ConfigureAwait(false);
                    await stream.FlushAsync(ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _service.Event -= OnEvent;
        }
    }

    static async Task WriteEventAsync(NetworkStream stream, string name, object data, CancellationToken ct)
    {
        var text = $"event: {name}\ndata: {JsonSerializer.Serialize(data, Json)}\n\n";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    static string Reason(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        409 => "Conflict",
        413 => "Payload Too Large",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        _ => "Error",
    };
}
