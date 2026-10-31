using System.Text.Json;
using Osas.Core;

namespace Adms.Cloud;

/// <summary>Per-machine sync preferences (which Studio projects sync, last known cloud revision).</summary>
public sealed class CloudSyncStore
{
    static string StorePath => SuiteWire.LocalPath("cloud-sync.json");

    static string LegacyStorePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OSAS",
            "cloud-sync.json");

    public Dictionary<long, ProjectSyncState> Projects { get; set; } = new();

    public sealed class ProjectSyncState
    {
        public bool Enabled { get; set; }
        public long LastKnownCloudRevision { get; set; }
        public long LastLocalRevision { get; set; }
        public string? OspPath { get; set; }
        public string? OspProjectId { get; set; }
    }

    public static CloudSyncStore Load()
    {
        try
        {
            if (File.Exists(StorePath))
                return JsonSerializer.Deserialize<CloudSyncStore>(File.ReadAllText(StorePath)) ?? new CloudSyncStore();
            if (File.Exists(LegacyStorePath))
            {
                var migrated = JsonSerializer.Deserialize<CloudSyncStore>(File.ReadAllText(LegacyStorePath)) ?? new CloudSyncStore();
                migrated.Save();
                return migrated;
            }
            return new CloudSyncStore();
        }
        catch
        {
            return new CloudSyncStore();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(SuiteWire.LocalDirectory);
        File.WriteAllText(StorePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public ProjectSyncState Get(long studioProjectId) =>
        Projects.TryGetValue(studioProjectId, out var s) ? s : new ProjectSyncState();
}
