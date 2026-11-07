using System.Text.Json;
using Missive.Application.Common;
using Missive.Application.Environments;
using Missive.Application.Requests;

namespace Missive.Desktop.Bridge;

/// <summary>
/// The message types the UI can send. Keep in sync with
/// <c>src/Missive.Client/src/app/models/messages.ts</c>.
/// </summary>
internal static class BridgeMessageTypes
{
    /// <summary>Shows a folder picker and opens the chosen folder. Payload: none. Result: workspace or null if cancelled.</summary>
    public const string WorkspacePick = "workspace.pick";

    /// <summary>Opens a folder by path. Payload: <see cref="OpenWorkspacePayload"/>. Result: workspace.</summary>
    public const string WorkspaceOpen = "workspace.open";

    /// <summary>Reopens the workspace from the previous session. Payload: none. Result: workspace, or null if there is none.</summary>
    public const string WorkspaceOpenLast = "workspace.openLast";

    /// <summary>
    /// Lists the workspace folders opened recently, the most recent first; folders that no longer
    /// exist are dropped. Payload: none. Result: recent workspaces.
    /// </summary>
    public const string WorkspaceRecent = "workspace.recent";

    /// <summary>Re-reads the open workspace. Payload: none. Result: workspace.</summary>
    public const string WorkspaceTree = "workspace.tree";

    /// <summary>Lists the formats the workspace can be exported to. Payload: none. Result: formats.</summary>
    public const string WorkspaceExportFormats = "workspace.exportFormats";

    /// <summary>
    /// Asks where to save, then exports the workspace for other apps. Payload:
    /// <see cref="ExportWorkspacePayload"/>. Result: what was written, or null if cancelled.
    /// </summary>
    public const string WorkspaceExport = "workspace.export";

    /// <summary>
    /// Asks for a file exported by another app and imports it into a folder. Payload:
    /// <see cref="ImportCollectionPayload"/>. Result: what was imported, or null if cancelled.
    /// </summary>
    public const string WorkspaceImport = "workspace.import";

    /// <summary>Reads a request file. Payload: <see cref="LoadRequestPayload"/>. Result: request.</summary>
    public const string RequestLoad = "request.load";

    /// <summary>Saves a new request in a folder. Payload: <see cref="CreateRequestPayload"/>. Result: saved request.</summary>
    public const string RequestCreate = "request.create";

    /// <summary>Saves an existing request file. Payload: <see cref="SaveRequestPayload"/>. Result: saved request.</summary>
    public const string RequestSave = "request.save";

    /// <summary>Copies a request as "… copy" next to it. Payload: <see cref="DuplicateRequestPayload"/>. Result: the saved copy.</summary>
    public const string RequestDuplicate = "request.duplicate";

    /// <summary>Moves a request file into another folder. Payload: <see cref="MoveRequestPayload"/>. Result: its new path.</summary>
    public const string RequestMove = "request.move";

    /// <summary>Deletes a request file. Payload: <see cref="DeleteRequestPayload"/>. Result: none.</summary>
    public const string RequestDelete = "request.delete";

    /// <summary>
    /// Sends a request (saved or not) and waits for the response. Payload: <see cref="SendRequestPayload"/>.
    /// Result: response.
    /// </summary>
    public const string RequestSend = "request.send";

    /// <summary>Cancels a request being sent. Payload: <see cref="CancelRequestPayload"/>. Result: whether it was being sent.</summary>
    public const string RequestCancel = "request.cancel";

    /// <summary>
    /// Asks where to save, then writes the body of a recent response exactly as received.
    /// Payload: <see cref="SaveResponseBodyPayload"/>. Result: <see cref="SavedFileDto"/>, or null if cancelled.
    /// </summary>
    public const string ResponseSaveBody = "response.saveBody";

    /// <summary>Lists the cookies kept for an environment. Payload: <see cref="CookieJarPayload"/>. Result: cookies.</summary>
    public const string CookieList = "cookie.list";

