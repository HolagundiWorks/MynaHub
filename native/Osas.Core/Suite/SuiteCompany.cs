using System.Text.Json;
using System.Text.Json.Nodes;
using Osas.Core;

namespace Osas.Core.Suite;

/// <summary>The company's own details, as printed on every document and shared by every app.</summary>
public sealed record CompanyDetails(
    string Name, string Address, string Email, string Phone, string Pan, string Gstin, string CoaRegistration, string BankDetails,
    string GstScheme, string UpiId, string ArchitectName, string LetterheadStyle, string AccentHex)
{
    internal static CompanyDetails From(JsonNode n)
    {
        string T(string key) => n[key]?.GetValue<string>() ?? "";
        return new CompanyDetails(T("name"), T("address"), T("email"), T("phone"), T("pan"), T("gstin"), T("coa_registration"), T("bank_details"),
            T("gst_scheme"), T("upi_id"), T("architect_name"), T("letterhead_style"), T("accent_hex"));
    }

    internal JsonObject ToJson() => new()
    {
        ["name"] = Name, ["address"] = Address, ["email"] = Email, ["phone"] = Phone, ["pan"] = Pan, ["gstin"] = Gstin,
        ["coa_registration"] = CoaRegistration, ["bank_details"] = BankDetails, ["gst_scheme"] = GstScheme, ["upi_id"] = UpiId,
        ["architect_name"] = ArchitectName, ["letterhead_style"] = LetterheadStyle, ["accent_hex"] = AccentHex,
    };
}

/// <summary>One of the company's users. They are the same for every app.</summary>
public sealed record UserInfo(long Id, string Username, string Role, bool Disabled)
{
    public string RoleLabel => RoleLabels.TryGetValue(Role, out var label) ? label : Role;

    public static readonly IReadOnlyDictionary<string, string> RoleLabels = new Dictionary<string, string>
    {
        ["owner"] = "Owner", ["principal"] = "Principal", ["accounts"] = "Accounts", ["document_editor"] = "Document editor", ["auditor"] = "Auditor",
    };
}

/// <summary>A one-time sign-in for an app OSAS is about to start. It works once, for two minutes.</summary>
public sealed record AppTicket(string Ticket, string Username);

/// <summary>
/// The company the suite is working in, opened and signed in by OSAS. OSAS holds the company file open, shows and edits the company's
/// details, and signs the apps in as the same person by giving each a ticket; it never keeps or passes the password.
/// </summary>
public sealed class SuiteCompany : IDisposable
{
    private readonly Engine _engine;

    private SuiteCompany(Engine engine, string path, string name, string uuid, string user, string role)
    {
        _engine = engine;
        Path = path;
        Name = name;
        Uuid = uuid;
        User = user;
        Role = role;
    }

    public string Path { get; }

    public string Name { get; private set; }

    public string Uuid { get; }

    public string User { get; }

    public string Role { get; }

