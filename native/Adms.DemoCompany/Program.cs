using System.Security.Cryptography;
using System.Text;
using Adms.Cloud;
using Adms.Interop;

// A sample company for trying MynaHub (and, optionally, MynaHub's cloud sync on YOUR OWN Neon project). Everything here is fictional.
//   --out <folder>   where to create "Demo Studio.osas" and demo-credentials.txt (default: Documents\MynaHub Demo)
//   --cloud          also link the demo company to your cloud and sync it. Needs NEON_AUTH_BASE_URL, NEON_DATA_API_URL,
//                    MYNA_DEMO_CLOUD_EMAIL and MYNA_DEMO_CLOUD_PASSWORD (a cloud account you created on your own Neon project).
// It never overwrites: if the file or the credentials file exists it stops. Passwords are random per run and are written only to the
// credentials file next to the company file, never to the console.

var cloud = args.Contains("--cloud", StringComparer.OrdinalIgnoreCase);
var outIndex = Array.FindIndex(args, a => a.Equals("--out", StringComparison.OrdinalIgnoreCase));
var outDir = outIndex >= 0 && outIndex + 1 < args.Length
    ? args[outIndex + 1]
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MynaHub Demo");
Directory.CreateDirectory(outDir);
var companyFile = Path.Combine(outDir, "Demo Studio.osas");
var credentialsFile = Path.Combine(outDir, "demo-credentials.txt");
if (File.Exists(companyFile) || File.Exists(credentialsFile))
{
    Console.Error.WriteLine($"Stopped: {outDir} already has a demo company or credentials file. Choose another folder with --out; nothing was changed.");
    return 2;
}

string Password()
{
    const string alphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    var bytes = RandomNumberGenerator.GetBytes(16);
    return "Demo-" + new string(bytes.Select(b => alphabet[b % alphabet.Length]).ToArray());
}

// username, role, who they are (fictional)
var people = new (string User, string Role, string Who)[]
{
    ("demo-owner", "owner", "Asha Verma, principal architect and owner (does everything)"),
    ("demo-principal", "principal", "Rohan Iyer, senior architect (views and issues documents)"),
    ("demo-accounts", "accounts", "Meera Nair, accounts (billing, books, posting)"),
    ("demo-editor", "document_editor", "Kabir Shah, junior architect (edits documents, no finance)"),
    ("demo-auditor", "auditor", "Tara Menon, external auditor (read only, sees the audit trail)"),
};
var passwords = people.ToDictionary(p => p.User, _ => Password());
var ownerPassword = passwords["demo-owner"];

