using System.Text.Json;
using Osas.Core;

namespace Adms.Cloud;

/// <summary>
/// Per-machine sync preferences of one company: which of its projects sync and the last known revisions. <c>cloud-sync.json</c> holds one such
/// slice for every company file used on this PC, so project 1 of one company is never project 1 of another. A file written before this
/// (version 1, one flat list of projects) belongs to the first company that opens it.
/// </summary>
public sealed class CloudSyncStore
{
    static string StorePath => SuiteWire.LocalPath("cloud-sync.json");

    static string LegacyStorePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OSAS",
            "cloud-sync.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    readonly string _file;

    CloudSyncStore(string companyId, string file, Dictionary<long, ProjectSyncState> projects)
    {
        CompanyId = companyId;
        _file = file;
        Projects = projects;
    }

    /// <summary>The company this slice belongs to: its uuid, or the path of its file when it has none.</summary>
    public string CompanyId { get; }

    public Dictionary<long, ProjectSyncState> Projects { get; }

    public sealed class ProjectSyncState
    {
        public bool Enabled { get; set; }
        public long LastKnownCloudRevision { get; set; }
        public long LastLocalRevision { get; set; }
        public string? OspPath { get; set; }
        public string? OspProjectId { get; set; }
    }

    /// <summary>The file as stored: version 2 has <see cref="Companies"/>; version 1 had the projects at the top.</summary>
    sealed class FileModel
    {
        public int Version { get; set; } = 2;
        public Dictionary<string, Dictionary<long, ProjectSyncState>> Companies { get; set; } = new();
        public Dictionary<long, ProjectSyncState>? Projects { get; set; }
    }

    public static CloudSyncStore Load(string companyId, string? file = null)
    {
        file ??= StorePath;
        var model = Read(file) ?? (file == StorePath ? Read(LegacyStorePath) : null) ?? new FileModel();
        if (model.Projects is { Count: > 0 } && model.Companies.Count == 0 && !string.IsNullOrWhiteSpace(companyId))
        {
            // A version 1 file: its projects belong to the company that opens it first.
            model.Companies[companyId] = model.Projects;
        }

        model.Projects = null;
        model.Version = 2;
        var mine = model.Companies.TryGetValue(companyId, out var slice) ? slice : new Dictionary<long, ProjectSyncState>();
        var store = new CloudSyncStore(companyId, file, mine);
        if (!File.Exists(file) && mine.Count > 0)
            store.Save();
        return store;
    }

    static FileModel? Read(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<FileModel>(File.ReadAllText(file)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Write this company's slice and keep every other company's as it is in the file now.</summary>
    public void Save()
    {
        if (string.IsNullOrWhiteSpace(CompanyId))
            return;
        var model = Read(_file) ?? new FileModel();
        model.Projects = null;
        model.Version = 2;
        model.Companies[CompanyId] = Projects;
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, JsonSerializer.Serialize(model, Json));
    }

    public ProjectSyncState Get(long studioProjectId) =>
        Projects.TryGetValue(studioProjectId, out var s) ? s : new ProjectSyncState();
}
