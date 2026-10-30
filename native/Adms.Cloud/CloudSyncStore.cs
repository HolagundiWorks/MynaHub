using System.Text.Json;

namespace Adms.Cloud;

/// <summary>Per-machine sync preferences (which Studio projects sync, last known cloud revision).</summary>
public sealed class CloudSyncStore
{
    static string StorePath =>
        System.IO.Path.Combine(
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
            if (!File.Exists(StorePath))
                return new CloudSyncStore();
            return JsonSerializer.Deserialize<CloudSyncStore>(File.ReadAllText(StorePath)) ?? new CloudSyncStore();
        }
        catch
        {
            return new CloudSyncStore();
        }
    }

    public void Save()
    {
        var dir = System.IO.Path.GetDirectoryName(StorePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(StorePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public ProjectSyncState Get(long studioProjectId) =>
        Projects.TryGetValue(studioProjectId, out var s) ? s : new ProjectSyncState();
}