var sb = new StringBuilder();
using (var session = new AdmsSession(outDir))
{
    var created = session.CreateCompany(companyFile, "Sample Architects (demo)", "demo-owner", ownerPassword, ownerPassword);
    var companyUuid = created.Summary.Uuid;
    session.Login("demo-owner", ownerPassword);

    session.Call("company_save_profile", new
    {
        profile = new
        {
            name = "Sample Architects (demo)",
            address = "1 Example Street, Sampletown 000000",
            email = "studio@sample-architects.example",
            phone = "+00 00 0000 0000",
            pan = "AAAPL1234C",
            gstin = "27AAAPL1234C1Z5",
            coa_registration = "DEMO-0000",
            bank_details = "Demo Bank, A/c 0000000000, IFSC DEMO0000001",
            gst_scheme = "regular",
            upi_id = "demo@upi",
            architect_name = "Asha Verma",
            letterhead_style = "standard",
            accent_hex = "b83e16",
        },
    });

    foreach (var p in people.Where(p => p.User != "demo-owner"))
        session.Call("user_create", new { username = p.User, password = passwords[p.User], role = p.Role, admin_password = ownerPassword });

    long Client(string name, string contact, string phone, string email, string address) =>
        session.Call("client_save", new { client = new { id = (long?)null, name, contact_person = contact, email, phone, address, pan = "", gstin = "" } }).GetInt64();
    long Project(long client, string code, string name, string site, string status) =>
        session.Call("project_save", new { project = new { id = (long?)null, client_id = client, code, name, site_address = site, status } }).GetInt64();
    long Task(long? project, string title, string status, int position, string due = "") =>
        session.Call("task_save", new { input = new { id = (long?)null, project_id = project, title, notes = "", status, position, due_date = due } }).GetInt64();

    var rao = Client("Rao Family", "Dr. Ananya Rao", "+00 11 1111 1111", "rao@family.example", "Plot 42, Hill Road");
    var transit = Client("Metro Transit Authority", "K. Somasekhar", "+00 22 2222 2222", "projects@transit.example", "Metro Bhavan, Main Road");
    var school = Client("Greenfield School Trust", "Mrs. Pillai", "+00 33 3333 3333", "office@greenfield.example", "Greenfield Campus");

    var villa = Project(rao, "DEMO-01", "Hillside Villa", "Hill Road, Sampletown", "active");
    var pavilion = Project(transit, "DEMO-02", "Transit Plaza Pavilion", "Main Road Station", "active");
    var library = Project(school, "DEMO-03", "School Library Annexe", "Greenfield Campus", "on_hold");
    var clinic = Project(rao, "DEMO-04", "Garden Clinic", "Hill Road, Sampletown", "completed");

    session.Call("project_stages_apply", new { project_id = villa, consultancy = "comprehensive", floors = 2 });
    session.Call("project_stages_apply", new { project_id = pavilion, consultancy = "comprehensive", floors = 1 });
    session.Call("project_stages_apply", new { project_id = library, consultancy = "comprehensive", floors = 1 });

    Task(villa, "Site survey and measured drawing", "done", 1, "2026-01-15");
    Task(villa, "Concept options for the clients", "doing", 2, "2026-02-10");
    Task(villa, "Structural consultant brief", "todo", 3, "2026-02-28");
    Task(pavilion, "Review canopy geometry with the engineer", "doing", 1, "2026-02-05");
    Task(pavilion, "Prepare tender drawing set", "todo", 2);
    Task(library, "Waiting for trust approval of the brief", "todo", 1);
    Task(clinic, "Handover and defects walk", "done", 1, "2025-12-10");
    Task(null, "Renew the practice software subscriptions", "todo", 1, "2026-03-31");
    Task(null, "Quarterly GST filing", "todo", 2, "2026-04-15");

    session.Call("proposal_save", new
    {
        input = new
        {
            id = (long?)null, client_id = rao, project_id = (long?)villa, title = "Hillside Villa: architectural services", basis = "percent",
            cost_paise = 500000000L, rate_bp = 600L, area_milli = 0L, area_rate_paise = 0L, lump_paise = 0L, gst_bp = 1800L,
            issued_date = "2026-01-05", valid_until = "2026-02-05", notes = "Demo proposal.",
        },
    });

    sb.AppendLine("MynaHub demo company: FICTIONAL, for trying the app only. Delete this folder when you are done.");
    sb.AppendLine($"Company file: {companyFile}");
    sb.AppendLine($"Company recovery code (keep it; it resets the owner): {created.Recovery}");
    sb.AppendLine();
    sb.AppendLine("Open MynaHub > Open company file, choose the .osas above, then sign in as any of:");
    foreach (var p in people)
        sb.AppendLine($"  {p.User,-15} role {p.Role,-16} password {passwords[p.User]}   ({p.Who})");
    sb.AppendLine();
    sb.AppendLine("Try the roles: the auditor cannot edit, the document editor cannot see accounts, only the owner manages users and settings.");

    if (cloud)
    {
        string Need(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v : throw new InvalidOperationException($"--cloud needs {n}.");
        using var auth = new CloudAuthClient(Need("NEON_AUTH_BASE_URL"));
        var cloudSession = await auth.SignInAsync(Need("MYNA_DEMO_CLOUD_EMAIL"), Need("MYNA_DEMO_CLOUD_PASSWORD"));
        using var api = new HubDataApi(Need("NEON_DATA_API_URL"), ct => auth.GetAccessTokenAsync(cloudSession, ct));
        var engine = new HubSyncEngine(session.Call, api);
        await engine.LinkAsync(companyUuid, "Sample Architects (demo)");
        var r = await engine.RunOnceAsync();
        await auth.SignOutAsync(cloudSession);
        sb.AppendLine();
        sb.AppendLine($"Cloud: linked as cloud company {companyUuid}; sent {r.Pushed} records ({(r.Complete ? "complete" : "some still waiting")}).");
        sb.AppendLine("To let another person's phone join it, create their cloud account on your own Neon project, then add them (owner only): docs/byo-neon.md.");
        Console.WriteLine($"Cloud sync: sent {r.Pushed} records, complete = {r.Complete}.");
    }
}

File.WriteAllText(credentialsFile, sb.ToString());
Console.WriteLine($"Demo company created: {companyFile}");
Console.WriteLine($"Users and passwords are in: {credentialsFile}");
return 0;
