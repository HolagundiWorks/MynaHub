using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Osas.Core.Agent;

/// <summary>Describes the model package: which file, its checksum, its licence and the context length it was prepared for.</summary>
public sealed record ModelManifest(string Name, string File, string Sha256, string License, int ContextTokens)
{
    public static ModelManifest Read(string path)
    {
        var o = JsonNode.Parse(System.IO.File.ReadAllText(path)) as JsonObject ?? throw new AssistantException("The model manifest could not be read.");
        string Text(string key) => o[key]?.GetValue<string>() ?? "";
        var file = Text("file");
        if (file.Length == 0 || file.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(file))
            throw new AssistantException("The model manifest names a file outside the model folder.");
        return new ModelManifest(Text("name"), file, Text("sha256").ToLowerInvariant(), Text("license"), o["context"]?.GetValue<int>() ?? 8192);
    }
}

/// <summary>The first-try settings for Tulu 3 8B. They are starting values to be measured, not tuned results.</summary>
public sealed record TuluProfile(double Temperature = 0.3, double TopP = 0.9, int MaxNewTokens = 768, int ContextTokens = 8192);

/// <summary>
/// O-Studio's own local inference component. It finds a bundled llama.cpp server and the Tulu model package, checks the model file
/// against its checksum, starts the server on a free loopback port the first time it is needed, waits until it answers, and stops it
/// when O-Studio closes or the person unloads the assistant. It depends on no other suite app and makes no network call outside this
/// computer.
/// </summary>
public sealed class LocalRuntime : IDisposable
{
    public const string ServerName = "llama-server.exe";

    private readonly string _server;
    private readonly string _modelFolder;
    private readonly TuluProfile _profile;
    private readonly object _gate = new();
    private Process? _process;
    private int _port;

    public LocalRuntime(string server, string modelFolder, TuluProfile? profile = null)
    {
        _server = server;
        _modelFolder = modelFolder;
        _profile = profile ?? new TuluProfile();
    }

    /// <summary>
    /// Looks for the package beside the program (<c>runtime\windows-x64\llama-server.exe</c> and <c>models\tulu</c>), then under the
    /// person's own data folder. Returns null when no runtime and model are both found.
    /// </summary>
    public static LocalRuntime? Locate(string programFolder, string dataFolder, TuluProfile? profile = null)
    {
        foreach (var root in new[] { programFolder, dataFolder })
        {
            var folder = Path.Combine(root, "models", "tulu");
            foreach (var server in new[] { Path.Combine(root, "runtime", "windows-x64", ServerName), Path.Combine(root, "runtime", ServerName) })
                if (File.Exists(server) && File.Exists(Path.Combine(folder, "manifest.json")))
                    return new LocalRuntime(server, folder, profile);
        }
        return null;
    }

    public bool Running
    {
        get
        {
            lock (_gate)
                return _process is { HasExited: false };
        }
    }

    /// <summary>The model file named by the manifest, or the reason it cannot be used: missing, or not matching its checksum.</summary>
    public async Task<(string? Path, string? Problem)> VerifyAsync(CancellationToken cancel = default)
    {
        ModelManifest manifest;
        try
        {
            manifest = ModelManifest.Read(Path.Combine(_modelFolder, "manifest.json"));
        }
        catch (Exception ex) when (ex is AssistantException or IOException or JsonException)
        {
            return (null, ex.Message);
        }
        var model = Path.Combine(_modelFolder, manifest.File);
        if (!File.Exists(model))
            return (null, $"The model file {manifest.File} is missing from {_modelFolder}.");
        if (manifest.Sha256.Length == 0)
            return (model, null);
        // Hashing several gigabytes takes a while, so a good result is remembered against the file's size and time.
        var info = new FileInfo(model);
        var stamp = Path.Combine(_modelFolder, ".verified");
        var key = $"{manifest.Sha256} {info.Length} {info.LastWriteTimeUtc.Ticks}";
        try
        {
            if (File.Exists(stamp) && File.ReadAllText(stamp) == key)
                return (model, null);
        }
        catch (IOException)
        {
        }
        string actual;
        await using (var stream = File.OpenRead(model))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancel)).ToLowerInvariant();
        if (actual != manifest.Sha256)
            return (null, "The model file is damaged or is not the expected one (its checksum does not match). Install the model again.");
        try
        {
            File.WriteAllText(stamp, key);
        }
        catch (IOException)
        {
        }
        return (model, null);
    }

    /// <summary>The arguments the server is started with. Loopback only, one request at a time.</summary>
    public IReadOnlyList<string> Arguments(string modelPath, int port) =>
    [
        "-m", modelPath,
        "-c", _profile.ContextTokens.ToString(),
        "--host", "127.0.0.1",
        "--port", port.ToString(),
        "--parallel", "1",
        "--no-webui",
    ];

    /// <summary>Starts the server if it is not running and waits until it reports it is ready.</summary>
    public async Task<IMessagesApi> StartAsync(CancellationToken cancel = default)
    {
        var (model, problem) = await VerifyAsync(cancel);
        if (model == null)
            throw new AssistantException(problem ?? "The model could not be checked.");
        lock (_gate)
        {
            if (_process is { HasExited: false })
                return new LlamaServerApi(_port, _profile);
            _port = FreePort();
            var start = new ProcessStartInfo(_server) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(_server)!, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in Arguments(model, _port))
                start.ArgumentList.Add(argument);
            try
            {
                _process = Process.Start(start) ?? throw new AssistantException("The local model could not be started.");
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new AssistantException("The local model runtime could not be started: " + ex.Message);
            }
            // The server writes a lot of progress text; read it and drop it so it can never block on a full pipe.
            _process.OutputDataReceived += (_, _) => { };
            _process.ErrorDataReceived += (_, _) => { };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            cancel.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_process == null || _process.HasExited)
                    throw new AssistantException("The local model stopped while starting. The computer may not have enough memory for it.");
            }
            try
            {
                using var response = await http.GetAsync($"http://127.0.0.1:{_port}/health", cancel);
                if (response.IsSuccessStatusCode)
                    return new LlamaServerApi(_port, _profile);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancel.IsCancellationRequested)
            {
            }
            await Task.Delay(500, cancel);
        }
        Stop();
        throw new AssistantException("The local model did not become ready in time.");
    }

    /// <summary>Stops the server and releases its memory.</summary>
    public void Stop()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
        }
        if (process == null)
            return;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        process.Dispose();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose() => Stop();
}

