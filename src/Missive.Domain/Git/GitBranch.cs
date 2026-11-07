namespace Missive.Domain.Git;

/// <summary>A local branch, or a remote-tracking branch (a remote's branch as of the last fetch).</summary>
/// <param name="Name">The short name: <c>main</c> for a local branch, <c>origin/main</c> for a remote one.</param>
/// <param name="Remote">For a remote-tracking branch, its remote (<c>origin</c>); <c>null</c> for a local branch.</param>
/// <param name="IsCurrent">Whether it is the checked-out branch.</param>
/// <param name="Upstream">For a local branch, the branch it tracks (<c>origin/main</c>), if any.</param>
/// <param name="CommitId">The commit it points to.</param>
/// <param name="CommittedAt">When that commit was made.</param>
/// <param name="Subject">That commit's subject line.</param>
public sealed record GitBranch(
    string Name,
    string? Remote,
    bool IsCurrent,
    string? Upstream,
    string CommitId,
    DateTimeOffset? CommittedAt,
    string Subject)
{
    public bool IsRemote => Remote is not null;

    /// <summary>
    /// The name a local branch checked out from this remote-tracking branch gets: its name
    /// without the remote (<c>origin/feature/x</c> → <c>feature/x</c>).
    /// </summary>
    public string LocalName => Remote is null ? Name : Name[(Remote.Length + 1)..];
}
