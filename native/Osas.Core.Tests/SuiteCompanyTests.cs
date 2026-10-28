using System.Text.Json.Nodes;
using Osas.Core.Suite;
using Xunit;

namespace Osas.Core.Tests;

/// <summary>
/// These talk to the real office engine. They need office.dll (build office-ffi in the O-Studio repository in release mode) and the
/// environment variable OFFICE_DLL_DIR set to its folder. Without it they do nothing, and one test says so when the variable is set but wrong.
/// </summary>
public class SuiteCompanyTests : IDisposable
{
    private const string Password = "correct horse battery";
    private static readonly bool Available = Locate();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "osas-suite-" + Guid.NewGuid().ToString("N")[..8]);

    public SuiteCompanyTests() => Directory.CreateDirectory(_dir);

    private static bool Locate()
    {
        var folder = Environment.GetEnvironmentVariable("OFFICE_DLL_DIR");
        if (!string.IsNullOrEmpty(folder))
            Engine.LookIn(folder);
        return Engine.IsAvailable();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private string NewCompany()
    {
        using var engine = Engine.Start(Path.Combine(_dir, "setup"));
        engine.Call("company.create", new JsonObject { ["path"] = Path.Combine(_dir, "Studio"), ["name"] = "Studio One", ["username"] = "owner", ["password"] = Password, ["confirm"] = Password });
        engine.Call("company.close");
        return Path.Combine(_dir, "Studio.osas");
    }

    [Fact]
    public void The_engine_is_found_when_its_folder_is_given()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OFFICE_DLL_DIR")))
            Assert.True(Available, "office.dll was not found in OFFICE_DLL_DIR");
    }

    [Fact]
    public void The_company_details_are_read_and_edited_through_the_shared_engine()
    {
        if (!Available) return;
        using var company = SuiteCompany.Open(Path.Combine(_dir, "osas"), NewCompany(), "owner", Password);
        Assert.Equal(("Studio One", "owner"), (company.Name, company.User));
        var details = company.Details();
        Assert.Equal("Studio One", details.Name);
        company.Save(details with { Name = "Studio One LLP", Gstin = "29ABCDE1234F1Z5", AccentHex = "3e5240", GstScheme = "regular" });
        var again = company.Details();
        Assert.Equal(("Studio One LLP", "29ABCDE1234F1Z5", "#3E5240"), (again.Name, again.Gstin, again.AccentHex));
        Assert.Equal("Studio One LLP", company.Name);
    }

    [Fact]
    public void An_app_is_signed_in_as_the_same_person_by_a_ticket_that_works_once_and_no_password_is_passed()
    {
        if (!Available) return;
        var path = NewCompany();
        using var company = SuiteCompany.Open(Path.Combine(_dir, "osas"), path, "owner", Password);
        var ticket = company.TicketFor("O-CoreOp");
        Assert.Equal(("owner", 64), (ticket.Username, ticket.Ticket.Length));
        Assert.DoesNotContain(Password, ticket.Ticket);
        using var app = Engine.Start(Path.Combine(_dir, "app"));
        app.Call("company.open", new JsonObject { ["path"] = path, ["secret"] = "", ["unlock"] = "" });
        var who = app.Call("auth.login_ticket", new JsonObject { ["ticket"] = ticket.Ticket });
        Assert.Equal("owner", who!["username"]!.GetValue<string>());
        Assert.NotNull(app.Call("company_profile_get"));
        var second = Assert.Throws<EngineException>(() => app.Call("auth.login_ticket", new JsonObject { ["ticket"] = ticket.Ticket }));
        Assert.Contains("expired", second.Message);
    }

    [Fact]
    public void Users_are_added_changed_and_given_new_passwords_and_each_change_needs_the_signed_in_persons_password()
    {
        if (!Available) return;
        var path = NewCompany();
        using var company = SuiteCompany.Open(Path.Combine(_dir, "osas"), path, "owner", Password);
        Assert.Single(company.Users());
        var refused = Assert.Throws<EngineException>(() => company.CreateUser("ravi", "a long enough password", "accounts", "not my password"));
        Assert.Contains("not accepted", refused.Message);
        var id = company.CreateUser("ravi", "a long enough password", "accounts", Password);
        company.UpdateUser(id, "principal", false, Password);
        Assert.Contains(company.Users(), u => u.Username == "ravi" && u.Role == "principal" && u.RoleLabel == "Principal" && !u.Disabled);
        company.SetPassword(id, "another long password", Password);
        company.UpdateUser(id, "principal", true, Password);
        Assert.Contains(company.Users(), u => u.Username == "ravi" && u.Disabled);
        // the last owner cannot be removed, and the person set up with a new password can sign in once allowed again
        var owner = company.Users().Single(u => u.Username == "owner");
        Assert.Contains("at least one active owner", Assert.Throws<EngineException>(() => company.UpdateUser(owner.Id, "owner", true, Password)).Message);
        company.UpdateUser(id, "principal", false, Password);
        using var ravi = SuiteCompany.Open(Path.Combine(_dir, "ravi"), path, "ravi", "another long password");
        Assert.Equal("principal", ravi.Role);
        Assert.Throws<EngineException>(() => ravi.Users());
    }

    [Fact]
    public void A_new_company_is_made_in_OSAS_with_its_owner_and_a_recovery_code_and_opens_signed_in()
    {
        if (!Available) return;
        var path = Path.Combine(_dir, "Fresh.osas");
        var (company, recovery) = SuiteCompany.Create(Path.Combine(_dir, "osas"), path, "Fresh Studio", "meera", Password, Password);
        using (company)
        {
            Assert.Equal(("Fresh Studio", "meera", "owner"), (company.Name, company.User, company.Role));
            Assert.True(File.Exists(path));
            Assert.False(string.IsNullOrWhiteSpace(recovery));
            Assert.Equal("Fresh Studio", company.Details().Name);
        }
        var refused = Assert.Throws<EngineException>(() => SuiteCompany.Create(Path.Combine(_dir, "osas2"), Path.Combine(_dir, "Other.osas"), "Other", "meera", Password, "a different password!"));
        Assert.False(string.IsNullOrWhiteSpace(refused.Message));
        Assert.False(File.Exists(Path.Combine(_dir, "Other.osas")), "nothing is left behind when the passwords differ");
    }

    [Fact]
    public void A_wrong_password_is_refused_and_the_choice_is_remembered_without_any_secret()
    {
        if (!Available) return;
        var path = NewCompany();
        Assert.Throws<EngineException>(() => SuiteCompany.Open(Path.Combine(_dir, "osas"), path, "owner", "not the password at all"));
        var settings = new SuiteSettings(Path.Combine(_dir, "settings", "suite.json"));
        Assert.Null(settings.CompanyPath);
        settings.CompanyPath = path;
        Assert.Equal(path, new SuiteSettings(Path.Combine(_dir, "settings", "suite.json")).CompanyPath);
        var text = File.ReadAllText(Path.Combine(_dir, "settings", "suite.json"));
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ticket", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_environment_an_app_is_started_with_names_the_company_and_the_ticket()
    {
        var env = SuiteSettings.AppEnvironment(@"C:\Files\Studio.osas", new AppTicket("abc", "owner"));
        Assert.Equal((@"C:\Files\Studio.osas", "abc", "owner"), (env["OSAS_COMPANY"], env["OSAS_TICKET"], env["OSAS_USER"]));
        var bare = SuiteSettings.AppEnvironment(@"C:\Files\Studio.osas", null);
        Assert.Single(bare);
    }
}
