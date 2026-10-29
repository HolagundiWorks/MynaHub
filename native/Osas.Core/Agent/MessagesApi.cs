using System.Text.Json.Nodes;

namespace Osas.Core.Agent;

/// <summary>Why the assistant could not answer. The message is meant to be shown to the person.</summary>
public sealed class AssistantException(string message) : Exception(message);

/// <summary>The one call the assistant makes to the model: a request in, the model's reply out.</summary>
public interface IMessagesApi
{
    Task<JsonObject> CreateAsync(JsonObject request, CancellationToken cancel);
}
