using Adms.Cloud;
using Adms.Interop;

// Two company files (A and B, two PCs in effect) sign in as the same cloud account, share one cloud company and converge. PASS/FAIL lines only.
string Need(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : throw new InvalidOperationException($"Set {name}.");

var authUrl = Need("NEON_AUTH_BASE_URL");
var apiUrl = Need("NEON_DATA_API_URL");
var email = Need("MYNA_SYNC_EMAIL");
var password = Need("MYNA_SYNC_PASSWORD");
var failures = 0;
void Check(string name, bool ok)
{
    Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
    if (!ok) failures++;
}

// Desktop to cloud to phone to desktop, in three steps around a run of the Android sync tests (see docs/hub-sync-design.md section 15):
//   e2e1 <dir>  : the owner's company file A sends records to a new cloud company and adds the phone account (MYNA_PHONE_EMAIL) as a member
//   (Android)   : the phone's sync code joins that company, receives them, edits some and sends back
//   e2e3 <dir>  : A syncs again and checks what the phone did
if (args.Length == 2 && args[0] is "e2e1" or "e2e3")
{
    var dir = args[1];
    Directory.CreateDirectory(dir);
    using var authE = new CloudAuthClient(authUrl);
    var sessionE = await authE.SignInAsync(email, password);
    using var apiE = new HubDataApi(apiUrl, ct => authE.GetAccessTokenAsync(sessionE, ct));
    var fileA = Path.Combine(dir, "A", "A.osas");
    using var sA = new AdmsSession(Path.Combine(dir, "A"));
    var eA = new HubSyncEngine(sA.Call, apiE);
    if (args[0] == "e2e1")
    {
        Directory.CreateDirectory(Path.Combine(dir, "A"));
        sA.CreateCompany(fileA, "E2E A", "owner", "correct horse battery", "correct horse battery");
        sA.Login("owner", "correct horse battery");
        var cl = sA.Call("client_save", new { client = new { id = (long?)null, name = "E2E Acme", contact_person = "", email = "", phone = "111", address = "Pune", pan = "", gstin = "" } }).GetInt64();
        var pr = sA.Call("project_save", new { project = new { id = (long?)null, client_id = cl, code = "E2E-1", name = "E2E Tower", site_address = "Baner", status = "active" } }).GetInt64();
        sA.Call("task_save", new { input = new { id = (long?)null, project_id = pr, title = "E2E desktop task", notes = "", status = "todo", position = 1, due_date = "2026-03-01" } });
        sA.Call("task_save", new { input = new { id = (long?)null, project_id = pr, title = "E2E to edit", notes = "", status = "todo", position = 2, due_date = "" } });
        var company = "e2e-" + Guid.NewGuid().ToString("N")[..12];
        await eA.LinkAsync(company, "E2E company");
        var r1 = await eA.RunOnceAsync();
        Console.WriteLine((r1.Pushed >= 4 && r1.Complete ? "PASS " : "FAIL ") + "desktop sent the company to the cloud");
        var phoneEmail = Need("MYNA_PHONE_EMAIL");
        using (var http = new HttpClient { BaseAddress = new Uri(apiUrl.TrimEnd('/') + "/") })
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "rpc/set_member")
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { p_company = company, p_email = phoneEmail, p_role = "member" }), System.Text.Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("Authorization", "Bearer " + await authE.GetAccessTokenAsync(sessionE));
            req.Headers.Add("Content-Profile", "hub");
            var res = await http.SendAsync(req);
            Console.WriteLine((res.IsSuccessStatusCode ? "PASS " : "FAIL ") + "the owner added the phone account as a member");
            if (!res.IsSuccessStatusCode) return 1;
        }
        File.WriteAllText(Path.Combine(dir, "company.txt"), company);
        Console.WriteLine("cloud company written to " + Path.Combine(dir, "company.txt"));
        return 0;
    }
    sA.OpenCompany(fileA);
    sA.Login("owner", "correct horse battery");
    var r3 = await eA.RunOnceAsync();
    var tasks = sA.Call("task_list", new { project_id = (long?)null });
    string St(string title) => tasks.EnumerateArray().FirstOrDefault(t => t.GetProperty("title").GetString() == title).ValueKind == System.Text.Json.JsonValueKind.Undefined ? "(missing)"
        : tasks.EnumerateArray().First(t => t.GetProperty("title").GetString() == title).GetProperty("status").GetString()!;
    var failed = 0;
    void C(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failed++; }
    C("desktop received the phone's pass", r3.Pulled >= 3 && r3.Complete);
    C("a task the phone finished is done on the desktop", St("E2E to edit") == "done");
    C("a task the phone added arrived on the desktop", St("E2E phone task") == "todo");
    C("the phone's client edit arrived on the desktop", sA.Call("client_list", new { include_archived = false }).EnumerateArray().Any(c => c.GetProperty("phone").GetString() == "777"));
    Console.WriteLine($"{failed} failure(s)");
    return failed;
}

