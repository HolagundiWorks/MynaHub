using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Osas.Core.Agent;

/// <summary>Why the assistant could not answer. The message is meant to be shown to the person and never contains the key.</summary>
public sealed class AssistantException(string message) : Exception(message);

/// <summary>The one call the assistant makes to the model: a request in, the model's reply out.</summary>
public interface IMessagesApi
{
    Task<JsonObject> CreateAsync(JsonObject request, CancellationToken cancel);
}

/// <summary>The Anthropic Messages API, over HTTPS. Only the question and the tool results are sent; nothing else from the computer.</summary>
public sealed class AnthropicApi : IMessagesApi, IDisposable
{
    public const string Endpoint = "https://api.anthropic.com/v1/messages";
    public const string Version = "2023-06-01";

    private readonly HttpClient _http;
    private readonly string _key;

    public AnthropicApi(string key, HttpMessageHandler? handler = null)
    {
        _key = key;
        _http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(120);
    }

    public async Task<JsonObject> CreateAsync(JsonObject request, CancellationToken cancel)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(request.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        };
        message.Headers.Add("x-api-key", _key);
        message.Headers.Add("anthropic-version", Version);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, cancel);
        }
        catch (HttpRequestException ex)
        {
            throw new AssistantException("The assistant could not be reached. Check the internet connection. " + Scrub(ex.Message));
        }
        catch (TaskCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new AssistantException("The assistant took too long to answer. Try again.");
        }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancel);
            JsonObject? body = null;
            try
            {
                body = JsonNode.Parse(text) as JsonObject;
            }
            catch (System.Text.Json.JsonException)
            {
            }
            if (response.IsSuccessStatusCode)
                return body ?? throw new AssistantException("The assistant's answer could not be read.");
            var detail = body?["error"]?["message"]?.GetValue<string>();
            throw new AssistantException(response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => "The API key was not accepted. Save a valid key in the assistant settings.",
                System.Net.HttpStatusCode.TooManyRequests => "The assistant is being asked too much just now, or the account has no credit left. Try again shortly.",
                System.Net.HttpStatusCode.NotFound => "The chosen model is not available to this account. Choose another model.",
                _ => $"The assistant could not answer ({(int)response.StatusCode}). {Scrub(detail ?? "")}".Trim(),
            });
        }
    }

    /// <summary>Takes the key out of any text before it is shown.</summary>
    private string Scrub(string text) => _key.Length == 0 ? text : text.Replace(_key, "[key]");

    public void Dispose() => _http.Dispose();
}
