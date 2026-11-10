using System.Text.Json;
using System.Text.Json.Nodes;
using Osas.Core;

namespace Adms.LoginWidget;

/// <summary>What an app asked the launcher for: the app to start and, when a project file was double-clicked, that file.</summary>
internal sealed record LaunchRequestData(string App, string? Osp);

/// <summary>
/// An app that was started on its own, or a project file that was double-clicked, writes <c>launch-request.json</c> (through
/// <c>O-Studio.Launcher.exe --launch &lt;app&gt;</c> or <c>--open &lt;file.osp&gt; [--app &lt;app&gt;]</c>) and the launcher starts that app once a company is
/// signed in and a project is locked (for a file: the project whose folder holds it). A request older than 15 minutes is ignored.
/// </summary>
internal static class LaunchRequest
{
    static string FilePath => SuiteWire.LocalPath("launch-request.json");

    internal static LaunchRequestData? FromArguments(string[] args)
    {
        string? launch = null, open = null, app = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            var value = args[i + 1];
            if (string.IsNullOrWhiteSpace(value))
                continue;
            if (string.Equals(args[i], "--launch", StringComparison.OrdinalIgnoreCase))
                launch = value;
            else if (string.Equals(args[i], "--open", StringComparison.OrdinalIgnoreCase))
                open = value;
            else if (string.Equals(args[i], "--app", StringComparison.OrdinalIgnoreCase))
                app = value;
        }

        if (open != null)
            return new LaunchRequestData(app ?? "MynaCAD", Path.GetFullPath(open));
        return launch != null ? new LaunchRequestData(launch, null) : null;
    }

    internal static void Write(LaunchRequestData request)
    {
        try
        {
            Directory.CreateDirectory(SuiteWire.LocalDirectory);
            var o = new JsonObject { ["app"] = request.App, ["requested_utc"] = DateTime.UtcNow.ToString("O") };
            if (request.Osp != null)
                o["osp"] = request.Osp;
            File.WriteAllText(FilePath, o.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal static LaunchRequestData? Read()
    {
        try
        {
            if (!File.Exists(FilePath) || JsonNode.Parse(File.ReadAllText(FilePath)) is not JsonObject o)
                return null;
            if (!DateTime.TryParse(o["requested_utc"]?.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var at)
                || DateTime.UtcNow - at.ToUniversalTime() > TimeSpan.FromMinutes(15))
                return null;
            return o["app"]?.GetValue<string>() is { Length: > 0 } app ? new LaunchRequestData(app, o["osp"]?.GetValue<string>()) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or FormatException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static void Clear()
    {
        try
        {
            File.Delete(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
