using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Missive.Application.Common;
using Missive.Application.Git;
using Missive.Application.Requests;
using Missive.Application.Settings;
using Missive.Application.Workspaces;
using Missive.Desktop.Bridge;
using Missive.Domain.Git;
using Missive.Domain.Requests;
using NSubstitute;

namespace Missive.Desktop.Tests.Bridge;

/// <summary>The git and last-workspace messages, through the real router and use cases.</summary>
public sealed class GitBridgeTests : IDisposable
{
    private const string Folder = "/work/my-api";

    private readonly IGitService _git = Substitute.For<IGitService>();
    private readonly IWorkspaceRepository _repository = Substitute.For<IWorkspaceRepository>();
    private readonly IAppSettingsStore _settings = Substitute.For<IAppSettingsStore>();
    private readonly IRequestFileFormat _format = Substitute.For<IRequestFileFormat>();
    private readonly CurrentWorkspace _current = new();
    private readonly ServiceProvider _services;
    private readonly BridgeRouter _router;

    public GitBridgeTests()
    {
        _services = new ServiceCollection()
            .AddSingleton(_git)
            .AddSingleton(_repository)
            .AddSingleton(_settings)
            .AddSingleton(_current)
            .AddSingleton(_format)
            .AddTransient<OpenWorkspace>()
            .AddTransient<OpenLastWorkspace>()
            .AddTransient<GetGitStatus>()
            .AddTransient<InitRepository>()
            .AddTransient<CommitChanges>()
            .AddTransient<DiscardChanges>()
            .AddTransient<PullChanges>()
            .AddTransient<PushChanges>()
            .AddTransient<FetchChanges>()
            .AddTransient<GetGitIdentity>()
            .AddTransient<SetGitIdentity>()
            .AddTransient<GetConflict>()
            .AddTransient<ResolveConflict>()
            .AddTransient<AbortMerge>()
            .AddTransient<GetGitHistory>()
            .AddTransient<GetCommit>()
            .AddTransient<GetCommitFile>()
            .AddTransient<GetFileDiff>()
            .AddTransient<ListBranches>()
            .AddTransient<SwitchBranch>()
            .AddTransient<CreateBranch>()
            .AddTransient<CheckoutRemoteBranch>()
            .AddTransient<DeleteBranch>()
            .AddTransient<ListRemotes>()
            .AddTransient<AddRemote>()
            .AddTransient<UpdateRemote>()
            .AddTransient<RemoveRemote>()
            .BuildServiceProvider();
        _router = BridgeHandlers.Create(_services, Substitute.For<IFileDialogs>());
    }

    public void Dispose() => _services.Dispose();

    [Fact]
    public async Task GitStatus_WithoutAWorkspace_IsReported()
    {
        var response = await SendAsync("git.status");

        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.Equal(ErrorCodes.NoWorkspace, ErrorCode(response));
    }

    [Fact]
    public async Task GitStatus_ReturnsTheStatusWithStringStatuses()
    {
        _current.Open(Folder);
        _git.GetStatusAsync(Folder, Arg.Any<CancellationToken>()).Returns(Result<GitStatus>.Success(
            new GitStatus(
                "main",
                null,
                1,
                0,
                false,
                [
                    new GitChange("users/get-user.yaml", GitFileStatus.Modified),
                    new GitChange("new.yaml", GitFileStatus.Renamed, "old.yaml"),
                ])));

        var response = await SendAsync("git.status");

        Assert.True(response.GetProperty("ok").GetBoolean(), response.ToString());
        var payload = response.GetProperty("payload");
        Assert.Equal("main", payload.GetProperty("branch").GetString());
        Assert.Equal(1, payload.GetProperty("ahead").GetInt32());
        Assert.False(payload.GetProperty("merging").GetBoolean());
        var changes = payload.GetProperty("changes");
        Assert.Equal("users/get-user.yaml", changes[0].GetProperty("path").GetString());
        Assert.Equal("modified", changes[0].GetProperty("status").GetString());
        Assert.Equal("renamed", changes[1].GetProperty("status").GetString());
        Assert.Equal("old.yaml", changes[1].GetProperty("originalPath").GetString());
    }

