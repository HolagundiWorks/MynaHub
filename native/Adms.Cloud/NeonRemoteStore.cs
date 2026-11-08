using Npgsql;
using Osas.Core.Database;

namespace Adms.Cloud;

/// <summary>
/// The database service's remote end: Neon Postgres and Object Storage through <see cref="OspCloudClient"/> (the same push and pull the launcher's
/// old Cloud sync used). Credentials come from <c>neon.env</c> (<see cref="NeonSettings"/>), read again on every call, so adding the file needs no restart.
/// </summary>
public sealed class NeonRemoteStore : IRemoteStore, IAsyncDisposable
{
    readonly SemaphoreSlim _gate = new(1, 1);
    OspCloudClient? _client;
    string _clientKey = "";

    public bool IsConfigured => NeonSettings.Load().IsComplete;

    public async Task<RemoteHead?> GetHeadAsync(string projectId, CancellationToken ct = default)
    {
        return await Guard(async () =>
        {
            var client = await ClientAsync(ct).ConfigureAwait(false);
            var head = await client.GetHeadAsync(projectId, ct).ConfigureAwait(false);
            return head is null ? null : new RemoteHead(head.LatestRevision, head.Sha256);
        }).ConfigureAwait(false);
    }

    public async Task<RemotePushResult> PushAsync(RemoteContext who, string ospPath, long expectedRemoteRevision, CancellationToken ct = default)
    {
        return await Guard(async () =>
        {
            var client = await ClientAsync(ct).ConfigureAwait(false);
            var result = await client.PushAsync(ospPath, who.OwnerId, who.User, expectedRemoteRevision, ct).ConfigureAwait(false);
            if (result.Ok && result.NewRevision is long rev)
                return new RemotePushResult(true, false, rev, result.Message);

            // A refusal because the cloud moved reads as a conflict; any other refusal is a plain failure.
            var projectId = Osas.Core.Database.OspLocalFile.ReadMeta(ospPath).ProjectId;
            var head = await client.GetHeadAsync(projectId, ct).ConfigureAwait(false);
            var current = head?.LatestRevision ?? 0;
            return new RemotePushResult(false, current != expectedRemoteRevision, current, result.Message);
        }).ConfigureAwait(false);
    }

    public async Task<RemotePullResult> PullToAsync(string projectId, string destPath, CancellationToken ct = default)
    {
        return await Guard(async () =>
        {
            var client = await ClientAsync(ct).ConfigureAwait(false);
            try
            {
                var (head, sha) = await client.DownloadLatestAsync(projectId, destPath, ct).ConfigureAwait(false);
                return head is null
                    ? new RemotePullResult(false, 0, null, "This project was never pushed to the cloud.")
                    : new RemotePullResult(true, head.LatestRevision, sha, $"Fetched revision {head.LatestRevision}.");
            }
            catch (InvalidDataException ex)
            {
                return new RemotePullResult(false, 0, null, ex.Message);
            }
        }).ConfigureAwait(false);
    }

    /// <summary>One client at a time per set of credentials; the schema is made once per client.</summary>
    async Task<OspCloudClient> ClientAsync(CancellationToken ct)
    {
        var settings = NeonSettings.Load();
        if (!settings.IsComplete)
            throw new RemoteUnavailableException("Neon is not set up on this PC (neon.env, see docs/CLOUD-NEON.md).");
        var key = settings.DatabaseUrl + "|" + settings.S3Endpoint + "|" + settings.S3AccessKeyId + "|" + settings.S3Bucket;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is not null && _clientKey == key)
                return _client;
            if (_client is not null)
                await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
            var client = new OspCloudClient(settings);
            try
            {
                await client.EnsureSchemaAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _client = client;
            _clientKey = key;
            return client;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Network and database failures mean "offline" for the service; the client is dropped so the next call connects afresh.</summary>
    async Task<T> Guard<T>(Func<Task<T>> work)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NpgsqlException or HttpRequestException or TimeoutException or IOException or System.Net.Sockets.SocketException
                                   or Amazon.Runtime.AmazonServiceException or Amazon.S3.AmazonS3Exception)
        {
            await ResetAsync().ConfigureAwait(false);
            throw new RemoteUnavailableException(ex.Message, ex);
        }
    }

    async Task ResetAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_client is not null)
                await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync() => await ResetAsync().ConfigureAwait(false);
}
