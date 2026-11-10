using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Adms.Interop;
using Microsoft.Data.Sqlite;
using Osas.Core;

// Seeds TestCo-<date> for the suite test program (docs/testing). Secrets go only to docs/testing/.local/credentials.txt.

var wireOnly = args.Contains("--wire-only", StringComparer.OrdinalIgnoreCase);
var positional = args.Where(a => !a.StartsWith("-", StringComparison.Ordinal)).ToArray();
var date = DateTime.UtcNow.ToString("yyyy-MM-dd");
var testRoot = positional.Length > 0
    ? positional[0]
    : Path.Combine(@"D:\Work Development\TestRuns", date);
Directory.CreateDirectory(testRoot);
CopyDocumentsDemoAssets(testRoot);

var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var credsPath = Path.Combine(repoRoot, "docs", "testing", ".local", "credentials.txt");
Directory.CreateDirectory(Path.GetDirectoryName(credsPath)!);

var engineDir = Path.Combine(repoRoot, "native", "Adms.LoginWidget", "bin", "Release", "net8.0-windows10.0.19041.0", "win-x64");
if (!File.Exists(Path.Combine(engineDir, "architect_office_desktop_lib.dll")))
    engineDir = AppContext.BaseDirectory;

NativeLibrary.Load(Path.Combine(engineDir, "architect_office_desktop_lib.dll"));
Osas.Core.Suite.Engine.LookIn(engineDir, "architect_office_desktop_lib.dll");

if (wireOnly)
{
    var existingCompany = Directory.EnumerateFiles(testRoot, "TestCo-*.osas").OrderByDescending(f => f).FirstOrDefault()
        ?? throw new InvalidOperationException($"No TestCo-*.osas under {testRoot}");
    const string wireOwner = "testowner";
    const string wirePassword = "TestSuite2026Nov!";
    long wireProjectId;
    using (var session = new AdmsSession(engineDir))
    {
        session.OpenCompany(existingCompany);
        session.Login(wireOwner, wirePassword);
        wireProjectId = session.Call("project_list", new { client_id = (long?)null, include_archived = false })
            .EnumerateArray()
            .First(p => p.GetProperty("code").GetString() == "TC-01")
            .GetProperty("id")
            .GetInt64();
        WireDocumentsProjectIntoWorkspace(session, wireProjectId, testRoot, existingCompany);
        session.CloseCompany();
    }

    AppendWorkspacePathsToCredentials(credsPath, testRoot, wireProjectId, existingCompany);
    Console.WriteLine("Documents project wired to TC-01.");
    Console.WriteLine($"  Company: {existingCompany}");
    Console.WriteLine($"  Credentials: {credsPath}");
    return;
}

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
    WireDocumentsProjectIntoWorkspace(session, projectId, testRoot, companyFile);

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
var demoDwg = Path.Combine(testRoot, "cad", "DEMO.dwg");
var docsOsp = Path.Combine(testRoot, "from-documents", "drawing.osp");
if (File.Exists(demoDwg))
    creds.AppendLine($"DemoDwg={demoDwg}");
if (File.Exists(docsOsp))
    creds.AppendLine($"DocumentsDrawingOsp={docsOsp}");
var docsProject = Path.Combine(testRoot, "from-documents", "projects", "001-1", "project.osp");
if (File.Exists(docsProject))
    creds.AppendLine($"DocumentsProjectOsp={docsProject}");
var wsRoot = Path.Combine(testRoot, "projects", "tc-01-1");
if (Directory.Exists(wsRoot))
    creds.AppendLine($"Tc01Workspace={wsRoot}");
File.WriteAllText(credsPath, creds.ToString());

Console.WriteLine("Test company seeded.");
Console.WriteLine($"  File: {companyFile}");
Console.WriteLine($"  Credentials: {credsPath}");
Console.WriteLine($"  suite.json points at test company (backup: {suiteBackup})");