    /// <summary>Opens the company file and signs in. A protected file also needs its password in <paramref name="filePassword"/>.</summary>
    public static SuiteCompany Open(string folder, string path, string user, string password, string filePassword = "")
    {
        var engine = Engine.Start(folder);
        try
        {
            var company = engine.Call("company.open", new JsonObject { ["path"] = path, ["secret"] = filePassword, ["unlock"] = filePassword.Length > 0 ? "password" : "" });
            var who = engine.Call("auth.login", new JsonObject { ["username"] = user, ["password"] = password });
            return new SuiteCompany(engine, path, company?["name"]?.GetValue<string>() ?? "", company?["uuid"]?.GetValue<string>() ?? "", who?["username"]?.GetValue<string>() ?? user, who?["role"]?.GetValue<string>() ?? "");
        }
        catch
        {
            engine.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Makes a new company file with its first user, the owner, and signs in as that person. The recovery code is returned once; it is the only
    /// way back in if the owner's password is forgotten, so the person must be shown it and asked to keep it.
    /// </summary>
    public static (SuiteCompany Company, string RecoveryCode) Create(string folder, string path, string companyName, string owner, string password, string confirm)
    {
        var engine = Engine.Start(folder);
        try
        {
            var made = engine.Call("company.create", new JsonObject { ["path"] = path, ["name"] = companyName, ["username"] = owner, ["password"] = password, ["confirm"] = confirm });
            var summary = made?["summary"];
            var who = engine.Call("auth.login", new JsonObject { ["username"] = owner, ["password"] = password });
            var actual = summary?["path"]?.GetValue<string>() ?? path;
            return (new SuiteCompany(engine, actual, summary?["name"]?.GetValue<string>() ?? companyName, summary?["uuid"]?.GetValue<string>() ?? "", who?["username"]?.GetValue<string>() ?? owner, who?["role"]?.GetValue<string>() ?? ""), made?["recovery"]?.GetValue<string>() ?? "");
        }
        catch
        {
            engine.Dispose();
            throw;
        }
    }

    public CompanyDetails Details() => CompanyDetails.From(_engine.Call("company_profile_get") ?? throw new EngineException("The company details could not be read."));

    /// <summary>Saves the details. Only someone who manages settings can; anyone else gets the engine's refusal.</summary>
    public void Save(CompanyDetails details)
    {
        _engine.Call("company_profile_save", new JsonObject { ["profile"] = details.ToJson() });
        Name = details.Name.Trim();
    }

    /// <summary>The company's users. Only someone who manages users can ask; anyone else gets the engine's refusal.</summary>
    public IReadOnlyList<UserInfo> Users() =>
        (_engine.Call("user_list") as JsonArray ?? [])
            .Where(n => n != null)
            .Select(n => new UserInfo(n!["id"]!.GetValue<long>(), n["username"]!.GetValue<string>(), n["role"]!.GetValue<string>(), n["disabled"]!.GetValue<bool>()))
            .ToList();

    /// <summary>Adds a user. Every change to people needs the signed-in person's own password again (<paramref name="adminPassword"/>).</summary>
    public long CreateUser(string username, string password, string role, string adminPassword) =>
        _engine.Call("user_create", new JsonObject { ["username"] = username, ["password"] = password, ["role"] = role, ["admin_password"] = adminPassword })!.GetValue<long>();

    /// <summary>Changes a user's role and whether they can sign in. The last active owner can never be demoted or disabled.</summary>
    public void UpdateUser(long id, string role, bool disabled, string adminPassword) =>
        _engine.Call("user_update", new JsonObject { ["id"] = id, ["role"] = role, ["disabled"] = disabled, ["admin_password"] = adminPassword });

    /// <summary>Sets a user's password, for someone who has forgotten theirs. It also clears any lockout.</summary>
    public void SetPassword(long id, string password, string adminPassword) =>
        _engine.Call("user_set_password", new JsonObject { ["id"] = id, ["password"] = password, ["admin_password"] = adminPassword });

    /// <summary>A ticket for one app. It works once, for two minutes, and signs that app in as the person signed in here.</summary>
    public AppTicket TicketFor(string app)
    {
        var t = _engine.Call("auth.ticket", new JsonObject { ["app"] = app }) ?? throw new EngineException("No ticket was given.");
        return new AppTicket(t["ticket"]!.GetValue<string>(), t["username"]?.GetValue<string>() ?? User);
    }

    /// <summary>The tools the assistant may use, from the engine's governed list. Every one only reads.</summary>
    public IReadOnlyList<Osas.Core.Agent.ToolSpec> AssistantTools() =>
        (_engine.Call("agent_tools") as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(t => new Osas.Core.Agent.ToolSpec(t["name"]!.GetValue<string>(), t["description"]?.GetValue<string>() ?? "", t["input_schema"]?.DeepClone() ?? new JsonObject()))
            .ToList();

    /// <summary>Runs one assistant tool as the person signed in here. The engine checks the person's role, checks the arguments and writes the audit entry.</summary>
    public string AssistantCall(string tool, JsonObject arguments)
    {
        try
        {
            return (_engine.Call("agent_call", new JsonObject { ["name"] = tool, ["arguments"] = arguments.DeepClone() }) ?? new JsonObject()).ToJsonString();
        }
        catch (EngineException ex)
        {
            throw new Osas.Core.Agent.AssistantException(ex.Message);
        }
    }

    public void Dispose()
    {
        try
        {
            _engine.Call("company.close");
        }
        catch (EngineException)
        {
            // Ending the session closes the file in any case.
        }
        _engine.Dispose();
    }
}

/// <summary>
/// What OSAS remembers between runs: which company file the suite works in. The apps follow it. It holds a path and nothing secret;
/// no password and no ticket is ever written down.
/// </summary>
public sealed class SuiteSettings
{
    private readonly string _file;

    public SuiteSettings(string? file = null) =>
        _file = file ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OSAS", "suite.json");

    public string? CompanyPath
    {
        get
        {
            try
            {
                if (File.Exists(_file) && JsonNode.Parse(File.ReadAllText(_file)) is JsonObject o && o["company_path"]?.GetValue<string>() is { Length: > 0 } path)
                    return path;
                // The monitor used to keep its own copy of this choice.
                var old = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_file)!, "company.txt");
                return File.Exists(old) && File.ReadAllText(old).Trim() is { Length: > 0 } text ? text : null;
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
            {
                return null;
            }
        }
        set
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_file)!);
            var o = File.Exists(_file) && JsonNode.Parse(File.ReadAllText(_file)) is JsonObject existing ? existing : new JsonObject();
            o["company_path"] = value;
            File.WriteAllText(_file, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    /// <summary>The last user name typed, to offer it again. A user name is not a secret.</summary>
    public string? LastUser
    {
        get
        {
            try
            {
                return File.Exists(_file) && JsonNode.Parse(File.ReadAllText(_file)) is JsonObject o ? o["last_user"]?.GetValue<string>() : null;
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
            {
                return null;
            }
        }
        set
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_file)!);
            var o = File.Exists(_file) && JsonNode.Parse(File.ReadAllText(_file)) is JsonObject existing ? existing : new JsonObject();
            o["last_user"] = value;
            File.WriteAllText(_file, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    /// <summary>A named setting that is not a secret (a program's location, a port). Returns null when it was never set.</summary>
    public string? Get(string key)
    {
        try
        {
            return File.Exists(_file) && JsonNode.Parse(File.ReadAllText(_file)) is JsonObject o ? o[key]?.GetValue<string>() : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    public void Set(string key, string? value)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_file)!);
        var o = File.Exists(_file) && JsonNode.Parse(File.ReadAllText(_file)) is JsonObject existing ? existing : new JsonObject();
        o[key] = value;
        File.WriteAllText(_file, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>The environment an app is started with: where the company file is, and the ticket that signs it in.</summary>
    public static IReadOnlyDictionary<string, string> AppEnvironment(string companyPath, AppTicket? ticket)
    {
        var env = new Dictionary<string, string> { [SuiteWire.EnvCompany] = companyPath };
        if (ticket != null)
        {
            env[SuiteWire.EnvTicket] = ticket.Ticket;
            env[SuiteWire.EnvUser] = ticket.Username;
        }
        return env;
    }
}
