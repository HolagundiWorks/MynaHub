using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Osas.Core.Monitor;

/// <summary>A program that has a company file open.</summary>
/// <param name="App">MynaHub, MynaOps, MynaCAD or OSAS; otherwise the program's own name.</param>
public sealed record Holder(string App, string Program, int ProcessId);

/// <summary>Finds which programs have a set of files open. A test supplies its own.</summary>
public interface IFileHolders
{
    IReadOnlyList<Holder> Holders(IReadOnlyList<string> files);
}

/// <summary>
/// Asks Windows (the Restart Manager) which programs hold a file open. It needs no administrator rights for the person's own
/// programs, reads nothing from the file, and changes nothing.
/// </summary>
public sealed class WindowsFileHolders : IFileHolders
{
    private const int ErrorMoreData = 234;

    [StructLayout(LayoutKind.Sequential)]
    private struct UniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME StartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessInfo
    {
        public UniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TsSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, int flags, StringBuilder key);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint files, string[] fileNames, uint apps, UniqueProcess[]? applications, uint services, string[]? serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] ProcessInfo[]? info, ref uint reasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);

    public IReadOnlyList<Holder> Holders(IReadOnlyList<string> files)
    {
        var existing = files.Where(File.Exists).ToArray();
        if (existing.Length == 0)
            return [];
        var key = new StringBuilder(33);
        if (RmStartSession(out var session, 0, key) != 0)
            return [];
        try
        {
            if (RmRegisterResources(session, (uint)existing.Length, existing, 0, null, 0, null) != 0)
                return [];
            uint needed = 0, count = 0, reasons = 0;
            var result = RmGetList(session, out needed, ref count, null, ref reasons);
            if (result != ErrorMoreData && result != 0)
                return [];
            var info = new ProcessInfo[Math.Max(needed, 1)];
            count = (uint)info.Length;
            if (RmGetList(session, out needed, ref count, info, ref reasons) != 0)
                return [];
            return info.Take((int)count).Select(p => Describe(p.Process.ProcessId, p.AppName)).DistinctBy(h => h.ProcessId).ToList();
        }
        finally
        {
            RmEndSession(session);
        }
    }

    /// <summary>Names a program the way the suite does.</summary>
    public static Holder Describe(int processId, string fallback)
    {
        string program;
        try
        {
            program = Process.GetProcessById(processId).ProcessName;
        }
        catch (ArgumentException)
        {
            program = fallback;
        }
        return new Holder(AppFor(program, fallback), program, processId);
    }

    public static string AppFor(string program, string fallback = "") =>
        program.ToLowerInvariant() switch
        {
            "o-studio" or "adms.desktop" => "MynaHub",
            "o-coreop" or "aqccore" => "MynaOps",
            "aad" or "o-draft" or "aad.winui" or "odraft.winui" => "MynaCAD",
            "osas" => "OSAS",
            _ => fallback.Length > 0 ? fallback : program,
        };
}
