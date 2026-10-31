using Osas.Core;

namespace Adms.Cloud;

/// <summary>Neon credentials for O-Studio only (see docs/CLOUD-NEON.md). Never pass to O-Draft or O-CoreOp.</summary>
public sealed class NeonSettings
{
    public string DatabaseUrl { get; init; } = "";
    public string? DatabaseUrlUnpooled { get; init; }
    public string S3Endpoint { get; init; } = "";
    public string S3Region { get; init; } = "";
    public string S3AccessKeyId { get; init; } = "";
    public string S3SecretAccessKey { get; init; } = "";
    public string S3Bucket { get; init; } = "uploads";

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(DatabaseUrl)
        && !string.IsNullOrWhiteSpace(S3Endpoint)
        && !string.IsNullOrWhiteSpace(S3Region)
        && !string.IsNullOrWhiteSpace(S3AccessKeyId)
        && !string.IsNullOrWhiteSpace(S3SecretAccessKey);

    /// <summary>Load from environment and optional <c>OSTUDIO_NEON_ENV_FILE</c> (KEY=VALUE lines).</summary>
    public static NeonSettings Load()
    {
        var path = Environment.GetEnvironmentVariable(SuiteWire.EnvNeonEnvFile);
        if (string.IsNullOrWhiteSpace(path))
        {
            var local = SuiteWire.LocalPath("neon.env");
            if (File.Exists(local))
                path = local;
        }
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            LoadEnvFile(path);

        return FromEnvironment();
    }

    /// <summary>Re-read a KEY=VALUE file into the current process environment.</summary>
    public static void ApplyFile(string path)
    {
        if (File.Exists(path))
            LoadEnvFile(path);
    }

    static NeonSettings FromEnvironment() =>
        new()
        {
            DatabaseUrl = FirstEnv("DATABASE_URL_POOLED", "DATABASE_URL") ?? "",
            DatabaseUrlUnpooled = Environment.GetEnvironmentVariable("DATABASE_URL_UNPOOLED")
                ?? Environment.GetEnvironmentVariable("DATABASE_URL"),
            S3Endpoint = Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL_S3") ?? "",
            S3Region = Environment.GetEnvironmentVariable("AWS_REGION") ?? "",
            S3AccessKeyId = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID") ?? "",
            S3SecretAccessKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY") ?? "",
            S3Bucket = Environment.GetEnvironmentVariable("S3_BUCKET") ?? "uploads",
        };

    static string? FirstEnv(params string[] keys)
    {
        foreach (var k in keys)
        {
            var v = Environment.GetEnvironmentVariable(k);
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }
        return null;
    }

    static void LoadEnvFile(string path)
    {
        foreach (var line in File.ReadAllLines(path))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#'))
                continue;
            var i = t.IndexOf('=');
            if (i <= 0)
                continue;
            var key = t[..i].Trim();
            var val = t[(i + 1)..].Trim().Trim('"');
            Environment.SetEnvironmentVariable(key, val);
        }
    }
}
