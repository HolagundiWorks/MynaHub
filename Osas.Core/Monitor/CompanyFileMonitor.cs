using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Osas.Core.Monitor;

/// <summary>A program that has the company file open, with what it is costing this computer right now.</summary>
public sealed record ProgramLoad(string App, string Program, int ProcessId, double CpuPercent, long MemoryBytes);

/// <summary>How much room one table takes inside the file.</summary>
public sealed record TableSpace(string Table, long Bytes);

/// <summary>What one look at a company file found. Nothing here is a name, an amount or the text of a record.</summary>
public sealed record CompanyStats(
    string Path,
    DateTime Sampled,
    bool Exists,
    bool Protected,
    bool IsCompanyFile,
    string? Problem,
    string Name,
    long MainBytes,
    long WalBytes,
    long BackupBytes,
    int BackupCount,
    DateTime? NewestBackup,
    long DriveFreeBytes,
    long DriveTotalBytes,
    int SchemaVersion,
    long PageSize,
    long PageCount,
    long FreePages,
    string JournalMode,
    long ProjectDocumentBytes,
    long StoredFileBytes,
    IReadOnlyDictionary<string, long> Counts,
    IReadOnlyList<TableSpace> Biggest,
    IReadOnlyList<ProgramLoad> OpenIn,
    long ChangesSinceLastLook,
    double ChangesPerMinute,
    string? LastOpened)
{
    /// <summary>Room inside the file that is free and would be given back by compacting it.</summary>
    public long FreeBytes => FreePages * PageSize;

    public long TotalOnDiskBytes => MainBytes + WalBytes;
}

/// <summary>
/// Watches one company file (.adms) from outside, without signing in and without ever changing it: how big it is, how much of that is
/// free or taken by what, which programs have it open and how busy they are, and how often other programs are writing to it.
/// It opens the file read-only and counts rows; it does not read names, amounts or documents.
/// A protected (encrypted) file can only be measured from outside, so its sizes and who has it open are all that is shown.
/// </summary>
public sealed class CompanyFileMonitor : IDisposable
{
    public const long AppId = 0x414F524D;
    private static readonly string[] CountedTables = ["clients", "projects", "project_documents", "invoices", "stored_files", "audit_log", "users"];
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly string _path;
    private readonly IFileHolders _holders;
    private SqliteConnection? _connection;
    private long _lastDataVersion = -1;
    private readonly Queue<(DateTime At, long Changes)> _recent = new();
    private readonly Dictionary<int, (TimeSpan Cpu, DateTime At)> _cpu = new();

    public CompanyFileMonitor(string path, IFileHolders? holders = null)
    {
        _path = path;
        _holders = holders ?? new WindowsFileHolders();
    }

