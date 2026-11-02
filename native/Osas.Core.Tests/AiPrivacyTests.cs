using Osas.Core.Suite;
using Xunit;

namespace Osas.Core.Tests;

public sealed class AiPrivacyTests
{
    [Fact]
    public void Defaults_are_the_careful_ones()
    {
        var p = new AiPrivacy();
        Assert.False(p.ShareProjectName);
        Assert.False(p.RememberConversation);
        Assert.True(p.AllowAttachments);
    }

    [Fact]
    public void Saved_choices_come_back_and_a_bad_file_gives_defaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aipriv-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, "ai-privacy.json");
        try
        {
            new AiPrivacy { ShareProjectName = true, RememberConversation = true, AllowAttachments = false }.Save(file);
            var back = AiPrivacy.Load(file);
            Assert.True(back.ShareProjectName);
            Assert.True(back.RememberConversation);
            Assert.False(back.AllowAttachments);
            File.WriteAllText(file, "{ not json");
            Assert.False(AiPrivacy.Load(file).ShareProjectName);
            Assert.False(AiPrivacy.Load(Path.Combine(dir, "missing.json")).ShareProjectName);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
