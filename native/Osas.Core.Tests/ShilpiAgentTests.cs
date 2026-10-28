using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Osas.Core.Suite;
using Xunit;

namespace Osas.Core.Tests;

public class ShilpiAgentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "osas-shilpi-" + Guid.NewGuid().ToString("N")[..8]);

    public ShilpiAgentTests() => Directory.CreateDirectory(_dir);

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

    private SuiteSettings Settings() => new(Path.Combine(_dir, "suite.json"));

    [Fact]
    public void Data_file_sits_beside_the_company_file()
    {
        Assert.Equal(Path.Combine("D:" + Path.DirectorySeparatorChar + "Firm", "Acme.shilpi.vdb"), ShilpiLink.DataFileFor(Path.Combine("D:" + Path.DirectorySeparatorChar + "Firm", "Acme.adms")));
    }

    [Fact]
    public void Settings_default_and_override()
    {
        var s = Settings();
        var link = new ShilpiLink(s);
        Assert.Equal(7420, link.Port);
        Assert.Equal(7421, link.HttpPort);
        s.Set("shilpi_port", "9000");
        s.Set("shilpi_http_port", "not a number");
        Assert.Equal(9000, link.Port);
        Assert.Equal(7421, link.HttpPort);
    }

    [Fact]
    public async Task Look_reports_down_when_nothing_listens()
    {
        var free = new TcpListener(IPAddress.Loopback, 0);
        free.Start();
        var port = ((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();
        var s = Settings();
        s.Set("shilpi_port", port.ToString());
        s.Set("shilpi_http_port", (port + 1).ToString());
        var status = await new ShilpiLink(s).Look(TimeSpan.FromMilliseconds(400));
        Assert.False(status.ServerUp);
        Assert.False(status.GatewayUp);
        Assert.Null(status.Records);
    }

    [Fact]
    public async Task Look_reads_the_record_count_from_the_gateway()
    {
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        var http = new HttpListener();
        int httpPort;
        while (true)
        {
            httpPort = Random.Shared.Next(20000, 60000);
            http.Prefixes.Clear();
            http.Prefixes.Add($"http://127.0.0.1:{httpPort}/");
            try
            {
                http.Start();
                break;
            }
            catch (HttpListenerException)
            {
            }
        }
        var serve = Task.Run(async () =>
        {
            var ctx = await http.GetContextAsync();
            var body = System.Text.Encoding.UTF8.GetBytes("{\"count\":42,\"cellSize\":8.5}");
            await ctx.Response.OutputStream.WriteAsync(body);
            ctx.Response.Close();
        });
        var s = Settings();
        s.Set("shilpi_port", port.ToString());
        s.Set("shilpi_http_port", httpPort.ToString());
        var status = await new ShilpiLink(s).Look(TimeSpan.FromSeconds(3));
        await serve;
        http.Stop();
        tcp.Stop();
        Assert.True(status.ServerUp);
        Assert.True(status.GatewayUp);
        Assert.Equal(42, status.Records);
        Assert.Equal(8.5, status.CellSize);
    }

    [Fact]
    public void A_remote_host_is_never_started_by_osas()
    {
        var s = Settings();
        s.Set("shilpi_host", "10.0.0.5");
        var why = new ShilpiLink(s).Start(Path.Combine(_dir, "x.vdb"));
        Assert.NotNull(why);
        Assert.False(ShilpiLink.IsLocal("10.0.0.5"));
    }

    [Fact]
    public void Client_config_names_the_company_and_user_but_holds_no_password()
    {
        var text = AgentLink.ClientConfig("office-mcp.exe", "company.adms", "accounts");
        var server = JsonNode.Parse(text)!["mcpServers"]!["office"]!;
        Assert.Equal("office-mcp.exe", (string)server["command"]!);
        Assert.Equal(new[] { "--company", "company.adms", "--user", "accounts" }, server["args"]!.AsArray().Select(a => (string)a!).ToArray());
        Assert.Contains(AgentLink.PasswordPlaceholder, text);
    }

    [Fact]
    public void Every_agent_tool_is_read_only_by_name()
    {
        Assert.All(AgentLink.Tools, t => Assert.DoesNotMatch(@"\.(create|update|delete|save|set|send)", t.Name));
    }
}
