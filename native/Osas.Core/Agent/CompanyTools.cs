using System.Text.Json.Nodes;
using Osas.Core.Suite;

namespace Osas.Core.Agent;

/// <summary>The assistant's tools, run against the company the person is signed in to here, with their own role.</summary>
public sealed class CompanyTools(SuiteCompany company) : IToolHost
{
    private IReadOnlyList<ToolSpec>? _tools;

    public IReadOnlyList<ToolSpec> Tools() => _tools ??= company.AssistantTools();

    public string Call(string name, JsonObject arguments) => company.AssistantCall(name, arguments);
}
