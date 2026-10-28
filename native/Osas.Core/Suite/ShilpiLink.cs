using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Osas.Core.Suite;

/// <summary>What one look at the ShilpiDB server and its web gateway found.</summary>
public sealed record ShilpiStatus(string Host, int Port, int HttpPort, bool ServerUp, bool GatewayUp, long? Records, double? CellSize, string? Problem);

/// <summary>
/// ShilpiDB is the suite's spatial store: drawing geometry as boxes with a payload, searched by place. Project business data stays in the
/// company file; ShilpiDB records point at it by ProjectRef. This class looks at a running server and starts or stops the one OSAS started.
/// It never reads record contents; only the count is asked for.
/// </summary>
public sealed class ShilpiLink
{
    public const string DefaultHost = "127.0.0.1";
    public const int DefaultPort = 7420;
    public const int DefaultHttpPort = 7421;

    private readonly SuiteSettings _settings;
    private Process? _server;
    private Process? _gateway;

    public ShilpiLink(SuiteSettings settings) => _settings = settings;

    public string Host => _settings.Get("shilpi_host") is { Length: > 0 } h ? h : DefaultHost;

    public int Port => int.TryParse(_settings.Get("shilpi_port"), out var p) && p is > 0 and < 65536 ? p : DefaultPort;

    public int HttpPort => int.TryParse(_settings.Get("shilpi_http_port"), out var p) && p is > 0 and < 65536 ? p : DefaultHttpPort;

    public bool StartedHere => _server is { HasExited: false };

    /// <summary>A server is only started on this computer's own address, so ShilpiDB is never opened to the network by OSAS.</summary>
    public static bool IsLocal(string host) => host is "127.0.0.1" or "localhost" or "::1";

    /// <summary>Where the spatial records for a company are kept: beside the company file, named after it.</summary>
    public static string DataFileFor(string companyPath) =>
        Path.Combine(Path.GetDirectoryName(companyPath) ?? ".", Path.GetFileNameWithoutExtension(companyPath) + ".shilpi.vdb");

    public string? FindProgram(string name)
    {
        var folders = new List<string?>
        {
            _settings.Get("shilpi_dir"),
            AppContext.BaseDirectory,
            Path.Combine(AppContext.BaseDirectory, "shilpidb"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "shilpidb"),
        };
        folders.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        return folders.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => Path.Combine(f!, name + ".exe")).FirstOrDefault(File.Exists);
    }

    public async Task<ShilpiStatus> Look(TimeSpan? wait = null, CancellationToken cancel = default)
    {
        var timeout = wait ?? TimeSpan.FromSeconds(1.5);
        var server = await CanConnect(Host, Port, timeout);
        long? records = null;
        double? cell = null;
        string? problem = null;
        var gateway = false;
        try
        {
            using var http = new HttpClient { Timeout = timeout };
            var text = await http.GetStringAsync($"http://{Host}:{HttpPort}/stats", cancel);
            gateway = true;
            if (JsonNode.Parse(text) is JsonObject o)
            {
                records = o["count"]?.GetValue<long>();
                cell = o["cellSize"]?.GetValue<double>();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            if (server)
                problem = "The server is running but the web gateway (shilpi-http) is not answering, so the record count is not shown.";
        }
        return new ShilpiStatus(Host, Port, HttpPort, server, gateway, records, cell, problem);
    }

    private static async Task<bool> CanConnect(string host, int port, TimeSpan timeout)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(timeout);
            await client.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException)
        {
            return false;
        }
    }

    /// <summary>Starts shilpid (and the gateway) on this computer for the given data file. Returns why not, or null when started.</summary>
    public string? Start(string dataFile)
    {
        if (!IsLocal(Host))
            return "OSAS only starts ShilpiDB on this computer. The host is set to another address, so it is only looked at, not started.";
        if (StartedHere)
            return null;
        var server = FindProgram("shilpid");
        if (server == null)
            return "shilpid.exe was not found. Install ShilpiDB, or set its folder in the OSAS settings (shilpi_dir).";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dataFile)!);
            _server = Process.Start(Hidden(server, $"--bind {Host}:{Port} --data \"{dataFile}\""));
            if (FindProgram("shilpi-http") is { } gateway)
                _gateway = Process.Start(Hidden(gateway, $"--bind {Host}:{HttpPort} --backend {Host}:{Port}"));
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return $"ShilpiDB could not be started: {ex.Message}";
        }
    }

    /// <summary>Stops only the programs this window started. A server someone else started is left alone.</summary>
    public void Stop()
    {
        foreach (var p in new[] { _gateway, _server })
        {
            try
            {
                if (p is { HasExited: false })
                {
                    p.Kill();
                    p.WaitForExit(3000);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            p?.Dispose();
        }
        _server = _gateway = null;
    }

    private static ProcessStartInfo Hidden(string exe, string args) =>
        new(exe, args) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe)! };
}
