using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Osas.Core;

namespace Adms.Cloud;

/// <summary>
/// The cloud sign-in kept on this PC, one entry per company: the Neon Auth session cookies sealed with DPAPI (current Windows user), and which
/// local user linked the cloud account. Offline, the app never needs this file: local sign-in does not depend on it.
/// </summary>
public sealed class CloudSessionStore
{
    public sealed class Entry
    {
        public string UserId { get; set; } = "";
        public string Email { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>The local user (company file) that linked this cloud account.</summary>
        public string LocalUser { get; set; } = "";
        /// <summary>Base64 of the DPAPI-protected session cookies.</summary>
        public string SealedSession { get; set; } = "";
        public string LinkedUtc { get; set; } = "";
    }

    sealed class FileModel
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, Entry> Companies { get; set; } = new();
    }

    public static string DefaultPath => SuiteWire.LocalPath("cloud-auth.json");

    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MynaHub.cloud-auth.v1");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    readonly string _file;
    readonly Func<byte[], byte[]> _seal;
    readonly Func<byte[], byte[]> _unseal;

    public CloudSessionStore(string? file = null, Func<byte[], byte[]>? seal = null, Func<byte[], byte[]>? unseal = null)
    {
        _file = file ?? DefaultPath;
        _seal = seal ?? (b => ProtectedData.Protect(b, Entropy, DataProtectionScope.CurrentUser));
        _unseal = unseal ?? (b => ProtectedData.Unprotect(b, Entropy, DataProtectionScope.CurrentUser));
    }

    public void Save(string companyId, CloudSession session, string localUser)
    {
        var model = Read();
        model.Companies[companyId] = new Entry
        {
            UserId = session.UserId,
            Email = session.Email,
            Name = session.Name,
            LocalUser = localUser,
            SealedSession = Convert.ToBase64String(_seal(Encoding.UTF8.GetBytes(session.SessionCookies))),
            LinkedUtc = DateTime.UtcNow.ToString("O"),
        };
        Write(model);
    }

    /// <summary>The stored session, or null when none exists or it cannot be opened (another Windows user, a corrupt file).</summary>
    public CloudSession? Load(string companyId)
    {
        if (!Read().Companies.TryGetValue(companyId, out var e) || e.SealedSession.Length == 0)
            return null;
        try
        {
            var cookies = Encoding.UTF8.GetString(_unseal(Convert.FromBase64String(e.SealedSession)));
            return new CloudSession { UserId = e.UserId, Email = e.Email, Name = e.Name, SessionCookies = cookies };
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    public Entry? Info(string companyId) => Read().Companies.TryGetValue(companyId, out var e) ? e : null;

    public void Clear(string companyId)
    {
        var model = Read();
        if (model.Companies.Remove(companyId))
            Write(model);
    }

    FileModel Read()
    {
        try
        {
            return File.Exists(_file) ? JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_file)) ?? new FileModel() : new FileModel();
        }
        catch (JsonException)
        {
            return new FileModel();
        }
    }

    void Write(FileModel model)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, JsonSerializer.Serialize(model, Json));
    }
}
