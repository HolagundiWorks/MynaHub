using System.Text.Json;
using System.Text.Json.Nodes;

namespace Osas.Core.Suite;

/// <summary>One thing the assistant is allowed to do. None of them changes a record.</summary>
public sealed record AgentTool(string Name, string Purpose);

/// <summary>
/// The suite's AI assistant. It is not a chat window inside OSAS: it is an MCP server (office-mcp) that an assistant such as Claude Desktop
/// or Claude Code starts. It works as one named company user, so it sees only what that user's role allows, and it can only call the fixed
/// read-only tools below. OSAS prepares the connection and never holds a password: the password goes into the assistant's own settings.
/// </summary>
public sealed class AgentLink
{
    public const string PasswordPlaceholder = "<the password of that user>";

    public static readonly IReadOnlyList<AgentTool> Tools =
    [
        new("clients.search", "Find clients by name"),
        new("projects.list", "List projects and their stage"),
        new("projects.brief", "One project's summary"),
        new("library.search", "Search the office library"),
        new("staff.list", "List staff"),
        new("proposals.list", "List proposals"),
        new("meetings.list", "List meetings"),
        new("finance.tds26as_check", "Check TDS against Form 26AS (needs finance access)"),
    ];

    private readonly SuiteSettings _settings;

    public AgentLink(SuiteSettings settings) => _settings = settings;

    public string? FindProgram()
    {
        var folders = new List<string?> { _settings.Get("agent_dir"), AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "agent") };
        folders.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        return folders.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => Path.Combine(f!, "office-mcp.exe")).FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// The block to paste into an assistant's MCP settings. The password is a placeholder on purpose; OSAS never writes a real one.
    /// <paramref name="program"/> is the full path to office-mcp.exe, or just its name when it was not found.
    /// </summary>
    public static string ClientConfig(string program, string companyPath, string user)
    {
        var server = new JsonObject
        {
            ["command"] = program,
            ["args"] = new JsonArray("--company", companyPath, "--user", user),
            ["env"] = new JsonObject { ["OFFICE_MCP_PASSWORD"] = PasswordPlaceholder },
        };
        var root = new JsonObject { ["mcpServers"] = new JsonObject { ["office"] = server } };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}