    /// <summary>Deletes one cookie from an environment's jar. Payload: <see cref="DeleteCookiePayload"/>. Result: the cookies left.</summary>
    public const string CookieDelete = "cookie.delete";

    /// <summary>Deletes every cookie kept for an environment. Payload: <see cref="CookieJarPayload"/>. Result: none.</summary>
    public const string CookieClear = "cookie.clear";

    /// <summary>Lists the values requests captured for an environment. Payload: <see cref="CapturedValuesPayload"/>. Result: captured values.</summary>
    public const string CaptureList = "capture.list";

    /// <summary>
    /// Forgets one captured value of an environment, or all of them when no name is given.
    /// Payload: <see cref="ClearCapturedValuesPayload"/>. Result: the captured values left.
    /// </summary>
    public const string CaptureClear = "capture.clear";

    /// <summary>Creates a folder. Payload: <see cref="CreateFolderPayload"/>. Result: folder.</summary>
    public const string FolderCreate = "folder.create";

    /// <summary>Renames a folder. Payload: <see cref="RenameFolderPayload"/>. Result: folder (new path).</summary>
    public const string FolderRename = "folder.rename";

    /// <summary>Moves a folder into another folder. Payload: <see cref="MoveFolderPayload"/>. Result: folder (new path).</summary>
    public const string FolderMove = "folder.move";

    /// <summary>Copies a folder with its contents as "… copy" next to it. Payload: <see cref="DuplicateFolderPayload"/>. Result: the copy.</summary>
    public const string FolderDuplicate = "folder.duplicate";

    /// <summary>Deletes a folder with its requests. Payload: <see cref="DeleteFolderPayload"/>. Result: none.</summary>
    public const string FolderDelete = "folder.delete";

    /// <summary>Lists the environments and the selected one. Payload: none. Result: environment list.</summary>
    public const string EnvironmentList = "environment.list";

    /// <summary>Reads an environment with its secret values. Payload: <see cref="EnvironmentIdPayload"/>. Result: environment.</summary>
    public const string EnvironmentLoad = "environment.load";

    /// <summary>Saves a new environment. Payload: <see cref="CreateEnvironmentPayload"/>. Result: saved environment.</summary>
    public const string EnvironmentCreate = "environment.create";

    /// <summary>Saves an existing environment. Payload: <see cref="SaveEnvironmentPayload"/>. Result: saved environment.</summary>
    public const string EnvironmentSave = "environment.save";

    /// <summary>Deletes an environment. Payload: <see cref="EnvironmentIdPayload"/>. Result: none.</summary>
    public const string EnvironmentDelete = "environment.delete";

    /// <summary>Selects the environment used to send requests. Payload: <see cref="SelectEnvironmentPayload"/>. Result: none.</summary>
    public const string EnvironmentSelect = "environment.select";

    /// <summary>Reads the open workspace's git status. Payload: none. Result: git status.</summary>
    public const string GitStatus = "git.status";

    /// <summary>Creates a git repository in the open workspace folder. Payload: none. Result: new status.</summary>
    public const string GitInit = "git.init";

    /// <summary>Commits changes. Payload: <see cref="GitCommitPayload"/>. Result: commit id and new status.</summary>
    public const string GitCommit = "git.commit";

    /// <summary>Pulls the current branch (merge, never rebase). Payload: none. Result: outcome and new status.</summary>
    public const string GitPull = "git.pull";

    /// <summary>Pushes (or publishes) the current branch, never forcing. Payload: none. Result: new status.</summary>
    public const string GitPush = "git.push";

    /// <summary>Downloads the remotes' latest commits without changing files. Payload: none. Result: new status.</summary>
    public const string GitFetch = "git.fetch";

    /// <summary>Reads the configured commit name and email. Payload: none. Result: the identity (parts may be missing).</summary>
    public const string GitIdentity = "git.identity";

    /// <summary>Sets the commit name and email. Payload: <see cref="GitSetIdentityPayload"/>. Result: the identity.</summary>
    public const string GitSetIdentity = "git.identity.set";

