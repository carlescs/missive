using Missive.Application.Common;
using Missive.Domain.Git;

namespace Missive.Application.Git;

/// <summary>Error codes and messages for git operations.</summary>
public static class GitErrors
{
    public const string WorkspaceNotFoundCode = "git.workspace_not_found";
    public const string GitNotInstalledCode = "git.not_installed";
    public const string NotARepositoryCode = "git.not_a_repository";
    public const string AlreadyARepositoryCode = "git.already_a_repository";
    public const string InvalidCommitMessageCode = "git.invalid_commit_message";
    public const string NothingToCommitCode = "git.nothing_to_commit";
    public const string PathsNotChangedCode = "git.paths_not_changed";
    public const string PartialCommitDuringMergeCode = "git.partial_commit_during_merge";
    public const string UnresolvedConflictsCode = "git.unresolved_conflicts";
    public const string DetachedHeadCode = "git.detached_head";
    public const string NoUpstreamCode = "git.no_upstream";
    public const string NoRemoteCode = "git.no_remote";
    public const string PushRejectedCode = "git.push_rejected";
    public const string CommandFailedCode = "git.command_failed";
    public const string NothingToDiscardCode = "git.nothing_to_discard";
    public const string DiscardDuringMergeCode = "git.discard_during_merge";
    public const string NotMergingCode = "git.not_merging";
    public const string NotConflictedCode = "git.not_conflicted";
    public const string ConflictMarkersRemainCode = "git.conflict_markers_remain";
    public const string CommitNotFoundCode = "git.commit_not_found";
    public const string FileNotInCommitCode = "git.file_not_in_commit";
    public const string FileNotChangedCode = "git.file_not_changed";
    public const string InvalidRemoteCode = "git.invalid_remote";
    public const string RemoteExistsCode = "git.remote_exists";
    public const string RemoteNotFoundCode = "git.remote_not_found";
    public const string AuthenticationFailedCode = "git.authentication_failed";
    public const string RemoteUnreachableCode = "git.remote_unreachable";
    public const string IdentityUnknownCode = "git.identity_unknown";
    public const string InvalidIdentityCode = "git.invalid_identity";
    public const string LocalChangesOverwrittenCode = "git.local_changes_overwritten";
    public const string RepositoryLockedCode = "git.repository_locked";
    public const string UnsafeRepositoryCode = "git.unsafe_repository";
    public const string UnrelatedHistoriesCode = "git.unrelated_histories";
    public const string UpstreamGoneCode = "git.upstream_gone";
    public const string NoCommitsCode = "git.no_commits";
    public const string InvalidBranchNameCode = "git.invalid_branch_name";
    public const string BranchExistsCode = "git.branch_exists";
    public const string BranchNotFoundCode = "git.branch_not_found";
    public const string UncommittedChangesCode = "git.uncommitted_changes";
    public const string BranchDuringMergeCode = "git.branch_during_merge";
    public const string BranchNotMergedCode = "git.branch_not_merged";
    public const string CurrentBranchCode = "git.current_branch";

    public static Error WorkspaceNotFound(string path) =>
        new(WorkspaceNotFoundCode, $"The workspace folder '{path}' does not exist.");

    public static Error GitNotInstalled() =>
        new(GitNotInstalledCode, "Git was not found. Install git and make sure it is on the PATH.");

    public static Error NotARepository(string path) =>
        new(NotARepositoryCode, $"The workspace folder '{path}' is not inside a git repository.");

    public static Error AlreadyARepository(string path) =>
        new(AlreadyARepositoryCode, $"The workspace folder '{path}' is already inside a git repository.");

    public static Error InvalidCommitMessage() =>
        new(InvalidCommitMessageCode, "Enter a commit message.");

    public static Error NothingToCommit() =>
        new(NothingToCommitCode, "There are no changes to commit.");

    public static Error PathsNotChanged(IEnumerable<string> paths) =>
        new(PathsNotChangedCode, $"These files have no changes to commit: {string.Join(", ", paths)}.");

    public static Error NothingToDiscard() =>
        new(NothingToDiscardCode, "Select the changes to discard.");

    public static Error DiscardDuringMerge() =>
        new(DiscardDuringMergeCode, "A merge is in progress; resolve the conflicts or abort the merge instead of discarding changes.");

    public static Error PartialCommitDuringMerge() =>
        new(PartialCommitDuringMergeCode, "A merge is in progress; commit all changes to complete it.");

    public static Error UnresolvedConflicts(IEnumerable<GitChange> conflicts) =>
        new(
            UnresolvedConflictsCode,
            "Resolve the merge conflicts and mark them as resolved (git add) first: "
                + string.Join(", ", conflicts.Select(c => c.Path))
                + ".");

    public static Error DetachedHead() =>
        new(DetachedHeadCode, "No branch is checked out (detached HEAD). Check out a branch first.");

    public static Error NoUpstream(string branch) =>
        new(NoUpstreamCode, $"The branch '{branch}' doesn't exist on the remote yet, so there is nothing to pull. Publish it first.");

    public static Error NoRemote() =>
        new(NoRemoteCode, "The repository has no remote yet. Add one under Remotes, then try again.");

    public static Error AmbiguousRemote() =>
        new(NoRemoteCode, "The repository has several remotes and none is named 'origin', so Missive can't tell which one to use. Rename the one to use to 'origin'.");

