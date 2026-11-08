namespace Osas.Core.Database;

public sealed record RemoteHead(long Revision, string? Sha256);

/// <summary>Who pushes: the company and the user of the Kivo session. Never a password or a key.</summary>
public sealed record RemoteContext(string OwnerId, string User);

public sealed record RemotePushResult(bool Ok, bool Conflict, long Revision, string Message);

public sealed record RemotePullResult(bool Ok, long Revision, string? Sha256, string Message);

/// <summary>The remote end is not reachable or not set up (no network, no <c>neon.env</c>). The service goes <c>offline</c> and keeps the local file.</summary>
public sealed class RemoteUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The cloud copy of the projects (Neon in the launcher; a fake in tests).</summary>
public interface IRemoteStore
{
    /// <summary>False when no credentials are set up. The service then reports <c>offline</c>.</summary>
    bool IsConfigured { get; }

    /// <summary>True when project files are deliberately kept on this PC (see <c>ProjectFilePolicy</c>): the service says <c>local</c>, not <c>offline</c>, and nothing is sent.</summary>
    bool CloudDisabled => false;

    /// <summary>The newest remote revision of the project, or null when it was never pushed. Throws <see cref="RemoteUnavailableException"/>.</summary>
    Task<RemoteHead?> GetHeadAsync(string projectId, CancellationToken ct = default);

    /// <summary>Send the file at its current revision. <paramref name="expectedRemoteRevision"/> is the remote revision the caller last saw (0 for none); a moved remote answers <c>Conflict</c>.</summary>
    Task<RemotePushResult> PushAsync(RemoteContext who, string ospPath, long expectedRemoteRevision, CancellationToken ct = default);

    /// <summary>Download the newest remote revision to <paramref name="destPath"/> (a sidecar; the open file is never touched).</summary>
    Task<RemotePullResult> PullToAsync(string projectId, string destPath, CancellationToken ct = default);
}

/// <summary>The store used while project files stay local: no network, no credentials, nothing sent.</summary>
public sealed class LocalOnlyRemoteStore : IRemoteStore
{
    public bool IsConfigured => false;

    public bool CloudDisabled => true;

    static RemoteUnavailableException Off() => new("Project files stay on this PC.");

    public Task<RemoteHead?> GetHeadAsync(string projectId, CancellationToken ct = default) => throw Off();

    public Task<RemotePushResult> PushAsync(RemoteContext who, string ospPath, long expectedRemoteRevision, CancellationToken ct = default) => throw Off();

    public Task<RemotePullResult> PullToAsync(string projectId, string destPath, CancellationToken ct = default) => throw Off();
}

