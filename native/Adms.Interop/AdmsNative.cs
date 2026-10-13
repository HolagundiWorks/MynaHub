using System.Runtime.InteropServices;

namespace Adms.Interop;

/// <summary>
/// ABI version 1. Returned buffers are owned by the caller and released with <see cref="adms_result_free"/>.
/// Status: 0 ok, 1 domain error, 2 bad request, 3 invalid handle, 4 internal.
/// <c>documents.sample_pdf</c> returns raw PDF bytes. Every other operation returns UTF-8 JSON.
/// </summary>
internal static partial class AdmsNative
{
    private const string Lib = "architect_office_desktop_lib";

    [LibraryImport(Lib)]
    internal static partial uint adms_api_version();

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial AdmsResult adms_session_create(string appDir);

    [LibraryImport(Lib)]
    internal static partial AdmsResult adms_session_destroy(ulong handle);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial AdmsResult adms_call(ulong handle, string op, string argsJson);

    [LibraryImport(Lib)]
    internal static partial void adms_result_free(AdmsResult result);
}

[StructLayout(LayoutKind.Sequential)]
internal struct AdmsResult
{
    public int Status;
    public nint Data;
    public nuint DataLength;
    public nint Error;
}
