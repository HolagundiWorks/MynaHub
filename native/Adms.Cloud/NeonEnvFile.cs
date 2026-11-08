using Osas.Core;

namespace Adms.Cloud;

/// <summary>Maintains <c>%LocalAppData%\O-Studio\neon.env</c> (service credentials for sync — not end-user Neon Auth).</summary>
public static class NeonEnvFile
{
    static readonly string[] SyncKeys =
    [
        "DATABASE_URL",
        "DATABASE_URL_POOLED",
        "DATABASE_URL_UNPOOLED",
        "AWS_ENDPOINT_URL_S3",
        "AWS_ACCESS_KEY_ID",
        "AWS_SECRET_ACCESS_KEY",
        "AWS_REGION",
        "S3_BUCKET",
        "NEON_BRANCH",
    ];

    public static string DefaultPath => SuiteWire.LocalPath("neon.env");

    public static IReadOnlyList<string> MissingForSync()
    {
        var s = NeonSettings.Load();
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(s.DatabaseUrl))
            missing.Add("DATABASE_URL or DATABASE_URL_POOLED");
        if (string.IsNullOrWhiteSpace(s.S3Endpoint))
            missing.Add("AWS_ENDPOINT_URL_S3");
        if (string.IsNullOrWhiteSpace(s.S3Region))
            missing.Add("AWS_REGION");
        if (string.IsNullOrWhiteSpace(s.S3AccessKeyId))
            missing.Add("AWS_ACCESS_KEY_ID");
        if (string.IsNullOrWhiteSpace(s.S3SecretAccessKey))
            missing.Add("AWS_SECRET_ACCESS_KEY");
        return missing;
    }

    /// <summary>Merge <paramref name="sourcePath"/> into the default neon.env (later files win on duplicate keys).</summary>
    public static void MergeIntoDefault(string sourcePath)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The env file was not found.", sourcePath);

        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        if (File.Exists(DefaultPath))
            ApplyLines(File.ReadAllLines(DefaultPath), merged);
        ApplyLines(File.ReadAllLines(sourcePath), merged);

        if (!merged.ContainsKey("S3_BUCKET") || string.IsNullOrWhiteSpace(merged["S3_BUCKET"]))
            merged["S3_BUCKET"] = "uploads";

        var db = merged.GetValueOrDefault("DATABASE_URL_POOLED") ?? merged.GetValueOrDefault("DATABASE_URL");
        if (string.IsNullOrWhiteSpace(db) || !LooksLikePostgresUrl(db))
            throw new InvalidOperationException("The file did not contain a valid DATABASE_URL or DATABASE_URL_POOLED (postgresql://…). Merge with a full neon deploy .env.local, not storage-only export.");

        Directory.CreateDirectory(Path.GetDirectoryName(DefaultPath)!);
        var lines = new List<string>
        {
            "# Kivo cloud sync — service credentials (gitignore on dev machines).",
            "# Postgres + Object Storage from neon deploy; not Neon Auth user login.",
        };
        foreach (var key in SyncKeys)
        {
            if (merged.TryGetValue(key, out var val) && !string.IsNullOrWhiteSpace(val))
                lines.Add($"{key}={QuoteIfNeeded(val)}");
        }

        File.WriteAllLines(DefaultPath, lines);
        NeonSettings.ApplyFile(DefaultPath);
    }

    static string QuoteIfNeeded(string value) =>
        value.Contains(' ') || value.Contains('#') ? $"\"{value.Replace("\"", "\\\"")}\"" : value;

    static bool LooksLikePostgresUrl(string url) =>
        url.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase);

    static void ApplyLines(IEnumerable<string> lines, Dictionary<string, string> into)
    {
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#'))
                continue;
            var i = t.IndexOf('=');
            if (i <= 0)
                continue;
            var key = t[..i].Trim();
            var val = t[(i + 1)..].Trim().Trim('"');
            if (SyncKeys.Contains(key, StringComparer.Ordinal))
                into[key] = val;
        }
    }
}
