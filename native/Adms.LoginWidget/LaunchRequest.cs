using System.Text.Json;
using System.Text.Json.Nodes;
using Osas.Core;

namespace Adms.LoginWidget;

/// <summary>
/// An app that was started on its own writes <c>launch-request.json</c> (through <c>O-Studio.Launcher.exe --launch &lt;app&gt;</c>) and leaves; the launcher
/// starts that app once a company is signed in and a project is locked. A request older than 15 minutes is ignored.
/// </summary>
internal static class LaunchRequest
{
    static string FilePath => SuiteWire.LocalPath("launch-request.json");

    internal static string? FromArguments(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--launch", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(args[i + 1]))
                return args[i + 1];
        }

        return null;
    }

    internal static void Write(string app)
    {
        try
        {
            Directory.CreateDirectory(SuiteWire.LocalDirectory);
            File.WriteAllText(FilePath, new JsonObject { ["app"] = app, ["requested_utc"] = DateTime.UtcNow.ToString("O") }.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal static string? Read()
    {
        try
        {
            if (!File.Exists(FilePath) || JsonNode.Parse(File.ReadAllText(FilePath)) is not JsonObject o)
                return null;
            if (!DateTime.TryParse(o["requested_utc"]?.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var at)
                || DateTime.UtcNow - at.ToUniversalTime() > TimeSpan.FromMinutes(15))
                return null;
            return o["app"]?.GetValue<string>();
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
