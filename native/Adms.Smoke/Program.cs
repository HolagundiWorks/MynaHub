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
