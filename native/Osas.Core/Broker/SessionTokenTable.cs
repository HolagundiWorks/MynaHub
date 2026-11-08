using System.Security.Cryptography;
using System.Text;

namespace Osas.Core.Broker;

/// <summary>Who a token was given to and for what. One token per (app, purpose, project).</summary>
public readonly record struct TokenKey(string App, string Purpose, string ProjectId);

/// <summary>The person and company a token stands for.</summary>
public sealed record BrokerIdentity(string User, string CompanyId, string CompanyName, IReadOnlyList<string> Roles);

/// <summary>A token that checked out: what it stands for and when it ends.</summary>
public sealed record TokenInfo(BrokerIdentity Identity, DateTime ExpiresUtc);

/// <summary>
/// The broker's token table (docs/contracts/session-broker.md, section 1). Tokens are 32 random bytes in base64url, live only in memory,
/// end after 8 hours without use or 24 hours after they were given, and are compared in constant time.
/// </summary>
public sealed class SessionTokenTable
{
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromHours(8);
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromHours(24);

    private sealed class Entry(byte[] token, string text, TokenKey key, BrokerIdentity identity, DateTime issued)
    {
        public readonly byte[] Token = token;
        public readonly string Text = text;
        public readonly TokenKey Key = key;
        public readonly BrokerIdentity Identity = identity;
        public readonly DateTime Issued = issued;
        public DateTime LastUsed = issued;
    }

    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];
    private readonly Func<DateTime> _clock;

    public SessionTokenTable(Func<DateTime>? clock = null) => _clock = clock ?? (() => DateTime.UtcNow);

    public int Count
    {
        get
        {
            lock (_gate)
            {
                Sweep();
                return _entries.Count;
            }
        }
    }

    /// <summary>Gives the token for this key: the live one if there is one, else a new one.</summary>
    public (string Token, DateTime ExpiresUtc) Issue(TokenKey key, BrokerIdentity identity)
    {
        lock (_gate)
        {
            Sweep();
            var now = _clock();
            var existing = _entries.FirstOrDefault(e => e.Key == key && e.Identity.User == identity.User && e.Identity.CompanyId == identity.CompanyId);
            if (existing != null)
            {
                existing.LastUsed = now;
                return (existing.Text, Expiry(existing));
            }
            var bytes = RandomNumberGenerator.GetBytes(32);
            var text = Base64Url(bytes);
            var entry = new Entry(Encoding.ASCII.GetBytes(text), text, key, identity, now);
            _entries.Add(entry);
            return (text, Expiry(entry));
        }
    }

    /// <summary>Checks a token. A good one counts as use, so the idle window moves on, but never past the absolute limit.</summary>
    public TokenInfo? Validate(string? token)
    {
        lock (_gate)
        {
            Sweep();
            var entry = Find(token);
            if (entry == null)
                return null;
            entry.LastUsed = _clock();
            return new TokenInfo(entry.Identity, Expiry(entry));
        }
    }

    /// <summary>The result of asking to renew: the new end, or why not.</summary>
    public enum RenewResult { Renewed, BadToken, Expired }

    /// <summary>Slides the idle window. Refused once the absolute limit has passed.</summary>
    public RenewResult Renew(string? token, out DateTime expiresUtc)
    {
        lock (_gate)
        {
            expiresUtc = default;
            var now = _clock();
            var entry = Find(token);
            if (entry == null)
                return RenewResult.BadToken;
            if (IsExpired(entry, now))
            {
                _entries.Remove(entry);
                return RenewResult.Expired;
            }
            entry.LastUsed = now;
            expiresUtc = Expiry(entry);
            return RenewResult.Renewed;
        }
    }

    /// <summary>Ends one token. False if there was no such token.</summary>
    public bool End(string? token)
    {
        lock (_gate)
        {
            var entry = Find(token);
            return entry != null && _entries.Remove(entry);
        }
    }

    public void Clear()
    {
        lock (_gate)
            _entries.Clear();
    }

    private Entry? Find(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 128)
            return null;
        var probe = Encoding.ASCII.GetBytes(token);
        Entry? found = null;
        // Every entry is compared, with no early exit, so the time taken does not show how much of a token was right.
        foreach (var e in _entries)
        {
            if (CryptographicOperations.FixedTimeEquals(e.Token, probe))
                found = e;
        }
        return found;
    }

    private void Sweep()
    {
        var now = _clock();
        _entries.RemoveAll(e => IsExpired(e, now));
    }

    private static bool IsExpired(Entry e, DateTime now) => now >= e.Issued + AbsoluteLifetime || now >= e.LastUsed + IdleLifetime;

    private static DateTime Expiry(Entry e)
    {
        var idle = e.LastUsed + IdleLifetime;
        var absolute = e.Issued + AbsoluteLifetime;
        return idle < absolute ? idle : absolute;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
