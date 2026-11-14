using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Adms.Cloud;

/// <summary>What a Neon Auth sign-in leaves behind. <see cref="SessionCookies"/> is the long-lived secret (stored with DPAPI); never log it.</summary>
public sealed class CloudSession
{
    public string UserId { get; init; } = "";
    public string Email { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>The "name=value" pairs of the Set-Cookie headers, joined with "; ".</summary>
    public string SessionCookies { get; init; } = "";
}

public sealed class CloudAuthException : Exception
{
    public CloudAuthException(string message, HttpStatusCode? status = null) : base(message) => Status = status;
    public HttpStatusCode? Status { get; }
    /// <summary>True when the server refused the credentials or the session (as opposed to the network failing).</summary>
    public bool Rejected => Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest;
}

/// <summary>
/// Neon Auth (Managed Better Auth) over HTTPS, for MynaHub only (docs/hub-sync-design.md section 3). Email and password: <c>POST /sign-in/email</c>,
/// then <c>GET /get-session</c> whose <c>set-auth-jwt</c> response header carries a short-lived access token. No Neon database credential is involved.
/// </summary>
public sealed class CloudAuthClient : IDisposable
{
    readonly HttpClient _http;

    /// <param name="baseUrl">NEON_AUTH_BASE_URL (a public endpoint, not a secret).</param>
    /// <param name="origin">Sent as the Origin header; must be a trusted origin of the Neon Auth branch.</param>
    public CloudAuthClient(string baseUrl, string origin = "http://localhost:3000", HttpMessageHandler? handler = null)
    {
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback))
            throw new ArgumentException("The Neon Auth address must be an https URL.", nameof(baseUrl));
        _http = handler is null ? new HttpClient(new HttpClientHandler { UseCookies = false }) : new HttpClient(handler);
        _http.BaseAddress = uri;
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.Add("Origin", origin);
    }

    public async Task<CloudSession> SignInAsync(string email, string password, CancellationToken ct = default)
    {
        using var res = await SendAsync(HttpMethod.Post, "sign-in/email", null, JsonContent.Create(new { email, password }), ct);
        if (!res.IsSuccessStatusCode)
            throw new CloudAuthException(
                res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest
                    ? "The cloud account email or password is wrong."
                    : $"Cloud sign-in failed ({(int)res.StatusCode}).",
                res.StatusCode);
        var cookies = CollectCookies(res);
        if (cookies.Length == 0)
            throw new CloudAuthException("The cloud service did not return a session.", res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var user = doc.RootElement.TryGetProperty("user", out var u) ? u : default;
        return new CloudSession
        {
            UserId = Str(user, "id"),
            Email = Str(user, "email"),
            Name = Str(user, "name"),
            SessionCookies = cookies,
        };
    }

    /// <summary>A fresh access token (JWT, memory only). A rejected session means the person must sign in again.</summary>
    public async Task<string> GetAccessTokenAsync(CloudSession session, CancellationToken ct = default)
    {
        using var res = await SendAsync(HttpMethod.Get, "get-session", session.SessionCookies, null, ct);
        if (!res.IsSuccessStatusCode)
            throw new CloudAuthException($"Cloud session check failed ({(int)res.StatusCode}).", res.StatusCode);
        if (res.Headers.TryGetValues("set-auth-jwt", out var jwt) && jwt.FirstOrDefault() is { Length: > 0 } token)
            return token;
        var body = await res.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body) || body.Trim() == "null")
            throw new CloudAuthException("The cloud session has ended; sign in again.", HttpStatusCode.Unauthorized);
        throw new CloudAuthException("The cloud service returned no access token.");
    }

    public async Task SignOutAsync(CloudSession session, CancellationToken ct = default)
    {
        try
        {
            using var _ = await SendAsync(HttpMethod.Post, "sign-out", session.SessionCookies, JsonContent.Create(new { }), ct);
        }
        catch (HttpRequestException)
        {
            // Offline sign-out still clears the local copy; the server session expires on its own.
        }
    }

    Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? cookies, HttpContent? content, CancellationToken ct)
    {
        var req = new HttpRequestMessage(method, path) { Content = content };
        if (!string.IsNullOrEmpty(cookies))
            req.Headers.TryAddWithoutValidation("Cookie", cookies);
        return _http.SendAsync(req, ct);
    }

    static string CollectCookies(HttpResponseMessage res) =>
        res.Headers.TryGetValues("Set-Cookie", out var all)
            ? string.Join("; ", all.Select(c => c.Split(';')[0].Trim()).Where(c => c.Contains('=') && !c.EndsWith('=')))
            : "";

    static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public void Dispose() => _http.Dispose();
}
