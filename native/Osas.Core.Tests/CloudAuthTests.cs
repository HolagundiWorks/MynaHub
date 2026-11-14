using System.Net;
using System.Text;
using Adms.Cloud;
using Xunit;

namespace Osas.Core.Tests;

public class CloudAuthTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "osas-cloudauth-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add(request);
            return Task.FromResult(reply(request));
        }
    }

    static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Sign_in_returns_the_user_and_the_session_cookies()
    {
        var h = new FakeHandler(r =>
        {
            var res = Json(HttpStatusCode.OK, "{\"user\":{\"id\":\"u1\",\"email\":\"a@example.test\",\"name\":\"A\"}}");
            res.Headers.Add("Set-Cookie", "neon-auth.session_token=abc; Path=/; HttpOnly");
            res.Headers.Add("Set-Cookie", "neon-auth.other=; Path=/");
            return res;
        });
        using var c = new CloudAuthClient("https://auth.example.test/db/auth", handler: h);
        var s = await c.SignInAsync("a@example.test", "pw");
        Assert.Equal("u1", s.UserId);
        Assert.Equal("neon-auth.session_token=abc", s.SessionCookies);
        Assert.EndsWith("/db/auth/sign-in/email", h.Seen[0].RequestUri!.AbsoluteUri);
        Assert.True(h.Seen[0].Headers.Contains("Origin"));
    }

    [Fact]
    public async Task Wrong_password_is_reported_as_rejected_without_echoing_it()
    {
        using var c = new CloudAuthClient("https://auth.example.test/a", handler: new FakeHandler(_ => Json(HttpStatusCode.Unauthorized, "{}")));
        var ex = await Assert.ThrowsAsync<CloudAuthException>(() => c.SignInAsync("a@example.test", "secret-pw"));
        Assert.True(ex.Rejected);
        Assert.DoesNotContain("secret-pw", ex.Message);
    }

    [Fact]
    public async Task Access_token_comes_from_the_set_auth_jwt_header_and_sends_the_cookie()
    {
        var h = new FakeHandler(r =>
        {
            var res = Json(HttpStatusCode.OK, "{\"session\":{}}");
            res.Headers.Add("set-auth-jwt", "jwt-value");
            return res;
        });
        using var c = new CloudAuthClient("https://auth.example.test/a", handler: h);
        var token = await c.GetAccessTokenAsync(new CloudSession { SessionCookies = "k=v" });
        Assert.Equal("jwt-value", token);
        Assert.Equal("k=v", h.Seen[0].Headers.GetValues("Cookie").Single());
    }

    [Fact]
    public async Task A_ended_session_asks_for_a_new_sign_in()
    {
        using var c = new CloudAuthClient("https://auth.example.test/a", handler: new FakeHandler(_ => Json(HttpStatusCode.OK, "null")));
        var ex = await Assert.ThrowsAsync<CloudAuthException>(() => c.GetAccessTokenAsync(new CloudSession { SessionCookies = "k=v" }));
        Assert.True(ex.Rejected);
    }

    /// <summary>
    /// Manual check against a real Neon Auth branch. Does nothing unless MYNA_LIVE_AUTH_URL, MYNA_LIVE_AUTH_EMAIL and MYNA_LIVE_AUTH_PASSWORD are set
    /// (use a disposable test account on the dev branch only); a pass without them proves nothing.
    /// </summary>
    [Fact]
    public async Task Live_sign_in_token_and_sign_out_against_a_dev_branch()
    {
        var url = Environment.GetEnvironmentVariable("MYNA_LIVE_AUTH_URL");
        var email = Environment.GetEnvironmentVariable("MYNA_LIVE_AUTH_EMAIL");
        var pw = Environment.GetEnvironmentVariable("MYNA_LIVE_AUTH_PASSWORD");
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(email) || string.IsNullOrEmpty(pw))
            return;
        using var c = new CloudAuthClient(url);
        var s = await c.SignInAsync(email, pw);
        Assert.False(string.IsNullOrEmpty(s.UserId));
        var jwt = await c.GetAccessTokenAsync(s);
        Assert.Equal(3, jwt.Split('.').Length);
        await c.SignOutAsync(s);
        await Assert.ThrowsAsync<CloudAuthException>(() => c.GetAccessTokenAsync(s));
    }

    [Fact]
    public void A_plain_http_address_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new CloudAuthClient("http://auth.example.test/a"));
    }

    [Fact]
    public void The_session_is_stored_sealed_per_company_and_cleared_on_sign_out()
    {
        var file = Path.Combine(_dir, "cloud-auth.json");
        // A reversible stand-in for DPAPI keeps the test machine-independent; the real store uses ProtectedData.
        var store = new CloudSessionStore(file, b => b.Reverse().ToArray(), b => b.Reverse().ToArray());
        store.Save("co-1", new CloudSession { UserId = "u1", Email = "a@example.test", SessionCookies = "k=SECRETCOOKIE" }, "owner");

        Assert.DoesNotContain("SECRETCOOKIE", File.ReadAllText(file));
        Assert.Equal("k=SECRETCOOKIE", store.Load("co-1")!.SessionCookies);
        Assert.Null(store.Load("co-2"));
        Assert.Equal("owner", store.Info("co-1")!.LocalUser);

        store.Clear("co-1");
        Assert.Null(store.Load("co-1"));
    }

    [Fact]
    public void A_session_that_cannot_be_opened_is_treated_as_signed_out()
    {
        var file = Path.Combine(_dir, "cloud-auth.json");
        new CloudSessionStore(file, b => b, b => b).Save("co-1", new CloudSession { SessionCookies = "k=v" }, "owner");
        var other = new CloudSessionStore(file, b => b, _ => throw new System.Security.Cryptography.CryptographicException());
        Assert.Null(other.Load("co-1"));
    }
}
