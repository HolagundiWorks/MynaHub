using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Osas.Core;

namespace Osas.Core.Agent;

/// <summary>
/// Calls the suite AI Core (<c>POST /v1/chat</c> on loopback) where the launcher hosts Olmo 3 7B. Tool calling uses the same JSON-in-chat
/// pattern as <see cref="LlamaServerApi"/>.
/// </summary>
public sealed class OlmoAiCoreApi : IMessagesApi
{
    readonly HttpClient _http;
    readonly string _address;
    int _calls;

    public OlmoAiCoreApi(int port = 8742, HttpMessageHandler? handler = null)
    {
        _address = $"http://127.0.0.1:{port}";
        _http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromMinutes(10);
        var token = ReadToken();
        if (token.Length > 0)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    static string ReadToken()
    {
        var env = Environment.GetEnvironmentVariable(SuiteWire.EnvAiToken)?.Trim();
        if (!string.IsNullOrEmpty(env)) return env;
        var path = SuiteWire.LocalPath(Path.Combine("ai", "auth.token"));
        try
        {
            if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } t) return t;
        }
        catch { }
        return "";
    }

    public static async Task<(bool ready, string? problem)> VerifyAsync(int port = 8742, CancellationToken cancel = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var token = ReadToken();
            if (token.Length > 0)
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.GetAsync($"http://127.0.0.1:{port}/v1/health", cancel).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return (false, "The assistant token on this computer does not match the host. Close every suite app and restart the launcher.");
            if (!response.IsSuccessStatusCode)
                return (false, "The assistant host is not reachable. Start the suite launcher (it hosts the assistant), then try again.");
            var text = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("status", out var st) && st.GetString() == "not_installed")
                return (false, "Olmo 3 7B or the llama.cpp runtime is not installed on this computer (see docs/LOCAL-MODEL.md in MynaOps).");
            return (true, null);
        }
        catch (HttpRequestException)
        {
            return (false, "Start the suite launcher — the assistant model runs there on this computer.");
        }
    }

    public async Task<JsonObject> CreateAsync(JsonObject request, CancellationToken cancel)
    {
        var messages = PromptChat.ToMessages(request);
        int maxTokens = request["max_tokens"]?.GetValue<int>() is int m and > 0 ? Math.Clamp(m, 16, 2048) : 512;
        var body = new JsonObject
        {
            ["messages"] = messages,
            ["max_tokens"] = maxTokens,
        };
        HttpResponseMessage response;
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, _address + "/v1/chat")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            response = await _http.SendAsync(message, cancel).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            throw new AssistantException("The assistant host is not running. Start the suite launcher, then ask again.");
        }
        catch (TaskCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new AssistantException("The assistant took too long to answer. Try a narrower question.");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
            JsonObject? parsed = null;
            try { parsed = JsonNode.Parse(text) as JsonObject; }
            catch (JsonException) { }
            if (!response.IsSuccessStatusCode)
            {
                var msg = parsed?["error"]?["message"]?.GetValue<string>() ?? parsed?["error"]?.GetValue<string>();
                throw new AssistantException($"The assistant could not answer ({(int)response.StatusCode}). {msg}".Trim());
            }

            var said = parsed?["message"]?["content"]?.GetValue<string>() ?? "";
            var content = new JsonArray();
            var wanted = false;
            if (PromptChat.AskedForTool(said) is { } asked)
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
                ["stop_reason"] = wanted ? "tool_use" : "end_turn",
                ["usage"] = new JsonObject { ["input_tokens"] = 0, ["output_tokens"] = 0 },
            };
        }
    }
}
