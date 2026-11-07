using Missive.Application.Common;
using Missive.Domain.Git;

namespace Missive.Application.Git;

/// <summary>
/// Git operations on the repository that contains a workspace folder. Implementations use the
/// user's own git configuration and credentials, never force-push or rewrite history, and never
/// resolve conflicts. Paths are relative to the workspace folder and use <c>/</c> separators.
/// Files under the workspace's <c>.missive/</c> folder are never reported or committed.
/// </summary>
public interface IGitService
{
    /// <summary>Reads the branch, upstream tracking and changed files of the workspace.</summary>
    Task<Result<GitStatus>> GetStatusAsync(string workspacePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new repository in the workspace folder (<c>git init</c>) and makes sure its
    /// <c>.gitignore</c> ignores <c>.missive/</c>. Fails if the folder is already inside a repository.
    /// </summary>
    Task<Result<Unit>> InitAsync(string workspacePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages and commits changes inside the workspace: the given <paramref name="paths"/>, or every
    /// change when <paramref name="paths"/> is <c>null</c>. Returns the new commit id.
    /// </summary>
    Task<Result<string>> CommitAsync(
        string workspacePath,
        CommitMessage message,
        IReadOnlyList<string>? paths,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Throws away the given uncommitted changes: modified and deleted files go back to their last
    /// committed content, new files (staged or untracked) are deleted, and a rename is undone.
    /// </summary>
    Task<Result<Unit>> DiscardChangesAsync(
        string workspacePath,
        IReadOnlyList<GitChange> changes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Pulls the current branch from its upstream with a merge (never a rebase). Conflicts are left
    /// in the working tree for the user and reported as <see cref="PullOutcome.Conflicted"/>.
    /// </summary>
    /// <param name="allowUnrelatedHistories">
    /// Merge even when the upstream shares no commit with the branch: for the first pull of a
    /// repository that was created locally and linked to an existing remote branch.
    /// </param>
    Task<Result<PullOutcome>> PullAsync(
        string workspacePath,
        bool allowUnrelatedHistories = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads the latest commits of every remote without changing the working tree, so the
    /// status's ahead/behind counts are current. Remote-tracking branches deleted on the remote are pruned.
    /// </summary>
    Task<Result<Unit>> FetchAsync(string workspacePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the workspace's remote (<c>origin</c>, or the only one) and, when it has a branch named
    /// <paramref name="branch"/>, makes it the branch's upstream. Returns whether it did.
    /// </summary>
    Task<Result<bool>> TrackRemoteBranchAsync(string workspacePath, string branch, CancellationToken cancellationToken = default);

    /// <summary>The commit identity git would use in the workspace; either part may be missing.</summary>
    Task<Result<GitIdentitySettings>> GetIdentityAsync(string workspacePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the commit identity, for every repository of the user (<paramref name="global"/>) or only
    /// the workspace's repository.
    /// </summary>
    Task<Result<Unit>> SetIdentityAsync(
        string workspacePath,
        GitIdentity identity,
        bool global,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Pushes the current branch without force: to its upstream branch (re-creating it if it was
    /// deleted on the remote), unless the user configured a separate push remote. With
    /// <paramref name="setUpstream"/>, publishes the branch to the workspace's remote and makes it
    /// the upstream.
    /// </summary>
    Task<Result<Unit>> PushAsync(string workspacePath, bool setUpstream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the base, current-branch and incoming versions of a conflicted file, and its working
    /// tree content.
    /// </summary>
    Task<Result<GitConflict>> GetConflictAsync(string workspacePath, string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves one conflicted file as the user chose: by taking one side's version (or its deletion),
    /// or by keeping the working tree file as edited. The file is then marked resolved (staged).
    /// </summary>
    Task<Result<Unit>> ResolveConflictAsync(
        string workspacePath,
        string path,
        ConflictResolution resolution,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Abandons the merge in progress and restores the branch to how it was before it
    /// (<c>git merge --abort</c>).
    /// </summary>
    Task<Result<Unit>> AbortMergeAsync(string workspacePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the commits reachable from HEAD that changed files inside the workspace folder, newest
    /// first, skipping the first <paramref name="skip"/> and returning at most
    /// <paramref name="maxCount"/>. A branch with no commits yet has an empty history.
    /// </summary>
    Task<Result<IReadOnlyList<GitCommitInfo>>> GetLogAsync(
        string workspacePath,
        int skip,
        int maxCount,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one commit, its full message and the workspace files it changed compared with its first
    /// parent. Fails with <see cref="GitErrors.CommitNotFoundCode"/> when there is no such commit.
    /// </summary>
    Task<Result<GitCommitDetails>> GetCommitAsync(
        string workspacePath,
        string commitId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a workspace file as it was in a commit; <c>null</c> when the file didn't exist there.
    /// </summary>
    Task<Result<string?>> ReadFileAtCommitAsync(
        string workspacePath,
        string commitId,
        string path,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the committed (<c>HEAD</c>) and working tree versions of one changed file, for
    /// showing its differences. Nothing is staged or changed.
    /// </summary>
    Task<Result<GitFileDiff>> GetFileDiffAsync(string workspacePath, GitChange change, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the local branches and the remote-tracking branches (without the remotes' <c>HEAD</c>
    /// pointers), each sorted by name, local ones first.
    /// </summary>
    Task<Result<IReadOnlyList<GitBranch>>> ListBranchesAsync(string workspacePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks out an existing local branch (<c>git switch</c>, never forced). Fails with
    /// <see cref="GitErrors.UncommittedChangesCode"/> when uncommitted changes would be overwritten.
    /// </summary>
    Task<Result<Unit>> SwitchBranchAsync(string workspacePath, string branch, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a branch at the current commit, without an upstream, and with
    /// <paramref name="switchTo"/> checks it out (uncommitted changes come along).
    /// </summary>
    Task<Result<Unit>> CreateBranchAsync(string workspacePath, BranchName name, bool switchTo, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a local branch named <paramref name="localName"/> from a remote-tracking branch, set
    /// to track it, and checks it out.
    /// </summary>
    Task<Result<Unit>> CheckoutRemoteBranchAsync(
        string workspacePath,
        string remoteBranch,
        BranchName localName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a local branch. Without <paramref name="force"/> a branch with commits that aren't
    /// merged (into its upstream or the current branch) is kept, failing with
    /// <see cref="GitErrors.BranchNotMergedCode"/>. Remote branches are never deleted.
    /// </summary>
    Task<Result<Unit>> DeleteBranchAsync(string workspacePath, string branch, bool force, CancellationToken cancellationToken = default);

    /// <summary>Lists the repository's remotes, sorted by name.</summary>
    Task<Result<IReadOnlyList<GitRemote>>> ListRemotesAsync(string workspacePath, CancellationToken cancellationToken = default);

    /// <summary>Adds a remote (<c>git remote add</c>). Nothing is fetched.</summary>
    Task<Result<Unit>> AddRemoteAsync(string workspacePath, RemoteName name, RemoteUrl url, CancellationToken cancellationToken = default);

    /// <summary>Renames a remote, with its remote-tracking branches and upstream settings (<c>git remote rename</c>).</summary>
    Task<Result<Unit>> RenameRemoteAsync(string workspacePath, string name, RemoteName newName, CancellationToken cancellationToken = default);

    /// <summary>Changes the URL a remote fetches from and pushes to (<c>git remote set-url</c>).</summary>
    Task<Result<Unit>> SetRemoteUrlAsync(string workspacePath, string name, RemoteUrl url, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a remote with its remote-tracking branches (<c>git remote remove</c>). Branches that
    /// tracked it lose their upstream; no commits are lost.
    /// </summary>
    Task<Result<Unit>> RemoveRemoteAsync(string workspacePath, string name, CancellationToken cancellationToken = default);
}

/// <summary>The configured commit identity; a part is <c>null</c> when it is not set.</summary>
public sealed record GitIdentitySettings(string? Name, string? Email)
{
    public bool IsComplete => Name is not null && Email is not null;
}
