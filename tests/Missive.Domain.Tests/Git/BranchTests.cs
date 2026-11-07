using Missive.Domain.Git;

namespace Missive.Domain.Tests.Git;

public class BranchTests
{
    [Theory]
    [InlineData("main")]
    [InlineData(" feature/login ")]
    [InlineData("release-1.2")]
    [InlineData("fix_#12")]
    public void BranchName_AcceptsGitBranchNames(string text)
    {
        Assert.True(BranchName.TryCreate(text, out var name, out _));
        Assert.Equal(text.Trim(), name.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("two words")]
    [InlineData("-f")]
    [InlineData("feature/")]
    [InlineData("a..b")]
    [InlineData("a:b")]
    [InlineData("topic.lock")]
    [InlineData(".hidden")]
    [InlineData("a/.b")]
    [InlineData("a@{1}")]
    [InlineData("HEAD")]
    [InlineData("@")]
    [InlineData("what?")]
    public void BranchName_RefusesWhatGitRefuses(string text)
    {
        Assert.False(BranchName.TryCreate(text, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void LocalName_DropsTheRemote()
    {
        var remote = new GitBranch("my/remote/feature/x", "my/remote", false, null, "abc", null, "");

        Assert.True(remote.IsRemote);
        Assert.Equal("feature/x", remote.LocalName);
        Assert.Equal("main", new GitBranch("main", null, true, "origin/main", "abc", null, "").LocalName);
    }
}
