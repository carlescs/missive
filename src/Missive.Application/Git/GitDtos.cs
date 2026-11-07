using Missive.Application.Requests;
using Missive.Domain.Git;

namespace Missive.Application.Git;

// Wire format of the git messages, sent to the UI by the bridge. Keep in sync with
// src/Missive.Client/src/app/models/messages.ts.

/// <param name="Status">One of <c>modified</c>, <c>added</c>, <c>deleted</c>, <c>renamed</c>, <c>untracked</c>, <c>conflicted</c>.</param>
public sealed record GitChangeDto(string Path, string Status, string? OriginalPath)
{
    public static GitChangeDto From(GitChange change) =>
        new(change.Path, GitStatusDto.StatusName(change.Status), change.OriginalPath);
}

public sealed record GitStatusDto(
    string? Branch,
    string? Upstream,
    int Ahead,
    int Behind,
    bool Merging,
    IReadOnlyList<GitChangeDto> Changes,
    string? MergeMessage = null,
    bool Unborn = false,
    bool UpstreamGone = false)
{
    public static GitStatusDto From(GitStatus status) =>
        new(
            status.Branch,
            status.Upstream,
            status.Ahead,
            status.Behind,
            status.IsMerging,
            status.Changes.Select(GitChangeDto.From).ToList(),
            status.MergeMessage,
            status.IsUnborn,
            status.IsUpstreamGone);

    internal static string StatusName(GitFileStatus status) =>
        status switch
        {
            GitFileStatus.Modified => "modified",
            GitFileStatus.Added => "added",
            GitFileStatus.Deleted => "deleted",
            GitFileStatus.Renamed => "renamed",
            GitFileStatus.Untracked => "untracked",
            GitFileStatus.Conflicted => "conflicted",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };
}

public sealed record GitCommitDto(string CommitId, GitStatusDto Status)
{
    public static GitCommitDto From(CommitChangesResult result) => new(result.CommitId, GitStatusDto.From(result.Status));
}

/// <param name="Outcome">One of <c>upToDate</c>, <c>updated</c>, <c>conflicted</c>.</param>
public sealed record GitPullDto(string Outcome, GitStatusDto Status)
{
    public static GitPullDto From(PullChangesResult result) =>
        new(
            result.Outcome switch
            {
                PullOutcome.UpToDate => "upToDate",
                PullOutcome.Updated => "updated",
                _ => "conflicted",
            },
            GitStatusDto.From(result.Status));
}

public sealed record GitCommitInfoDto(
    string Id,
    IReadOnlyList<string> Parents,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset Date,
    string Subject)
{
    public static GitCommitInfoDto From(GitCommitInfo commit) =>
        new(commit.Id, commit.ParentIds, commit.AuthorName, commit.AuthorEmail, commit.AuthoredAt, commit.Subject);
}

public sealed record GitHistoryDto(IReadOnlyList<GitCommitInfoDto> Commits, bool HasMore)
{
    public static GitHistoryDto From(GitHistoryPage page) =>
        new(page.Commits.Select(GitCommitInfoDto.From).ToList(), page.HasMore);
}

public sealed record GitCommitDetailsDto(GitCommitInfoDto Commit, string Body, IReadOnlyList<GitChangeDto> Files)
{
    public static GitCommitDetailsDto From(GitCommitDetails details) =>
        new(GitCommitInfoDto.From(details.Commit), details.Body, details.Files.Select(GitChangeDto.From).ToList());
}