    [Fact]
    public async Task GitCommit_PassesTheMessageAndPaths()
    {
        _current.Open(Folder);
        _git.GetStatusAsync(Folder, Arg.Any<CancellationToken>()).Returns(
            Result<GitStatus>.Success(new GitStatus("main", "origin/main", 0, 0, false, [new GitChange("a.yaml", GitFileStatus.Modified)])),
            Result<GitStatus>.Success(new GitStatus("main", "origin/main", 1, 0, false, [])));
        _git.CommitAsync(default!, default!, default, default).ReturnsForAnyArgs(Result<string>.Success("abc123"));

        var response = await SendAsync("git.commit", """{ "message": "Update A", "paths": ["a.yaml"] }""");

        Assert.True(response.GetProperty("ok").GetBoolean(), response.ToString());
        Assert.Equal("abc123", response.GetProperty("payload").GetProperty("commitId").GetString());
        _ = _git.Received(1).CommitAsync(
            Folder,
            Arg.Is<CommitMessage>(m => m.Value == "Update A"),
            Arg.Is<IReadOnlyList<string>?>(p => p != null && p.SequenceEqual(new[] { "a.yaml" })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GitCommit_WithoutAMessage_IsReported()
    {
        _current.Open(Folder);

        var response = await SendAsync("git.commit", """{ "message": "  " }""");

        Assert.Equal(GitErrors.InvalidCommitMessageCode, ErrorCode(response));
    }

    [Fact]
    public async Task GitPull_ReturnsTheOutcome()
    {
        _current.Open(Folder);
        _git.GetStatusAsync(Folder, Arg.Any<CancellationToken>())
            .Returns(Result<GitStatus>.Success(new GitStatus("main", "origin/main", 0, 0, false, [])));
        _git.PullAsync(Folder, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Result<PullOutcome>.Success(PullOutcome.UpToDate));

        var response = await SendAsync("git.pull");

        Assert.Equal("upToDate", response.GetProperty("payload").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task GitStatus_ReportsUnbornBranchesAndDeletedUpstreams()
    {
        _current.Open(Folder);
        _git.GetStatusAsync(Folder, Arg.Any<CancellationToken>()).Returns(Result<GitStatus>.Success(
            new GitStatus("main", "origin/main", 0, 0, false, [], IsUnborn: true, IsUpstreamGone: true)));

        var payload = (await SendAsync("git.status")).GetProperty("payload");

        Assert.True(payload.GetProperty("unborn").GetBoolean());
        Assert.True(payload.GetProperty("upstreamGone").GetBoolean());
    }

    [Fact]
    public async Task GitFetch_ReturnsTheNewStatus()
    {
        _current.Open(Folder);
        _git.ListRemotesAsync(Folder, Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<GitRemote>>.Success([new GitRemote("origin", "u", "u")]));
        _git.FetchAsync(Folder, Arg.Any<CancellationToken>()).Returns(Result<Unit>.Success(Unit.Value));
        _git.GetStatusAsync(Folder, Arg.Any<CancellationToken>())
            .Returns(Result<GitStatus>.Success(new GitStatus("main", "origin/main", 0, 3, false, [])));

        var response = await SendAsync("git.fetch");

        Assert.True(response.GetProperty("ok").GetBoolean(), response.ToString());
        Assert.Equal(3, response.GetProperty("payload").GetProperty("behind").GetInt32());
    }

    [Fact]
    public async Task GitIdentity_CanBeReadAndSet()
    {
        _current.Open(Folder);
        _git.GetIdentityAsync(Folder, Arg.Any<CancellationToken>()).Returns(
            Result<GitIdentitySettings>.Success(new GitIdentitySettings(null, null)),
            Result<GitIdentitySettings>.Success(new GitIdentitySettings("Ada", "ada@example.com")));
        _git.SetIdentityAsync(default!, default!, default, default).ReturnsForAnyArgs(Result<Unit>.Success(Unit.Value));

        var read = await SendAsync("git.identity");
        var set = await SendAsync("git.identity.set", """{ "name": "Ada", "email": "ada@example.com", "global": false }""");

        Assert.Equal(System.Text.Json.JsonValueKind.Null, read.GetProperty("payload").GetProperty("name").ValueKind);
        Assert.Equal("Ada", set.GetProperty("payload").GetProperty("name").GetString());
        _ = _git.Received(1).SetIdentityAsync(Folder, Arg.Any<GitIdentity>(), false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GitPush_Rejected_IsReported()
    {
        _current.Open(Folder);
        _git.GetStatusAsync(Folder, Arg.Any<CancellationToken>())
            .Returns(Result<GitStatus>.Success(new GitStatus("main", "origin/main", 1, 0, false, [])));
        _git.PushAsync(default!, default, default)
            .ReturnsForAnyArgs(Result<Unit>.Failure(GitErrors.PushRejected("! refs/heads/main [rejected]")));

        var response = await SendAsync("git.push");

        Assert.Equal(GitErrors.PushRejectedCode, ErrorCode(response));
    }

    [Fact]
    public async Task GitInit_InitializesTheWorkspaceAndReturnsTheStatus()
    {
        _current.Open(Folder);
        _git.InitAsync(Folder, Arg.Any<CancellationToken>()).Returns(Result<Unit>.Success(Unit.Value));
        _git.GetStatusAsync(Folder, Arg.Any<CancellationToken>())
            .Returns(Result<GitStatus>.Success(new GitStatus("main", null, 0, 0, false, [])));

        var response = await SendAsync("git.init");

        Assert.True(response.GetProperty("ok").GetBoolean(), response.ToString());
        Assert.Equal("main", response.GetProperty("payload").GetProperty("branch").GetString());
        await _git.Received(1).InitAsync(Folder, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OpenLast_WithNothingRemembered_ReturnsNull()
    {
        _settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(AppSettings.Default);

        var response = await SendAsync("workspace.openLast");

        Assert.True(response.GetProperty("ok").GetBoolean(), response.ToString());
        Assert.Equal(JsonValueKind.Null, response.GetProperty("payload").ValueKind);
    }

    [Fact]
    public async Task GitConflict_ReturnsBothSides()
    {
        _current.Open(Folder);
        _git.GetStatusAsync(Folder, Arg.Any<CancellationToken>()).Returns(Result<GitStatus>.Success(
            new GitStatus("main", "origin/main", 1, 1, true, [new GitChange("a.yaml", GitFileStatus.Conflicted)], "Merge branch 'main'")));
        _git.GetConflictAsync(Folder, "a.yaml", Arg.Any<CancellationToken>()).Returns(Result<GitConflict>.Success(
            new GitConflict("a.yaml", "name: A\n", "name: Bob\n", null, "name: Bob\n")));

        var response = await SendAsync("git.conflict", """{ "path": "a.yaml" }""");

        Assert.True(response.GetProperty("ok").GetBoolean(), response.ToString());
        var payload = response.GetProperty("payload");
        Assert.Equal("name: Bob\n", payload.GetProperty("ours").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("theirs").ValueKind);
        Assert.False(payload.GetProperty("hasConflictMarkers").GetBoolean());
    }

    [Theory]
    [InlineData("ours", ConflictResolution.Ours)]
    [InlineData("theirs", ConflictResolution.Theirs)]
    public async Task GitResolve_PassesTheUsersChoice(string name, ConflictResolution resolution)
    {
        _current.Open(Folder);
        _git.GetStatusAsync(Folder, Arg.Any<CancellationToken>()).Returns(
            Result<GitStatus>.Success(new GitStatus("main", "origin/main", 1, 1, true, [new GitChange("a.yaml", GitFileStatus.Conflicted)])),
            Result<GitStatus>.Success(new GitStatus("main", "origin/main", 1, 1, true, [new GitChange("a.yaml", GitFileStatus.Modified)], "Merge")));
        _git.ResolveConflictAsync(default!, default!, default, default).ReturnsForAnyArgs(Result<Unit>.Success(Unit.Value));

        var response = await SendAsync("git.resolve", $$"""{ "path": "a.yaml", "resolution": "{{name}}" }""");

        Assert.True(response.GetProperty("ok").GetBoolean(), response.ToString());
        Assert.Equal("Merge", response.GetProperty("payload").GetProperty("mergeMessage").GetString());
        _ = _git.Received(1).ResolveConflictAsync(Folder, "a.yaml", resolution, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GitResolve_UnknownResolution_IsRejected()
    {
        _current.Open(Folder);

        var response = await SendAsync("git.resolve", """{ "path": "a.yaml", "resolution": "both" }""");

        Assert.Equal("bad_payload", ErrorCode(response));
        _ = _git.DidNotReceiveWithAnyArgs().ResolveConflictAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task GitAbortMerge_WithoutAMerge_IsReported()
    {
        _current.Open(Folder);
        _git.GetStatusAsync(Folder, Arg.Any<CancellationToken>())
            .Returns(Result<GitStatus>.Success(new GitStatus("main", "origin/main", 0, 0, false, [])));

        var response = await SendAsync("git.abortMerge");

        Assert.Equal(GitErrors.NotMergingCode, ErrorCode(response));
    }

    [Fact]
    public async Task GitLog_ReturnsAPageOfCommits()
    {
        _current.Open(Folder);
        IReadOnlyList<GitCommitInfo> log =
        [
            new("abc1234", ["def5678"], "Alice", "alice@example.com", new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), "Add users"),
        ];
        _git.GetLogAsync(Folder, 50, 51, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<GitCommitInfo>>.Success(log));

        var response = await SendAsync("git.log", """{ "skip": 50 }""");

        Assert.True(response.GetProperty("ok").GetBoolean(), response.ToString());
        var payload = response.GetProperty("payload");
        Assert.False(payload.GetProperty("hasMore").GetBoolean());
        var commit = payload.GetProperty("commits")[0];
        Assert.Equal("abc1234", commit.GetProperty("id").GetString());
        Assert.Equal("def5678", commit.GetProperty("parents")[0].GetString());
        Assert.Equal("Alice", commit.GetProperty("authorName").GetString());
        Assert.Equal("Add users", commit.GetProperty("subject").GetString());
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), commit.GetProperty("date").GetDateTimeOffset());
    }

    [Fact]
    public async Task GitShow_ReturnsTheMessageAndFiles()
    {
        _current.Open(Folder);
        _git.GetCommitAsync(Folder, "abc1234", Arg.Any<CancellationToken>()).Returns(Result<GitCommitDetails>.Success(
            new GitCommitDetails(
                new GitCommitInfo("abc1234", [], "Alice", "alice@example.com", DateTimeOffset.UnixEpoch, "Initial"),
                "Details",
                [new GitChange("a.yaml", GitFileStatus.Added)])));

        var response = await SendAsync("git.show", """{ "commitId": "abc1234" }""");

        Assert.True(response.GetProperty("ok").GetBoolean(), response.ToString());
        var payload = response.GetProperty("payload");
        Assert.Equal("Initial", payload.GetProperty("commit").GetProperty("subject").GetString());
        Assert.Equal("Details", payload.GetProperty("body").GetString());
        Assert.Equal("added", payload.GetProperty("files")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task GitCommitFile_ReturnsBothVersions_AndHidesBinaryContent()
    {
        _current.Open(Folder);
        _git.GetCommitAsync(Folder, "abc1234", Arg.Any<CancellationToken>()).Returns(Result<GitCommitDetails>.Success(
            new GitCommitDetails(
                new GitCommitInfo("abc1234", ["def5678"], "Alice", "alice@example.com", DateTimeOffset.UnixEpoch, "Change"),
                "",
                [new GitChange("a.yaml", GitFileStatus.Modified), new GitChange("logo.png", GitFileStatus.Modified)])));
        _git.ReadFileAtCommitAsync(Folder, "def5678", "a.yaml", Arg.Any<CancellationToken>()).Returns(Result<string?>.Success("name: A\n"));
        _git.ReadFileAtCommitAsync(Folder, "abc1234", "a.yaml", Arg.Any<CancellationToken>()).Returns(Result<string?>.Success("name: B\n"));
        _git.ReadFileAtCommitAsync(Folder, "def5678", "logo.png", Arg.Any<CancellationToken>()).Returns(Result<string?>.Success("PNG\0\0"));
        _git.ReadFileAtCommitAsync(Folder, "abc1234", "logo.png", Arg.Any<CancellationToken>()).Returns(Result<string?>.Success("PNG\0\x01"));
        _format.Parse(default!, default!).ReturnsForAnyArgs(
            new HttpRequestDefinition("Get user", RequestMethod.Get, "https://x", [], [], RequestAuth.None, RequestBody.None));

        var text = await SendAsync("git.commitFile", """{ "commitId": "abc1234", "path": "a.yaml" }""");
        var binary = await SendAsync("git.commitFile", """{ "commitId": "abc1234", "path": "logo.png" }""");

        Assert.True(text.GetProperty("ok").GetBoolean(), text.ToString());
        Assert.Equal("name: A\n", text.GetProperty("payload").GetProperty("before").GetString());
        Assert.Equal("name: B\n", text.GetProperty("payload").GetProperty("after").GetString());
        Assert.Equal("modified", text.GetProperty("payload").GetProperty("change").GetProperty("status").GetString());
        Assert.True(binary.GetProperty("payload").GetProperty("binary").GetBoolean());
        Assert.Equal(JsonValueKind.Null, binary.GetProperty("payload").GetProperty("after").ValueKind);
    }

    [Fact]
    public async Task GitDiff_ReturnsBothVersions_AndTheParsedRequests()
    {
        _current.Open(Folder);
        var change = new GitChange("users/get-user.yaml", GitFileStatus.Modified);
        _git.GetStatusAsync(Folder, Arg.Any<CancellationToken>())
            .Returns(Result<GitStatus>.Success(new GitStatus("main", null, 0, 0, false, [change])));
        _git.GetFileDiffAsync(Folder, change, Arg.Any<CancellationToken>())
            .Returns(Result<GitFileDiff>.Success(new GitFileDiff(change.Path, change.Status, null, "a", "b")));
        _format.Parse(default!, default!).ReturnsForAnyArgs(
            new HttpRequestDefinition("Get user", RequestMethod.Get, "https://x", [], [], RequestAuth.None, RequestBody.None));

        var response = await SendAsync("git.diff", """{ "path": "users/get-user.yaml" }""");

        Assert.True(response.GetProperty("ok").GetBoolean(), response.ToString());
        var payload = response.GetProperty("payload");
        Assert.Equal("modified", payload.GetProperty("status").GetString());
        Assert.Equal("a", payload.GetProperty("original").GetString());
        Assert.Equal("b", payload.GetProperty("modified").GetString());
        Assert.False(payload.GetProperty("binary").GetBoolean());
        Assert.True(payload.GetProperty("isRequest").GetBoolean());
        Assert.Equal("GET", payload.GetProperty("modifiedRequest").GetProperty("method").GetString());
    }

    [Fact]
    public async Task GitRemoteList_ReturnsTheRemotes()
    {
        _current.Open(Folder);
        _git.ListRemotesAsync(Folder, Arg.Any<CancellationToken>()).Returns(
            Result<IReadOnlyList<GitRemote>>.Success([new GitRemote("origin", "https://a", "ssh://b")]));

        var response = await SendAsync("git.remote.list");

        Assert.True(response.GetProperty("ok").GetBoolean(), response.ToString());
        var remote = Assert.Single(response.GetProperty("payload").EnumerateArray());
        Assert.Equal("origin", remote.GetProperty("name").GetString());
        Assert.Equal("https://a", remote.GetProperty("fetchUrl").GetString());
        Assert.Equal("ssh://b", remote.GetProperty("pushUrl").GetString());
    }

    [Fact]
    public async Task GitRemoteAdd_AddsTheRemote()
    {
        _current.Open(Folder);
        _git.ListRemotesAsync(Folder, Arg.Any<CancellationToken>()).Returns(
            Result<IReadOnlyList<GitRemote>>.Success([]),
            Result<IReadOnlyList<GitRemote>>.Success([new GitRemote("origin", "https://a", "https://a")]));
        _git.AddRemoteAsync(default!, default!, default!).ReturnsForAnyArgs(Result<Unit>.Success(Unit.Value));

        var response = await SendAsync("git.remote.add", """{ "name": "origin", "url": "https://a" }""");

        Assert.True(response.GetProperty("ok").GetBoolean(), response.ToString());
        Assert.Single(response.GetProperty("payload").EnumerateArray());
    }

    [Fact]
    public async Task GitRemoteUpdate_AndRemove_ReportUnknownRemotes()
    {
        _current.Open(Folder);
        _git.ListRemotesAsync(Folder, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<GitRemote>>.Success([]));

        var update = await SendAsync("git.remote.update", """{ "name": "origin", "newName": "up", "url": "https://a" }""");
        var remove = await SendAsync("git.remote.remove", """{ "name": "origin" }""");

        Assert.Equal(GitErrors.RemoteNotFoundCode, ErrorCode(update));
        Assert.Equal(GitErrors.RemoteNotFoundCode, ErrorCode(remove));
    }

    [Fact]
    public async Task GitBranchList_ReturnsBranchesWithTheLocalBranchTrackingEachRemoteOne()
    {
        _current.Open(Folder);
        _git.GetStatusAsync(Folder, Arg.Any<CancellationToken>())
            .Returns(Result<GitStatus>.Success(new GitStatus("main", "origin/main", 0, 0, false, [])));
        _git.ListBranchesAsync(Folder, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<GitBranch>>.Success([
            new GitBranch("main", null, true, "origin/main", "a1", null, "Init"),
            new GitBranch("origin/main", "origin", false, null, "a1", null, "Init"),
            new GitBranch("origin/feature/x", "origin", false, null, "b2", null, "X"),
        ]));

        var response = await SendAsync("git.branch.list");

        Assert.True(response.GetProperty("ok").GetBoolean(), response.ToString());
        var payload = response.GetProperty("payload");
        Assert.Equal("main", payload.GetProperty("status").GetProperty("branch").GetString());
        var branches = payload.GetProperty("branches");
        Assert.True(branches[0].GetProperty("current").GetBoolean());
        Assert.Equal("main", branches[1].GetProperty("trackedBy").GetString());
        Assert.Equal("feature/x", branches[2].GetProperty("localName").GetString());
        Assert.Equal(JsonValueKind.Null, branches[2].GetProperty("trackedBy").ValueKind);
    }

    [Fact]
    public async Task GitBranchDelete_PassesForce()
    {
        _current.Open(Folder);
        _git.GetStatusAsync(Folder, Arg.Any<CancellationToken>())
            .Returns(Result<GitStatus>.Success(new GitStatus("main", null, 0, 0, false, [])));
        _git.ListBranchesAsync(Folder, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<GitBranch>>.Success([
            new GitBranch("main", null, true, null, "a1", null, "Init"),
            new GitBranch("old", null, false, null, "b2", null, "Old"),
        ]));
        _git.DeleteBranchAsync(default!, default!, default).ReturnsForAnyArgs(Result<Unit>.Success(Unit.Value));

        var response = await SendAsync("git.branch.delete", """{ "name": "old", "force": true }""");

        Assert.True(response.GetProperty("ok").GetBoolean(), response.ToString());
        await _git.Received(1).DeleteBranchAsync(Folder, "old", true, Arg.Any<CancellationToken>());
    }

    private async Task<JsonElement> SendAsync(string type, string? payload = null)
    {
        var message = payload is null
            ? $$"""{ "id": "1", "type": "{{type}}" }"""
            : $$"""{ "id": "1", "type": "{{type}}", "payload": {{payload}} }""";
        var json = await _router.HandleAsync(message);
        Assert.NotNull(json);
        return JsonDocument.Parse(json).RootElement;
    }

    private static string? ErrorCode(JsonElement response) =>
        response.GetProperty("error").GetProperty("code").GetString();
}
