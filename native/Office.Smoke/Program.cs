using Office.Interop;

// Drives the office C interface the way a native app such as MynaOps would: create a company, sign in, add the app's own tables,
// then work with the shared clients, projects and library.
var root = Path.Combine(Path.GetTempPath(), "office-smoke-" + Environment.ProcessId);
Directory.CreateDirectory(root);
try
{
    if (OfficeSession.ApiVersion != 1)
        throw new InvalidOperationException("Unexpected office ABI version.");
    using var session = new OfficeSession(root);
    var created = session.CreateCompany(Path.Combine(root, "Studio"), "Studio", "owner", "correct horse battery", "correct horse battery");
    if (created.Recovery.Length < 10)
        throw new InvalidOperationException("The company should be created with a recovery code.");
    try { session.Call("client_list", new { include_archived = false }); throw new InvalidOperationException("Operations should need a sign-in."); }
    catch (OfficeException ex) when (ex.Status == 1) { }
    session.Login("owner", "correct horse battery");

    var client = session.Call("client_save", new { client = new { id = (long?)null, name = "Acme", contact_person = "", email = "", phone = "", address = "", pan = "", gstin = "" } }).GetInt64();
    session.Call("project_save", new { project = new { id = (long?)null, client_id = client, code = "", name = "Villa", site_address = "Baner", status = "active" } });
    var projects = session.Call("project_list", new { client_id = (long?)null, include_archived = false });
    if (projects.GetArrayLength() != 1 || projects[0].GetProperty("client_name").GetString() != "Acme")
        throw new InvalidOperationException("The project should be listed with its client.");

    const string schedules = "CREATE TABLE aqc_schedules (id INTEGER PRIMARY KEY, project_id INTEGER REFERENCES projects(id), title TEXT NOT NULL);";
    if (session.ApplyTrack("aqc", [schedules]) != 1 || session.ApplyTrack("aqc", [schedules]) != 1)
        throw new InvalidOperationException("The app's own tables should apply once.");
    try { session.ApplyTrack("aad", ["ALTER TABLE clients ADD COLUMN note TEXT;"]); throw new InvalidOperationException("Changing a shared table should be refused."); }
    catch (OfficeException ex) when (ex.Status == 1) { }

    session.Call("library_save", new { input = new { id = (long?)null, kind = "lesson", title = "Waterproofing", category = "", reference = "", body = "Test tanks first" } });
    if (session.Call("library_list", new { kind = (string?)null, search = "tanks" }).GetArrayLength() != 1)
        throw new InvalidOperationException("The library search should find the entry.");
    session.Logout();
    session.CloseCompany();
    Console.WriteLine("Office native smoke test passed.");
}
finally
{
    try { Directory.Delete(root, true); } catch (IOException) { }
}