    /// <summary>Reads both sides of a conflicted file. Payload: <see cref="GitConflictPayload"/>. Result: the versions.</summary>
    public const string GitConflict = "git.conflict";

    /// <summary>Resolves one conflicted file as the user chose. Payload: <see cref="GitResolvePayload"/>. Result: new status.</summary>
    public const string GitResolve = "git.resolve";

    /// <summary>Discards uncommitted changes to files. Payload: <see cref="GitDiscardPayload"/>. Result: new status.</summary>
    public const string GitDiscard = "git.discard";

    /// <summary>Abandons the merge in progress. Payload: none. Result: new status.</summary>
    public const string GitAbortMerge = "git.abortMerge";

    /// <summary>
    /// Reads a page of the commits that changed the workspace, newest first. Payload:
    /// <see cref="GitLogPayload"/>. Result: commits and whether more follow.
    /// </summary>
    public const string GitLog = "git.log";

    /// <summary>Reads a commit with its message and changed files. Payload: <see cref="GitCommitIdPayload"/>. Result: commit details.</summary>
    public const string GitShow = "git.show";

    /// <summary>
    /// Reads a file before and after a commit. Payload: <see cref="GitCommitFilePayload"/>. Result:
    /// both versions.
    /// </summary>
    public const string GitCommitFile = "git.commitFile";

    /// <summary>
    /// Reads the committed and working tree versions of a changed file, parsing request files.
    /// Payload: <see cref="GitDiffPayload"/>. Result: the diff.
    /// </summary>
    public const string GitDiff = "git.diff";

    /// <summary>Lists the repository's remotes. Payload: none. Result: remotes.</summary>
    public const string GitRemoteList = "git.remote.list";

    /// <summary>Adds a remote. Payload: <see cref="GitAddRemotePayload"/>. Result: remotes.</summary>
    public const string GitRemoteAdd = "git.remote.add";

    /// <summary>Renames a remote and/or changes its URL. Payload: <see cref="GitUpdateRemotePayload"/>. Result: remotes.</summary>
    public const string GitRemoteUpdate = "git.remote.update";

    /// <summary>Removes a remote. Payload: <see cref="GitRemoteNamePayload"/>. Result: remotes.</summary>
    public const string GitRemoteRemove = "git.remote.remove";

    /// <summary>
    /// Looks for a newer version of the app and downloads it. Payload: none. Result: update status.
    /// </summary>
    public const string UpdateCheck = "update.check";

    /// <summary>Installs the downloaded update and restarts the app. Payload: none. Result: none (the app exits).</summary>
    public const string UpdateRestart = "update.restart";

    /// <summary>Reads the user settings. Payload: none. Result: settings.</summary>
    public const string SettingsGet = "settings.get";

    /// <summary>Sets the color theme. Payload: <see cref="SetThemePayload"/>. Result: settings.</summary>
    public const string SettingsSetTheme = "settings.setTheme";
}

/// <summary>A message from the UI. <see cref="Id"/> correlates the response.</summary>
internal sealed record BridgeRequest(string Id, string Type, JsonElement? Payload);

/// <summary>The answer to a <see cref="BridgeRequest"/>: either <see cref="Payload"/> or <see cref="Error"/>.</summary>
internal sealed record BridgeResponse(string Id, bool Ok, object? Payload = null, Error? Error = null);

internal sealed record OpenWorkspacePayload(string Path);

internal sealed record SetThemePayload(string Theme);

/// <param name="Format">The id of an export format, e.g. <c>postman</c> or <c>insomnia</c>.</param>
/// <param name="IncludeEnvironments">Whether to export the environments (secret values never are).</param>
internal sealed record ExportWorkspacePayload(string Format, bool IncludeEnvironments = true);

/// <param name="Folder">The folder to import into; <c>""</c> is the workspace root.</param>
internal sealed record ImportCollectionPayload(string Folder = "");

internal sealed record LoadRequestPayload(string Path);

