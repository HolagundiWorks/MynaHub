using System.Text.Json;
using Osas.Core;
using Xunit;

namespace Osas.Core.Tests;

public class SuiteCatalogTests
{
    [Fact]
    public void Apps_include_studio_and_coreop()
    {
        Assert.Contains(SuiteCatalog.Apps, a => a.TicketApp == "O-Studio");
        Assert.Contains(SuiteCatalog.Apps, a => a.TicketApp == "O-CoreOp");
    }

    [Fact]
    public void Draft_is_started_through_its_WinUI_shell_first()
    {
        // BUG-0020: o-draft.exe was started, not the shell the suite installs as ODraft.WinUi.exe.
        var draft = SuiteCatalog.Apps.Single(a => a.TicketApp == "O-Draft");
        Assert.Equal("ODraft.WinUi.exe", draft.ExeNames[0]);
    }

    [Fact]
    public void The_removed_Myna_launcher_is_not_an_app_of_the_suite()
    {
        // 2026-11-11: the launcher (O-Studio.Launcher.exe) was removed; the catalogue lists the four apps only.
        Assert.Equal(new[] { "MynaHub", "MynaOps", "MynaCAD", "MynaMod" }, SuiteCatalog.Apps.Select(a => a.Title).ToArray());
        Assert.DoesNotContain(SuiteCatalog.Apps.SelectMany(a => a.ExeNames), e => e.Contains("Launcher", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Starting_an_app_passes_no_AI_or_sign_in_environment()
    {
        // MynaOps and MynaCAD find the AI agent themselves (MynaOps hosts it); MynaHub passes nothing.
        var names = typeof(SuiteWire).GetFields().Select(f => f.Name).ToArray();
        Assert.DoesNotContain(names, n => n.StartsWith("EnvAi", StringComparison.Ordinal));
        Assert.Null(typeof(SuiteCatalog).GetMethod("ApplyServiceEnvironment"));
    }
}
