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
}
