using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Osas.Core.Database;

/// <summary>What the database service needs from a local <c>.osp</c> (SQLite; see docs/ODP-PROJECT-FILE-AND-SYNC.md): the <c>meta</c> keys and a hash of the file.</summary>
public static class OspLocalFile
{
    public sealed record Meta(string ProjectId, string Name, long Revision);

    /// <summary>Reads <c>meta.project_id</c>, <c>name</c> and <c>revision</c>. Opens read-only, so the apps that have the file open are not disturbed.</summary>
    public static Meta ReadMeta(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("The project file does not exist.", path);
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        try
        {
            using var conn = new SqliteConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM meta";
            var values = new Dictionary<string, string>();
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                    values[r.GetString(0)] = r.GetString(1);
            }

            _ = long.TryParse(values.GetValueOrDefault("revision"), out var rev);
            return new Meta(values.GetValueOrDefault("project_id") ?? "", values.GetValueOrDefault("name") ?? "", rev);
        }
        catch (SqliteException ex)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} is not a project file: {ex.Message}");
        }
    }

    /// <summary>Lower-case hex SHA-256 of the file as it is on disk now (shared read; the apps may have it open).</summary>
    public static string Sha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