/// <summary>
/// The assistant's model call, made to the bundled llama.cpp server on this computer. Tulu 3's own chat template has no tool-calling,
/// so the tools are described in the prompt and the model asks for one with a small JSON reply (see <see cref="OllamaApi.ToChat"/>).
/// </summary>
public sealed class LlamaServerApi : IMessagesApi
{
    private readonly HttpClient _http;
    private readonly string _address;
    private readonly TuluProfile _profile;
    private int _calls;

    public LlamaServerApi(int port, TuluProfile? profile = null, HttpMessageHandler? handler = null)
    {
        _address = $"http://127.0.0.1:{port}";
        _profile = profile ?? new TuluProfile();
        _http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromMinutes(10);
    }

    public async Task<JsonObject> CreateAsync(JsonObject request, CancellationToken cancel)
    {
        var chat = OllamaApi.ToChat(request, promptTools: true);
        var body = new JsonObject
        {
            ["messages"] = chat["messages"]!.DeepClone(),
            ["temperature"] = _profile.Temperature,
            ["top_p"] = _profile.TopP,
            ["max_tokens"] = _profile.MaxNewTokens,
            ["stream"] = false,
        };
        HttpResponseMessage response;
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, _address + "/v1/chat/completions") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            response = await _http.SendAsync(message, cancel);
        }
        catch (HttpRequestException)
        {
            throw new AssistantException("The local model is not running. Ask again to start it.");
        }
        catch (TaskCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new AssistantException("The local model took too long to answer. Try a narrower question.");
        }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancel);
            JsonObject? parsed = null;
            try
            {
                parsed = JsonNode.Parse(text) as JsonObject;
            }
            catch (JsonException)
            {
            }
            if (!response.IsSuccessStatusCode)
                throw new AssistantException($"The local model could not answer ({(int)response.StatusCode}). {parsed?["error"]?["message"]?.GetValue<string>()}".Trim());
            var choice = (parsed?["choices"] as JsonArray)?.FirstOrDefault() as JsonObject ?? throw new AssistantException("The local model's answer could not be read.");
            var content = new JsonArray();
            var said = choice["message"]?["content"]?.GetValue<string>() ?? "";
            var wanted = false;
            if (OllamaApi.AskedForTool(said) is { } asked)
            {
                content.Add(new JsonObject { ["type"] = "tool_use", ["id"] = "call_" + Interlocked.Increment(ref _calls), ["name"] = asked.Name, ["input"] = asked.Arguments });
                wanted = true;
            }
            else if (said.Length > 0)
            {
                content.Add(new JsonObject { ["type"] = "text", ["text"] = said });
            }
            return new JsonObject
            {
                ["content"] = content,
                ["stop_reason"] = wanted ? "tool_use" : choice["finish_reason"]?.GetValue<string>() == "length" ? "max_tokens" : "end_turn",
                ["usage"] = new JsonObject { ["input_tokens"] = parsed?["usage"]?["prompt_tokens"]?.GetValue<int>() ?? 0, ["output_tokens"] = parsed?["usage"]?["completion_tokens"]?.GetValue<int>() ?? 0 },
            };
        }
    }
}
