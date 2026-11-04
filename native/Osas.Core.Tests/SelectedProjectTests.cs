using Osas.Core;
using Xunit;

namespace Osas.Core.Tests;

public class SelectedProjectTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "osas-selproj-" + Guid.NewGuid().ToString("N")[..8]);
    private string File => Path.Combine(_dir, "selected-project.json");
    private const string Company = @"C:\Practice\Acme.osas";

    private static SelectedProject Project(string company = Company) =>
        new(company, 17, "P-2026-017", "Rao Residence", @"C:\Practice\projects\p17\project.osp", "anita", DateTimeOffset.Parse("2026-11-01T06:10:00Z"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void A_locked_project_reads_back_for_its_company()
    {
        SelectedProjectStore.Lock(Project(), File);
        var read = SelectedProjectStore.Read(Company, File);
        Assert.Equal(Project(), read);
    }

    [Fact]
    public void Another_company_or_no_company_sees_no_selection()
    {
        SelectedProjectStore.Lock(Project(), File);
        Assert.Null(SelectedProjectStore.Read(@"C:\Practice\Other.osas", File));
        Assert.Null(SelectedProjectStore.Read("", File));
        Assert.Null(SelectedProjectStore.Read(null, File));
    }

    [Fact]
    public void The_company_path_is_compared_without_regard_to_case()
    {
        SelectedProjectStore.Lock(Project(), File);
        Assert.NotNull(SelectedProjectStore.Read(Company.ToUpperInvariant(), File));
    }

    [Fact]
    public void A_missing_damaged_or_foreign_file_reads_as_none()
    {
        Assert.Null(SelectedProjectStore.Read(Company, File));
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File, "{ not json");
        Assert.Null(SelectedProjectStore.Read(Company, File));
        System.IO.File.WriteAllText(File, "{\"format\":\"something-else\",\"company\":\"" + Company.Replace("\\", "\\\\") + "\",\"project\":{\"id\":1,\"osp\":\"x\"}}");
        Assert.Null(SelectedProjectStore.Read(Company, File));
    }

    [Fact]
    public void The_environment_carries_the_project_only_while_one_is_locked()
    {
        Assert.Empty(SelectedProjectStore.EnvironmentFor(Company, File));
        SelectedProjectStore.Lock(Project(), File);
        var env = SelectedProjectStore.EnvironmentFor(Company, File);
        Assert.Equal("17", env[SuiteWire.EnvProjectId]);
        Assert.Equal(Project().OspPath, env[SuiteWire.EnvProjectOsp]);
        Assert.Equal("Rao Residence", env[SuiteWire.EnvProjectName]);
        SelectedProjectStore.Clear(File);
        Assert.Empty(SelectedProjectStore.EnvironmentFor(Company, File));
    }

    [Fact]
    public void Locking_again_replaces_the_selection()
    {
        SelectedProjectStore.Lock(Project(), File);
        SelectedProjectStore.Lock(Project() with { ProjectId = 18, Name = "Next" }, File);
        Assert.Equal(18, SelectedProjectStore.Read(Company, File)!.ProjectId);
    }
}
