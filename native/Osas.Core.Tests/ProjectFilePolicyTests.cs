using Xunit;

namespace Osas.Core.Tests;

public sealed class ProjectFilePolicyTests
{
    [Fact]
    public void Project_files_are_not_pushed_unless_the_machine_opts_in()
    {
        var old = Environment.GetEnvironmentVariable(ProjectFilePolicy.EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(ProjectFilePolicy.EnvVar, null);
            Assert.False(ProjectFilePolicy.PushToCloud);
            Environment.SetEnvironmentVariable(ProjectFilePolicy.EnvVar, "0");
            Assert.False(ProjectFilePolicy.PushToCloud);
            Environment.SetEnvironmentVariable(ProjectFilePolicy.EnvVar, "1");
            Assert.True(ProjectFilePolicy.PushToCloud);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProjectFilePolicy.EnvVar, old);
        }
    }
}