static void CopyDocumentsDemoAssets(string testRoot)
{
    var oneDriveDocs = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "OneDrive",
        "Documents");
    var localDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    var documentsRoot = Directory.Exists(oneDriveDocs) ? oneDriveDocs : localDocs;
    if (!Directory.Exists(documentsRoot))
        return;

    var cadDir = Path.Combine(testRoot, "cad");
    Directory.CreateDirectory(cadDir);
    var osasTest = Path.Combine(documentsRoot, "OSAS TEST");
    foreach (var name in new[] { "DEMO.dwg", "D1.dwg" })
    {
        var src = Path.Combine(osasTest, name);
        if (File.Exists(src))
            File.Copy(src, Path.Combine(cadDir, name), true);
    }

    var fromDocs = Path.Combine(testRoot, "from-documents");
    Directory.CreateDirectory(fromDocs);
    var drawingOsp = Path.Combine(documentsRoot, "drawing.osp");
    if (File.Exists(drawingOsp))
        File.Copy(drawingOsp, Path.Combine(fromDocs, "drawing.osp"), true);

    var srcProjectDir = Path.Combine(documentsRoot, "projects", "001-1");
    var destProjectDir = Path.Combine(fromDocs, "projects", "001-1");
    if (Directory.Exists(srcProjectDir))
    {
        Directory.CreateDirectory(destProjectDir);
        foreach (var file in Directory.EnumerateFiles(srcProjectDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(srcProjectDir, file);
            var dest = Path.Combine(destProjectDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, true);
        }
    }

    var demoDwg = Path.Combine(cadDir, "DEMO.dwg");
    var tcDraft = Path.Combine(testRoot, "projects", "tc-01-1", "draft");
    if (File.Exists(demoDwg) && Directory.Exists(tcDraft))
        File.Copy(demoDwg, Path.Combine(tcDraft, "DEMO.dwg"), true);
}

static string? DocumentsProjectOsp(string testRoot)
{
    var from001 = Path.Combine(testRoot, "from-documents", "projects", "001-1", "project.osp");
    if (File.Exists(from001))
        return from001;
    var drawing = Path.Combine(testRoot, "from-documents", "drawing.osp");
    return File.Exists(drawing) ? drawing : null;
}

static void WireDocumentsProjectIntoWorkspace(AdmsSession session, long projectId, string testRoot, string companyFile)
{
    var docsOsp = DocumentsProjectOsp(testRoot);
    if (docsOsp is null)
        return;

    var root = TryEngineWorkspaceWire(session, projectId, docsOsp)
        ?? FileWireDocumentsProject(testRoot, projectId, docsOsp, companyFile);

    var demoDwg = Path.Combine(testRoot, "cad", "DEMO.dwg");
    if (File.Exists(demoDwg))
    {
        var draftDir = Path.Combine(root, "draft");
        Directory.CreateDirectory(draftDir);
        File.Copy(demoDwg, Path.Combine(draftDir, "DEMO.dwg"), true);
        EnsureManifestMatchesCompany(companyFile, projectId, root);
    }
    else
    {
        EnsureManifestMatchesCompany(companyFile, projectId, root);
    }

    SyncDraftProjectJson(root);
}

static string? TryEngineWorkspaceWire(AdmsSession session, long projectId, string docsOsp)
{
    try
    {
        session.Call("project_workspace_ensure", new { project_id = projectId });
        session.Call("project_workspace_attach_osp", new { project_id = projectId, osp_path = Path.GetFullPath(docsOsp) });
        var ws = session.Call("project_workspace_get", new { project_id = projectId });
        if (ws.ValueKind == JsonValueKind.Object && ws.TryGetProperty("root", out var rootEl))
            return rootEl.GetString();
    }
    catch (AdmsException)
    {
        // Older engine builds may not expose workspace ops; fall back to on-disk wiring.
    }

    return null;
}

