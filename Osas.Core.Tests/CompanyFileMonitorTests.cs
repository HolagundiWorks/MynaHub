using Microsoft.Data.Sqlite;
using Osas.Core.Monitor;
using Xunit;

namespace Osas.Core.Tests;

public class CompanyFileMonitorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "osas-monitor-" + Guid.NewGuid().ToString("N")[..8]);

    public CompanyFileMonitorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class NoHolders : IFileHolders
    {
        public IReadOnlyList<Holder> Holders(IReadOnlyList<string> files) => [];
    }

    private sealed class FixedHolders(params Holder[] holders) : IFileHolders
    {
        public IReadOnlyList<Holder> Holders(IReadOnlyList<string> files) => holders;
    }

    /// <summary>A file shaped like a company file: the marker, the company row and a few of the tables.</summary>
    private string CompanyFile(string name = "Demo Studio")
    {
        var path = Path.Combine(_dir, "Demo.adms");
        using var db = Open(path);
        Run(db, "PRAGMA journal_mode = WAL");
        Run(db, $"PRAGMA application_id = {CompanyFileMonitor.AppId}");
        Run(db, "PRAGMA user_version = 29");
        Run(db, "CREATE TABLE company_file (id INTEGER PRIMARY KEY, display_name TEXT, last_opened_at TEXT)");
        Run(db, $"INSERT INTO company_file VALUES (1, '{name}', '2026-10-19 09:00:00')");
        Run(db, "CREATE TABLE clients (id INTEGER PRIMARY KEY, name TEXT)");
        Run(db, "CREATE TABLE projects (id INTEGER PRIMARY KEY, name TEXT)");
        Run(db, "CREATE TABLE project_documents (project_id INTEGER, kind TEXT, body TEXT)");
        Run(db, "CREATE TABLE stored_files (key TEXT, bytes INTEGER, data BLOB)");
        Run(db, "INSERT INTO clients (name) VALUES ('A'), ('B')");
        Run(db, "INSERT INTO projects (name) VALUES ('P')");
        Run(db, "INSERT INTO project_documents VALUES (1, 'aqc.project', '" + new string('x', 5000) + "')");
        Run(db, "INSERT INTO stored_files VALUES ('k', 1234, zeroblob(1234))");
        return path;
    }

    private static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        return db;
    }

    private static void Run(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void A_company_file_is_measured_without_signing_in()
    {
        var path = CompanyFile();
        using var monitor = new CompanyFileMonitor(path, new NoHolders());
        var s = monitor.Sample();
        Assert.Null(s.Problem);
        Assert.True(s.IsCompanyFile);
        Assert.Equal("Demo Studio", s.Name);
        Assert.Equal(29, s.SchemaVersion);
        Assert.Equal((2L, 1L, 1L), (s.Counts["clients"], s.Counts["projects"], s.Counts["project_documents"]));
        Assert.Equal(5000, s.ProjectDocumentBytes);
        Assert.Equal(1234, s.StoredFileBytes);
        Assert.True(s.MainBytes > 0);
        Assert.True(s.PageCount > 0 && s.PageSize > 0);
        Assert.Equal("wal", s.JournalMode);
        Assert.True(s.DriveTotalBytes >= s.DriveFreeBytes && s.DriveFreeBytes > 0);
        Assert.Equal("2026-10-19 09:00:00", s.LastOpened);
    }

    [Fact]
    public void A_write_by_another_program_is_seen_once_and_the_monitors_own_reads_are_not()
    {
        var path = CompanyFile();
        using var monitor = new CompanyFileMonitor(path, new NoHolders());
        Assert.Equal(0, monitor.Sample().ChangesSinceLastLook);
        Assert.Equal(0, monitor.Sample().ChangesSinceLastLook);
        using (var other = Open(path))
        {
            Run(other, "INSERT INTO clients (name) VALUES ('C')");
            Run(other, "INSERT INTO clients (name) VALUES ('D')");
        }
        var s = monitor.Sample();
        Assert.Equal(1, s.ChangesSinceLastLook);
        Assert.Equal(1, s.ChangesPerMinute);
        Assert.Equal(4, s.Counts["clients"]);
        Assert.Equal(0, monitor.Sample().ChangesSinceLastLook);
    }

    [Fact]
    public void Free_room_inside_the_file_and_the_biggest_tables_are_reported()
    {
        var path = CompanyFile();
        using (var db = Open(path))
        {
            Run(db, "INSERT INTO project_documents VALUES (2, 'aqc.project', zeroblob(200000))");
            Run(db, "DELETE FROM project_documents WHERE project_id = 2");
        }
        using var monitor = new CompanyFileMonitor(path, new NoHolders());
        var s = monitor.Sample();
        Assert.True(s.FreePages > 0, "deleting a large row leaves free pages");
        Assert.True(s.FreeBytes > 100_000);
        if (s.Biggest.Count > 0)
            Assert.All(s.Biggest, t => Assert.True(t.Bytes > 0));
    }

    [Fact]
    public void Backups_beside_the_file_are_counted()
    {
        var path = CompanyFile();
        var folder = Path.Combine(_dir, "Demo backups");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "one.adms"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(folder, "two.adms"), new byte[500]);
        using var monitor = new CompanyFileMonitor(path, new NoHolders());
        var s = monitor.Sample();
        Assert.Equal((1500L, 2), (s.BackupBytes, s.BackupCount));
        Assert.NotNull(s.NewestBackup);
    }

    [Fact]
    public void A_protected_file_shows_only_what_can_be_seen_from_outside()
    {
        var path = Path.Combine(_dir, "Locked.adms");
        File.WriteAllBytes(path, [.. System.Text.Encoding.ASCII.GetBytes("AOSENC1\n"), .. new byte[500]]);
        using var monitor = new CompanyFileMonitor(path, new NoHolders());
        var s = monitor.Sample();
        Assert.True(s.Protected);
        Assert.Equal(508, s.MainBytes);
        Assert.Empty(s.Counts);
        Assert.Contains("protected", s.Problem);
    }

    [Fact]
    public void A_missing_or_foreign_file_is_a_message_not_a_crash()
    {
        using var missing = new CompanyFileMonitor(Path.Combine(_dir, "nope.adms"), new NoHolders());
        Assert.False(missing.Sample().Exists);
        var text = Path.Combine(_dir, "notes.adms");
        File.WriteAllText(text, "not a database at all, just text");
        using var notDb = new CompanyFileMonitor(text, new NoHolders());
        Assert.Contains("not a company file", notDb.Sample().Problem);
        var other = Path.Combine(_dir, "other.adms");
        using (var db = Open(other))
            Run(db, "CREATE TABLE t (x)");
        using var foreign = new CompanyFileMonitor(other, new NoHolders());
        var s = foreign.Sample();
        Assert.False(s.IsCompanyFile);
        Assert.Contains("not a company file", s.Problem);
    }

    [Fact]
    public void Programs_that_have_the_file_open_are_named_as_apps_with_their_load()
    {
        var path = CompanyFile();
        var me = Environment.ProcessId;
        using var monitor = new CompanyFileMonitor(path, new FixedHolders(new Holder("O-Studio", "Adms.Desktop", me + 100000), new Holder("O-CoreOp", "AQCCore", me + 100001)));
        var s = monitor.Sample();
        Assert.Equal(["O-Studio", "O-CoreOp"], s.OpenIn.Select(p => p.App).ToArray());
    }

    [Fact]
    public void Windows_reports_this_program_as_holding_a_file_it_has_open()
    {
        var file = Path.Combine(_dir, "held.bin");
        using var open = new FileStream(file, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
        var holders = new WindowsFileHolders().Holders([file]);
        Assert.Contains(holders, h => h.ProcessId == Environment.ProcessId);
    }

    [Theory]
    [InlineData("Adms.Desktop", "O-Studio")]
    [InlineData("AQCCore", "O-CoreOp")]
    [InlineData("aad", "O-Draft")]
    [InlineData("o-draft", "O-Draft")]
    [InlineData("OSAS", "OSAS")]
    [InlineData("notepad", "notepad")]
    public void Programs_are_named_the_way_the_suite_names_them(string program, string app) =>
        Assert.Equal(app, WindowsFileHolders.AppFor(program));
}
