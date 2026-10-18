using Adms.Interop;

var root = Path.Combine(Path.GetTempPath(), "adms-smoke-" + Environment.ProcessId);
Directory.CreateDirectory(root);
try
{
    if (AdmsSession.ApiVersion != 1)
        throw new InvalidOperationException($"Expected ABI version 1, got {AdmsSession.ApiVersion}.");

    using (var session = new AdmsSession(root))
    {
        var created = session.CreateCompany(
            Path.Combine(root, "Studio.adms"),
            "वास्तु कार्यालय",
            "owner",
            "correct horse battery",
            "correct horse battery");
        if (created.Summary.Name != "वास्तु कार्यालय" || created.Recovery.Length < 8)
            throw new InvalidOperationException("Company creation did not return the company name and recovery code.");

        var signedIn = session.Login("owner", "correct horse battery");
        if (signedIn.Username != "owner")
            throw new InvalidOperationException("Login did not return the owner.");

        var profile = session.GetProfile();
        if (profile.Name != "वास्तु कार्यालय")
            throw new InvalidOperationException("Company profile did not round-trip as UTF-8.");

        // The project path: the engine's next step must follow a project from set-up to billing through the real bridge.
        var clientId = session.Call("client_save", new
        {
            client = new { id = (long?)null, name = "Acme", contact_person = "", email = "", phone = "", address = "Pune", pan = "", gstin = "" },
        }).GetInt64();
        var projectId = session.Call("project_save", new
        {
            project = new { id = (long?)null, client_id = clientId, code = "P-1", name = "Villa", site_address = "Baner", status = "active" },
        }).GetInt64();
        var path = session.Call("project_path", new { project_id = projectId });
        if (path.GetProperty("next").GetProperty("kind").GetString() != "assign_stages")
            throw new InvalidOperationException("A new project should point at assigning stages first.");
        session.Call("project_stages_apply", new { project_id = projectId, consultancy = "comprehensive", floors = 1 });
        var afterStages = session.Call("project_path", new { project_id = projectId });
        if (afterStages.GetProperty("next").GetProperty("kind").GetString() != "new_agreement")
            throw new InvalidOperationException("With stages assigned the next step should be the agreement.");
        var attention = session.Call("project_attention");
        if (attention.GetArrayLength() != 1 || attention[0].GetProperty("action").GetProperty("kind").GetString() != "new_agreement")
            throw new InvalidOperationException("Pulse should list the project with its next action.");

        var pdf = session.SamplePdf();
        if (pdf.Length < 5 || pdf[0] != (byte)'%' || pdf[1] != (byte)'P')
            throw new InvalidOperationException("The engine did not return a PDF.");

        try
        {
            session.OpenCompany(created.Summary.Path);
            throw new InvalidOperationException("A second open of the same company should have been refused.");
        }
        catch (AdmsException ex) when (ex.Message.Contains("Close the current company", StringComparison.Ordinal))
        {
        }

        session.CloseCompany();
    }

    using (var again = new AdmsSession(root))
    {
        var summary = again.OpenCompany(Path.Combine(root, "Studio.adms"));
        if (summary.Name != "वास्तु कार्यालय")
            throw new InvalidOperationException("The company file stayed locked after the session was released.");
        again.CloseCompany();
    }

    Console.WriteLine("ADMS native smoke test passed.");
}
finally
{
    try
    {
        Directory.Delete(root, recursive: true);
    }
    catch (IOException)
    {
    }
}
