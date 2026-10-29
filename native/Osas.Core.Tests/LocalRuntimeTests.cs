using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Osas.Core.Agent;
using Xunit;

namespace Osas.Core.Tests;

public class LocalRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "osas-runtime-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private LocalRuntime Package(byte[] weights, string? sha = null, string file = "model.gguf")
    {
        var runtime = Path.Combine(_root, "runtime", "windows-x64");
        var models = Path.Combine(_root, "models", "tulu");
        Directory.CreateDirectory(runtime);
        Directory.CreateDirectory(models);
        File.WriteAllText(Path.Combine(runtime, LocalRuntime.ServerName), "not a real program");
        File.WriteAllBytes(Path.Combine(models, "model.gguf"), weights);
        var hash = sha ?? Convert.ToHexString(SHA256.HashData(weights)).ToLowerInvariant();
        File.WriteAllText(Path.Combine(models, "manifest.json"), $"{{\"name\":\"Tulu 3 8B\",\"file\":\"{file}\",\"sha256\":\"{hash}\",\"license\":\"Llama 3.1 Community License\",\"context\":8192}}");
        return LocalRuntime.Locate(_root, Path.Combine(_root, "none"))!;
    }

    [Fact]
    public void The_package_is_found_beside_the_program_and_nothing_is_found_when_it_is_missing()
    {
        Assert.Null(LocalRuntime.Locate(_root, Path.Combine(_root, "none")));
        Assert.NotNull(Package([1, 2, 3]));
    }

    [Fact]
    public async Task A_model_that_matches_its_checksum_is_accepted_and_a_changed_one_is_refused()
    {
        var runtime = Package([1, 2, 3, 4]);
        var (path, problem) = await runtime.VerifyAsync();
        Assert.Null(problem);
        Assert.EndsWith("model.gguf", path);
        File.WriteAllBytes(path!, [9, 9, 9, 9, 9]);
        var (again, why) = await runtime.VerifyAsync();
        Assert.Null(again);
        Assert.Contains("checksum", why);
    }

    [Fact]
    public async Task A_missing_model_file_gives_a_clear_error()
    {
        var runtime = Package([1]);
        File.Delete(Path.Combine(_root, "models", "tulu", "model.gguf"));
        var (path, problem) = await runtime.VerifyAsync();
        Assert.Null(path);
        Assert.Contains("missing", problem);
    }

    [Theory]
    [InlineData("..\\evil.gguf")]
    [InlineData("C:\\elsewhere\\model.gguf")]
    public async Task A_manifest_cannot_point_outside_its_folder(string file)
    {
        var runtime = Package([1], file: file.Replace("\\", "\\\\"));
        var (path, problem) = await runtime.VerifyAsync();
        Assert.Null(path);
        Assert.Contains("outside", problem);
    }

    [Fact]
    public void The_server_is_started_on_the_loopback_address_only_with_one_request_at_a_time()
    {
        var args = Package([1]).Arguments("m.gguf", 39321);
        Assert.Equal("127.0.0.1", args[args.ToList().IndexOf("--host") + 1]);
        Assert.Equal("39321", args[args.ToList().IndexOf("--port") + 1]);
        Assert.Equal("1", args[args.ToList().IndexOf("--parallel") + 1]);
        Assert.Equal("8192", args[args.ToList().IndexOf("-c") + 1]);
    }

    private sealed class Capture(string body) : HttpMessageHandler
    {
        public string Sent { get; private set; } = "";
        public Uri? Uri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Uri = request.RequestUri;
            Sent = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancel);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task The_server_call_uses_the_profile_and_a_json_reply_naming_a_tool_becomes_a_tool_use()
    {
        var handler = new Capture("{\"choices\":[{\"message\":{\"content\":\"{\\\"tool\\\":\\\"clients_search\\\",\\\"arguments\\\":{\\\"query\\\":\\\"Rao\\\"}}\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":30,\"completion_tokens\":9}}");
        var api = new LlamaServerApi(39321, new TuluProfile(), handler);
        var reply = await api.CreateAsync(new JsonObject
        {
            ["model"] = "tulu",
            ["system"] = "be brief",
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "find Rao" }),
            ["tools"] = new JsonArray(new JsonObject { ["name"] = "clients_search", ["description"] = "Find clients.", ["input_schema"] = new JsonObject { ["type"] = "object" } }),
        }, default);
        Assert.Equal("http://127.0.0.1:39321/v1/chat/completions", handler.Uri!.ToString());
        var sent = JsonNode.Parse(handler.Sent)!;
        Assert.Equal(0.3, sent["temperature"]!.GetValue<double>());
        Assert.Equal(0.9, sent["top_p"]!.GetValue<double>());
        Assert.Equal(768, sent["max_tokens"]!.GetValue<int>());
        Assert.Contains("clients_search", sent["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Equal("tool_use", reply["stop_reason"]!.GetValue<string>());
        Assert.Equal("Rao", reply["content"]![0]!["input"]!["query"]!.GetValue<string>());
        Assert.Equal(30, reply["usage"]!["input_tokens"]!.GetValue<int>());
    }
}

