using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using Npgsql;
using Office.Shared;

namespace Adms.Cloud;

public sealed record CloudHead(long LatestRevision, string? Sha256);

public sealed record PushResult(bool Ok, string Message, long? NewRevision);

public sealed record PullResult(bool Ok, string Message, long? NewRevision);

/// <summary>Postgres metadata + Object Storage snapshots for `.osp` files.</summary>
public sealed class OspCloudClient : IAsyncDisposable
{
    readonly NeonSettings _settings;
    readonly NpgsqlDataSource _pg;
    readonly IAmazonS3 _s3;

    public OspCloudClient(NeonSettings settings)
    {
        _settings = settings;
        _pg = NpgsqlDataSource.Create(NeonPostgres.ToNpgsqlConnectionString(settings.DatabaseUrl));
        _s3 = new AmazonS3Client(
            settings.S3AccessKeyId,
            settings.S3SecretAccessKey,
            new AmazonS3Config
            {
                ServiceURL = settings.S3Endpoint,
                AuthenticationRegion = settings.S3Region,
                ForcePathStyle = true,
            });
    }

    public static string SnapshotKey(string projectId, long revision) =>
        $"projects/{projectId}/r{revision}.osp";

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var cmd = _pg.CreateCommand(OspSyncSchema.Sql);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        await using var cmd = _pg.CreateCommand("SELECT 1");
        var n = (int?)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) ?? 0;
        return n == 1;
    }

    /// <summary>Verify the configured bucket accepts API calls (HeadBucket).</summary>
    public async Task<(bool Ok, string Message)> TestStorageAsync(CancellationToken ct = default)
    {
        try
        {
            await _s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _settings.S3Bucket,
                MaxKeys = 1,
            }, ct).ConfigureAwait(false);
            return (true, $"Bucket “{_settings.S3Bucket}” is reachable.");
        }
        catch (Exception ex)
        {
            return (false, $"Object Storage: {ex.Message}");
        }
    }

    public async Task<CloudHead?> GetHeadAsync(string projectId, CancellationToken ct = default)
    {
        await using var cmd = _pg.CreateCommand(
            """
            SELECT latest_revision FROM osp_project WHERE id = @id
            """);
        cmd.Parameters.AddWithValue("id", projectId);
        var o = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (o is null or DBNull)
            return null;
        var rev = Convert.ToInt64(o);
        await using var revCmd = _pg.CreateCommand(
            """
            SELECT sha256 FROM osp_project_revision
            WHERE project_id = @id AND revision = @rev
            """);
        revCmd.Parameters.AddWithValue("id", projectId);
        revCmd.Parameters.AddWithValue("rev", rev);
        var sha = await revCmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        return new CloudHead(rev, sha);
    }

    public async Task<PushResult> PushAsync(
        string ospPath,
        string ownerId,
        string pushedBy,
        long expectedCloudRevision,
        CancellationToken ct = default)
    {
        if (!File.Exists(ospPath))
            return new PushResult(false, "The project file does not exist yet. Save it from Tren or Falcon first.", null);
        if (!OdpFile.LooksLikeOdp(ospPath))
            return new PushResult(false, "Not a valid .osp project file.", null);

        OdpMeta meta;
        byte[] bytes;
        using (var odp = OdpFile.Open(ospPath))
        {
            meta = odp.Meta();
            if (string.IsNullOrWhiteSpace(meta.ProjectId))
                return new PushResult(false, "The project file has no project_id in meta.", null);
            bytes = await File.ReadAllBytesAsync(ospPath, ct).ConfigureAwait(false);
        }

        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var head = await GetHeadAsync(meta.ProjectId, ct).ConfigureAwait(false);
        var cloudRev = head?.LatestRevision ?? 0;
        if (cloudRev != expectedCloudRevision)
        {
            return new PushResult(
                false,
                cloudRev > expectedCloudRevision
                    ? $"Cloud is ahead (revision {cloudRev}). Pull before you push."
                    : $"Cloud revision changed (now {cloudRev}). Refresh and try again.",
                null);
        }

        if (meta.Revision <= cloudRev)
            return new PushResult(true, $"Already synced at revision {cloudRev}.", cloudRev);

        var key = SnapshotKey(meta.ProjectId, meta.Revision);
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _settings.S3Bucket,
            Key = key,
            InputStream = new MemoryStream(bytes),
            ContentType = "application/x-osp",
        }, ct).ConfigureAwait(false);

        await using var conn = await _pg.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        long currentCloud;
        await using (var sel = new NpgsqlCommand("SELECT latest_revision FROM osp_project WHERE id = @id FOR UPDATE", conn, tx))
        {
            sel.Parameters.AddWithValue("id", meta.ProjectId);
            var o = await sel.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (o is null or DBNull)
                currentCloud = 0;
            else
                currentCloud = Convert.ToInt64(o);
        }

        if (currentCloud != expectedCloudRevision)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return new PushResult(
                false,
                currentCloud > expectedCloudRevision
                    ? $"Cloud is ahead (revision {currentCloud}). Pull before you push."
                    : $"Cloud revision changed (now {currentCloud}). Refresh and try again.",
                null);
        }

        if (currentCloud == 0)
        {
            await using var insProj = new NpgsqlCommand(
                """
                INSERT INTO osp_project (id, owner_id, name, latest_revision)
                VALUES (@id, @owner, @name, @rev)
                """, conn, tx);
            insProj.Parameters.AddWithValue("id", meta.ProjectId);
            insProj.Parameters.AddWithValue("owner", ownerId);
            insProj.Parameters.AddWithValue("name", meta.Name);
            insProj.Parameters.AddWithValue("rev", meta.Revision);
            await insProj.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        else
        {
            await using var upd = new NpgsqlCommand(
                """
                UPDATE osp_project SET name = @name, latest_revision = @rev, updated_at = now()
                WHERE id = @id AND latest_revision = @expected
                """, conn, tx);
            upd.Parameters.AddWithValue("id", meta.ProjectId);
            upd.Parameters.AddWithValue("name", meta.Name);
            upd.Parameters.AddWithValue("rev", meta.Revision);
            upd.Parameters.AddWithValue("expected", expectedCloudRevision);
            if (await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return new PushResult(false, "Another device pushed first. Pull, then push again.", null);
            }
        }

        await using (var ins = new NpgsqlCommand(
                         """
                         INSERT INTO osp_project_revision
                           (project_id, revision, pushed_by, sha256, size_bytes, storage_key)
                         VALUES (@pid, @rev, @by, @sha, @size, @key)
                         """, conn, tx))
        {
            ins.Parameters.AddWithValue("pid", meta.ProjectId);
            ins.Parameters.AddWithValue("rev", meta.Revision);
            ins.Parameters.AddWithValue("by", pushedBy);
            ins.Parameters.AddWithValue("sha", sha);
            ins.Parameters.AddWithValue("size", (long)bytes.Length);
            ins.Parameters.AddWithValue("key", key);
            await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new PushResult(true, $"Pushed revision {meta.Revision} ({bytes.Length / 1024} KB).", meta.Revision);
    }

    public async Task<PullResult> PullAsync(string ospPath, long localRevision, CancellationToken ct = default)
    {
        if (!File.Exists(ospPath))
            return new PullResult(false, "Local project file is missing.", null);

        OdpMeta meta;
        using (var odp = OdpFile.Open(ospPath))
            meta = odp.Meta();
        if (string.IsNullOrWhiteSpace(meta.ProjectId))
            return new PullResult(false, "Local file has no project_id.", null);

        var head = await GetHeadAsync(meta.ProjectId, ct).ConfigureAwait(false);
        if (head is null)
            return new PullResult(false, "This project has never been pushed to the cloud.", null);
        if (head.LatestRevision <= localRevision)
            return new PullResult(true, $"Cloud is at revision {head.LatestRevision}; local is {localRevision}.", head.LatestRevision);

        var key = SnapshotKey(meta.ProjectId, head.LatestRevision);
        using var response = await _s3.GetObjectAsync(_settings.S3Bucket, key, ct).ConfigureAwait(false);
        await using var stream = response.ResponseStream;
        var tmp = ospPath + ".cloud.tmp";
        await using (var file = File.Create(tmp))
            await stream.CopyToAsync(file, ct).ConfigureAwait(false);

        var pulledSha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(tmp, ct).ConfigureAwait(false))).ToLowerInvariant();
        if (head.Sha256 is not null && !string.Equals(head.Sha256, pulledSha, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(tmp);
            return new PullResult(false, "Downloaded snapshot checksum did not match cloud metadata.", null);
        }

        File.Move(tmp, ospPath, overwrite: true);
        return new PullResult(
            true,
            $"Pulled revision {head.LatestRevision}. Tren and Falcon will notice on their next revision poll.",
            head.LatestRevision);
    }

    /// <summary>
    /// Download the newest snapshot of <paramref name="projectId"/> to <paramref name="destPath"/> and check it against the cloud checksum. Unlike
    /// <see cref="PullAsync"/> it needs no local file and never touches the project file: the database service uses it to fill the <c>.osp.incoming</c> sidecar.
    /// </summary>
    public async Task<(CloudHead? Head, string Sha256)> DownloadLatestAsync(string projectId, string destPath, CancellationToken ct = default)
    {
        var head = await GetHeadAsync(projectId, ct).ConfigureAwait(false);
        if (head is null)
            return (null, "");
        var key = SnapshotKey(projectId, head.LatestRevision);
        using var response = await _s3.GetObjectAsync(_settings.S3Bucket, key, ct).ConfigureAwait(false);
        await using (var stream = response.ResponseStream)
        await using (var file = File.Create(destPath))
            await stream.CopyToAsync(file, ct).ConfigureAwait(false);
        var sha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(destPath, ct).ConfigureAwait(false))).ToLowerInvariant();
        if (head.Sha256 is not null && !string.Equals(head.Sha256, sha, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(destPath);
            throw new InvalidDataException("Downloaded snapshot checksum did not match cloud metadata.");
        }

        return (head, sha);
    }

    public async ValueTask DisposeAsync()
    {
        await _pg.DisposeAsync().ConfigureAwait(false);
        _s3.Dispose();
    }
}
