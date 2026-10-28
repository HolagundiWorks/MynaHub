using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Osas.Core.Suite;

/// <summary>An error the office engine reported. The message is written for the person using the app.</summary>
public sealed class EngineException(string message) : Exception(message);

/// <summary>
/// One session with the office engine (office.dll). The contract is the office interface written out in the O-Studio repository
/// (docs/office-abi.md): start a session, call operations by name with a JSON object, end the session. One session holds one company
/// file. OSAS uses it to sign in, read and edit the company details, and ask for the one-time tickets it hands to the apps.
/// </summary>
public sealed class Engine : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Reply
    {
        public int Status;
        public nint Data;
        public nuint DataLength;
        public nint Error;
    }

    private static class Native
    {
        [DllImport("office", CallingConvention = CallingConvention.Cdecl)]
        public static extern uint office_api_version();

        [DllImport("office", CallingConvention = CallingConvention.Cdecl)]
        public static extern Reply office_session_create([MarshalAs(UnmanagedType.LPUTF8Str)] string folder);

        [DllImport("office", CallingConvention = CallingConvention.Cdecl)]
        public static extern Reply office_session_destroy(ulong handle);

        [DllImport("office", CallingConvention = CallingConvention.Cdecl)]
        public static extern Reply office_call(ulong handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string operation, [MarshalAs(UnmanagedType.LPUTF8Str)] string? arguments);

        [DllImport("office", CallingConvention = CallingConvention.Cdecl)]
        public static extern void office_result_free(Reply reply);
    }

    private readonly object _gate = new();
    private ulong _handle;

    private Engine(ulong handle) => _handle = handle;

    /// <summary>True when office.dll is beside the app and speaks version 1.</summary>
    public static bool IsAvailable()
    {
        try
        {
            return Native.office_api_version() == 1;
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Makes the loader look for office.dll in a folder, for tests and for builds where it is not beside the program.</summary>
    public static void LookIn(string folder)
    {
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(Engine).Assembly, (name, _, _) =>
            {
                var path = Path.Combine(folder, "office.dll");
                return name == "office" && File.Exists(path) && NativeLibrary.TryLoad(path, out var handle) ? handle : 0;
            });
        }
        catch (InvalidOperationException)
        {
            // The loader allows one resolver for the program; the folder chosen first stays.
        }
    }

    public static Engine Start(string folder)
    {
        Directory.CreateDirectory(folder);
        return new Engine(Take(Native.office_session_create(folder))!["handle"]!.GetValue<ulong>());
    }

    private static JsonNode? Take(Reply reply)
    {
        try
        {
            if (reply.Status != 0)
                throw new EngineException(reply.Error == 0 ? "The office engine reported an error." : Marshal.PtrToStringUTF8(reply.Error) ?? "The office engine reported an error.");
            if (reply.Data == 0 || reply.DataLength == 0)
                return null;
            var bytes = new byte[(int)reply.DataLength];
            Marshal.Copy(reply.Data, bytes, 0, bytes.Length);
            return JsonNode.Parse(Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            Native.office_result_free(reply);
        }
    }

    public JsonNode? Call(string operation, JsonObject? arguments = null)
    {
        lock (_gate)
        {
            if (_handle == 0)
                throw new ObjectDisposedException(nameof(Engine));
            return Take(Native.office_call(_handle, operation, arguments?.ToJsonString()));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_handle == 0)
                return;
            var handle = _handle;
            _handle = 0;
            try
            {
                Take(Native.office_session_destroy(handle));
            }
            catch (EngineException)
            {
                // The session is gone either way.
            }
        }
    }
}