internal sealed record CreateRequestPayload(string Folder, RequestDto Request);

internal sealed record SaveRequestPayload(string Path, RequestDto Request);

/// <param name="Id">Chosen by the UI; pass it to <see cref="BridgeMessageTypes.RequestCancel"/> to cancel.</param>
internal sealed record SendRequestPayload(string Id, RequestDto Request);

internal sealed record CancelRequestPayload(string Id);

/// <param name="Id">The id the response's request was sent with.</param>
internal sealed record SaveResponseBodyPayload(string Id);

internal sealed record SavedFileDto(string Path);

/// <param name="Environment">The environment's file name in the <c>environments</c> folder.</param>
internal sealed record CookieJarPayload(string Environment);

/// <param name="Environment">The environment's file name in the <c>environments</c> folder.</param>
internal sealed record CapturedValuesPayload(string Environment);

/// <param name="Name">The variable to forget; omitted forgets every captured value.</param>
internal sealed record ClearCapturedValuesPayload(string Environment, string? Name = null);

internal sealed record DeleteCookiePayload(string Environment, string Name, string Domain, string Path);

/// <param name="Folder">The destination folder; <c>""</c> is the workspace root.</param>
internal sealed record MoveRequestPayload(string Path, string Folder);

/// <param name="Parent">The destination folder; <c>""</c> is the workspace root.</param>
internal sealed record MoveFolderPayload(string Path, string Parent);

/// <param name="Parent">The containing folder; <c>""</c> is the workspace root.</param>
internal sealed record CreateFolderPayload(string Parent, string Name);

internal sealed record RenameFolderPayload(string Path, string Name);

internal sealed record DeleteRequestPayload(string Path);

internal sealed record DuplicateRequestPayload(string Path);

internal sealed record DuplicateFolderPayload(string Path);

internal sealed record DeleteFolderPayload(string Path);

/// <param name="Id">The environment's file name in the <c>environments</c> folder.</param>
internal sealed record EnvironmentIdPayload(string Id);

internal sealed record CreateEnvironmentPayload(EnvironmentDto Environment);

internal sealed record SaveEnvironmentPayload(string Id, EnvironmentDto Environment);

/// <param name="Id">The environment to select, or <c>null</c> for none.</param>
internal sealed record SelectEnvironmentPayload(string? Id);

/// <param name="Paths">Workspace-relative paths to commit; omitted or empty commits every change.</param>
internal sealed record GitDiscardPayload(IReadOnlyList<string> Paths);

internal sealed record GitCommitPayload(string Message, IReadOnlyList<string>? Paths = null);

internal sealed record GitConflictPayload(string Path);

/// <param name="Global">Whether to use it in all of the user's repositories, not only the workspace's.</param>
internal sealed record GitSetIdentityPayload(string Name, string Email, bool Global = true);

/// <param name="Resolution"><c>ours</c>, <c>theirs</c> or <c>edited</c>.</param>
internal sealed record GitResolvePayload(string Path, string Resolution);

/// <param name="Skip">How many of the newest commits to skip; 0 for the first page.</param>
/// <param name="Limit">The page size; omitted uses the default.</param>
internal sealed record GitLogPayload(int Skip = 0, int? Limit = null);

internal sealed record GitCommitIdPayload(string CommitId);

internal sealed record GitCommitFilePayload(string CommitId, string Path);

internal sealed record GitDiffPayload(string Path);

internal sealed record GitAddRemotePayload(string Name, string Url);

/// <param name="Name">The remote to change.</param>
/// <param name="NewName">Its new name (the same name keeps it).</param>
/// <param name="Url">Its new URL (the current URL keeps it).</param>
internal sealed record GitUpdateRemotePayload(string Name, string NewName, string Url);

internal sealed record GitRemoteNamePayload(string Name);

internal static class BridgeErrorCodes
{
    public const string UnknownType = "unknown_message";
    public const string BadPayload = "bad_payload";
    public const string Internal = "internal_error";
}
