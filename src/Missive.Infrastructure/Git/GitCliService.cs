using System.Globalization;
using Missive.Application.Common;
using Missive.Application.Git;
using Missive.Domain.Git;

namespace Missive.Infrastructure.Git;

/// <summary>
/// <see cref="IGitService"/> backed by the git command-line client, run in the workspace folder.
/// </summary>
public sealed class GitCliService : IGitService
{
    /// <summary>Keeps the workspace's local-only folder out of status and commits, even if it is not git-ignored.</summary>
    private const string ExcludeLocalFolder = ":(exclude).missive";

    /// <summary>How long to wait before each new try of a command that found the repository locked.</summary>
    private static readonly TimeSpan[] LockRetryDelays =
        [TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1)];

    private readonly GitCommandRunner _runner;

    public GitCliService()
        : this("git")
    {
    }

    /// <param name="gitExecutable">The git executable to run: a name on the PATH or a full path.</param>
    public GitCliService(string gitExecutable)
    {
        _runner = new GitCommandRunner(gitExecutable);
    }

    public async Task<Result<GitStatus>> GetStatusAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        var prefix = await GetWorkspacePrefixAsync(workspacePath, cancellationToken);
        if (prefix.IsFailure)
        {
            return prefix.Error!;
        }

        var status = await RunAsync(
            workspacePath,
            ["--no-optional-locks", "status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all", "--", ".", ExcludeLocalFolder],
            cancellationToken);
        if (status.IsFailure)
        {
            return status.Error!;
        }

        if (!status.Value.Succeeded)
        {
            return GitFailure.Classify("status", status.Value.StandardError);
        }

        var merging = await IsMergingAsync(workspacePath, cancellationToken);
        if (merging.IsFailure)
        {
            return merging.Error!;
        }

        var parsed = PorcelainStatusParser.Parse(status.Value.StandardOutput, prefix.Value, merging.Value);
        if (!merging.Value)
        {
            return parsed;
        }

        var mergeMessage = await ReadMergeMessageAsync(workspacePath, cancellationToken);
        if (mergeMessage.IsFailure)
        {
            return mergeMessage.Error!;
        }

        return parsed with { MergeMessage = mergeMessage.Value };
    }

    public async Task<Result<Unit>> InitAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        var prefix = await GetWorkspacePrefixAsync(workspacePath, cancellationToken);
        if (prefix.IsSuccess)
        {
            return GitErrors.AlreadyARepository(workspacePath);
        }

        // Only a folder git positively reports as outside any repository is initialized; any other
        // failure (git missing, unsafe ownership...) is passed on rather than nesting a repository.
        if (prefix.Error!.Code != GitErrors.NotARepositoryCode)
        {
            return prefix.Error!;
        }

        var init = await RunCheckedAsync(workspacePath, "init", ["init", "--quiet"], cancellationToken);
        if (init.IsFailure)
        {
            return init.Error!;
        }

        try
        {
            await GitIgnoreFile.EnsureIgnoredAsync(workspacePath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GitErrors.CommandFailed("init", $"The repository was created, but .gitignore could not be updated: {ex.Message}");
        }

        return Unit.Value;
    }

    public async Task<Result<GitConflict>> GetConflictAsync(
        string workspacePath,
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!IsSafeRelativePath(path))
        {
            return GitErrors.NotConflicted(path);
        }

        var prefix = await GetWorkspacePrefixAsync(workspacePath, cancellationToken);
        if (prefix.IsFailure)
        {
            return prefix.Error!;
        }

        var versions = new string?[3];
        for (var stage = 1; stage <= 3; stage++)
        {
            var version = await ReadStageAsync(workspacePath, path, stage, cancellationToken);
            if (version.IsFailure)
            {
                return version.Error!;
            }

            versions[stage - 1] = version.Value;
        }

        var working = await ReadWorkingFileAsync(workspacePath, path, "show", cancellationToken);
        if (working.IsFailure)
        {
            return working.Error!;
        }

        return new GitConflict(path, versions[0], versions[1], versions[2], working.Value);
    }

    public async Task<Result<Unit>> ResolveConflictAsync(
        string workspacePath,
        string path,
        ConflictResolution resolution,
        CancellationToken cancellationToken = default)
    {
        if (!IsSafeRelativePath(path))
        {
            return GitErrors.NotConflicted(path);
        }

        var prefix = await GetWorkspacePrefixAsync(workspacePath, cancellationToken);
        if (prefix.IsFailure)
        {
            return prefix.Error!;
        }

        var pathspec = ":(literal)" + path;
        if (resolution == ConflictResolution.Edited)
        {
            // Stages the file as it is now, or its deletion if the user deleted it.
            var added = await RunCheckedAsync(workspacePath, "add", ["add", "--all", "--", pathspec], cancellationToken);
            return Done(added);
        }

        var stage = resolution == ConflictResolution.Ours ? 2 : 3;
        var version = await ReadStageAsync(workspacePath, path, stage, cancellationToken);
        if (version.IsFailure)
        {
            return version.Error!;
        }

        if (version.Value is null)
        {
            // The chosen side deleted the file: resolving to it means deleting it.
            var removed = await RunCheckedAsync(workspacePath, "rm", ["rm", "--quiet", "--force", "--", pathspec], cancellationToken);
            return Done(removed);
        }

        var side = resolution == ConflictResolution.Ours ? "--ours" : "--theirs";
        var checkout = await RunCheckedAsync(workspacePath, "checkout", ["checkout", side, "--", pathspec], cancellationToken);
        if (checkout.IsFailure)
        {
            return checkout.Error!;
        }

        var add = await RunCheckedAsync(workspacePath, "add", ["add", "--", pathspec], cancellationToken);
        return Done(add);
    }

    public async Task<Result<Unit>> AbortMergeAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        var prefix = await GetWorkspacePrefixAsync(workspacePath, cancellationToken);
        if (prefix.IsFailure)
        {
            return prefix.Error!;
        }

        var abort = await RunCheckedAsync(workspacePath, "merge --abort", ["merge", "--abort"], cancellationToken);
        return Done(abort);
    }

    public async Task<Result<GitFileDiff>> GetFileDiffAsync(
        string workspacePath,
        GitChange change,
        CancellationToken cancellationToken = default)
    {
        if (!IsSafeRelativePath(change.Path) || (change.OriginalPath is { } from && !IsSafeRelativePath(from)))
        {
            return GitErrors.FileNotChanged(change.Path);
        }

        var prefix = await GetWorkspacePrefixAsync(workspacePath, cancellationToken);
        if (prefix.IsFailure)
        {
            return prefix.Error!;
        }

        // Both sides are read whatever the status says: a file removed from the index but kept on
        // disk is reported as deleted and untracked, yet exists in HEAD and in the working tree.
        // "./" makes the path relative to the working directory (the workspace) instead of the repository root.
        var committed = await RunAsync(workspacePath, ["show", $"HEAD:./{change.OriginalPath ?? change.Path}"], cancellationToken);
        if (committed.IsFailure)
        {
            return committed.Error!;
        }

        // Fails when there is no commit yet, or the file isn't in it.
        var original = committed.Value.Succeeded ? committed.Value.StandardOutput : null;

        var modified = await ReadWorkingFileAsync(workspacePath, change.Path, "diff", cancellationToken);
        if (modified.IsFailure)
        {
            return modified.Error!;
        }

        var binary = (original is not null && GitFileDiff.LooksBinary(original))
            || (modified.Value is not null && GitFileDiff.LooksBinary(modified.Value));

        return binary
            ? new GitFileDiff(change.Path, change.Status, change.OriginalPath, null, null, IsBinary: true)
            : new GitFileDiff(change.Path, change.Status, change.OriginalPath, original, modified.Value);
    }

    /// <summary>
    /// The working tree version of a file, for a diff or a conflict, or <c>null</c> when it doesn't
    /// exist. Links are never followed, so a link can't expose a file outside the workspace: a
    /// symbolic link reads as its target path, which is also what git stores for it, and a path
    /// through a linked folder is refused. <paramref name="command"/> names the operation in errors.
    /// </summary>
    private static async Task<Result<string?>> ReadWorkingFileAsync(
        string workspacePath,
        string path,
        string command,
        CancellationToken cancellationToken)
    {
        try
        {
            var current = workspacePath;
            var segments = path.Split('/');
            for (var i = 0; i < segments.Length; i++)
            {
                current = Path.Combine(current, segments[i]);
                var info = new FileInfo(current);
                if (!info.Exists && !Directory.Exists(current) && info.LinkTarget is null)
                {
                    return Result<string?>.Success(null);
                }

                if (info.LinkTarget is { } target)
                {
                    return i == segments.Length - 1
                        ? Result<string?>.Success(target)
                        : GitErrors.CommandFailed(command, $"'{path}' is inside a linked folder, so it is not read.");
                }
            }

            return File.Exists(current)
                ? Result<string?>.Success(await File.ReadAllTextAsync(current, cancellationToken))
                : Result<string?>.Success(null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GitErrors.CommandFailed(command, $"Could not read '{path}': {ex.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<GitBranch>>> ListBranchesAsync(
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        var prefix = await GetWorkspacePrefixAsync(workspacePath, cancellationToken);
        if (prefix.IsFailure)
        {
            return prefix.Error!;
        }

        var remotes = await RunCheckedAsync(workspacePath, "remote", ["remote"], cancellationToken);
        if (remotes.IsFailure)
        {
            return remotes.Error!;
        }

        var refs = await RunCheckedAsync(
            workspacePath,
            "for-each-ref",
            [
                "for-each-ref",
                "--format=%(refname)%00%(objectname)%00%(upstream:short)%00%(HEAD)%00%(committerdate:iso-strict)%00%(symref)%00%(contents:subject)",
                "refs/heads",
                "refs/remotes",
            ],
            cancellationToken);
        if (refs.IsFailure)
        {
            return refs.Error!;
        }

        var remoteNames = remotes.Value.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Result<IReadOnlyList<GitBranch>>.Success(ParseBranches(refs.Value.StandardOutput, remoteNames));
    }

    /// <summary>
    /// Parses the <c>git for-each-ref</c> output of <see cref="ListBranchesAsync"/>: one line per
    /// ref with NUL-separated fields. Symbolic refs (a remote's <c>HEAD</c>) are skipped.
    /// </summary>
    internal static IReadOnlyList<GitBranch> ParseBranches(string output, IReadOnlyList<string> remotes)
    {
        const string Heads = "refs/heads/";
        const string Remotes = "refs/remotes/";
        var local = new List<GitBranch>();
        var remote = new List<GitBranch>();
        foreach (var line in output.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('\0');
            if (fields.Length < 7 || fields[5].Length > 0)
            {
                continue;
            }

            DateTimeOffset? date = DateTimeOffset.TryParse(fields[4], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed
                : null;
            if (fields[0].StartsWith(Heads, StringComparison.Ordinal))
            {
                local.Add(new GitBranch(
                    fields[0][Heads.Length..],
                    null,
                    fields[3] == "*",
                    fields[2].Length > 0 ? fields[2] : null,
                    fields[1],
                    date,
                    fields[6]));
            }
            else if (fields[0].StartsWith(Remotes, StringComparison.Ordinal))
            {
                var name = fields[0][Remotes.Length..];
                // Remote names can contain slashes: the longest remote the name starts with owns it.
                var owner = remotes
                    .Where(r => name.StartsWith(r + "/", StringComparison.Ordinal))
                    .MaxBy(r => r.Length)
                    ?? (name.Contains('/') ? name[..name.IndexOf('/')] : null);
                if (owner is not null)
                {
                    remote.Add(new GitBranch(name, owner, false, null, fields[1], date, fields[6]));
                }
            }
        }

        return [.. local.OrderBy(b => b.Name, StringComparer.Ordinal), .. remote.OrderBy(b => b.Name, StringComparer.Ordinal)];
    }

    public async Task<Result<Unit>> SwitchBranchAsync(string workspacePath, string branch, CancellationToken cancellationToken = default)
    {
        var prefix = await GetWorkspacePrefixAsync(workspacePath, cancellationToken);
        if (prefix.IsFailure)
        {
            return prefix.Error!;
        }

        // --no-guess: never create a branch from a remote one by accident.
        return await RunSwitchAsync(workspacePath, ["switch", "--no-guess", branch], cancellationToken);
    }

    public async Task<Result<Unit>> CreateBranchAsync(
        string workspacePath,
        BranchName name,
        bool switchTo,
        CancellationToken cancellationToken = default)
    {
        var prefix = await GetWorkspacePrefixAsync(workspacePath, cancellationToken);
        if (prefix.IsFailure)
        {
            return prefix.Error!;
        }

        if (!switchTo)
        {
            return Done(await RunCheckedAsync(workspacePath, "branch", ["branch", "--no-track", name.Value], cancellationToken));
        }

        return await RunSwitchAsync(workspacePath, ["switch", "--no-track", "-c", name.Value], cancellationToken);
    }

    public async Task<Result<Unit>> CheckoutRemoteBranchAsync(
        string workspacePath,
        string remoteBranch,
        BranchName localName,
        CancellationToken cancellationToken = default)
    {
        var prefix = await GetWorkspacePrefixAsync(workspacePath, cancellationToken);
        if (prefix.IsFailure)
        {
            return prefix.Error!;
        }

        return await RunSwitchAsync(
            workspacePath,
            ["switch", "--track", "-c", localName.Value, "refs/remotes/" + remoteBranch],
            cancellationToken);
    }

    public async Task<Result<Unit>> DeleteBranchAsync(
        string workspacePath,
        string branch,
        bool force,
        CancellationToken cancellationToken = default)
    {
        var prefix = await GetWorkspacePrefixAsync(workspacePath, cancellationToken);
        if (prefix.IsFailure)
        {
            return prefix.Error!;
        }

        var result = await RunAsync(workspacePath, ["branch", force ? "-D" : "-d", "--", branch], cancellationToken);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        if (!result.Value.Succeeded)
        {
            var details = Details(result.Value);
            return details.Contains("not fully merged", StringComparison.OrdinalIgnoreCase)
                ? GitErrors.BranchNotMerged(branch)
                : GitFailure.Classify("branch", details);
        }

        return Unit.Value;
    }

    /// <summary>Runs a <c>git switch</c>, explaining refusals over uncommitted changes.</summary>
    private async Task<Result<Unit>> RunSwitchAsync(string workspacePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await RunAsync(workspacePath, arguments, cancellationToken);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        if (result.Value.Succeeded)
        {
            return Unit.Value;
        }

        var details = Details(result.Value);
        if (details.Contains("would be overwritten by checkout", StringComparison.OrdinalIgnoreCase)
            || details.Contains("would be overwritten by switch", StringComparison.OrdinalIgnoreCase)
            || details.Contains("untracked working tree files would be overwritten", StringComparison.OrdinalIgnoreCase))
        {
            return GitErrors.UncommittedChanges(GitFailure.OverwrittenPaths(details));
        }

        if (details.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            var created = arguments.ToList().IndexOf("-c");
            return GitErrors.BranchExists(created >= 0 && created + 1 < arguments.Count ? arguments[created + 1] : string.Empty);
        }

        return GitFailure.Classify("switch", details);
    }

    public async Task<Result<IReadOnlyList<GitRemote>>> ListRemotesAsync(
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        var prefix = await GetWorkspacePrefixAsync(workspacePath, cancellationToken);
        if (prefix.IsFailure)
        {
            return prefix.Error!;
        }

        var result = await RunCheckedAsync(workspacePath, "remote", ["remote", "--verbose"], cancellationToken);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        return Result<IReadOnlyList<GitRemote>>.Success(ParseRemotes(result.Value.StandardOutput));
    }

    public Task<Result<Unit>> AddRemoteAsync(
        string workspacePath,
        RemoteName name,
        RemoteUrl url,
        CancellationToken cancellationToken = default) =>
        RunRemoteAsync(workspacePath, "remote add", ["remote", "add", "--", name.Value, url.Value], cancellationToken);

    public Task<Result<Unit>> RenameRemoteAsync(
        string workspacePath,
        string name,
        RemoteName newName,
        CancellationToken cancellationToken = default) =>
        RunRemoteAsync(workspacePath, "remote rename", ["remote", "rename", "--", name, newName.Value], cancellationToken);

    public Task<Result<Unit>> SetRemoteUrlAsync(
        string workspacePath,
        string name,
        RemoteUrl url,
        CancellationToken cancellationToken = default) =>
        RunRemoteAsync(workspacePath, "remote set-url", ["remote", "set-url", "--", name, url.Value], cancellationToken);

    public Task<Result<Unit>> RemoveRemoteAsync(
        string workspacePath,
        string name,
        CancellationToken cancellationToken = default) =>
        RunRemoteAsync(workspacePath, "remote remove", ["remote", "remove", "--", name], cancellationToken);

    private async Task<Result<Unit>> RunRemoteAsync(
        string workspacePath,
        string command,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var prefix = await GetWorkspacePrefixAsync(workspacePath, cancellationToken);
        if (prefix.IsFailure)
        {
            return prefix.Error!;
        }

        // "--" keeps names and URLs from ever being read as options.
        return Done(await RunCheckedAsync(workspacePath, command, arguments, cancellationToken));
    }

    /// <summary>
    /// Parses <c>git remote --verbose</c>: a <c>name&lt;TAB&gt;url (fetch)</c> and a
    /// <c>name&lt;TAB&gt;url (push)</c> line per remote.
    /// </summary>
    internal static IReadOnlyList<GitRemote> ParseRemotes(string output)
    {
        var fetch = new Dictionary<string, string>(StringComparer.Ordinal);
        var push = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            if (tab <= 0)
            {
                continue;
            }

            var name = line[..tab];
            var rest = line[(tab + 1)..];
            if (rest.EndsWith(" (fetch)", StringComparison.Ordinal))
            {
                fetch[name] = rest[..^" (fetch)".Length];
            }
            else if (rest.EndsWith(" (push)", StringComparison.Ordinal))
            {
                push[name] = rest[..^" (push)".Length];
            }
        }

        return fetch.Keys
            .Union(push.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(name =>
            {
                var fetchUrl = fetch.GetValueOrDefault(name) ?? push.GetValueOrDefault(name) ?? string.Empty;
                return new GitRemote(name, fetchUrl, push.GetValueOrDefault(name) ?? fetchUrl);
            })
            .ToList();
    }

    public async Task<Result<IReadOnlyList<GitCommitInfo>>> GetLogAsync(
        string workspacePath,
        int skip,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        var prefix = await GetWorkspacePrefixAsync(workspacePath, cancellationToken);
        if (prefix.IsFailure)
        {
            return prefix.Error!;
        }

        var head = await GetHeadAsync(workspacePath, cancellationToken);
        if (head.IsFailure)
        {
            return head.Error!;
        }

        if (head.Value.Length == 0)
        {
            // A branch with no commits yet.
            return Result<IReadOnlyList<GitCommitInfo>>.Success([]);
        }

        var log = await RunCheckedAsync(
            workspacePath,
            "log",
            [
                "log",
                "--no-show-signature",
                "--no-color",
                $"--format={CommitLog.Format}{CommitLog.RecordSeparator}",
                $"--skip={Math.Max(0, skip)}",
                $"--max-count={Math.Max(0, maxCount)}",
                "HEAD",
                "--",
                ".",
                ExcludeLocalFolder,
            ],
            cancellationToken);
        if (log.IsFailure)
        {
            return log.Error!;
        }

        return Result<IReadOnlyList<GitCommitInfo>>.Success(CommitLog.ParseLog(log.Value.StandardOutput));
    }

    public async Task<Result<GitCommitDetails>> GetCommitAsync(
        string workspacePath,
        string commitId,
        CancellationToken cancellationToken = default)
    {
        var prefix = await GetWorkspacePrefixAsync(workspacePath, cancellationToken);
        if (prefix.IsFailure)
        {
            return prefix.Error!;
        }

        var id = await ResolveCommitAsync(workspacePath, commitId, cancellationToken);
        if (id.IsFailure)
        {
            return id.Error!;
        }

        var show = await RunCheckedAsync(
            workspacePath,
            "show",
            ["show", "--no-show-signature", "--no-color", "--no-patch", $"--format={CommitLog.Format}{CommitLog.FieldSeparator}%b", id.Value],
            cancellationToken);
        if (show.IsFailure)
        {
            return show.Error!;
        }

        var (commit, body) = CommitLog.ParseCommit(show.Value.StandardOutput);

        // Compared with the first parent, like most git tools show a merge. --relative limits the
        // diff to the workspace folder and makes its paths relative to it.
        List<string> diffArguments = ["diff-tree", "-r", "-z", "--no-commit-id", "--name-status", "-M", "--relative"];
        if (commit.FirstParentId is { } parent)
        {
            diffArguments.AddRange([parent, commit.Id]);
        }
        else
        {
            diffArguments.AddRange(["--root", commit.Id]);
        }

        var diff = await RunCheckedAsync(workspacePath, "diff-tree", diffArguments, cancellationToken);
        if (diff.IsFailure)
        {
            return diff.Error!;
        }

        var files = CommitLog.ParseNameStatus(diff.Value.StandardOutput)
            .Where(f => !IsLocalFolder(f.Path))
            .ToList();
        return new GitCommitDetails(commit, body, files);
    }

    public async Task<Result<string?>> ReadFileAtCommitAsync(
        string workspacePath,
        string commitId,
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!IsSafeRelativePath(path) || IsLocalFolder(path))
        {
            return Result<string?>.Success(null);
        }

        var prefix = await GetWorkspacePrefixAsync(workspacePath, cancellationToken);
        if (prefix.IsFailure)
        {
            return prefix.Error!;
        }

        var id = await ResolveCommitAsync(workspacePath, commitId, cancellationToken);
        if (id.IsFailure)
        {
            return id.Error!;
        }

        // "./" makes the path relative to the working directory (the workspace) instead of the repository root.
        var result = await RunAsync(workspacePath, ["show", "--no-textconv", $"{id.Value}:./{path}"], cancellationToken);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        return Result<string?>.Success(result.Value.Succeeded ? result.Value.StandardOutput : null);
    }

    /// <summary>
    /// The full id of a commit given as a (possibly abbreviated) hexadecimal id. Anything else,
    /// including ref names and options, is reported as not found.
    /// </summary>
    private async Task<Result<string>> ResolveCommitAsync(string workspacePath, string commitId, CancellationToken cancellationToken)
    {
        if (commitId.Length is < 4 or > 64 || !commitId.All(char.IsAsciiHexDigit))
        {
            return GitErrors.CommitNotFound(commitId);
        }

        var result = await RunAsync(workspacePath, ["rev-parse", "--quiet", "--verify", commitId + "^{commit}"], cancellationToken);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        return result.Value.Succeeded
            ? result.Value.StandardOutput.Trim()
            : GitErrors.CommitNotFound(commitId);
    }

    private static bool IsLocalFolder(string path) =>
        path == ".missive" || path.StartsWith(".missive/", StringComparison.Ordinal);

    /// <summary>
    /// One version of a conflicted file from the index: stage 1 is the common ancestor, 2 the
    /// current branch, 3 the incoming branch. <c>null</c> when that side has no such file.
    /// </summary>
    private async Task<Result<string?>> ReadStageAsync(string workspacePath, string path, int stage, CancellationToken cancellationToken)
    {
        // "./" makes the path relative to the working directory (the workspace) instead of the repository root.
        var result = await RunAsync(workspacePath, ["show", $":{stage}:./{path}"], cancellationToken);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        return Result<string?>.Success(result.Value.Succeeded ? result.Value.StandardOutput : null);
    }

    /// <summary>git's proposed merge commit message, without its comment lines; <c>null</c> if there is none.</summary>
    private async Task<Result<string?>> ReadMergeMessageAsync(string workspacePath, CancellationToken cancellationToken)
    {
        var gitPath = await RunCheckedAsync(workspacePath, "rev-parse", ["rev-parse", "--git-path", "MERGE_MSG"], cancellationToken);
        if (gitPath.IsFailure)
        {
            return gitPath.Error!;
        }

        var file = Path.Combine(workspacePath, gitPath.Value.StandardOutput.TrimEnd('\r', '\n'));
        try
        {
            if (!File.Exists(file))
            {
                return Result<string?>.Success(null);
            }

            var lines = (await File.ReadAllLinesAsync(file, cancellationToken)).Where(line => !line.StartsWith('#'));
            var message = string.Join('\n', lines).Trim();
            return Result<string?>.Success(message.Length == 0 ? null : message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The message is only a suggestion; the merge can be committed without it.
            return Result<string?>.Success(null);
        }
    }

    private static Result<Unit> Done(Result<GitCommandResult> result) =>
        result.IsFailure ? Result<Unit>.Failure(result.Error!) : Result<Unit>.Success(Unit.Value);

    /// <summary>Defense in depth: a workspace-relative path that can't leave the workspace.</summary>
    private static bool IsSafeRelativePath(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && !Path.IsPathRooted(path)
        && !path.Replace('\\', '/').Split('/').Any(segment => segment is "" or "." or "..");

    public async Task<Result<string>> CommitAsync(
        string workspacePath,
        CommitMessage message,
        IReadOnlyList<string>? paths,
        CancellationToken cancellationToken = default)
    {
        var prefix = await GetWorkspacePrefixAsync(workspacePath, cancellationToken);
        if (prefix.IsFailure)
        {
            return prefix.Error!;
        }

        var merging = await IsMergingAsync(workspacePath, cancellationToken);
        if (merging.IsFailure)
        {
            return merging.Error!;
        }

        if (merging.Value && paths is not null)
        {
            return GitErrors.PartialCommitDuringMerge();
        }

        // Literal pathspecs, so file names are never read as glob patterns.
        List<string> pathspecs = paths is null
            ? [".", ExcludeLocalFolder]
            : [.. paths.Select(p => ":(literal)" + p), ExcludeLocalFolder];

        // git add refuses any pathspec that names an ignored path, the exclusion included, so it is
        // only passed when it is needed: when the folder is not git-ignored already.
        var ignored = await IsLocalFolderIgnoredAsync(workspacePath, cancellationToken);
        if (ignored.IsFailure)
        {
            return ignored.Error!;
        }

        var addPathspecs = ignored.Value ? pathspecs.Where(p => p != ExcludeLocalFolder).ToList() : pathspecs;
        var add = await RunCheckedAsync(workspacePath, "add", ["add", "--all", "--", .. addPathspecs], cancellationToken);
        if (add.IsFailure)
        {
            return add.Error!;
        }

        // A merge commit must include the whole index; otherwise commit only the selected paths,
        // leaving anything else the user staged untouched.
        List<string> commitArguments = merging.Value
            ? ["commit", "--message", message.Value]
            : ["commit", "--message", message.Value, "--only", "--", .. pathspecs];

        var commit = await RunCheckedAsync(workspacePath, "commit", commitArguments, cancellationToken);
        if (commit.IsFailure)
        {
            return commit.Error!;
        }

        var head = await RunCheckedAsync(workspacePath, "rev-parse", ["rev-parse", "HEAD"], cancellationToken);
        if (head.IsFailure)
        {
            return head.Error!;
        }

        return head.Value.StandardOutput.Trim();
    }

    public async Task<Result<Unit>> DiscardChangesAsync(
        string workspacePath,
        IReadOnlyList<GitChange> changes,
        CancellationToken cancellationToken = default)
    {
        var unsafePath = changes
            .SelectMany(c => c.OriginalPath is null ? [c.Path] : new[] { c.Path, c.OriginalPath })
            .FirstOrDefault(p => !IsSafeRelativePath(p));
        if (unsafePath is not null)
        {
            return GitErrors.FileNotChanged(unsafePath);
        }

        var prefix = await GetWorkspacePrefixAsync(workspacePath, cancellationToken);
        if (prefix.IsFailure)
        {
            return prefix.Error!;
        }

        static string Literal(string path) => ":(literal)" + path;

        var untracked = changes.Where(c => c.Status == GitFileStatus.Untracked).Select(c => Literal(c.Path)).ToList();
        // New files are not in HEAD, so they are removed from the index and the disk instead of restored.
        var added = changes
            .Where(c => c.Status is GitFileStatus.Added or GitFileStatus.Renamed)
            .Select(c => Literal(c.Path))
            .ToList();
        var restored = changes
            .Where(c => c.Status is GitFileStatus.Modified or GitFileStatus.Deleted)
            .Select(c => Literal(c.Path))
            .Concat(changes.Where(c => c.Status == GitFileStatus.Renamed && c.OriginalPath is not null).Select(c => Literal(c.OriginalPath!)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (added.Count > 0)
        {
            var remove = await RunCheckedAsync(workspacePath, "rm", ["rm", "--quiet", "--force", "--", .. added], cancellationToken);
            if (remove.IsFailure)
            {
                return remove.Error!;
            }
        }

        if (restored.Count > 0)
        {
            var restore = await RunCheckedAsync(
                workspacePath,
                "restore",
                ["restore", "--source=HEAD", "--staged", "--worktree", "--", .. restored],
                cancellationToken);
            if (restore.IsFailure)
            {
                return restore.Error!;
            }
        }

        if (untracked.Count > 0)
        {
            var clean = await RunCheckedAsync(workspacePath, "clean", ["clean", "--force", "--quiet", "-d", "-x", "--", .. untracked], cancellationToken);
            if (clean.IsFailure)
            {
                return clean.Error!;
            }
        }

        return Unit.Value;
    }

    public async Task<Result<PullOutcome>> PullAsync(
        string workspacePath,
        bool allowUnrelatedHistories = false,
        CancellationToken cancellationToken = default)
    {
        var prefix = await GetWorkspacePrefixAsync(workspacePath, cancellationToken);
        if (prefix.IsFailure)
        {
            return prefix.Error!;
        }

        var headBefore = await GetHeadAsync(workspacePath, cancellationToken);
        if (headBefore.IsFailure)
        {
            return headBefore.Error!;
        }

        // Always merge: rebasing would rewrite the user's local commits.
        List<string> arguments = ["pull", "--no-rebase", "--no-edit"];
        if (allowUnrelatedHistories)
        {
            arguments.Add("--allow-unrelated-histories");
        }

        var pull = await RunAsync(workspacePath, arguments, cancellationToken);
        if (pull.IsFailure)
        {
            return pull.Error!;
        }

        if (!pull.Value.Succeeded)
        {
            var conflicts = await RunAsync(
                workspacePath,
                ["diff", "--name-only", "--diff-filter=U", "-z"],
                cancellationToken);
            if (conflicts.IsFailure)
            {
                return conflicts.Error!;
            }

            if (conflicts.Value.Succeeded && conflicts.Value.StandardOutput.Length > 0)
            {
                return PullOutcome.Conflicted;
            }

            return GitFailure.Classify("pull", Details(pull.Value));
        }

        var headAfter = await GetHeadAsync(workspacePath, cancellationToken);
        if (headAfter.IsFailure)
        {
            return headAfter.Error!;
        }

        return headAfter.Value == headBefore.Value ? PullOutcome.UpToDate : PullOutcome.Updated;
    }

    public async Task<Result<Unit>> PushAsync(
        string workspacePath,
        bool setUpstream,
        CancellationToken cancellationToken = default)
    {
        var prefix = await GetWorkspacePrefixAsync(workspacePath, cancellationToken);
        if (prefix.IsFailure)
        {
            return prefix.Error!;
        }

        List<string> arguments = ["push", "--porcelain"];
        if (setUpstream)
        {
            var remote = await GetDefaultRemoteAsync(workspacePath, cancellationToken);
            if (remote.IsFailure)
            {
                return remote.Error!;
            }

            arguments.AddRange(["--set-upstream", remote.Value, "HEAD"]);
        }
        else
        {
            var target = await GetPushTargetAsync(workspacePath, cancellationToken);
            if (target.IsFailure)
            {
                return target.Error!;
            }

            if (target.Value is var (remoteName, mergeRef))
            {
                arguments.AddRange([remoteName, "HEAD:" + mergeRef]);
            }
        }

        var push = await RunAsync(workspacePath, arguments, cancellationToken);
        if (push.IsFailure)
        {
            return push.Error!;
        }

        // With --porcelain, each ref is reported on stdout; '!' marks a rejected ref.
        var rejected = push.Value.StandardOutput
            .Split('\n')
            .Where(line => line.StartsWith('!'))
            .ToList();
        if (rejected.Count > 0)
        {
            return GitErrors.PushRejected(string.Join('\n', rejected));
        }

        if (!push.Value.Succeeded)
        {
            return GitFailure.Classify("push", Details(push.Value));
        }

        return Unit.Value;
    }

    /// <summary>
    /// Where a plain <c>git push</c> of a branch with an upstream should go: its upstream branch, even
    /// when it is named differently from the local one (which <c>push.default=simple</c> refuses) or
    /// was deleted on the remote. <c>null</c> leaves it to git: when the user configured a separate
    /// push remote, or the upstream is a local branch.
    /// </summary>
    private async Task<Result<(string Remote, string MergeRef)?>> GetPushTargetAsync(
        string workspacePath,
        CancellationToken cancellationToken)
    {
        var branch = await RunAsync(workspacePath, ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken);
        if (branch.IsFailure)
        {
            return branch.Error!;
        }

        if (!branch.Value.Succeeded)
        {
            return Result<(string, string)?>.Success(null);
        }

        var name = branch.Value.StandardOutput.Trim();
        var remote = await ReadConfigAsync(workspacePath, $"branch.{name}.remote", cancellationToken);
        var merge = await ReadConfigAsync(workspacePath, $"branch.{name}.merge", cancellationToken);
        var pushRemote = await ReadConfigAsync(workspacePath, $"branch.{name}.pushRemote", cancellationToken);
        var pushDefault = await ReadConfigAsync(workspacePath, "remote.pushDefault", cancellationToken);
        foreach (var read in new[] { remote, merge, pushRemote, pushDefault })
        {
            if (read.IsFailure)
            {
                return read.Error!;
            }
        }

        if (remote.Value is not { Length: > 0 } remoteName
            || remoteName == "."
            || merge.Value is not { } mergeRef
            || !mergeRef.StartsWith("refs/heads/", StringComparison.Ordinal)
            || pushRemote.Value is not null
            || pushDefault.Value is not null)
        {
            return Result<(string, string)?>.Success(null);
        }

        return Result<(string, string)?>.Success((remoteName, mergeRef));
    }

    /// <summary>A git configuration value as git resolves it for the workspace, or <c>null</c> when it is not set.</summary>
    private async Task<Result<string?>> ReadConfigAsync(string workspacePath, string key, CancellationToken cancellationToken)
    {
        var result = await RunAsync(workspacePath, ["config", "--get", key], cancellationToken);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        // Exit code 1 means the key is not set.
        if (result.Value.ExitCode == 1)
        {
            return Result<string?>.Success(null);
        }

        if (!result.Value.Succeeded)
        {
            return GitFailure.Classify("config", Details(result.Value));
        }

        var value = result.Value.StandardOutput.TrimEnd('\r', '\n');
        return Result<string?>.Success(value);
    }

    public async Task<Result<Unit>> FetchAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        var prefix = await GetWorkspacePrefixAsync(workspacePath, cancellationToken);
        if (prefix.IsFailure)
        {
            return prefix.Error!;
        }

        // --prune drops remote-tracking branches deleted on the remote, so a deleted upstream shows up.
        return Done(await RunCheckedAsync(workspacePath, "fetch", ["fetch", "--all", "--prune", "--quiet"], cancellationToken));
    }

    public async Task<Result<bool>> TrackRemoteBranchAsync(
        string workspacePath,
        string branch,
        CancellationToken cancellationToken = default)
    {
        var prefix = await GetWorkspacePrefixAsync(workspacePath, cancellationToken);
        if (prefix.IsFailure)
        {
            return prefix.Error!;
        }

        var remote = await GetDefaultRemoteAsync(workspacePath, cancellationToken);
        if (remote.IsFailure)
        {
            return remote.Error!;
        }

        var fetch = await RunCheckedAsync(workspacePath, "fetch", ["fetch", "--quiet", "--", remote.Value], cancellationToken);
        if (fetch.IsFailure)
        {
            return fetch.Error!;
        }

        var exists = await RunAsync(
            workspacePath,
            ["rev-parse", "--quiet", "--verify", $"refs/remotes/{remote.Value}/{branch}^{{commit}}"],
            cancellationToken);
        if (exists.IsFailure)
        {
            return exists.Error!;
        }

        if (!exists.Value.Succeeded)
        {
            return false;
        }

        // Set directly rather than with "git branch --set-upstream-to", which needs the branch to
        // have a commit: a new, empty repository can start from the remote's branch.
        var setRemote = await RunCheckedAsync(workspacePath, "config", ["config", $"branch.{branch}.remote", remote.Value], cancellationToken);
        if (setRemote.IsFailure)
        {
            return setRemote.Error!;
        }

        var setMerge = await RunCheckedAsync(workspacePath, "config", ["config", $"branch.{branch}.merge", $"refs/heads/{branch}"], cancellationToken);
        if (setMerge.IsFailure)
        {
            return setMerge.Error!;
        }

        return true;
    }

    public async Task<Result<GitIdentitySettings>> GetIdentityAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        var prefix = await GetWorkspacePrefixAsync(workspacePath, cancellationToken);
        if (prefix.IsFailure)
        {
            return prefix.Error!;
        }

        var name = await ReadConfigAsync(workspacePath, "user.name", cancellationToken);
        if (name.IsFailure)
        {
            return name.Error!;
        }

        var email = await ReadConfigAsync(workspacePath, "user.email", cancellationToken);
        if (email.IsFailure)
        {
            return email.Error!;
        }

        static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return new GitIdentitySettings(NonBlank(name.Value), NonBlank(email.Value));
    }

    public async Task<Result<Unit>> SetIdentityAsync(
        string workspacePath,
        GitIdentity identity,
        bool global,
        CancellationToken cancellationToken = default)
    {
        var prefix = await GetWorkspacePrefixAsync(workspacePath, cancellationToken);
        if (prefix.IsFailure)
        {
            return prefix.Error!;
        }

        string[] scope = global ? ["--global"] : ["--local"];
        var name = await RunCheckedAsync(workspacePath, "config", ["config", .. scope, "user.name", identity.Name], cancellationToken);
        if (name.IsFailure)
        {
            return name.Error!;
        }

        return Done(await RunCheckedAsync(workspacePath, "config", ["config", .. scope, "user.email", identity.Email], cancellationToken));
    }

    /// <summary>
    /// Checks that the workspace is inside a work tree and returns its path relative to the
    /// repository root, with a trailing <c>/</c> (empty at the root).
    /// </summary>
    private async Task<Result<string>> GetWorkspacePrefixAsync(string workspacePath, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(workspacePath))
        {
            return GitErrors.WorkspaceNotFound(workspacePath);
        }

        var result = await RunAsync(workspacePath, ["rev-parse", "--show-prefix"], cancellationToken);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        if (!result.Value.Succeeded)
        {
            var notARepository = result.Value.StandardError.Contains(
                "not a git repository",
                StringComparison.OrdinalIgnoreCase);
            return notARepository
                ? GitErrors.NotARepository(workspacePath)
                : GitFailure.Classify("rev-parse", result.Value.StandardError);
        }

        return result.Value.StandardOutput.TrimEnd('\r', '\n');
    }

    /// <summary>Whether git ignores the workspace's <c>.missive/</c> folder (its <c>.gitignore</c> entry is in place).</summary>
    private async Task<Result<bool>> IsLocalFolderIgnoredAsync(string workspacePath, CancellationToken cancellationToken)
    {
        // Exit code 0: ignored; 1: not ignored; anything else is an error.
        var result = await RunAsync(workspacePath, ["check-ignore", "--quiet", "--", ".missive"], cancellationToken);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        return result.Value.ExitCode switch
        {
            0 => true,
            1 => false,
            _ => GitFailure.Classify("check-ignore", Details(result.Value)),
        };
    }

    private async Task<Result<bool>> IsMergingAsync(string workspacePath, CancellationToken cancellationToken)
    {
        var result = await RunAsync(workspacePath, ["rev-parse", "--quiet", "--verify", "MERGE_HEAD"], cancellationToken);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        return result.Value.Succeeded;
    }

    /// <summary>The commit HEAD points to, or an empty string on a branch with no commits yet.</summary>
    private async Task<Result<string>> GetHeadAsync(string workspacePath, CancellationToken cancellationToken)
    {
        var result = await RunAsync(workspacePath, ["rev-parse", "--quiet", "--verify", "HEAD"], cancellationToken);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        return result.Value.Succeeded ? result.Value.StandardOutput.Trim() : string.Empty;
    }

    /// <summary>The remote to publish a new branch to: <c>origin</c>, or the only remote.</summary>
    private async Task<Result<string>> GetDefaultRemoteAsync(string workspacePath, CancellationToken cancellationToken)
    {
        var result = await RunCheckedAsync(workspacePath, "remote", ["remote"], cancellationToken);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        var remotes = result.Value.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (remotes.Contains("origin", StringComparer.Ordinal))
        {
            return "origin";
        }

        return remotes.Length switch
        {
            1 => remotes[0],
            0 => GitErrors.NoRemote(),
            _ => GitErrors.AmbiguousRemote(),
        };
    }

    private async Task<Result<GitCommandResult>> RunCheckedAsync(
        string workspacePath,
        string command,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(workspacePath, arguments, cancellationToken);
        if (result.IsSuccess && !result.Value.Succeeded)
        {
            return GitFailure.Classify(command, Details(result.Value));
        }

        return result;
    }

    /// <summary>
    /// Runs git. A command that fails because another git process (an editor, an IDE's background
    /// refresh) holds a lock is tried again after a short wait: git takes its locks before changing
    /// anything, so trying again is safe.
    /// </summary>
    private async Task<Result<GitCommandResult>> RunAsync(
        string workspacePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var result = await _runner.RunAsync(workspacePath, arguments, cancellationToken);
                if (result.Succeeded || attempt >= LockRetryDelays.Length || !GitFailure.IsLocked(result.StandardError))
                {
                    return result;
                }

                await Task.Delay(LockRetryDelays[attempt], cancellationToken);
            }
        }
        catch (GitNotFoundException)
        {
            return GitErrors.GitNotInstalled();
        }
    }

    /// <summary>What git printed about a failure; some commands report on stdout rather than stderr.</summary>
    private static string Details(GitCommandResult result) =>
        string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
}
