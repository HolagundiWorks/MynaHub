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
            Path.Combine(root, "Studio.osas"),
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
        var activity = session.Call("client_activity", new { client_id = clientId });
        if (activity.GetProperty("name").GetString() != "Acme" || activity.GetProperty("invoices").GetArrayLength() != 0 || !activity.GetProperty("finance").GetBoolean())
            throw new InvalidOperationException("The client's records should be readable and empty for a new client.");
        var siteId = session.Call("site_record_save", new { input = new { id = (long?)null, project_id = projectId, kind = "snag", record_date = "2026-01-10", title = "Crack", details = "", location = "", assigned_to = "", due_date = "", status = "open" } }).GetInt64();
        if (session.Call("site_record_list", new { project_id = projectId, kind = (string?)null }).GetArrayLength() != 1 || siteId <= 0)
            throw new InvalidOperationException("The site record should be listed.");
        var enquiryId = session.Call("enquiry_save", new { input = new { id = (long?)null, received_date = "2026-01-10", name = "Rao", contact = "", source = "", brief = "", status = "new", follow_up = "", notes = "" } }).GetInt64();
        session.Call("enquiry_convert", new { id = enquiryId });
        if (session.Call("permit_list", new { project_id = projectId }).GetArrayLength() != 0)
            throw new InvalidOperationException("No permits were added.");
        var staffId = session.Call("staff_save", new { input = new { id = (long?)null, name = "Meera", role = "Architect", specialisation = "", phone = "", email = "", joined_date = "2024-01-01", left_date = "" } }).GetInt64();
        session.Call("leave_save", new { input = new { id = (long?)null, staff_id = staffId, kind = "casual", from_date = "2026-05-04", to_date = "2026-05-06", status = "approved", reason = "" } });
        if (session.Call("leave_list", new { }).GetArrayLength() != 1 || session.Call("staff_list", new { }).GetArrayLength() != 1)
            throw new InvalidOperationException("The roster and leave should be listed.");
        session.Call("library_save", new { input = new { id = (long?)null, kind = "lesson", title = "Waterproofing", category = "", reference = "", body = "Test tanks first" } });
        if (session.Call("library_list", new { kind = (string?)null, search = "tanks" }).GetArrayLength() != 1)
            throw new InvalidOperationException("The library search should find the entry.");

        var proposalId = session.Call("proposal_save", new { input = new { id = (long?)null, client_id = clientId, project_id = (long?)null, title = "Villa design", basis = "percent", cost_paise = 500000000L, rate_bp = 600L, area_milli = 0L, area_rate_paise = 0L, lump_paise = 0L, gst_bp = 1800L, issued_date = "2026-10-01", valid_until = "2026-10-31", notes = "" } }).GetInt64();
        session.Call("proposal_set_status", new { id = proposalId, status = "sent" });
        if (session.Call("proposal_list", new { client_id = (long?)null })[0].GetProperty("fee_paise").GetInt64() != 30000000L)
            throw new InvalidOperationException("A 6 percent fee on 50,00,000.00 should be 3,00,000.00.");
        session.Call("meeting_save", new { input = new { id = (long?)null, meeting_date = "2026-10-02", title = "Site visit", client_id = clientId, project_id = (long?)null, attendees = "", notes = "", decisions = "Move the stair", follow_up_date = "", follow_up_done = false } });
        if (session.Call("meeting_list", new { project_id = (long?)null }).GetArrayLength() != 1)
            throw new InvalidOperationException("The meeting should be listed.");
        var crew = session.Call("staff_save", new { input = new { id = (long?)null, name = "Ravi", role = "", specialisation = "", phone = "", email = "", joined_date = "2024-01-01", left_date = "" } }).GetInt64();
        session.Call("attendance_mark", new { staff_id = crew, day = "2026-10-02", status = "half", note = (string?)null });
        if (session.Call("attendance_month", new { month = "2026-10" }).EnumerateArray().First(r => r.GetProperty("name").GetString() == "Ravi").GetProperty("worked_half_days").GetInt64() != 1)
            throw new InvalidOperationException("One half day should count as one half-day.");
        var imported = session.Call("tds26as_import", new { csv = "Deductor,TAN,Date,Amount,Tax Deducted" + (char)10 + "Acme,PUNA12345B,2026-06-10,100000,10000" + (char)10 });
        if (imported.GetProperty("added").GetInt64() != 1 || session.Call("tds26as_reconcile", new { from = "2026-04-01", to = "2027-03-31" }).GetProperty("only_reported").GetArrayLength() != 1)
            throw new InvalidOperationException("The 26AS entry should be imported and reported as not booked.");

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

        var taxPeriod = new { from = "2026-04-01", to = "2027-03-31" };
        var taxAbstract = session.Call("report_tax_abstract", taxPeriod);
        if (taxAbstract.GetProperty("from").GetString() != "2026-04-01" || taxAbstract.GetProperty("tds_by_clients_total").GetProperty("deducted").GetInt64() != 0)
            throw new InvalidOperationException("The tax abstract should read an empty period.");
        var taxCsv = session.Call("report_tax_abstract_csv", taxPeriod).GetString() ?? "";
        if (!taxCsv.StartsWith("Tax abstract", StringComparison.Ordinal) || !taxCsv.Contains("Tax payments", StringComparison.Ordinal))
            throw new InvalidOperationException("The tax abstract CSV is missing its sections.");

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
        var summary = again.OpenCompany(Path.Combine(root, "Studio.osas"));
        if (summary.Name != "वास्तु कार्यालय")
            throw new InvalidOperationException("The company file stayed locked after the session was released.");
        again.CloseCompany();
    }

    Console.WriteLine("Kivo native smoke test passed.");
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
