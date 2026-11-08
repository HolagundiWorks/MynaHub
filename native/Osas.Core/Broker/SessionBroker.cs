using System.Text.Json;
using System.Text.Json.Nodes;

namespace Osas.Core.Broker;

/// <summary>What the broker needs from Kivo: who is signed in, and a way to ask the person to sign in.</summary>
public interface IBrokerHost
{
    /// <summary>The person signed in now, or null when signed out or the session is locked.</summary>
    BrokerIdentity? CurrentIdentity { get; }

    /// <summary>When the current sign-in began (UTC). Only read while <see cref="CurrentIdentity"/> is not null.</summary>
    DateTime SignedInUtc { get; }

    /// <summary>
    /// Brings Kivo to the front with its sign-in and waits. Returns the person who signed in, or null if they closed it.
    /// Calls made while a sign-in is already showing share it.
    /// </summary>
    Task<BrokerIdentity?> RequestSignInAsync(CancellationToken cancel);
}

/// <summary>
/// The requests of docs/contracts/session-broker.md, section 1, with no pipe and no window in it so it can be tested alone.
/// <see cref="HandleAsync"/> takes one JSON request and gives the one JSON response.
/// </summary>
public sealed class SessionBroker
{
    public static readonly string[] Apps = ["tren", "falcon", "myna", "dbservice"];
    public static readonly string[] Purposes = ["db_connect", "save_to_office", "import", "sync"];

    private readonly IBrokerHost _host;

    public SessionBroker(IBrokerHost host, SessionTokenTable? tokens = null)
    {
        _host = host;
        Tokens = tokens ?? new SessionTokenTable();
    }

    public SessionTokenTable Tokens { get; }

    /// <summary>Kivo signed out, closed the company or ended the session: every token ends.</summary>
    public void EndAllSessions() => Tokens.Clear();

    public async Task<string> HandleAsync(string requestJson, CancellationToken cancel = default)
    {
        JsonObject request;
        try
        {
            request = JsonNode.Parse(requestJson) as JsonObject ?? throw new JsonException();
        }
        catch (JsonException)
        {
            return Fail("bad_request", "The request is not a JSON object.");
        }

        if (!TryInt(request["v"], out var version) || version != 1)
            return Fail("bad_request", "Only protocol version 1 is understood.");
        var op = Str(request["op"]);
        try
        {
            return op switch
            {
                "status" => Status(),
                "ask_session" => await AskAsync(request, cancel),
                "validate" => Validate(request),
                "renew" => Renew(request),
                "end_session" => End(request),
                _ => Fail("bad_request", "Unknown op."),
            };
        }
        catch (OperationCanceledException)
        {
            return Fail("cancelled", "The request was cancelled.");
        }
    }

    private string Status()
    {
        var who = _host.CurrentIdentity;
        if (who == null)
            return Ok(new JsonObject { ["signed_in"] = false });
        return Ok(new JsonObject
        {
            ["signed_in"] = true,
            ["user"] = who.User,
            ["company_id"] = who.CompanyId,
            ["company_name"] = who.CompanyName,
            ["expires_utc"] = Iso(_host.SignedInUtc + SessionTokenTable.AbsoluteLifetime),
        });
    }

    private async Task<string> AskAsync(JsonObject request, CancellationToken cancel)
    {
        var app = Str(request["app"]);
        var purpose = Str(request["purpose"]);
        if (!Apps.Contains(app) || !Purposes.Contains(purpose))
            return Fail("bad_request", "Unknown app or purpose.");
        var project = Str(request["project_id"]);
        var interactive = request["interactive"] is not JsonValue flag || !flag.TryGetValue<bool>(out var wanted) || wanted;

        var who = _host.CurrentIdentity;
        if (who == null)
        {
            if (!interactive)
                return Fail("signed_out", "Kivo is not signed in.");
            who = await _host.RequestSignInAsync(cancel);
            if (who == null)
                return Fail("cancelled", "The sign-in was closed.");
        }

        if (!Allows(who, purpose))
            return Fail("denied", $"Your role does not allow \"{purpose}\".");

        var (token, expires) = Tokens.Issue(new TokenKey(app, purpose, project), who);
        return Ok(new JsonObject
        {
            ["token"] = token,
            ["expires_utc"] = Iso(expires),
            ["user"] = who.User,
            ["company_id"] = who.CompanyId,
            ["roles"] = RolesJson(who),
        });
    }

    /// <summary>An auditor may only connect to read; everyone else may do everything. Roles come from the company file.</summary>
    public static bool Allows(BrokerIdentity who, string purpose)
    {
        if (purpose == "db_connect")
            return true;
        return !who.Roles.All(r => string.Equals(r, "auditor", StringComparison.OrdinalIgnoreCase));
    }

    private string Validate(JsonObject request)
    {
        var info = Tokens.Validate(Str(request["token"]));
        if (info == null)
            return Ok(new JsonObject { ["valid"] = false });
        return Ok(new JsonObject
        {
            ["valid"] = true,
            ["user"] = info.Identity.User,
            ["company_id"] = info.Identity.CompanyId,
            ["roles"] = RolesJson(info.Identity),
            ["expires_utc"] = Iso(info.ExpiresUtc),
        });
    }

    private string Renew(JsonObject request)
    {
        switch (Tokens.Renew(Str(request["token"]), out var expires))
        {
            case SessionTokenTable.RenewResult.Renewed:
                return Ok(new JsonObject { ["expires_utc"] = Iso(expires) });
            case SessionTokenTable.RenewResult.Expired:
                return Fail("expired", "The session reached its limit. Ask again.");
            default:
                return Fail("bad_token", "That token is not known.");
        }
    }

    private string End(JsonObject request)
    {
        if (request["token"] is { } token)
            Tokens.End(Str(token));
        else
            Tokens.Clear();
        return Ok(new JsonObject());
    }

    private static JsonArray RolesJson(BrokerIdentity who)
    {
        var roles = new JsonArray();
        foreach (var r in who.Roles)
            roles.Add(r);
        return roles;
    }

    private static string Iso(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    private static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static bool TryInt(JsonNode? n, out int value)
    {
        value = 0;
        return n is JsonValue v && v.TryGetValue<int>(out value);
    }

    private static string Ok(JsonObject result)
    {
        var o = new JsonObject { ["v"] = 1, ["ok"] = true };
        foreach (var (k, v) in result.ToList())
        {
            result.Remove(k);
            o[k] = v;
        }
        return o.ToJsonString();
    }

    private static string Fail(string code, string message) =>
        new JsonObject { ["v"] = 1, ["ok"] = false, ["error"] = code, ["message"] = message }.ToJsonString();
}