/// <summary>
/// A file before and after a commit; a version is <c>null</c> when the file didn't exist there. For
/// request files, each existing side is also parsed.
/// </summary>
/// <param name="Binary">Whether the file looks binary; its versions are then left out.</param>
/// <param name="IsRequest">Whether the file is a request file of the workspace.</param>
/// <param name="OriginalRequest">The parsed version before the commit, when <paramref name="IsRequest"/> and it could be read.</param>
/// <param name="ModifiedRequest">The parsed version in the commit, likewise.</param>
/// <param name="RequestError">Why a side of a request file could not be read as a request, if it couldn't.</param>
public sealed record GitFileRevisionDto(
    string CommitId,
    GitChangeDto Change,
    string? Before,
    string? After,
    bool Binary,
    bool IsRequest = false,
    RequestDto? OriginalRequest = null,
    RequestDto? ModifiedRequest = null,
    string? RequestError = null)
{
    internal static GitFileRevisionDto From(GitFileRevision revision, GitRequestSides requests) =>
        revision.IsBinary
            ? new(revision.CommitId, GitChangeDto.From(revision.Change), null, null, true)
            : new(
                revision.CommitId,
                GitChangeDto.From(revision.Change),
                revision.Before,
                revision.After,
                false,
                requests.IsRequest,
                requests.OriginalRequest,
                requests.ModifiedRequest,
                requests.RequestError);
}

/// <summary>Both sides of a conflicted file; a side is <c>null</c> when it deleted the file.</summary>
public sealed record GitConflictDto(
    string Path,
    string? Base,
    string? Ours,
    string? Theirs,
    string? Working,
    bool HasConflictMarkers)
{
    public static GitConflictDto From(GitConflict conflict) =>
        new(conflict.Path, conflict.Base, conflict.Ours, conflict.Theirs, conflict.Working, conflict.HasConflictMarkers);
}

/// <summary>
/// The committed and working tree versions of a changed file. A text is <c>null</c> when the file
/// doesn't exist on that side, or is binary. For request files, each existing side is also parsed.
/// </summary>
/// <param name="IsRequest">Whether the file is a request file of the workspace.</param>
/// <param name="OriginalRequest">The parsed committed request, when <paramref name="IsRequest"/> and it could be read.</param>
/// <param name="ModifiedRequest">The parsed working tree request, likewise.</param>
/// <param name="RequestError">Why a side of a request file could not be read as a request, if it couldn't.</param>
public sealed record GitFileDiffDto(
    string Path,
    string Status,
    string? OriginalPath,
    bool Binary,
    string? Original,
    string? Modified,
    bool IsRequest,
    RequestDto? OriginalRequest = null,
    RequestDto? ModifiedRequest = null,
    string? RequestError = null);

/// <param name="PushUrl">The URL pushed to; equal to <paramref name="FetchUrl"/> unless a push URL is set.</param>
public sealed record GitRemoteDto(string Name, string FetchUrl, string PushUrl)
{
    public static GitRemoteDto From(GitRemote remote) => new(remote.Name, remote.FetchUrl, remote.PushUrl);

    public static IReadOnlyList<GitRemoteDto> From(IReadOnlyList<GitRemote> remotes) => remotes.Select(From).ToList();
}

/// <param name="Name">The short name: <c>main</c>, or <c>origin/main</c> for a remote-tracking branch.</param>
/// <param name="Remote">For a remote-tracking branch, its remote; <c>null</c> for a local branch.</param>
/// <param name="LocalName">For a remote-tracking branch, the name a local branch checked out from it gets.</param>
/// <param name="TrackedBy">For a remote-tracking branch, the local branch that tracks it, if any.</param>
public sealed record GitBranchDto(
    string Name,
    string? Remote,
    bool Current,
    string? Upstream,
    string CommitId,
    DateTimeOffset? Date,
    string Subject,
    string? LocalName = null,
    string? TrackedBy = null);

public sealed record GitBranchesDto(GitStatusDto Status, IReadOnlyList<GitBranchDto> Branches)
{
    public static GitBranchesDto From(GitBranchesResult result)
    {
        var branches = result.Branches;
        return new(
            GitStatusDto.From(result.Status),
            branches
                .Select(b => new GitBranchDto(
                    b.Name,
                    b.Remote,
                    b.IsCurrent,
                    b.Upstream,
                    b.CommitId,
                    b.CommittedAt,
                    b.Subject,
                    b.IsRemote ? b.LocalName : null,
                    b.IsRemote ? branches.FirstOrDefault(l => !l.IsRemote && l.Upstream == b.Name)?.Name : null))
                .ToList());
    }
}

public sealed record GitIdentityDto(string? Name, string? Email)
{
    public static GitIdentityDto From(GitIdentitySettings identity) => new(identity.Name, identity.Email);
}
