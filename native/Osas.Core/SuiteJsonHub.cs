using System.Text.Json;

namespace Osas.Core;

/// <summary>
/// Shared suite.json read/write for Kivo and the company login widget. Never stores passwords or tickets.
/// </summary>
public static class SuiteJsonHub
{
    public static string SuiteFile => SuiteWire.LocalPath("suite.json");

    public static string? CompanyPath()
    {
        try
        {
            if (!File.Exists(SuiteFile))
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(SuiteFile));
            return doc.RootElement.TryGetProperty("company_path", out var p)
                && p.GetString() is { Length: > 0 } path
                && File.Exists(path)
                ? path
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string? RememberedUsername()
    {
        try
        {
            if (!File.Exists(SuiteFile))
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(SuiteFile));
            return doc.RootElement.TryGetProperty("username", out var u) && u.GetString() is { Length: > 0 } name
                ? name
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void RememberCompany(string path, string? username = null, string chosenBy = "O-Studio")
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            Directory.CreateDirectory(SuiteWire.LocalDirectory);
            var priorUser = username;
            if (string.IsNullOrWhiteSpace(priorUser))
                priorUser = RememberedUsername();
            var payload = JsonSerializer.Serialize(new
            {
                company_path = path,
                username = string.IsNullOrWhiteSpace(priorUser) ? null : priorUser.Trim(),
                chosen_by = chosenBy,
                chosen_at = DateTimeOffset.UtcNow.ToString("O"),
            });
            File.WriteAllText(SuiteFile, payload);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
