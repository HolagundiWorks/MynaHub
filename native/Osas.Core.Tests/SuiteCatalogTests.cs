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
        // BUG-0020: the launcher started o-draft.exe, not the shell the suite installs as ODraft.WinUi.exe.
        var draft = SuiteCatalog.Apps.Single(a => a.TicketApp == "O-Draft");
        Assert.Equal("ODraft.WinUi.exe", draft.ExeNames[0]);
    }
}
