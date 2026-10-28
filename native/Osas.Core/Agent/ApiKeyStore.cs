using System.Security.Cryptography;
using System.Text;

namespace Osas.Core.Agent;

/// <summary>
/// Keeps the person's own Anthropic API key, encrypted for their Windows account (DPAPI), in %LOCALAPPDATA%\OSAS. The key is never
/// written to the company file, to a setting, or to a log, and is not shown again after it is saved. The environment variable
/// ANTHROPIC_API_KEY is used when nothing is saved.
/// </summary>
public sealed class ApiKeyStore
{
    private readonly string _file;
    private readonly Func<string, string?> _environment;

    public ApiKeyStore(string? file = null, Func<string, string?>? environment = null)
    {
        _file = file ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OSAS", "assistant.dat");
        _environment = environment ?? Environment.GetEnvironmentVariable;
    }

    /// <summary>The key to use, or null when there is none.</summary>
    public string? Load()
    {
        try
        {
            if (File.Exists(_file))
            {
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(_file), Entropy, DataProtectionScope.CurrentUser);
                var key = Encoding.UTF8.GetString(plain).Trim();
                if (key.Length > 0)
                    return key;
            }
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException)
        {
            // An unreadable file is the same as no saved key; the person can save one again.
        }
        return _environment("ANTHROPIC_API_KEY") is { Length: > 0 } fromEnvironment ? fromEnvironment.Trim() : null;
    }

    public bool HasKey => Load() is not null;

    /// <summary>Whether a key was saved here, as opposed to coming from the environment.</summary>
    public bool IsSaved => File.Exists(_file);

    public void Save(string key)
    {
        key = key.Trim();
        if (key.Length < 20 || key.Any(char.IsWhiteSpace))
            throw new ArgumentException("That does not look like an API key. Copy the whole key from the Anthropic console.");
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllBytes(_file, ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser));
    }

    public void Forget()
    {
        if (File.Exists(_file))
            File.Delete(_file);
    }

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OSAS assistant key");
}
