using System.Runtime.InteropServices;
using System.Text.Json;

namespace Adms.Interop;

public sealed class AdmsException : Exception
{
    public AdmsException(int status, string message) : base(message)
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

public sealed class CompanyProfile
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Pan { get; set; } = "";
    public string Gstin { get; set; } = "";
    public string CoaRegistration { get; set; } = "";
    public string BankDetails { get; set; } = "";
    public string GstScheme { get; set; } = "";
    public string UpiId { get; set; } = "";
    public string ArchitectName { get; set; } = "";
    public string LetterheadStyle { get; set; } = "";
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
/// One Rust-managed company session. Closing or disposing it releases the company-file lock.
/// </summary>
public sealed class AdmsSession : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _gate = new();
    private ulong _handle;
    private bool _disposed;

    public AdmsSession(string appDirectory)
    {
        if (string.IsNullOrWhiteSpace(appDirectory))
            throw new ArgumentException("Choose an application folder.", nameof(appDirectory));
        var result = AdmsNative.adms_session_create(appDirectory);
        try
        {
            EnsureOk(result);
            var created = JsonSerializer.Deserialize<HandlePayload>(ReadText(result), Json)
                ?? throw new AdmsException(4, "The engine did not return a session.");
            if (created.Handle == 0)
                throw new AdmsException(4, "The engine did not return a session.");
            _handle = created.Handle;
        }
        finally
        {
            AdmsNative.adms_result_free(result);
        }
    }

    public static uint ApiVersion => AdmsNative.adms_api_version();

    public CompanySummary OpenCompany(string path) =>
        Invoke<CompanySummary>("company.open", new { path });

    public CreatedCompany CreateCompany(string path, string name, string username, string password, string confirm) =>
        Invoke<CreatedCompany>("company.create", new { path, name, username, password, confirm });

    public void CloseCompany() => InvokeRaw("company.close", new { });

    public SessionInfo Login(string username, string password) =>
        Invoke<SessionInfo>("auth.login", new { username, password });

    public CompanyProfile GetProfile() => Invoke<CompanyProfile>("company.profile", new { });

    public byte[] SamplePdf() => InvokeRaw("documents.sample_pdf", new { });

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
            var result = AdmsNative.adms_session_destroy(_handle);
            AdmsNative.adms_result_free(result);
            _handle = 0;
        }
    }

    private T Invoke<T>(string op, object args)
    {
        var bytes = InvokeRaw(op, args);
        return JsonSerializer.Deserialize<T>(bytes, Json)
            ?? throw new AdmsException(4, "The engine returned an empty response.");
    }

    private byte[] InvokeRaw(string op, object args)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var json = JsonSerializer.Serialize(args, Json);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var result = AdmsNative.adms_call(_handle, op, json);
            try
            {
                EnsureOk(result);
                return ReadBytes(result);
            }
            finally
            {
                AdmsNative.adms_result_free(result);
            }
        }
    }

    private static void EnsureOk(AdmsResult result)
    {
        if (result.Status == 0)
            return;
        throw new AdmsException(result.Status, ReadText(result));
    }

    private static byte[] ReadBytes(AdmsResult result)
    {
        if (result.Data == 0 || result.DataLength == 0)
            return [];
        var length = checked((int)result.DataLength);
        var bytes = new byte[length];
        Marshal.Copy(result.Data, bytes, 0, length);
        return bytes;
    }

    private static string ReadText(AdmsResult result)
    {
        if (result.Status == 0)
            return System.Text.Encoding.UTF8.GetString(ReadBytes(result));
        if (result.Error == 0)
            return "The engine reported an error.";
        return Marshal.PtrToStringUTF8(result.Error) ?? "The engine reported an error.";
    }

    private sealed class HandlePayload
    {
        public ulong Handle { get; set; }
    }
}