    public static Error AuthenticationFailed(string details) =>
        new(
            AuthenticationFailedCode,
            WithDetails(
                "Git couldn't sign in to the remote. Missive uses your own git credentials: for an HTTPS remote, set up a credential helper "
                    + "(such as Git Credential Manager) or sign in once from a terminal; for an SSH remote, add your key to the SSH agent "
                    + "and connect once from a terminal so the host is trusted.",
                details));

    public static Error RemoteUnreachable(string details) =>
        new(RemoteUnreachableCode, WithDetails("Couldn't reach the remote. Check your network connection and the remote's URL.", details));

    public static Error IdentityUnknown() =>
        new(IdentityUnknownCode, "Git doesn't know who you are yet. Enter the name and email to record on your commits, then commit again.");

    public static Error InvalidIdentity() =>
        new(InvalidIdentityCode, "Enter a name and an email address (without < or > or line breaks).");

    public static Error LocalChangesOverwritten(IReadOnlyList<string> paths) =>
        new(
            LocalChangesOverwrittenCode,
            paths.Count == 0
                ? "The pull would overwrite uncommitted changes. Commit or discard them, then pull again."
                : $"The pull would overwrite your uncommitted changes to: {string.Join(", ", paths)}. Commit or discard them, then pull again.");

    public static Error RepositoryLocked(string details) =>
        new(
            RepositoryLockedCode,
            WithDetails(
                "Another git program is working in this repository. Wait for it to finish and try again; if none is running, "
                    + "a git process may have crashed, and the lock file named below must be deleted.",
                details));

    public static Error UnsafeRepository(string details) =>
        new(
            UnsafeRepositoryCode,
            WithDetails(
                "Git refuses to use this repository because its folder belongs to another user. If you trust it, run the command below in a terminal.",
                details));

    public static Error UnrelatedHistories() =>
        new(UnrelatedHistoriesCode, "The upstream branch has a history unrelated to yours, so git won't merge it.");

    public static Error UpstreamGone(string upstream) =>
        new(UpstreamGoneCode, $"The upstream branch '{upstream}' no longer exists on the remote. Publish the branch to create it again.");

    public static Error NoCommits() =>
        new(NoCommitsCode, "The branch has no commits yet. Commit your changes first, then publish it.");

    public static Error InvalidBranchName(string message) =>
        new(InvalidBranchNameCode, message);

    public static Error BranchExists(string name) =>
        new(BranchExistsCode, $"A branch named '{name}' already exists.");

    public static Error LocalBranchExists(string name) =>
        new(BranchExistsCode, $"A local branch named '{name}' already exists. Switch to it instead.");

    public static Error BranchNotFound(string name) =>
        new(BranchNotFoundCode, $"There is no branch named '{name}'.");

    public static Error UncommittedChanges(IReadOnlyList<string> paths) =>
        new(
            UncommittedChangesCode,
            paths.Count == 0
                ? "You have uncommitted changes that switching branches could overwrite. Commit or discard them, then switch."
                : $"You have uncommitted changes to: {string.Join(", ", paths)}. Commit or discard them, then switch branches.");

    public static Error NoCommitsToBranchFrom() =>
        new(NoCommitsCode, "The current branch has no commits yet, so a new branch has nothing to start from. Commit first.");

    public static Error BranchDuringMerge() =>
        new(BranchDuringMergeCode, "A merge is in progress; complete it or abort it before switching branches.");

    public static Error BranchNotMerged(string name) =>
        new(
            BranchNotMergedCode,
            $"The branch '{name}' has commits that are not merged anywhere else; deleting it would lose them.");

    public static Error CurrentBranch(string name) =>
        new(CurrentBranchCode, $"'{name}' is the current branch. Switch to another branch before deleting it.");

    public static Error PushRejected(string details) =>
        new(PushRejectedCode, WithDetails("The remote rejected the push. Pull the latest changes and try again.", details));

    public static Error NotMerging() =>
        new(NotMergingCode, "No merge is in progress.");

    public static Error NotConflicted(string path) =>
        new(NotConflictedCode, $"'{path}' has no merge conflict to resolve.");

    public static Error ConflictMarkersRemain(string path) =>
        new(
            ConflictMarkersRemainCode,
            $"'{path}' still contains conflict markers (<<<<<<<, =======, >>>>>>>). Edit them out, or keep one side's version.");

    public static Error CommitNotFound(string commitId) =>
        new(CommitNotFoundCode, $"The commit '{commitId}' was not found in this repository.");

    public static Error FileNotInCommit(string commitId, string path) =>
        new(FileNotInCommitCode, $"'{path}' was not changed in commit '{Abbreviate(commitId)}'.");

    public static Error FileNotChanged(string path) =>
        new(FileNotChangedCode, $"'{path}' has no changes to show.");

    public static Error InvalidRemote(string message) =>
        new(InvalidRemoteCode, message);

    public static Error RemoteExists(string name) =>
        new(RemoteExistsCode, $"A remote named '{name}' already exists.");

    public static Error RemoteNotFound(string name) =>
        new(RemoteNotFoundCode, $"There is no remote named '{name}'.");

    public static Error CommandFailed(string command, string details) =>
        new(CommandFailedCode, WithDetails($"git {command} failed.", details));

    private static string Abbreviate(string commitId) => commitId.Length > 7 ? commitId[..7] : commitId;

    private static string WithDetails(string message, string details) =>
        string.IsNullOrWhiteSpace(details) ? message : $"{message}\n{details.Trim()}";
}
