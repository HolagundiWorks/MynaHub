using System.Runtime.InteropServices;
using System.Text.Json;

namespace Office.Interop;

public sealed class OfficeException : Exception
{
    public OfficeException(int status, string message) : base(message)
    {
        Status = status;
    }

    public int Status { get; }
}

public sealed class CompanySummary
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Uuid { get; set; } = "";
}

public sealed class SessionInfo
{
    public string Username { get; set; } = "";
    public string Role { get; set; } = "";
}

public sealed class CreatedCompany
{
    public CompanySummary Summary { get; set; } = new();
    public string Recovery { get; set; } = "";
}

/// <summary>
/// One session on the shared office company file. Disposing it closes the file and releases its lock, so another app can open it.
/// Only one app can have the file open at a time.
/// </summary>
public sealed class OfficeSession : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _gate = new();
    private ulong _handle;
    private bool _disposed;

    public OfficeSession(string appDirectory)
    {
        if (string.IsNullOrWhiteSpace(appDirectory))
            throw new ArgumentException("Choose an application folder.", nameof(appDirectory));
        var result = OfficeNative.office_session_create(appDirectory);
        try
        {
            EnsureOk(result);
            var created = JsonSerializer.Deserialize<HandlePayload>(ReadText(result), Json)
                ?? throw new OfficeException(4, "The engine did not return a session.");
            if (created.Handle == 0)
                throw new OfficeException(4, "The engine did not return a session.");
            _handle = created.Handle;
        }
        finally
        {
            OfficeNative.office_result_free(result);
        }
    }

    public static uint ApiVersion => OfficeNative.office_api_version();

    public CompanySummary OpenCompany(string path, string secret = "") => Invoke<CompanySummary>("company.open", new { path, secret, unlock = "password" });

    public CreatedCompany CreateCompany(string path, string name, string username, string password, string confirm) =>
        Invoke<CreatedCompany>("company.create", new { path, name, username, password, confirm });

    public void CloseCompany() => InvokeRaw("company.close", new { });

    public SessionInfo Login(string username, string password) => Invoke<SessionInfo>("auth.login", new { username, password });

    public void Logout() => InvokeRaw("auth.logout", new { });

    /// <summary>Adds an app's own tables to the file. Tables must be named track_..., and nothing that already exists may change.</summary>
    public int ApplyTrack(string track, IReadOnlyList<string> migrations) => Call("track.apply", new { name = track, migrations }).GetInt32();

    /// <summary>Runs a named operation. See docs/office-abi.md for the list.</summary>
    public JsonElement Call(string operation, object? args = null)
    {
        var bytes = InvokeRaw(operation, args ?? new { });
        if (bytes.Length == 0)
            return JsonDocument.Parse("null").RootElement.Clone();
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            var result = OfficeNative.office_session_destroy(_handle);
            OfficeNative.office_result_free(result);
            _handle = 0;
        }
    }

    private T Invoke<T>(string op, object args)
    {
        var bytes = InvokeRaw(op, args);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new OfficeException(4, "The engine returned an empty response.");
    }

    private byte[] InvokeRaw(string op, object args)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var json = JsonSerializer.Serialize(args, Json);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var result = OfficeNative.office_call(_handle, op, json);
            try
            {
                EnsureOk(result);
                return ReadBytes(result);
            }
            finally
            {
                OfficeNative.office_result_free(result);
            }
        }
    }

    private static void EnsureOk(OfficeResult result)
    {
        if (result.Status != 0)
            throw new OfficeException(result.Status, ReadText(result));
    }

    private static byte[] ReadBytes(OfficeResult result)
    {
        if (result.Data == 0 || result.DataLength == 0)
            return [];
        var bytes = new byte[checked((int)result.DataLength)];
        Marshal.Copy(result.Data, bytes, 0, bytes.Length);
        return bytes;
    }

    private static string ReadText(OfficeResult result)
    {
        if (result.Status == 0)
            return System.Text.Encoding.UTF8.GetString(ReadBytes(result));
        return result.Error == 0 ? "The engine reported an error." : Marshal.PtrToStringUTF8(result.Error) ?? "The engine reported an error.";
    }

    private sealed class HandlePayload
    {
        public ulong Handle { get; set; }
    }
}
