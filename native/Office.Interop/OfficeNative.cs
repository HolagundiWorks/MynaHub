using System.Runtime.InteropServices;

namespace Office.Interop;

/// <summary>
/// Version 1 of the office C interface. Returned buffers belong to the caller and are released with <see cref="office_result_free"/>.
/// Status: 0 ok, 1 domain error, 2 bad request, 3 invalid handle, 4 internal. Every operation returns UTF-8 JSON.
/// </summary>
internal static partial class OfficeNative
{
    private const string Lib = "office";

    [LibraryImport(Lib)]
    internal static partial uint office_api_version();

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial OfficeResult office_session_create(string appDir);

    [LibraryImport(Lib)]
    internal static partial OfficeResult office_session_destroy(ulong handle);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial OfficeResult office_call(ulong handle, string op, string argsJson);

    [LibraryImport(Lib)]
    internal static partial void office_result_free(OfficeResult result);
}

[StructLayout(LayoutKind.Sequential)]
internal struct OfficeResult
{
    public int Status;
    public nint Data;
    public nuint DataLength;
    public nint Error;
}
