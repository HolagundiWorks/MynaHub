using Osas.Core;
using Xunit;

namespace Osas.Core.Tests;

public class AppCatalogTests
{
    private sealed class FakeComputer : IProbe
    {
        public Dictionary<string, string> Registry = new();
        public HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Versions = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Path = new();
        public Dictionary<SuiteApp, string> Saved = new();

        public string? RegistryValue(string key, string name) => Registry.GetValueOrDefault(key + "|" + name);
        public bool FileExists(string path) => Files.Contains(path);
        public string? FileVersion(string path) => Versions.GetValueOrDefault(path);
        public string? ExpandedFolder(string name) => name.Replace("%LOCALAPPDATA%", @"C:\Users\me\AppData\Local");
        public IEnumerable<string> PathFolders() => Path;
        public string? SavedPath(SuiteApp app) => Saved.GetValueOrDefault(app);
    }

    private static AppInfo Of(FakeComputer c, SuiteApp app) => new AppCatalog(c).Detect().Single(a => a.App == app);

    [Fact]
    public void An_app_the_installer_registered_is_found_with_its_version()
    {
        var pc = new FakeComputer();
        pc.Registry[@"HKCU\Software\Human Centric Works\OStudio|InstallDir"] = @"D:\Apps\O-Studio";
        pc.Files.Add(@"D:\Apps\O-Studio\O-Studio.exe");
        pc.Versions[@"D:\Apps\O-Studio\O-Studio.exe"] = "0.1.0";
        var studio = Of(pc, SuiteApp.Studio);
        Assert.True(studio.Installed);
        Assert.Equal(@"D:\Apps\O-Studio\O-Studio.exe", studio.ExePath);
        Assert.Equal("0.1.0", studio.Version);
        Assert.Equal("registered by its installer", studio.Source);
        Assert.False(Of(pc, SuiteApp.Metra).Installed);
        Assert.False(Of(pc, SuiteApp.Draft).Installed);
    }

    [Fact]
    public void An_install_that_still_has_the_earlier_program_names_is_found()
    {
        var pc = new FakeComputer();
        pc.Registry[@"HKCU\Software\Human Centric Works\OStudio|InstallDir"] = @"D:\Apps\O-Studio";
        pc.Files.Add(@"D:\Apps\O-Studio\Adms.Desktop.exe");
        Assert.Equal(@"D:\Apps\O-Studio\Adms.Desktop.exe", Of(pc, SuiteApp.Studio).ExePath);
        pc.Files.Add(@"D:\Apps\O-Studio\O-Studio.exe");
        Assert.Equal(@"D:\Apps\O-Studio\O-Studio.exe", Of(pc, SuiteApp.Studio).ExePath);
        var old = new FakeComputer();
        old.Files.Add(@"C:\Users\me\AppData\Local\Programs\AQC-Core\AQCCore.exe");
        Assert.Equal(@"C:\Users\me\AppData\Local\Programs\AQC-Core\AQCCore.exe", Of(old, SuiteApp.Metra).ExePath);
    }

    [Fact]
    public void The_usual_folder_and_the_old_folder_names_are_tried_and_the_path_is_the_last_resort()
    {
        var pc = new FakeComputer();
        pc.Files.Add(@"C:\Users\me\AppData\Local\Programs\AQC-Core\AQCCore.exe");
        Assert.Equal("in the usual folder", Of(pc, SuiteApp.Metra).Source);
        pc.Files.Add(@"C:\Users\me\AppData\Local\Programs\O-CoreOp\O-CoreOp.exe");
        Assert.Equal(@"C:\Users\me\AppData\Local\Programs\O-CoreOp\O-CoreOp.exe", Of(pc, SuiteApp.Metra).ExePath);
        pc.Path.Add(@"C:\Tools");
        pc.Files.Add(@"C:\Tools\aad.exe");
        var draft = Of(pc, SuiteApp.Draft);
        Assert.Equal((@"C:\Tools\aad.exe", "on the path"), (draft.ExePath, draft.Source));
    }