static string FileWireDocumentsProject(string testRoot, long projectId, string docsOsp, string companyFile)
{
    var (code, name) = ReadProjectMeta(companyFile, projectId);
    var slug = Slug(code, name, projectId);
    var root = Path.Combine(testRoot, "projects", slug);
    Directory.CreateDirectory(Path.Combine(root, "draft"));
    Directory.CreateDirectory(Path.Combine(root, "form"));
    File.Copy(docsOsp, Path.Combine(root, "project.osp"), true);
    if (!File.Exists(Path.Combine(root, "form", "model.blend")))
        File.WriteAllBytes(Path.Combine(root, "form", "model.blend"), []);
    if (!File.Exists(Path.Combine(root, "draft", "model.dxf")))
        File.WriteAllText(Path.Combine(root, "draft", "model.dxf"), "0\nSECTION\n2\nHEADER\n0\nENDSEC\n0\nSECTION\n2\nENTITIES\n0\nENDSEC\n0\nEOF\n");

    EnsureManifestMatchesCompany(companyFile, projectId, root);
    SetWorkspacePath(companyFile, projectId, root);
    return root;
}

static (string Code, string Name) ReadProjectMeta(string companyFile, long projectId)
{
    using var conn = OpenCompanyDb(companyFile);
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT code, name FROM projects WHERE id=$id";
    cmd.Parameters.AddWithValue("$id", projectId);
    using var r = cmd.ExecuteReader();
    if (!r.Read())
        return ("TC-01", "Demo Villa");
    return (r.GetString(0), r.GetString(1));
}

static void SetWorkspacePath(string companyFile, long projectId, string root)
{
    using var conn = OpenCompanyDb(companyFile);
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE projects SET workspace_path=$path, updated_at=datetime('now') WHERE id=$id";
    cmd.Parameters.AddWithValue("$path", Path.GetFullPath(root));
    cmd.Parameters.AddWithValue("$id", projectId);
    cmd.ExecuteNonQuery();
}

static void EnsureManifestMatchesCompany(string companyFile, long projectId, string workspaceRoot)
{
    var companyUuid = ReadCompanyUuid(companyFile);
    var manifestPath = Path.Combine(workspaceRoot, "osas.project.json");
    var (code, name) = ReadProjectMeta(companyFile, projectId);
    var drawingId = ReadOrCreateDrawingId(manifestPath);
    var officeId = $"{companyUuid}:{projectId}";
    var manifest = new Dictionary<string, object?>
    {
        ["v"] = 1,
        ["format"] = "osp",
        ["name"] = name,
        ["code"] = code,
        ["project_ref"] = new Dictionary<string, object> { ["v"] = 1, ["company_uuid"] = companyUuid, ["project_id"] = projectId },
        ["drawing_id"] = drawingId,
        ["project_office_id"] = officeId,
        ["files"] = new Dictionary<string, string>
        {
            ["project"] = "project.osp",
            ["draft"] = File.Exists(Path.Combine(workspaceRoot, "draft", "DEMO.dwg")) ? "draft/DEMO.dwg" : "draft/model.dxf",
            ["form"] = "form/model.blend",
            ["odraft_project"] = "draft/project.json",
        },
        ["apps"] = new Dictionary<string, object>
        {
            ["ostudio"] = new Dictionary<string, string> { ["role"] = "owner" },
            ["odraft"] = new Dictionary<string, string>
            {
                ["file"] = "project.osp",
                ["drawing"] = File.Exists(Path.Combine(workspaceRoot, "draft", "DEMO.dwg")) ? "draft/DEMO.dwg" : "draft/model.dxf",
            },
            ["ocoreop"] = new Dictionary<string, string> { ["file"] = "project.osp" },
            ["oform"] = new Dictionary<string, string> { ["file"] = "form/model.blend" },
        },
    };
    File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

    var draftProjectPath = Path.Combine(workspaceRoot, "draft", "project.json");
    if (!File.Exists(draftProjectPath))
    {
        var draftProject = new
        {
            schema_version = 1,
            project = new { id = $"prj-{projectId}", code, name, type = "building", description = "Scaffolded by MynaHub", units = new { length = "mm", area = "m2" } },
            floors = new[] { new { id = "fl-ground", name = "Ground", code = "GF", kind = "floor", sequence = 0, elevation = 0.0, floor_to_floor = 3000.0 } },
            suite = new { osas_project = "../osas.project.json", drawing_id = drawingId, floors_hint = 1, project_ref = new { v = 1, company_uuid = companyUuid, project_id = projectId } },
        };
        File.WriteAllText(draftProjectPath, JsonSerializer.Serialize(draftProject, new JsonSerializerOptions { WriteIndented = true }));
    }
}

