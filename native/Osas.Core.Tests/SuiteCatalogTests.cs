using System.Text.Json;
using Osas.Core;
using Xunit;

namespace Osas.Core.Tests;

public class SuiteCatalogTests
{
    [Fact]
    public void ReadTicket_extracts_secret_from_payload()
    {
        using var doc = JsonDocument.Parse("""{"ticket":"abc123"}""");
        Assert.Equal("abc123", SuiteCatalog.ReadTicket(doc.RootElement));
    }

    [Fact]
    public void ReadTicket_returns_null_when_missing()
    {
        using var doc = JsonDocument.Parse("{}");
        Assert.Null(SuiteCatalog.ReadTicket(doc.RootElement));
    }

    [Fact]
    public void Apps_include_studio_and_coreop()
    {
        Assert.Contains(SuiteCatalog.Apps, a => a.TicketApp == "O-Studio");
        Assert.Contains(SuiteCatalog.Apps, a => a.TicketApp == "O-CoreOp");
    }

    [Fact]
    public void Draft_is_started_through_its_WinUI_shell_first()
    {
        // BUG-0020: the launcher started o-draft.exe, not the shell the suite installs as ODraft.WinUi.exe.
        var draft = SuiteCatalog.Apps.Single(a => a.TicketApp == "O-Draft");
        Assert.Equal("ODraft.WinUi.exe", draft.ExeNames[0]);
    }

    [Fact]
    public void The_environment_of_an_app_start_names_the_locked_project_when_there_is_one()
    {
        var company = Path.Combine(Path.GetTempPath(), "osas-envproj-" + Guid.NewGuid().ToString("N")[..8], "Co.osas");
        var file = Path.Combine(Path.GetDirectoryName(company)!, "selected-project.json");
        try
        {
            Assert.Empty(SelectedProjectStore.EnvironmentFor(company, file));
            SelectedProjectStore.Lock(new SelectedProject(company, 5, "P5", "Five", @"C:\p\project.osp", "a", DateTimeOffset.UtcNow), file);
            Assert.Equal("5", SelectedProjectStore.EnvironmentFor(company, file)[SuiteWire.EnvProjectId]);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(company)!, true); } catch (IOException) { }
        }
    }
}
