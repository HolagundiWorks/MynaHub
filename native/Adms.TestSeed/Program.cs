using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Adms.Interop;
using Osas.Core;

// Seeds TestCo-<date> for the suite test program (docs/testing). Secrets go only to docs/testing/.local/credentials.txt.

var date = DateTime.UtcNow.ToString("yyyy-MM-dd");
var testRoot = args.Length > 0
    ? args[0]
    : Path.Combine(@"D:\Work Development\TestRuns", date);
Directory.CreateDirectory(testRoot);

var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var credsPath = Path.Combine(repoRoot, "docs", "testing", ".local", "credentials.txt");
Directory.CreateDirectory(Path.GetDirectoryName(credsPath)!);

var engineDir = Path.Combine(repoRoot, "native", "Adms.LoginWidget", "bin", "Release", "net8.0-windows10.0.19041.0", "win-x64");
if (!File.Exists(Path.Combine(engineDir, "architect_office_desktop_lib.dll")))
    engineDir = AppContext.BaseDirectory;

NativeLibrary.Load(Path.Combine(engineDir, "architect_office_desktop_lib.dll"));
Osas.Core.Suite.Engine.LookIn(engineDir, "architect_office_desktop_lib.dll");

var companyFile = Path.Combine(testRoot, $"TestCo-{date}.osas");
if (File.Exists(companyFile))
    File.Delete(companyFile);

const string owner = "testowner";
const string ownerPassword = "TestSuite2026Nov!";
const string rolePassword = "TestRole2026Nov!";

string recovery;
using (var session = new AdmsSession(engineDir))
{
    var created = session.CreateCompany(companyFile, "TestCo Demo Practice", owner, ownerPassword, ownerPassword);
    recovery = created.Recovery;
    if (string.IsNullOrWhiteSpace(recovery))
        throw new InvalidOperationException("Company create did not return a recovery code.");

    session.Login(owner, ownerPassword);

    session.Call("company_save_profile", new
    {
        profile = new
        {
            name = "TestCo Demo Practice",
            address = "12 Sample Road, Pune, Maharashtra 411001",
            email = "studio@testco.demo",
            phone = "+91 20 0000 0000",
            pan = "AABCT1234C",
            gstin = "27AABCT1234C1Z5",
            coa_registration = "COA-TEST-001",
            bank_details = "Test Bank, A/c 0000000000, IFSC TEST0000001",
            gst_scheme = "regular",
            upi_id = "testco@upi",
            architect_name = "Demo Architect",
            letterhead_style = "standard",
            accent_hex = "3e5240",
        },
    });

    session.Call("user_create", new { username = "testprincipal", password = rolePassword, role = "principal", admin_password = ownerPassword });
    session.Call("user_create", new { username = "testaccounts", password = rolePassword, role = "accounts", admin_password = ownerPassword });
    session.Call("user_create", new { username = "testeditor", password = rolePassword, role = "document_editor", admin_password = ownerPassword });
    session.Call("user_create", new { username = "testauditor", password = rolePassword, role = "auditor", admin_password = ownerPassword });

    var clientId = session.Call("client_save", new
    {
        client = new { id = (long?)null, name = "TestCo Client", contact_person = "Patil", email = "client@testco.demo", phone = "", address = "Baner", pan = "", gstin = "" },
    }).GetInt64();
    var projectId = session.Call("project_save", new
    {
        project = new { id = (long?)null, client_id = clientId, code = "TC-01", name = "Demo Villa", site_address = "Baner", status = "active" },
    }).GetInt64();
    session.Call("project_stages_apply", new { project_id = projectId, consultancy = "comprehensive", floors = 1 });

    var ticket = session.Call("auth.ticket", new { app = "O-CoreOp" });
    if (SuiteCatalog.ReadTicket(ticket) is not { Length: > 0 })
        throw new InvalidOperationException("Ticket issue failed after seed.");

    session.CloseCompany();
}

var smallOspSource = Path.Combine(repoRoot, "..", "O-COREOP", "BBSDesktop", "Office.Shared.Tests", "Fixtures", "small.osp");
var smallOspDest = Path.Combine(testRoot, "Demo-Villa.osp");
if (File.Exists(smallOspSource))
    File.Copy(smallOspSource, smallOspDest, true);

var suiteBackup = Path.Combine(testRoot, "suite.json.backup");
var suiteLive = SuiteJsonHub.SuiteFile;
if (File.Exists(suiteLive))
    File.Copy(suiteLive, suiteBackup, true);

SuiteJsonHub.RememberCompany(companyFile, owner, "Adms.TestSeed");

var creds = new StringBuilder();
creds.AppendLine($"# Test suite credentials — {date} — do not commit");
creds.AppendLine($"CompanyFile={companyFile}");
creds.AppendLine($"Owner={owner}");
creds.AppendLine($"OwnerPassword={ownerPassword}");
creds.AppendLine($"RecoveryCode={recovery}");
creds.AppendLine($"RolePassword={rolePassword}");
creds.AppendLine("Users=testprincipal, testaccounts, testeditor, testauditor");
creds.AppendLine("Project=TC-01 Demo Villa");
creds.AppendLine($"SmallOsp={smallOspDest}");
File.WriteAllText(credsPath, creds.ToString());

Console.WriteLine("Test company seeded.");
Console.WriteLine($"  File: {companyFile}");
Console.WriteLine($"  Credentials: {credsPath}");
Console.WriteLine($"  suite.json points at test company (backup: {suiteBackup})");