var root = Path.Combine(Path.GetTempPath(), "adms-synccheck-" + Environment.ProcessId);
Directory.CreateDirectory(root);
try
{
    using var auth = new CloudAuthClient(authUrl);
    var cloud = await auth.SignInAsync(email, password);
    if (Environment.GetEnvironmentVariable("MYNA_SYNC_DEBUG") == "1")
    {
        // Claim NAMES only, never values.
        var part = (await auth.GetAccessTokenAsync(cloud)).Split('.')[1].Replace('-', '+').Replace('_', '/');
        part = part.PadRight(part.Length + (4 - part.Length % 4) % 4, '=');
        using var claims = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(part));
        Console.WriteLine("token claims: " + string.Join(",", claims.RootElement.EnumerateObject().Select(p => p.Name)));
        Console.WriteLine("session user id present: " + !string.IsNullOrEmpty(cloud.UserId));
    }
    using var api = new HubDataApi(apiUrl, ct => auth.GetAccessTokenAsync(cloud, ct));
    var cloudCompany = "synccheck-" + Guid.NewGuid().ToString("N")[..12];

    (AdmsSession Session, HubSyncEngine Engine) Open(string name)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        var s = new AdmsSession(dir);
        s.CreateCompany(Path.Combine(dir, name + ".osas"), "Check " + name, "owner", "correct horse battery", "correct horse battery");
        s.Login("owner", "correct horse battery");
        return (s, new HubSyncEngine(s.Call, api));
    }

    var (a, ea) = Open("A");
    var (b, eb) = Open("B");
    using (a) using (b)
    {
        var clientId = a.Call("client_save", new { client = new { id = (long?)null, name = "Acme", contact_person = "", email = "", phone = "", address = "Pune", pan = "ABCDE1234F", gstin = "" } }).GetInt64();
        var projectId = a.Call("project_save", new { project = new { id = (long?)null, client_id = clientId, code = "P-1", name = "Villa", site_address = "Baner", status = "active" } }).GetInt64();
        var taskId = a.Call("task_save", new { input = new { id = (long?)null, project_id = projectId, title = "Site visit", notes = "", status = "todo", position = 1, due_date = "" } }).GetInt64();
        var task2 = a.Call("task_save", new { input = new { id = (long?)null, project_id = (long?)null, title = "Pay vendor", notes = "", status = "todo", position = 2, due_date = "" } }).GetInt64();

        await ea.LinkAsync(cloudCompany, "Sync check");
        var ra = await ea.RunOnceAsync();
        Check("A pushed its existing clients, projects and tasks", ra.Pushed >= 4 && ra.Complete);

        await eb.LinkAsync(cloudCompany, "Sync check");
        var rb = await eb.RunOnceAsync();
        Check("B pulled them into an empty company file", rb.Pulled >= 4 && rb.Complete);
        var bClients = b.Call("client_list", new { include_archived = false });
        var bProjects = b.Call("project_list", new { client_id = (long?)null, include_archived = false });
        var bTasks = b.Call("task_list", new { project_id = (long?)null });
        Check("B has the client, the project and both tasks", bClients.GetArrayLength() == 1 && bProjects.GetArrayLength() == 1 && bTasks.GetArrayLength() == 2);
        Check("B's project belongs to B's copy of the client", bProjects[0].GetProperty("client_id").GetInt64() == bClients[0].GetProperty("id").GetInt64());
        Check("the PAN did not leave A", !bClients[0].GetProperty("pan").GetString()!.Contains("ABCDE"));

        // an edit on B reaches A
        var bTask = bTasks.EnumerateArray().First(t => t.GetProperty("title").GetString() == "Site visit");
        b.Call("task_set_status", new { id = bTask.GetProperty("id").GetInt64(), status = "doing", position = (long?)1 });
        await eb.RunOnceAsync();
        await ea.RunOnceAsync();
        var aTask = a.Call("task_list", new { project_id = (long?)null }).EnumerateArray().First(t => t.GetProperty("id").GetInt64() == taskId);
        Check("an edit on B arrives on A", aTask.GetProperty("status").GetString() == "doing");

        // a delete on A reaches B
        a.Call("task_delete", new { id = task2 });
        await ea.RunOnceAsync();
        await eb.RunOnceAsync();
        Check("a delete on A removes the task on B", b.Call("task_list", new { project_id = (long?)null }).GetArrayLength() == 1);

        // both edit the same client before either syncs: one wins, both end identical, nothing is lost silently
        var aClient = a.Call("client_list", new { include_archived = false })[0];
        var bClient = b.Call("client_list", new { include_archived = false })[0];
        a.Call("client_save", new { client = new { id = (long?)aClient.GetProperty("id").GetInt64(), name = "Acme", contact_person = "", email = "", phone = "111", address = "Pune", pan = "", gstin = "" } });
        await Task.Delay(1100);
        b.Call("client_save", new { client = new { id = (long?)bClient.GetProperty("id").GetInt64(), name = "Acme", contact_person = "", email = "", phone = "222", address = "Pune", pan = "", gstin = "" } });
        await eb.RunOnceAsync();
        await ea.RunOnceAsync();
        await eb.RunOnceAsync();
        var phoneA = a.Call("client_list", new { include_archived = false })[0].GetProperty("phone").GetString();
        var phoneB = b.Call("client_list", new { include_archived = false })[0].GetProperty("phone").GetString();
        Check("a conflicting edit settles on the later one on both sides", phoneA == "222" && phoneB == "222");

        var again = await ea.RunOnceAsync();
        Check("a pass with nothing to do pushes nothing", again.Pushed == 0 && again.Complete);
    }
    await auth.SignOutAsync(cloud);
}
finally
{
    try { Directory.Delete(root, true); } catch (IOException) { }
}
Console.WriteLine($"{failures} failure(s)");
return failures;