    public string Path => _path;

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }

    private static string Sidecar(string path, string suffix) => path + suffix;

    private static long Length(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    /// <summary>Takes one look. Never throws for a file that is missing, protected, damaged or in use; the problem is in the result.</summary>
    public CompanyStats Sample()
    {
        var now = DateTime.Now;
        var exists = File.Exists(_path);
        var folder = System.IO.Path.GetDirectoryName(_path) ?? "";
        var stem = System.IO.Path.GetFileNameWithoutExtension(_path);
        var (backupBytes, backupCount, newest) = Backups(System.IO.Path.Combine(folder, $"{stem} backups"));
        var (free, total) = Drive(_path);
        var holders = Holding();

        long main = Length(_path), wal = Length(Sidecar(_path, "-wal"));
        string? problem = null;
        var isProtected = false;
        var isCompany = false;
        var name = stem;
        int schema = 0;
        long pageSize = 0, pageCount = 0, freePages = 0, documentBytes = 0, storedBytes = 0, changes = 0;
        string journal = "", lastOpened = "";
        var counts = new Dictionary<string, long>();
        var biggest = new List<TableSpace>();

        if (!exists)
        {
            problem = "That file was not found.";
        }
        else
        {
            var head = ReadHead(_path);
            if (head.StartsWith("AOSENC1", StringComparison.Ordinal))
            {
                isProtected = true;
                problem = "This company file is protected, so only its size and who has it open can be shown.";
            }
            else if (!head.StartsWith("SQLite format 3", StringComparison.Ordinal))
            {
                problem = "This is not a company file.";
            }
            else
            {
                try
                {
                    var db = Connection();
                    isCompany = Scalar(db, "PRAGMA application_id") == AppId;
                    if (!isCompany)
                    {
                        problem = "This is a database, but not an ADMS company file.";
                    }
                    else
                    {
                        schema = (int)Scalar(db, "PRAGMA user_version");
                        pageSize = Scalar(db, "PRAGMA page_size");
                        pageCount = Scalar(db, "PRAGMA page_count");
                        freePages = Scalar(db, "PRAGMA freelist_count");
                        journal = Text(db, "PRAGMA journal_mode");
                        name = Text(db, "SELECT display_name FROM company_file WHERE id = 1") is { Length: > 0 } n ? n : stem;
                        lastOpened = Text(db, "SELECT COALESCE(last_opened_at, '') FROM company_file WHERE id = 1");
                        foreach (var table in CountedTables)
                            if (TryScalar(db, $"SELECT COUNT(*) FROM {table}") is { } rows)
                                counts[table] = rows;
                        documentBytes = TryScalar(db, "SELECT COALESCE(SUM(length(CAST(body AS BLOB))), 0) FROM project_documents") ?? 0;
                        storedBytes = TryScalar(db, "SELECT COALESCE(SUM(bytes), 0) FROM stored_files") ?? 0;
                        biggest = BiggestTables(db);
                        changes = ChangesSince(db, now);
                    }
                }
                catch (SqliteException ex)
                {
                    problem = ex.SqliteErrorCode is 5 or 6 ? "The file is busy right now; try again in a moment." : "The file could not be read: " + ex.Message;
                    Dispose();
                }
            }
        }

        var perMinute = RecentRate(now);
        return new CompanyStats(
            _path, now, exists, isProtected, isCompany, problem, name, main, wal, backupBytes, backupCount, newest, free, total, schema,
            pageSize, pageCount, freePages, journal, documentBytes, storedBytes, counts, biggest, holders, changes, perMinute, lastOpened.Length > 0 ? lastOpened : null);
    }

    private static string ReadHead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[16];
            var read = stream.Read(buffer, 0, buffer.Length);
            return System.Text.Encoding.Latin1.GetString(buffer, 0, read);
        }
        catch (IOException)
        {
            return "";
        }
    }

    private SqliteConnection Connection()
    {
        if (_connection != null)
            return _connection;
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        _connection = connection;
        _lastDataVersion = -1;
        return connection;
    }

    private static long Scalar(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar() ?? 0, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static long? TryScalar(SqliteConnection db, string sql)
    {
        try
        {
            return Scalar(db, sql);
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private static string Text(SqliteConnection db, string sql)
    {
        try
        {
            using var command = db.CreateCommand();
            command.CommandText = sql;
            return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
        }
        catch (SqliteException)
        {
            return "";
        }
    }

    private static List<TableSpace> BiggestTables(SqliteConnection db)
    {
        var list = new List<TableSpace>();
        try
        {
            using var command = db.CreateCommand();
            command.CommandText = "SELECT name, SUM(pgsize) AS bytes FROM dbstat GROUP BY name ORDER BY bytes DESC LIMIT 6";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                list.Add(new TableSpace(reader.GetString(0), reader.GetInt64(1)));
        }
        catch (SqliteException)
        {
            // Some builds of SQLite leave this statistics view out; the rest of the picture is still shown.
        }
        return list;
    }

    /// <summary>
    /// SQLite's data_version, read on a connection that stays open, moves when another connection has committed a write since the last
    /// look. Several commits between two looks can show as one move, so this counts looks that saw a write, not commits. The first look
    /// has nothing to compare with.
    /// </summary>
    private long ChangesSince(SqliteConnection db, DateTime now)
    {
        var version = Scalar(db, "PRAGMA data_version");
        var changes = _lastDataVersion < 0 || version == _lastDataVersion ? 0 : 1;
        _lastDataVersion = version;
        if (changes > 0)
            _recent.Enqueue((now, changes));
        return changes;
    }

    private double RecentRate(DateTime now)
    {
        while (_recent.Count > 0 && now - _recent.Peek().At > Window)
            _recent.Dequeue();
        return _recent.Sum(r => r.Changes);
    }

    private static (long Bytes, int Count, DateTime? Newest) Backups(string folder)
    {
        if (!Directory.Exists(folder))
            return (0, 0, null);
        try
        {
            var files = new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
            return (files.Sum(f => f.Length), files.Count, files.Count == 0 ? null : files.Max(f => f.LastWriteTime));
        }
        catch (IOException)
        {
            return (0, 0, null);
        }
    }

    private static (long Free, long Total) Drive(string path)
    {
        try
        {
            var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
                return (0, 0);
            var drive = new DriveInfo(root);
            return (drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return (0, 0);
        }
    }

    private List<ProgramLoad> Holding()
    {
        var self = Environment.ProcessId;
        var found = _holders.Holders([_path, Sidecar(_path, ".aoslock")]).Where(h => h.ProcessId != self).ToList();
        var now = DateTime.UtcNow;
        var list = new List<ProgramLoad>();
        var seen = new HashSet<int>();
        foreach (var holder in found)
        {
            seen.Add(holder.ProcessId);
            double cpu = 0;
            long memory = 0;
            try
            {
                using var process = Process.GetProcessById(holder.ProcessId);
                memory = process.WorkingSet64;
                var used = process.TotalProcessorTime;
                if (_cpu.TryGetValue(holder.ProcessId, out var before) && now > before.At)
                    cpu = 100 * (used - before.Cpu).TotalSeconds / (now - before.At).TotalSeconds / Environment.ProcessorCount;
                _cpu[holder.ProcessId] = (used, now);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
            {
                // The program ended or cannot be inspected; it is still listed as having the file open.
            }
            list.Add(new ProgramLoad(holder.App, holder.Program, holder.ProcessId, Math.Max(0, Math.Round(cpu, 1)), memory));
        }
        foreach (var gone in _cpu.Keys.Where(k => !seen.Contains(k)).ToList())
            _cpu.Remove(gone);
        return list;
    }
}