/// <summary>Runs the real server and model. Set TULU_DIR to a folder holding runtime\windows-x64 and models\tulu; otherwise these do nothing.</summary>
public class LocalRuntimeLiveTests
{
    private static LocalRuntime? Live()
    {
        var folder = Environment.GetEnvironmentVariable("TULU_DIR");
        return string.IsNullOrEmpty(folder) ? null : LocalRuntime.Locate(folder, folder);
    }

    private sealed class NoTools : IToolHost
    {
        public IReadOnlyList<ToolSpec> Tools() => [];

        public string Call(string name, System.Text.Json.Nodes.JsonObject arguments) => throw new AssistantException("No tools here.");
    }

    [Fact]
    public async Task The_real_model_starts_answers_and_stops_leaving_no_process_behind()
    {
        using var runtime = Live();
        if (runtime == null)
            return;
        var (path, problem) = await runtime.VerifyAsync();
        Assert.True(path != null, problem);
        var api = await runtime.StartAsync();
        Assert.True(runtime.Running);
        var assistant = new Assistant(api, new NoTools(), "tulu", "Studio One", "owner", "Owner");
        var answer = await assistant.AskAsync("Reply with the single word: ready");
        Assert.False(string.IsNullOrWhiteSpace(answer.Text));
        runtime.Stop();
        Assert.False(runtime.Running);
        await Task.Delay(500);
        Assert.Empty(System.Diagnostics.Process.GetProcessesByName("llama-server"));
    }

    private sealed class OneTool : IToolHost
    {
        public List<string> Calls { get; } = new();

        public IReadOnlyList<ToolSpec> Tools() =>
        [
            new("clients_search", "Find clients by a word in their name. Leave the query empty to list them all.", new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "object",
                ["properties"] = new System.Text.Json.Nodes.JsonObject { ["query"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "string" } },
                ["required"] = new System.Text.Json.Nodes.JsonArray(),
            }),
        ];

        public string Call(string name, System.Text.Json.Nodes.JsonObject arguments)
        {
            Calls.Add(name);
            return "[{\"name\":\"Rao Constructions\",\"city\":\"Mysuru\"},{\"name\":\"Iyer Family Trust\",\"city\":\"Bengaluru\"}]";
        }
    }

    [Fact]
    public async Task The_real_model_asks_for_a_tool_reads_its_result_and_answers_from_it()
    {
        using var runtime = Live();
        if (runtime == null)
            return;
        var tools = new OneTool();
        var assistant = new Assistant(await runtime.StartAsync(), tools, "tulu", "Studio One", "owner", "Owner");
        var answer = await assistant.AskAsync("Which city is the client Rao Constructions in?");
        runtime.Stop();
        Assert.NotEmpty(tools.Calls);
        Assert.Contains("Mysuru", answer.Text);
    }
}