static string ReadCompanyUuid(string companyFile)
{
    using var conn = OpenCompanyDb(companyFile);
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT company_uuid FROM company_file LIMIT 1";
    return cmd.ExecuteScalar() as string ?? throw new InvalidOperationException("Company UUID missing.");
}

static string ReadOrCreateDrawingId(string manifestPath)
{
    if (File.Exists(manifestPath))
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (doc.RootElement.TryGetProperty("drawing_id", out var id) && id.GetString() is { Length: > 0 } existing)
            return existing;
    }

    return Guid.NewGuid().ToString();
}

static SqliteConnection OpenCompanyDb(string companyFile)
{
    var conn = new SqliteConnection($"Data Source={companyFile}");
    conn.Open();
    return conn;
}

static string Slug(string code, string name, long projectId)
{
    var raw = string.IsNullOrWhiteSpace(code) ? name : code;
    var outChars = new List<char>();
    foreach (var ch in raw)
    {
        if (char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')
            outChars.Add(char.ToLowerInvariant(ch));
        else if (char.IsWhiteSpace(ch) || ch is '/' or '\\')
        {
            if (outChars.Count > 0 && outChars[^1] != '-')
                outChars.Add('-');
        }

        if (outChars.Count >= 48)
            break;
    }

    while (outChars.Count > 0 && outChars[^1] == '-')
        outChars.RemoveAt(outChars.Count - 1);
    var stem = outChars.Count == 0 ? $"p{projectId}" : new string(outChars.ToArray());
    return $"{stem}-{projectId}";
}

static void SyncDraftProjectJson(string workspaceRoot)
{
    var manifestPath = Path.Combine(workspaceRoot, "osas.project.json");
    var draftProjectPath = Path.Combine(workspaceRoot, "draft", "project.json");
    if (!File.Exists(manifestPath) || !File.Exists(draftProjectPath))
        return;
    using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
    var m = manifest.RootElement;
    using var draftDoc = JsonDocument.Parse(File.ReadAllText(draftProjectPath));
    var draft = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(draftDoc.RootElement.GetRawText())
        ?? new Dictionary<string, JsonElement>();
    var project = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(draft["project"].GetRawText())
        ?? new Dictionary<string, JsonElement>();
    project["code"] = JsonSerializer.SerializeToElement(m.GetProperty("code").GetString());
    project["name"] = JsonSerializer.SerializeToElement(m.GetProperty("name").GetString());
    draft["project"] = JsonSerializer.SerializeToElement(project);
    draft["suite"] = JsonSerializer.SerializeToElement(new
    {
        osas_project = "../osas.project.json",
        drawing_id = m.GetProperty("drawing_id").GetString(),
        floors_hint = 1,
        project_ref = JsonSerializer.Deserialize<JsonElement>(m.GetProperty("project_ref").GetRawText()),
    });
    File.WriteAllText(draftProjectPath, JsonSerializer.Serialize(draft, new JsonSerializerOptions { WriteIndented = true }));
}

static void AppendWorkspacePathsToCredentials(string credsPath, string testRoot, long projectId, string companyFile)
{
    if (!File.Exists(credsPath))
        return;
    var lines = File.ReadAllLines(credsPath)
        .Where(l => !l.StartsWith("Tc01Workspace=", StringComparison.Ordinal)
            && !l.StartsWith("Tc01ProjectId=", StringComparison.Ordinal))
        .ToList();
    var ws = Path.Combine(testRoot, "projects", "tc-01-1");
    if (Directory.Exists(ws))
        lines.Add($"Tc01Workspace={ws}");
    lines.Add($"Tc01ProjectId={projectId}");
    File.WriteAllLines(credsPath, lines);
}
