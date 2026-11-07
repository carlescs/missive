using Missive.Application.Git;
using Missive.Domain.Git;
using Missive.Infrastructure.Git;

namespace Missive.Infrastructure.Tests.Git;

/// <summary>Runs <see cref="GitCliService"/> against real temporary repositories.</summary>
public sealed class GitCliServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly GitCliService _git = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task MissingFolder_IsReported()
    {
        var result = await _git.GetStatusAsync(_temp.Combine("missing"));

        Assert.Equal(GitErrors.WorkspaceNotFoundCode, result.Error?.Code);
    }

    [Fact]
    public async Task FolderOutsideARepository_IsReported()
    {
        var result = await _git.GetStatusAsync(_temp.Path);

        Assert.Equal(GitErrors.NotARepositoryCode, result.Error?.Code);
    }

    [Fact]
    public async Task Init_CreatesARepositoryThatIgnoresTheLocalFolder()
    {
        var workspace = _temp.Combine("new");
        Write(workspace, "health.yaml", "name: Health\n");
        Write(workspace, ".missive/secrets.yaml", "apiToken: secret\n");

        var result = await _git.InitAsync(workspace);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(Directory.Exists(Path.Combine(workspace, ".git")));
        Assert.Equal(".missive/\n", File.ReadAllText(Path.Combine(workspace, ".gitignore")));
        var status = await _git.GetStatusAsync(workspace);
        Assert.True(status.IsSuccess, status.Error?.Message);
        Assert.Equal([".gitignore", "health.yaml"], status.Value.Changes.Select(c => c.Path).Order());
    }

    [Fact]
    public async Task Init_AppendsToAnExistingGitIgnore()
    {
        var workspace = _temp.Combine("new");
        Write(workspace, ".gitignore", "*.log");

        var result = await _git.InitAsync(workspace);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("*.log\n.missive/\n", File.ReadAllText(Path.Combine(workspace, ".gitignore")));
    }

    [Fact]
    public async Task Init_LeavesAGitIgnoreThatAlreadyIgnoresTheLocalFolder()
    {
        var workspace = _temp.Combine("new");
        Write(workspace, ".gitignore", "/.missive\r\nbin/\r\n");

        var result = await _git.InitAsync(workspace);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("/.missive\r\nbin/\r\n", File.ReadAllText(Path.Combine(workspace, ".gitignore")));
    }

    [Fact]
    public async Task Init_InsideARepository_IsRefused()
    {
        var repository = InitRepository("repo");
        var workspace = Path.Combine(repository, "api");
        Directory.CreateDirectory(workspace);

        var result = await _git.InitAsync(workspace);

        Assert.Equal(GitErrors.AlreadyARepositoryCode, result.Error?.Code);
        Assert.False(Directory.Exists(Path.Combine(workspace, ".git")));
        Assert.False(File.Exists(Path.Combine(workspace, ".gitignore")));
    }

    [Fact]
    public async Task Init_MissingFolder_IsReported()
    {
        var result = await _git.InitAsync(_temp.Combine("missing"));

        Assert.Equal(GitErrors.WorkspaceNotFoundCode, result.Error?.Code);
    }

    [Fact]
    public async Task MissingGitExecutable_IsReported()
    {
        var git = new GitCliService("missive-no-such-git-executable");

        var result = await git.GetStatusAsync(_temp.Path);

        Assert.Equal(GitErrors.GitNotInstalledCode, result.Error?.Code);
    }

    [Fact]
    public async Task Status_ListsWorkspaceChangesRelativeToTheWorkspace()
    {
        var repository = InitRepository("repo");
        var workspace = Path.Combine(repository, "api");
        Write(workspace, "health.yaml", "name: Health\n");
        Write(workspace, "old.yaml", "name: Old\n");
        Write(repository, "README.md", "# Repo\n");
        TestGit.CommitAll(repository, "Initial");

        Write(workspace, "health.yaml", "name: Health check\n");
        File.Delete(Path.Combine(workspace, "old.yaml"));
        Write(workspace, "users/get user.yaml", "name: Get user\n");
        Write(workspace, ".missive/secrets.yaml", "apiToken: secret\n");
        Write(repository, "README.md", "# Changed outside the workspace\n");

        var result = await _git.GetStatusAsync(workspace);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var status = result.Value;
        Assert.Equal("main", status.Branch);
        Assert.Null(status.Upstream);
        Assert.False(status.IsMerging);
        Assert.Equal(
            new[]
            {
                new GitChange("health.yaml", GitFileStatus.Modified),
                new GitChange("old.yaml", GitFileStatus.Deleted),
                new GitChange("users/get user.yaml", GitFileStatus.Untracked),
            },
            status.Changes);
    }

    [Fact]
    public async Task Commit_CommitsEveryWorkspaceChange_ButNotLocalOrOutsideFiles()
    {
        var repository = InitRepository("repo");
        var workspace = Path.Combine(repository, "api");
        Write(workspace, "users/create-user.yaml", "name: Create user\n");
        Write(workspace, ".missive/secrets.yaml", "apiToken: secret\n");
        Write(repository, "outside.txt", "not part of the workspace\n");

        var result = await _git.CommitAsync(workspace, Message("Add create user"), paths: null);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(TestGit.Head(repository), result.Value);
        Assert.Equal("api/users/create-user.yaml\n", CommittedFiles(repository));
        Assert.Equal("Add create user", TestGit.Run(repository, "log", "-1", "--format=%B").Trim());
        var status = await _git.GetStatusAsync(workspace);
        Assert.Empty(status.Value.Changes);
    }

    [Fact]
    public async Task Discard_RevertsEditsAndDeletions_AndRemovesNewFiles()
    {
        var repository = InitRepository("repo");
        Write(repository, "edited.yaml", "name: Edited\n");
        Write(repository, "deleted.yaml", "name: Deleted\n");
        Write(repository, "old.yaml", "name: Old\n");
        Write(repository, "keep.yaml", "name: Keep\n");
        TestGit.CommitAll(repository, "Initial");
        Write(repository, "edited.yaml", "name: Changed\n");
        File.Delete(Path.Combine(repository, "deleted.yaml"));
        TestGit.Run(repository, "mv", "old.yaml", "renamed.yaml");
        Write(repository, "staged.yaml", "name: Staged\n");
        TestGit.Run(repository, "add", "staged.yaml");
        Write(repository, "untracked/new.yaml", "name: New\n");
        Write(repository, "keep.yaml", "name: Keep changed\n");

        var status = (await _git.GetStatusAsync(repository)).Value;
        var result = await _git.DiscardChangesAsync(repository, status.Changes.Where(c => c.Path != "keep.yaml").ToList());

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("name: Edited\n", File.ReadAllText(Path.Combine(repository, "edited.yaml")));
        Assert.True(File.Exists(Path.Combine(repository, "deleted.yaml")));
        Assert.True(File.Exists(Path.Combine(repository, "old.yaml")));
        Assert.False(File.Exists(Path.Combine(repository, "renamed.yaml")));
        Assert.False(File.Exists(Path.Combine(repository, "staged.yaml")));
        Assert.False(File.Exists(Path.Combine(repository, "untracked", "new.yaml")));
        var after = (await _git.GetStatusAsync(repository)).Value;
        Assert.Equal(new[] { new GitChange("keep.yaml", GitFileStatus.Modified) }, after.Changes);
    }

    [Fact]
    public async Task Discard_RemovesAnUntrackedFileEvenAfterRestoringItsIgnoreRule()
    {
        var repository = InitRepository("repo");
        Write(repository, ".gitignore", "*.log\n");
        TestGit.CommitAll(repository, "Ignore logs");
        Write(repository, ".gitignore", string.Empty);
        Write(repository, "debug.log", "debug\n");

        var status = (await _git.GetStatusAsync(repository)).Value;
        var result = await _git.DiscardChangesAsync(repository, status.Changes);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.False(File.Exists(Path.Combine(repository, "debug.log")));
        Assert.Equal("*.log\n", File.ReadAllText(Path.Combine(repository, ".gitignore")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Commit_WithAGitIgnoredLocalFolder_Succeeds(bool selectedPaths)
    {
        // The usual setup: Missive's init adds .missive/ to .gitignore, and the folder holds secrets.
        var workspace = _temp.Combine("repo");
        Write(workspace, "a.yaml", "name: A\n");
        Write(workspace, ".missive/secrets.yaml", "token: secret\n");
        Assert.True((await _git.InitAsync(workspace)).IsSuccess);
        TestGit.Configure(workspace);

        var result = await _git.CommitAsync(workspace, Message("Add A"), selectedPaths ? ["a.yaml", ".gitignore"] : null);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(".gitignore\na.yaml\n", CommittedFiles(workspace));
    }

    [Fact]
    public async Task Commit_WithAnUnignoredLocalFolder_LeavesItOut()
    {
        var repository = InitRepository("repo");
        Write(repository, "a.yaml", "name: A\n");
        Write(repository, ".missive/secrets.yaml", "token: secret\n");

        var result = await _git.CommitAsync(repository, Message("Add A"), null);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("a.yaml\n", CommittedFiles(repository));
    }

    [Fact]
    public async Task Commit_WithPaths_CommitsOnlyThosePaths()
    {
        var repository = InitRepository("repo");
        Write(repository, "a.yaml", "name: A\n");
        Write(repository, "b.yaml", "name: B\n");

        var result = await _git.CommitAsync(repository, Message("Add A"), ["a.yaml"]);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("a.yaml\n", CommittedFiles(repository));
        var status = await _git.GetStatusAsync(repository);
        Assert.Equal(new[] { new GitChange("b.yaml", GitFileStatus.Untracked) }, status.Value.Changes);
    }

    [Fact]
    public async Task Push_WithSetUpstream_PublishesTheBranch()
    {
        var remote = InitBareRemote();
        var repository = InitRepository("repo");
        TestGit.Run(repository, "remote", "add", "origin", remote);
        Write(repository, "a.yaml", "name: A\n");
        TestGit.CommitAll(repository, "Add A");

        var push = await _git.PushAsync(repository, setUpstream: true);

        Assert.True(push.IsSuccess, push.Error?.Message);
        var status = await _git.GetStatusAsync(repository);
        Assert.Equal("origin/main", status.Value.Upstream);
        Assert.Equal(0, status.Value.Ahead);
        Assert.Equal(TestGit.Head(repository), TestGit.Run(remote, "rev-parse", "main").Trim());
    }

    [Fact]
    public async Task PushThenPull_SyncsTwoClones()
    {
        var (alice, bob) = InitTwoClones();
        Write(alice, "a.yaml", "name: A changed\n");
        TestGit.CommitAll(alice, "Change A");

        var push = await _git.PushAsync(alice, setUpstream: false);
        var pull = await _git.PullAsync(bob);
        var again = await _git.PullAsync(bob);

        Assert.True(push.IsSuccess, push.Error?.Message);
        Assert.True(pull.IsSuccess, pull.Error?.Message);
        Assert.Equal(PullOutcome.Updated, pull.Value);
        Assert.True(again.IsSuccess, again.Error?.Message);
        Assert.Equal(PullOutcome.UpToDate, again.Value);
        Assert.Equal("name: A changed\n", File.ReadAllText(Path.Combine(bob, "a.yaml")));
    }

    [Fact]
    public async Task Push_BehindTheRemote_IsRejected()
    {
        var (alice, bob) = InitTwoClones();
        Write(alice, "a.yaml", "name: From Alice\n");
        TestGit.CommitAll(alice, "Alice");
        TestGit.Run(alice, "push", "--quiet");
        Write(bob, "b.yaml", "name: From Bob\n");
        TestGit.CommitAll(bob, "Bob");

        var push = await _git.PushAsync(bob, setUpstream: false);

        Assert.Equal(GitErrors.PushRejectedCode, push.Error?.Code);
    }

    [Fact]
    public async Task Pull_WithConflictingChanges_LeavesTheConflictForTheUser()
    {
        var (alice, bob) = InitTwoClones();
        Write(alice, "a.yaml", "name: From Alice\n");
        TestGit.CommitAll(alice, "Alice");
        TestGit.Run(alice, "push", "--quiet");
        Write(bob, "a.yaml", "name: From Bob\n");
        TestGit.CommitAll(bob, "Bob");

        var pull = await _git.PullAsync(bob);

        Assert.True(pull.IsSuccess, pull.Error?.Message);
        Assert.Equal(PullOutcome.Conflicted, pull.Value);
        var status = (await _git.GetStatusAsync(bob)).Value;
        Assert.True(status.IsMerging);
        Assert.Equal(new[] { new GitChange("a.yaml", GitFileStatus.Conflicted) }, status.Changes);
        Assert.Contains("<<<<<<<", File.ReadAllText(Path.Combine(bob, "a.yaml")));
    }

    [Fact]
    public async Task Conflict_ReportsAllVersionsAndTheMergeMessage()
    {
        var bob = await ConflictedMergeAsync();

        var status = (await _git.GetStatusAsync(bob)).Value;
        var conflict = await _git.GetConflictAsync(bob, "a.yaml");

        Assert.StartsWith("Merge branch", status.MergeMessage);
        Assert.DoesNotContain("#", status.MergeMessage);
        Assert.True(conflict.IsSuccess, conflict.Error?.Message);
        Assert.Equal("name: A\n", conflict.Value.Base);
        Assert.Equal("name: From Bob\n", conflict.Value.Ours);
        Assert.Equal("name: From Alice\n", conflict.Value.Theirs);
        Assert.True(conflict.Value.HasConflictMarkers);

        var deletedByThem = (await _git.GetConflictAsync(bob, "d.yaml")).Value;
        Assert.Equal("name: Changed by Bob\n", deletedByThem.Ours);
        Assert.Null(deletedByThem.Theirs);
    }

    [Fact]
    public async Task Conflict_WithTheFileReplacedByASymbolicLink_DoesNotFollowIt()
    {
        var bob = await ConflictedMergeAsync();
        var secret = _temp.Combine("secret.txt");
        File.WriteAllText(secret, "password\n");
        File.Delete(Path.Combine(bob, "a.yaml"));
        if (!TryCreateSymbolicLink(Path.Combine(bob, "a.yaml"), secret))
        {
            return; // Creating links needs extra rights on Windows.
        }

        var conflict = await _git.GetConflictAsync(bob, "a.yaml");

        Assert.True(conflict.IsSuccess, conflict.Error?.Message);
        Assert.Equal(secret, conflict.Value.Working);
    }

    [Fact]
    public async Task ResolvingEveryFile_ThenCommitting_CompletesTheMerge()
    {
        var bob = await ConflictedMergeAsync();

        var theirs = await _git.ResolveConflictAsync(bob, "a.yaml", ConflictResolution.Theirs);
        var deleted = await _git.ResolveConflictAsync(bob, "d.yaml", ConflictResolution.Theirs);

        Assert.True(theirs.IsSuccess, theirs.Error?.Message);
        Assert.True(deleted.IsSuccess, deleted.Error?.Message);
        Assert.Equal("name: From Alice\n", File.ReadAllText(Path.Combine(bob, "a.yaml")));
        Assert.False(File.Exists(Path.Combine(bob, "d.yaml")));
        var status = (await _git.GetStatusAsync(bob)).Value;
        Assert.True(status.IsMerging);
        Assert.False(status.HasConflicts);

        var commit = await _git.CommitAsync(bob, Message(status.MergeMessage!), paths: null);

        Assert.True(commit.IsSuccess, commit.Error?.Message);
        var after = (await _git.GetStatusAsync(bob)).Value;
        Assert.False(after.IsMerging);
        Assert.Equal(2, TestGit.Run(bob, "rev-list", "--parents", "-n", "1", "HEAD").Trim().Split(' ').Length - 1);
    }

    [Fact]
    public async Task Resolve_Ours_And_Edited()
    {
        var bob = await ConflictedMergeAsync();
        Write(bob, "a.yaml", "name: From Bob and Alice\n");

        var edited = await _git.ResolveConflictAsync(bob, "a.yaml", ConflictResolution.Edited);
        var ours = await _git.ResolveConflictAsync(bob, "d.yaml", ConflictResolution.Ours);

        Assert.True(edited.IsSuccess, edited.Error?.Message);
        Assert.True(ours.IsSuccess, ours.Error?.Message);
        Assert.Equal("name: Changed by Bob\n", File.ReadAllText(Path.Combine(bob, "d.yaml")));
        Assert.Equal("name: From Bob and Alice\n", TestGit.Run(bob, "show", ":a.yaml"));
        Assert.False((await _git.GetStatusAsync(bob)).Value.HasConflicts);
    }

    [Fact]
    public async Task AbortMerge_RestoresTheBranch()
    {
        var bob = await ConflictedMergeAsync();

        var abort = await _git.AbortMergeAsync(bob);

        Assert.True(abort.IsSuccess, abort.Error?.Message);
        var status = (await _git.GetStatusAsync(bob)).Value;
        Assert.False(status.IsMerging);
        Assert.Empty(status.Changes);
        Assert.Null(status.MergeMessage);
        Assert.Equal("name: From Bob\n", File.ReadAllText(Path.Combine(bob, "a.yaml")));
    }

    /// <summary>
    /// Bob's clone after a pull that conflicts on <c>a.yaml</c> (both changed it) and
    /// <c>d.yaml</c> (Alice deleted it, Bob changed it).
    /// </summary>
    [Fact]
    public async Task Diff_ReadsTheCommittedAndWorkingVersions()
    {
        var repository = InitRepository("repo");
        var workspace = Path.Combine(repository, "api");
        Write(workspace, "users/get.yaml", "name: Get\n");
        Write(workspace, "old.yaml", "name: Old\n");
        TestGit.CommitAll(repository, "Initial");
        Write(workspace, "users/get.yaml", "name: Get user\n");
        Write(workspace, "new.yaml", "name: New\n");
        File.Delete(Path.Combine(workspace, "old.yaml"));

        var modified = await _git.GetFileDiffAsync(workspace, new GitChange("users/get.yaml", GitFileStatus.Modified));
        var added = await _git.GetFileDiffAsync(workspace, new GitChange("new.yaml", GitFileStatus.Untracked));
        var deleted = await _git.GetFileDiffAsync(workspace, new GitChange("old.yaml", GitFileStatus.Deleted));

        Assert.True(modified.IsSuccess, modified.Error?.Message);
        Assert.Equal("name: Get\n", modified.Value.Original);
        Assert.Equal("name: Get user\n", modified.Value.Modified);
        Assert.Null(added.Value.Original);
        Assert.Equal("name: New\n", added.Value.Modified);
        Assert.Equal("name: Old\n", deleted.Value.Original);
        Assert.Null(deleted.Value.Modified);
    }

    [Fact]
    public async Task Diff_OfARename_ReadsTheOriginalAtItsOldPath()
    {
        var workspace = InitRepository("repo");
        Write(workspace, "a.yaml", "name: A\n");
        TestGit.CommitAll(workspace, "Initial");
        TestGit.Run(workspace, "mv", "a.yaml", "b.yaml");
        Write(workspace, "b.yaml", "name: B\n");

        var diff = await _git.GetFileDiffAsync(workspace, new GitChange("b.yaml", GitFileStatus.Renamed, "a.yaml"));

        Assert.True(diff.IsSuccess, diff.Error?.Message);
        Assert.Equal("name: A\n", diff.Value.Original);
        Assert.Equal("name: B\n", diff.Value.Modified);
    }

    [Fact]
    public async Task Diff_BeforeTheFirstCommit_HasNoOriginal()
    {
        var workspace = InitRepository("repo");
        Write(workspace, "a.yaml", "name: A\n");

        var diff = await _git.GetFileDiffAsync(workspace, new GitChange("a.yaml", GitFileStatus.Modified));

        Assert.True(diff.IsSuccess, diff.Error?.Message);
        Assert.Null(diff.Value.Original);
        Assert.Equal("name: A\n", diff.Value.Modified);
    }

    [Fact]
    public async Task Diff_OfABinaryFile_HasNoText()
    {
        var workspace = InitRepository("repo");
        File.WriteAllBytes(Path.Combine(workspace, "logo.png"), [0x89, 0x50, 0x00, 0x01]);

        var diff = await _git.GetFileDiffAsync(workspace, new GitChange("logo.png", GitFileStatus.Untracked));

        Assert.True(diff.Value.IsBinary);
        Assert.Null(diff.Value.Modified);
    }

    [Fact]
    public async Task Diff_OfAPathOutsideTheWorkspace_IsRefused()
    {
        var workspace = InitRepository("repo");

        var diff = await _git.GetFileDiffAsync(workspace, new GitChange("../secret.txt", GitFileStatus.Modified));

        Assert.Equal(GitErrors.FileNotChangedCode, diff.Error?.Code);
    }

    [Fact]
    public async Task Diff_OfAFileRemovedFromTheIndexButKept_ComparesBothVersions()
    {
        var workspace = InitRepository("repo");
        Write(workspace, "a.yaml", "name: A\n");
        TestGit.CommitAll(workspace, "Initial");
        TestGit.Run(workspace, "rm", "--cached", "--quiet", "a.yaml");
        Write(workspace, "a.yaml", "name: A2\n");

        var deleted = await _git.GetFileDiffAsync(workspace, new GitChange("a.yaml", GitFileStatus.Deleted));
        var untracked = await _git.GetFileDiffAsync(workspace, new GitChange("a.yaml", GitFileStatus.Untracked));

        Assert.Equal(("name: A\n", "name: A2\n"), (deleted.Value.Original, deleted.Value.Modified));
        Assert.Equal(("name: A\n", "name: A2\n"), (untracked.Value.Original, untracked.Value.Modified));
    }

    [Fact]
    public async Task Diff_OfASymbolicLink_ShowsItsTarget_WithoutFollowingIt()
    {
        var workspace = InitRepository("repo");
        var secret = _temp.Combine("secret.txt");
        File.WriteAllText(secret, "password\n");
        if (!TryCreateSymbolicLink(Path.Combine(workspace, "link.yaml"), secret))
        {
            return; // Creating links needs extra rights on Windows.
        }

        var diff = await _git.GetFileDiffAsync(workspace, new GitChange("link.yaml", GitFileStatus.Untracked));

        Assert.True(diff.IsSuccess, diff.Error?.Message);
        Assert.Equal(secret, diff.Value.Modified);
    }

    [Fact]
    public async Task Diff_ThroughALinkedFolder_IsRefused()
    {
        var workspace = InitRepository("repo");
        var outside = _temp.Combine("outside");
        Write(outside, "secret.yaml", "password\n");
        if (!TryCreateSymbolicLink(Path.Combine(workspace, "linked"), outside))
        {
            return; // Creating links needs extra rights on Windows.
        }

        var diff = await _git.GetFileDiffAsync(workspace, new GitChange("linked/secret.yaml", GitFileStatus.Untracked));

        Assert.Equal(GitErrors.CommandFailedCode, diff.Error?.Code);
        Assert.DoesNotContain("password", diff.Error!.Message);
    }

    [Fact]
    public async Task Remotes_CanBeAddedRenamedChangedAndRemoved()
    {
        var workspace = InitRepository("repo");
        Assert.Empty((await _git.ListRemotesAsync(workspace)).Value);

        Assert.True((await _git.AddRemoteAsync(workspace, Name("origin"), Url("https://example.com/a.git"))).IsSuccess);
        Assert.True((await _git.AddRemoteAsync(workspace, Name("backup"), Url("git@example.com:b.git"))).IsSuccess);
        TestGit.Run(workspace, "remote", "set-url", "--push", "backup", "ssh://example.com/push.git");

        var remotes = (await _git.ListRemotesAsync(workspace)).Value;
        Assert.Equal(
            [
                new GitRemote("backup", "git@example.com:b.git", "ssh://example.com/push.git"),
                new GitRemote("origin", "https://example.com/a.git", "https://example.com/a.git"),
            ],
            remotes);

        Assert.True((await _git.RenameRemoteAsync(workspace, "origin", Name("upstream"))).IsSuccess);
        Assert.True((await _git.SetRemoteUrlAsync(workspace, "upstream", Url("https://example.com/c.git"))).IsSuccess);
        Assert.True((await _git.RemoveRemoteAsync(workspace, "backup")).IsSuccess);

        remotes = (await _git.ListRemotesAsync(workspace)).Value;
        Assert.Equal([new GitRemote("upstream", "https://example.com/c.git", "https://example.com/c.git")], remotes);
    }

    [Fact]
    public async Task Remotes_GitFailures_AreReported()
    {
        var workspace = InitRepository("repo");

        var result = await _git.RemoveRemoteAsync(workspace, "missing");

        Assert.Equal(GitErrors.CommandFailedCode, result.Error?.Code);
    }

    [Fact]
    public void ParseRemotes_ReadsFetchAndPushUrls()
    {
        var remotes = GitCliService.ParseRemotes(
            "origin\thttps://a (fetch)\r\norigin\thttps://a (push)\nfork\t/path with spaces (fetch)\nfork\tssh://p (push)\n");

        Assert.Equal(
            [new GitRemote("fork", "/path with spaces", "ssh://p"), new GitRemote("origin", "https://a", "https://a")],
            remotes);
    }

    [Fact]
    public async Task Pull_OverUncommittedChanges_NamesTheFiles()
    {
        var (alice, bob) = InitTwoClones();
        Write(alice, "a.yaml", "name: From Alice\n");
        TestGit.CommitAll(alice, "Alice");
        TestGit.Run(alice, "push", "--quiet");
        Write(bob, "a.yaml", "name: Unsaved work\n");

        var pull = await _git.PullAsync(bob);

        Assert.Equal(GitErrors.LocalChangesOverwrittenCode, pull.Error?.Code);
        Assert.Contains("a.yaml", pull.Error!.Message);
        Assert.Equal("name: Unsaved work\n", File.ReadAllText(Path.Combine(bob, "a.yaml")));
    }

    [Fact]
    public async Task TrackRemoteBranch_ThenPull_JoinsALocalRepositoryWithAnExistingRemote()
    {
        var (alice, _) = InitTwoClones();
        var remote = _temp.Combine("remote.git");
        var local = InitRepository("local");
        Write(local, "b.yaml", "name: B\n");
        TestGit.CommitAll(local, "Local start");
        TestGit.Run(local, "remote", "add", "origin", remote);

        var tracked = await _git.TrackRemoteBranchAsync(local, "main");
        var pull = await _git.PullAsync(local, allowUnrelatedHistories: true);

        Assert.True(tracked.IsSuccess, tracked.Error?.Message);
        Assert.True(tracked.Value);
        Assert.True(pull.IsSuccess, pull.Error?.Message);
        Assert.Equal(PullOutcome.Updated, pull.Value);
        Assert.True(File.Exists(Path.Combine(local, "a.yaml")));
        var status = (await _git.GetStatusAsync(local)).Value;
        Assert.Equal("origin/main", status.Upstream);
        Assert.Equal(2, status.Ahead);
        Assert.Equal(0, status.Behind);
        Assert.True(Directory.Exists(alice));
    }

    [Fact]
    public async Task Pull_UnrelatedHistories_WithoutPermission_IsExplained()
    {
        InitTwoClones();
        var local = InitRepository("local");
        Write(local, "b.yaml", "name: B\n");
        TestGit.CommitAll(local, "Local start");
        TestGit.Run(local, "remote", "add", "origin", _temp.Combine("remote.git"));
        await _git.TrackRemoteBranchAsync(local, "main");

        var pull = await _git.PullAsync(local);

        Assert.Equal(GitErrors.UnrelatedHistoriesCode, pull.Error?.Code);
    }

    [Fact]
    public async Task TrackRemoteBranch_BranchNotOnTheRemote_ReturnsFalse()
    {
        InitTwoClones();
        var local = InitRepository("local");
        TestGit.Run(local, "remote", "add", "origin", _temp.Combine("remote.git"));
        TestGit.Run(local, "checkout", "--quiet", "-b", "feature");

        var tracked = await _git.TrackRemoteBranchAsync(local, "feature");

        Assert.True(tracked.IsSuccess, tracked.Error?.Message);
        Assert.False(tracked.Value);
        Assert.Null((await _git.GetStatusAsync(local)).Value.Upstream);
    }

    [Fact]
    public async Task TrackRemoteBranch_WithoutRemotes_IsReported()
    {
        var local = InitRepository("local");

        var tracked = await _git.TrackRemoteBranchAsync(local, "main");

        Assert.Equal(GitErrors.NoRemoteCode, tracked.Error?.Code);
    }

    [Fact]
    public async Task Status_OfANewRepository_IsUnborn()
    {
        var repository = InitRepository("repo");

        var status = (await _git.GetStatusAsync(repository)).Value;

        Assert.True(status.IsUnborn);
        Assert.False(status.IsUpstreamGone);
    }

    [Fact]
    public async Task Push_ToAnUpstreamWithAnotherName_PushesThere()
    {
        var (alice, _) = InitTwoClones();
        TestGit.Run(alice, "checkout", "--quiet", "-b", "work", "--track", "origin/main");
        Write(alice, "b.yaml", "name: B\n");
        TestGit.CommitAll(alice, "Add B");

        var push = await _git.PushAsync(alice, setUpstream: false);

        Assert.True(push.IsSuccess, push.Error?.Message);
        Assert.Equal(TestGit.Head(alice), TestGit.Run(_temp.Combine("remote.git"), "rev-parse", "main").Trim());
    }

    [Fact]
    public async Task Fetch_ReportsADeletedUpstream_AndPushCreatesItAgain()
    {
        var (alice, bob) = InitTwoClones();
        TestGit.Run(alice, "checkout", "--quiet", "-b", "feature");
        TestGit.Run(alice, "push", "--quiet", "--set-upstream", "origin", "feature");
        TestGit.Run(bob, "push", "--quiet", "origin", "--delete", "feature");

        var fetch = await _git.FetchAsync(alice);
        var gone = (await _git.GetStatusAsync(alice)).Value;
        var push = await _git.PushAsync(alice, setUpstream: false);
        var after = (await _git.GetStatusAsync(alice)).Value;

        Assert.True(fetch.IsSuccess, fetch.Error?.Message);
        Assert.True(gone.IsUpstreamGone);
        Assert.True(push.IsSuccess, push.Error?.Message);
        Assert.False(after.IsUpstreamGone);
        Assert.Equal("origin/feature", after.Upstream);
    }

    [Fact]
    public async Task Fetch_UpdatesTheCommitsToPull()
    {
        var (alice, bob) = InitTwoClones();
        Write(alice, "a.yaml", "name: From Alice\n");
        TestGit.CommitAll(alice, "Alice");
        TestGit.Run(alice, "push", "--quiet");

        var fetch = await _git.FetchAsync(bob);

        Assert.True(fetch.IsSuccess, fetch.Error?.Message);
        Assert.Equal(1, (await _git.GetStatusAsync(bob)).Value.Behind);
        Assert.Equal("name: A\n", File.ReadAllText(Path.Combine(bob, "a.yaml")));
    }

    [Fact]
    public async Task Commit_WhileAnotherProcessHoldsTheIndexLock_IsRetried()
    {
        var repository = InitRepository("repo");
        Write(repository, "a.yaml", "name: A\n");
        var lockFile = Path.Combine(repository, ".git", "index.lock");
        File.WriteAllText(lockFile, string.Empty);
        _ = Task.Delay(100).ContinueWith(_ => File.Delete(lockFile));

        var result = await _git.CommitAsync(repository, Message("Add A"), null);

        Assert.True(result.IsSuccess, result.Error?.Message);
    }

    [Fact]
    public async Task Commit_WhileTheIndexStaysLocked_IsReported()
    {
        var repository = InitRepository("repo");
        Write(repository, "a.yaml", "name: A\n");
        File.WriteAllText(Path.Combine(repository, ".git", "index.lock"), string.Empty);

        var result = await _git.CommitAsync(repository, Message("Add A"), null);

        Assert.Equal(GitErrors.RepositoryLockedCode, result.Error?.Code);
        Assert.Contains("index.lock", result.Error!.Message);
    }

    [Fact]
    public async Task Commit_WithoutAnIdentity_AsksForOne()
    {
        var repository = InitRepository("repo");
        // Blank local values override any identity in the machine's global configuration.
        TestGit.Run(repository, "config", "user.name", "");
        TestGit.Run(repository, "config", "user.email", "");
        Write(repository, "a.yaml", "name: A\n");

        var commit = await _git.CommitAsync(repository, Message("Add A"), null);
        var identity = await _git.GetIdentityAsync(repository);

        Assert.Equal(GitErrors.IdentityUnknownCode, commit.Error?.Code);
        Assert.False(identity.Value.IsComplete);
    }

    [Fact]
    public async Task SetIdentity_ForTheRepository_IsUsedByCommits()
    {
        var repository = InitRepository("repo");
        Assert.True(GitIdentity.TryCreate("Ada Lovelace", "ada@example.com", out var identity));

        var set = await _git.SetIdentityAsync(repository, identity, global: false);
        var read = await _git.GetIdentityAsync(repository);
        Write(repository, "a.yaml", "name: A\n");
        await _git.CommitAsync(repository, Message("Add A"), null);

        Assert.True(set.IsSuccess, set.Error?.Message);
        Assert.Equal(new GitIdentitySettings("Ada Lovelace", "ada@example.com"), read.Value);
        Assert.Equal("Ada Lovelace <ada@example.com>", TestGit.Run(repository, "log", "-1", "--format=%an <%ae>").Trim());
    }

    private async Task<string> ConflictedMergeAsync()
    {
        var (alice, bob) = InitTwoClones();
        Write(alice, "d.yaml", "name: D\n");
        TestGit.CommitAll(alice, "Add D");
        TestGit.Run(alice, "push", "--quiet");
        TestGit.Run(bob, "pull", "--quiet", "--no-rebase");

        Write(alice, "a.yaml", "name: From Alice\n");
        File.Delete(Path.Combine(alice, "d.yaml"));
        TestGit.CommitAll(alice, "Alice");
        TestGit.Run(alice, "push", "--quiet");
        Write(bob, "a.yaml", "name: From Bob\n");
        Write(bob, "d.yaml", "name: Changed by Bob\n");
        TestGit.CommitAll(bob, "Bob");

        var pull = await _git.PullAsync(bob);
        Assert.Equal(PullOutcome.Conflicted, pull.Value);
        return bob;
    }

    [Fact]
    public async Task Branches_AreCreatedListedSwitchedAndDeleted()
    {
        var repository = InitRepository("repo");
        Write(repository, "a.yaml", "name: A\n");
        TestGit.CommitAll(repository, "Add A");

        Assert.True(BranchName.TryCreate("feature/login", out var feature, out _));
        var created = await _git.CreateBranchAsync(repository, feature, switchTo: false);
        Assert.True(created.IsSuccess, created.Error?.Message);
        var branches = await _git.ListBranchesAsync(repository);
        Assert.True(branches.IsSuccess, branches.Error?.Message);
        Assert.Equal(["feature/login", "main"], branches.Value.Select(b => b.Name));
        Assert.True(branches.Value.Single(b => b.Name == "main").IsCurrent);
        Assert.Equal("Add A", branches.Value[0].Subject);
        Assert.Equal(TestGit.Head(repository), branches.Value[0].CommitId);
        Assert.NotNull(branches.Value[0].CommittedAt);

        var switched = await _git.SwitchBranchAsync(repository, "feature/login");
        Assert.True(switched.IsSuccess, switched.Error?.Message);
        Assert.Equal("feature/login", (await _git.GetStatusAsync(repository)).Value.Branch);

        Write(repository, "b.yaml", "name: B\n");
        TestGit.CommitAll(repository, "Add B");
        Assert.True((await _git.SwitchBranchAsync(repository, "main")).IsSuccess);
        Assert.False(File.Exists(Path.Combine(repository, "b.yaml")));

        var unmerged = await _git.DeleteBranchAsync(repository, "feature/login", force: false);
        Assert.Equal(GitErrors.BranchNotMergedCode, unmerged.Error?.Code);
        var forced = await _git.DeleteBranchAsync(repository, "feature/login", force: true);
        Assert.True(forced.IsSuccess, forced.Error?.Message);
        Assert.Equal(["main"], (await _git.ListBranchesAsync(repository)).Value.Select(b => b.Name));
    }

    [Fact]
    public async Task CreateBranch_WithSwitch_KeepsUncommittedChanges()
    {
        var repository = InitRepository("repo");
        Write(repository, "a.yaml", "name: A\n");
        TestGit.CommitAll(repository, "Add A");
        Write(repository, "a.yaml", "name: A2\n");

        Assert.True(BranchName.TryCreate("wip", out var wip, out _));
        var created = await _git.CreateBranchAsync(repository, wip, switchTo: true);

        Assert.True(created.IsSuccess, created.Error?.Message);
        var status = (await _git.GetStatusAsync(repository)).Value;
        Assert.Equal("wip", status.Branch);
        Assert.Null(status.Upstream);
        Assert.Equal("a.yaml", Assert.Single(status.Changes).Path);
        Assert.Equal(GitErrors.BranchExistsCode, (await _git.CreateBranchAsync(repository, wip, switchTo: true)).Error?.Code);
    }

    [Fact]
    public async Task SwitchBranch_ThatWouldOverwriteChanges_IsRefusedAndNamesTheFiles()
    {
        var repository = InitRepository("repo");
        Write(repository, "a.yaml", "name: A\n");
        TestGit.CommitAll(repository, "Add A");
        TestGit.Run(repository, "switch", "--quiet", "-c", "other");
        Write(repository, "a.yaml", "name: Other\n");
        TestGit.CommitAll(repository, "Change A");
        TestGit.Run(repository, "switch", "--quiet", "main");
        Write(repository, "a.yaml", "name: Mine\n");

        var result = await _git.SwitchBranchAsync(repository, "other");

        Assert.Equal(GitErrors.UncommittedChangesCode, result.Error?.Code);
        Assert.Contains("a.yaml", result.Error!.Message);
        Assert.Equal("name: Mine\n", File.ReadAllText(Path.Combine(repository, "a.yaml")));
    }

    [Fact]
    public async Task RemoteBranches_AreListedAndCheckedOutAsTrackingBranches()
    {
        var (alice, bob) = InitTwoClones();
        TestGit.Run(alice, "switch", "--quiet", "-c", "feature/x");
        Write(alice, "x.yaml", "name: X\n");
        TestGit.CommitAll(alice, "Add X");
        TestGit.Run(alice, "push", "--quiet", "origin", "feature/x");
        TestGit.Run(bob, "fetch", "--quiet");

        var branches = (await _git.ListBranchesAsync(bob)).Value;
        Assert.Equal(["main", "origin/feature/x", "origin/main"], branches.Select(b => b.Name));
        var remote = branches.Single(b => b.Name == "origin/feature/x");
        Assert.Equal("origin", remote.Remote);
        Assert.Equal("origin/main", branches.Single(b => b.Name == "main").Upstream);

        Assert.True(BranchName.TryCreate(remote.LocalName, out var local, out _));
        var checkedOut = await _git.CheckoutRemoteBranchAsync(bob, remote.Name, local);

        Assert.True(checkedOut.IsSuccess, checkedOut.Error?.Message);
        var status = (await _git.GetStatusAsync(bob)).Value;
        Assert.Equal("feature/x", status.Branch);
        Assert.Equal("origin/feature/x", status.Upstream);
        Assert.True(File.Exists(Path.Combine(bob, "x.yaml")));
    }

    [Fact]
    public void ParseBranches_AssignsRemoteBranchesToTheLongestMatchingRemote()
    {
        var output = string.Join('\n',
            "refs/heads/main\0abc\0origin/main\0*\02026-01-02T03:04:05+00:00\0\0Initial",
            "refs/remotes/origin/HEAD\0abc\0\0 \02026-01-02T03:04:05+00:00\0refs/remotes/origin/main\0Initial",
            "refs/remotes/team/eu/feature\0def\0\0 \0\0\0Work");

        var branches = GitCliService.ParseBranches(output, ["origin", "team", "team/eu"]);

        Assert.Equal(["main", "team/eu/feature"], branches.Select(b => b.Name));
        Assert.True(branches[0].IsCurrent);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), branches[0].CommittedAt);
        Assert.Equal("team/eu", branches[1].Remote);
        Assert.Equal("feature", branches[1].LocalName);
        Assert.Null(branches[1].CommittedAt);
    }

    private string InitRepository(string name)
    {
        var path = _temp.Combine(name);
        Directory.CreateDirectory(path);
        TestGit.Run(path, "init", "--quiet", "--initial-branch=main");
        TestGit.Configure(path);
        return path;
    }

    private string InitBareRemote()
    {
        var path = _temp.Combine("remote.git");
        Directory.CreateDirectory(path);
        TestGit.Run(path, "init", "--quiet", "--bare", "--initial-branch=main");
        return path;
    }

    /// <summary>A remote with one commit on <c>main</c>, cloned twice.</summary>
    private (string Alice, string Bob) InitTwoClones()
    {
        var remote = InitBareRemote();
        var seed = InitRepository("seed");
        Write(seed, "a.yaml", "name: A\n");
        TestGit.CommitAll(seed, "Add A");
        TestGit.Run(seed, "push", "--quiet", remote, "main");

        string Clone(string name)
        {
            // Set before checkout, so a global core.autocrlf=true (Windows runners) can't rewrite
            // line endings and make the clone look modified.
            TestGit.Run(_temp.Path, "clone", "--quiet", "--config", "core.autocrlf=false", remote, name);
            var path = _temp.Combine(name);
            TestGit.Configure(path, name);
            return path;
        }

        return (Clone("alice"), Clone("bob"));
    }

    private static void Write(string folder, string relativePath, string content)
    {
        var path = Path.Combine(folder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static bool TryCreateSymbolicLink(string path, string target)
    {
        try
        {
            if (Directory.Exists(target))
            {
                Directory.CreateSymbolicLink(path, target);
            }
            else
            {
                File.CreateSymbolicLink(path, target);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static RemoteName Name(string text) =>
        RemoteName.TryCreate(text, out var name, out var error) ? name : throw new ArgumentException(error);

    private static RemoteUrl Url(string text) =>
        RemoteUrl.TryCreate(text, out var url, out var error) ? url : throw new ArgumentException(error);

    private static CommitMessage Message(string text) =>
        CommitMessage.TryCreate(text, out var message) ? message : throw new ArgumentException(text);

    private static string CommittedFiles(string repository) =>
        TestGit.Run(repository, "show", "--name-only", "--format=", "HEAD").ReplaceLineEndings("\n").TrimStart('\n');
}
