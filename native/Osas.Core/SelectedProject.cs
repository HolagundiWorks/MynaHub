using System.Text.Json;
using System.Text.Json.Nodes;

namespace Osas.Core;

/// <summary>The project a user locked in the launcher: which company it belongs to, its identity and its project file.</summary>
public sealed record SelectedProject(string CompanyPath, long ProjectId, string Code, string Name, string OspPath, string LockedBy, DateTimeOffset LockedAt);

/// <summary>
/// <c>%LOCALAPPDATA%\O-Studio\selected-project.json</c>: the launcher (or O-Studio) writes the locked project, O-CoreOp and O-Draft start on it.
/// It is a file of its own so that writing the company choice in <c>suite.json</c> never drops it. A selection made for another company reads as none.
/// </summary>
public static class SelectedProjectStore
{
    public static string File => SuiteWire.LocalPath(SuiteWire.SelectedProjectFile);

    public static SelectedProject? Read(string? companyPath, string? file = null)
    {
        if (string.IsNullOrWhiteSpace(companyPath))
            return null;
        file ??= File;
        try
        {
            if (!System.IO.File.Exists(file) || JsonNode.Parse(System.IO.File.ReadAllText(file)) is not JsonObject o)
                return null;
            if (o["format"]?.GetValue<string>() != SuiteWire.SelectedProjectFormat || o["project"] is not JsonObject p)
                return null;
            var company = o["company"]?.GetValue<string>() ?? "";
            if (!string.Equals(Path.GetFullPath(company), Path.GetFullPath(companyPath), StringComparison.OrdinalIgnoreCase))
                return null;
            var osp = p["osp"]?.GetValue<string>() ?? "";
            if (osp.Length == 0 || p["id"] is null)
                return null;
            return new SelectedProject(
                company,
                p["id"]!.GetValue<long>(),
                p["code"]?.GetValue<string>() ?? "",
                p["name"]?.GetValue<string>() ?? "",
                osp,
                o["locked_by"]?.GetValue<string>() ?? "",
                DateTimeOffset.TryParse(o["locked_utc"]?.GetValue<string>(), out var at) ? at : default);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or FormatException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public static void Lock(SelectedProject project, string? file = null)
    {
        file ??= File;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var o = new JsonObject
        {
            ["format"] = SuiteWire.SelectedProjectFormat,
            ["version"] = 1,
            ["company"] = project.CompanyPath,
            ["project"] = new JsonObject
            {
                ["id"] = project.ProjectId,
                ["code"] = project.Code,
                ["name"] = project.Name,
                ["osp"] = project.OspPath,
            },
            ["locked_by"] = project.LockedBy,
            ["locked_utc"] = project.LockedAt.UtcDateTime.ToString("O"),
        };
        var tmp = file + ".tmp";
        System.IO.File.WriteAllText(tmp, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        System.IO.File.Move(tmp, file, overwrite: true);
    }

    public static void Clear(string? file = null)
    {
        try
        {
            System.IO.File.Delete(file ?? File);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The environment that points an app at the locked project of <paramref name="companyPath"/>; empty when none is locked.</summary>
    public static IReadOnlyDictionary<string, string> EnvironmentFor(string companyPath, string? file = null)
    {
        var env = new Dictionary<string, string>();
        if (Read(companyPath, file) is { } p)
        {
            env[SuiteWire.EnvProjectId] = p.ProjectId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            env[SuiteWire.EnvProjectOsp] = p.OspPath;
        }

        return env;
    }
}
