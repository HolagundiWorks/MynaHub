using Osas.Core.Suite;
using Xunit;

namespace Osas.Core.Tests;

public class ServiceStatusTests
{
    [Theory]
    [InlineData("ready (Olmo 3 7B on port 8742)", ServiceLevel.Running)]
    [InlineData("starting (the launcher hosts it)", ServiceLevel.Waiting)]
    [InlineData("checking…", ServiceLevel.Waiting)]
    [InlineData("not running (port 8742 is taken by another program)", ServiceLevel.Stopped)]
    [InlineData("model or runtime not installed", ServiceLevel.Stopped)]
    [InlineData("token mismatch: close the suite apps and restart the launcher", ServiceLevel.Stopped)]
    public void The_AI_agent_line_maps_to_a_level(string text, ServiceLevel expected) =>
        Assert.Equal(expected, ServiceStatus.ForAi(text));

    [Theory]
    [InlineData("running (2 projects syncing)", ServiceLevel.Running)]
    [InlineData("running, no project is switched on (Kivo > Cloud sync)", ServiceLevel.Running)]
    [InlineData("could not connect (14:40:23 Postgres: Failed to connect); retrying every 30 s", ServiceLevel.Stopped)]
    [InlineData("error: boom", ServiceLevel.Stopped)]
    [InlineData("credentials found; starts at sign-in", ServiceLevel.Waiting)]
    [InlineData("stopped (signed out); starts again at the next sign-in", ServiceLevel.Off)]
    [InlineData("not set up (add neon.env, see docs/CLOUD-NEON.md)", ServiceLevel.Off)]
    [InlineData("neon.env has no database address", ServiceLevel.Off)]
    public void The_database_server_line_maps_to_a_level(string text, ServiceLevel expected) =>
        Assert.Equal(expected, ServiceStatus.ForCloud(text));

    [Theory]
    [InlineData("TestCo.osas (868 KB), open, signed in as testowner", ServiceLevel.Running)]
    [InlineData("TestCo.osas (868 KB), open, sign-in needed", ServiceLevel.Waiting)]
    [InlineData("TestCo.osas (868 KB), open, session locked", ServiceLevel.Waiting)]
    [InlineData("TestCo.osas (868 KB), closed", ServiceLevel.Off)]
    [InlineData("no company file chosen", ServiceLevel.Off)]
    [InlineData("TestCo.osas not found", ServiceLevel.Stopped)]
    public void The_company_file_line_maps_to_a_level(string text, ServiceLevel expected) =>
        Assert.Equal(expected, ServiceStatus.ForCompanyFile(text));

    [Fact]
    public void Every_level_has_a_word() =>
        Assert.All(Enum.GetValues<ServiceLevel>(), level => Assert.False(string.IsNullOrWhiteSpace(ServiceStatus.Word(level))));
}
