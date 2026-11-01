using Adms.Cloud;
using Xunit;

namespace Osas.Core.Tests;

public class CloudSyncStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "osas-cloudstore-" + Guid.NewGuid().ToString("N")[..8]);
    private string File => Path.Combine(_dir, "cloud-sync.json");

    public CloudSyncStoreTests() => Directory.CreateDirectory(_dir);

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
    public void Two_companies_with_the_same_project_number_keep_their_own_switches()
    {
        var a = CloudSyncStore.Load("company-a", File);
        a.Projects[1] = new CloudSyncStore.ProjectSyncState { Enabled = true, LastKnownCloudRevision = 7 };
        a.Save();

        var b = CloudSyncStore.Load("company-b", File);
        Assert.False(b.Get(1).Enabled);
        b.Projects[1] = new CloudSyncStore.ProjectSyncState { Enabled = false, LastKnownCloudRevision = 2 };
        b.Save();

        var aAgain = CloudSyncStore.Load("company-a", File);
        Assert.True(aAgain.Get(1).Enabled);
        Assert.Equal(7, aAgain.Get(1).LastKnownCloudRevision);
        Assert.Equal(2, CloudSyncStore.Load("company-b", File).Get(1).LastKnownCloudRevision);
    }

    [Fact]
    public void Saving_one_company_leaves_the_other_as_it_is_in_the_file_now()
    {
        var a = CloudSyncStore.Load("company-a", File);
        var b = CloudSyncStore.Load("company-b", File);
        b.Projects[5] = new CloudSyncStore.ProjectSyncState { Enabled = true };
        b.Save();
        a.Projects[1] = new CloudSyncStore.ProjectSyncState { Enabled = true };
        a.Save();
        Assert.True(CloudSyncStore.Load("company-b", File).Get(5).Enabled);
        Assert.True(CloudSyncStore.Load("company-a", File).Get(1).Enabled);
    }

    [Fact]
    public void A_version_1_file_goes_to_the_first_company_that_opens_it()
    {
        System.IO.File.WriteAllText(File, "{\"Projects\":{\"3\":{\"Enabled\":true,\"LastKnownCloudRevision\":9}}}");
        var first = CloudSyncStore.Load("company-a", File);
        Assert.True(first.Get(3).Enabled);
        Assert.Equal(9, first.Get(3).LastKnownCloudRevision);
        first.Save();
        Assert.False(CloudSyncStore.Load("company-b", File).Get(3).Enabled);
        Assert.True(CloudSyncStore.Load("company-a", File).Get(3).Enabled);
    }

    [Fact]
    public void Without_a_company_nothing_is_saved()
    {
        var none = CloudSyncStore.Load("", File);
        none.Projects[1] = new CloudSyncStore.ProjectSyncState { Enabled = true };
        none.Save();
        Assert.False(System.IO.File.Exists(File));
    }

    [Fact]
    public void A_damaged_file_reads_as_empty()
    {
        System.IO.File.WriteAllText(File, "{ not json");
        Assert.False(CloudSyncStore.Load("company-a", File).Get(1).Enabled);
    }
}
