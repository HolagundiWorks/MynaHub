using Npgsql;

namespace Adms.Cloud;

/// <summary>Neon deploy URLs are postgres URIs with query params; Npgsql key=value parsing breaks on <c>sslmode=require</c>.</summary>
internal static class NeonPostgres
{
    internal static string ToNpgsqlConnectionString(string databaseUrl)
    {
        var url = databaseUrl.Trim();
        if (!url.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            return url;

        var uri = new Uri(url);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            SslMode = SslMode.Require,
        };
        if (uri.Port > 0)
            builder.Port = uri.Port;

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            builder.Username = Uri.UnescapeDataString(parts[0]);
            if (parts.Length > 1)
                builder.Password = Uri.UnescapeDataString(parts[1]);
        }

        ApplyQuery(uri.Query, builder);
        return builder.ConnectionString;
    }

    static void ApplyQuery(string query, NpgsqlConnectionStringBuilder builder)
    {
        if (string.IsNullOrEmpty(query))
            return;
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = pair[..eq];
            var val = Uri.UnescapeDataString(pair[(eq + 1)..]);
            if (key.Equals("sslmode", StringComparison.OrdinalIgnoreCase))
            {
                builder.SslMode = val.ToLowerInvariant() switch
                {
                    "disable" => SslMode.Disable,
                    "prefer" => SslMode.Prefer,
                    "require" or "verify-ca" or "verify-full" => SslMode.Require,
                    _ => builder.SslMode,
                };
            }
        }
    }
}