    [Fact]
    public void A_path_the_person_chose_wins_when_it_exists_and_is_ignored_when_it_does_not()
    {
        var pc = new FakeComputer();
        pc.Files.Add(@"C:\Users\me\AppData\Local\Programs\O-Draft\aad.exe");
        pc.Saved[SuiteApp.Draft] = @"E:\CAD\aad.exe";
        Assert.Equal("in the usual folder", Of(pc, SuiteApp.Draft).Source);
        pc.Files.Add(@"E:\CAD\aad.exe");
        var draft = Of(pc, SuiteApp.Draft);
        Assert.Equal((@"E:\CAD\aad.exe", "chosen by you"), (draft.ExePath, draft.Source));
        pc.Saved[SuiteApp.Draft] = @"E:\CAD\notes.txt";
        Assert.Equal("in the usual folder", Of(pc, SuiteApp.Draft).Source);
    }

    [Fact]
    public void An_install_in_the_folder_used_before_the_rename_is_still_found()
    {
        var pc = new FakeComputer();
        pc.Files.Add(@"C:\Users\me\AppData\Local\Programs\O-Metra\AQCCore.exe");
        Assert.True(Of(pc, SuiteApp.Metra).Installed);
    }

    [Fact]
    public void O_Draft_is_found_by_its_new_program_name_and_an_older_install_by_the_old_one()
    {
        var pc = new FakeComputer();
        pc.Files.Add(@"C:\Users\me\AppData\Local\Programs\AAD\aad.exe");
        Assert.Equal(@"C:\Users\me\AppData\Local\Programs\AAD\aad.exe", Of(pc, SuiteApp.Draft).ExePath);
        pc.Files.Add(@"C:\Users\me\AppData\Local\Programs\O-Draft\o-draft.exe");
        Assert.Equal(@"C:\Users\me\AppData\Local\Programs\O-Draft\o-draft.exe", Of(pc, SuiteApp.Draft).ExePath);
    }

    [Fact]
    public void The_registry_can_point_at_a_missing_folder_without_breaking_anything()
    {
        var pc = new FakeComputer();
        pc.Registry[@"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\{A8F3C2E1-9B4D-4E6A-8C1F-2D7E5A9B0C3D}_is1|InstallLocation"] = "\"D:\\Gone\\O-CoreOp\\\"";
        Assert.False(Of(pc, SuiteApp.Metra).Installed);
    }

    [Fact]
    public void Saved_paths_round_trip_and_refuse_anything_that_is_not_an_absolute_exe()
    {
        var file = Path.Combine(Path.GetTempPath(), "osas-test-" + Guid.NewGuid() + ".json");
        try
        {
            var saved = new SavedPaths(file);
            Assert.Empty(saved.Load());
            Assert.True(saved.Save(SuiteApp.Metra, @"D:\Apps\AQCCore.exe"));
            Assert.False(saved.Save(SuiteApp.Metra, @"relative\AQCCore.exe"));
            Assert.False(saved.Save(SuiteApp.Metra, @"D:\Apps\notes.txt"));
            Assert.False(saved.Save(SuiteApp.Metra, ""));
            Assert.Equal(@"D:\Apps\AQCCore.exe", saved.Load()["Metra"]);
            File.WriteAllText(file, "{ not json");
            Assert.Empty(saved.Load());
            File.WriteAllText(file, "{\"Draft\":\"C:\\\\x\\\\evil.bat\",\"Studio\":\"C:\\\\x\\\\ok.exe\"}");
            Assert.Equal(new[] { "Studio" }, saved.Load().Keys.ToArray());
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Every_app_has_a_name_a_purpose_and_a_program_name()
    {
        var all = new AppCatalog(new FakeComputer()).Detect();
        Assert.Equal(new[] { "MynaHub", "MynaOps", "MynaCAD" }, all.Select(a => a.Name).ToArray());
        Assert.All(all, a => Assert.False(string.IsNullOrWhiteSpace(a.Purpose) || string.IsNullOrWhiteSpace(a.ExeName)));
    }
}
