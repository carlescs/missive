using Missive.Application.Captures;
using Missive.Application.Common;
using Missive.Application.Cookies;
using Missive.Application.Environments;
using Missive.Application.Folders;
using Missive.Application.Git;
using Missive.Application.Http;
using Missive.Application.Interchange;
using Missive.Domain.Git;
using Missive.Application.Requests;
using Missive.Application.Settings;
using Missive.Application.Updates;
using Missive.Application.Workspaces;
using Microsoft.Extensions.DependencyInjection;

namespace Missive.Desktop.Bridge;

/// <summary>Maps each bridge message type to the use case that handles it.</summary>
internal static class BridgeHandlers
{
    public static BridgeRouter Create(IServiceProvider services, IFileDialogs dialogs)
    {
        T Use<T>() where T : notnull => services.GetRequiredService<T>();

        return new BridgeRouter()
            .Register(BridgeMessageTypes.WorkspacePick, async ct =>
            {
                var folder = await dialogs.PickFolderAsync("Open workspace folder");
                if (folder is null)
                {
                    return Result<WorkspaceDto?>.Success(null);
                }

                var opened = await Use<OpenWorkspace>().ExecuteAsync(folder, ct);
                return opened.IsSuccess ? opened.Value : Result<WorkspaceDto?>.Failure(opened.Error!);
            })
            .Register<OpenWorkspacePayload, WorkspaceDto>(BridgeMessageTypes.WorkspaceOpen,
                (p, ct) => Use<OpenWorkspace>().ExecuteAsync(p.Path, ct))
            .Register(BridgeMessageTypes.WorkspaceOpenLast,
                ct => Use<OpenLastWorkspace>().ExecuteAsync(ct))
            .Register(BridgeMessageTypes.WorkspaceRecent,
                ct => Use<GetRecentWorkspaces>().ExecuteAsync(ct))
            .Register(BridgeMessageTypes.WorkspaceTree,
                ct => Use<GetWorkspaceTree>().ExecuteAsync(ct))
            .Register(BridgeMessageTypes.WorkspaceExportFormats,
                _ => Task.FromResult(Result<IReadOnlyList<ExportFormatDto>>.Success(Use<ExportWorkspace>().Formats())))
            .Register<ExportWorkspacePayload, ExportResultDto?>(BridgeMessageTypes.WorkspaceExport, async (p, ct) =>
            {
                var exporter = Use<ExportWorkspace>();
                var format = exporter.Formats().FirstOrDefault(f => string.Equals(f.Id, p.Format, StringComparison.OrdinalIgnoreCase));
                if (format is null)
                {
                    return new Error(ErrorCodes.InvalidRequest, $"Unknown export format '{p.Format}'.");
                }

                var destination = await dialogs.PickSaveFileAsync($"Export as {format.Name}", [new FileFilter(format.Name, ["*.json"])]);
                if (destination is null)
                {
                    return Result<ExportResultDto?>.Success(null);
                }

                var exported = await exporter.ExecuteAsync(new ExportWorkspaceRequest(format.Id, destination, p.IncludeEnvironments), ct);
                return exported.IsSuccess ? exported.Value : Result<ExportResultDto?>.Failure(exported.Error!);
            })
            .Register<ImportCollectionPayload, ImportResultDto?>(BridgeMessageTypes.WorkspaceImport, async (p, ct) =>
            {
                var source = await dialogs.PickFileAsync("Import requests", ImportFilters);
                if (source is null)
                {
                    return Result<ImportResultDto?>.Success(null);
                }

                var imported = await Use<ImportCollection>().ExecuteAsync(new ImportCollectionRequest(source, p.Folder), ct);
                return imported.IsSuccess ? imported.Value : Result<ImportResultDto?>.Failure(imported.Error!);
            })
            .Register<LoadRequestPayload, RequestDto>(BridgeMessageTypes.RequestLoad,
                (p, ct) => Use<LoadRequest>().ExecuteAsync(p.Path, ct))
            .Register<CreateRequestPayload, SavedRequestDto>(BridgeMessageTypes.RequestCreate,
                (p, ct) => Use<CreateRequest>().ExecuteAsync(p.Folder, p.Request, ct))
            .Register<SaveRequestPayload, SavedRequestDto>(BridgeMessageTypes.RequestSave,
                (p, ct) => Use<SaveRequest>().ExecuteAsync(p.Path, p.Request, ct))
            .Register<DuplicateRequestPayload, SavedRequestDto>(BridgeMessageTypes.RequestDuplicate,
                (p, ct) => Use<DuplicateRequest>().ExecuteAsync(p.Path, ct))
            .Register<MoveRequestPayload, MovedRequestDto>(BridgeMessageTypes.RequestMove,
                (p, _) => Task.FromResult(Use<MoveRequest>().Execute(p.Path, p.Folder)))
            .Register<SendRequestPayload, ResponseDto>(BridgeMessageTypes.RequestSend,
                (p, ct) => Use<SendRequest>().ExecuteAsync(p.Id, p.Request, ct))
            .Register<CancelRequestPayload, bool>(BridgeMessageTypes.RequestCancel,
                (p, _) => Task.FromResult(Result<bool>.Success(Use<CancelRequest>().Execute(p.Id))))
            .Register<SaveResponseBodyPayload, SavedFileDto?>(BridgeMessageTypes.ResponseSaveBody, async (p, ct) =>
            {
                var saver = Use<SaveResponseBody>();
                var fileName = saver.SuggestedFileName(p.Id);
                if (!fileName.IsSuccess)
                {
                    return fileName.Error!;
                }

                var destination = await dialogs.PickSaveFileAsync($"Save response as {fileName.Value}", SaveFilters(fileName.Value));
                if (destination is null)
                {
                    return Result<SavedFileDto?>.Success(null);
                }

                var saved = await saver.ExecuteAsync(p.Id, destination, ct);
                return saved.IsSuccess ? new SavedFileDto(saved.Value) : Result<SavedFileDto?>.Failure(saved.Error!);
            })
            .Register<CookieJarPayload, IReadOnlyList<CookieDto>>(BridgeMessageTypes.CookieList,
                (p, ct) => Use<ListCookies>().ExecuteAsync(p.Environment, ct))
            .Register<DeleteCookiePayload, IReadOnlyList<CookieDto>>(BridgeMessageTypes.CookieDelete,
                (p, ct) => Use<DeleteCookie>().ExecuteAsync(p.Environment, p.Name, p.Domain, p.Path, ct))
            .Register<CookieJarPayload, Unit?>(BridgeMessageTypes.CookieClear,
                async (p, ct) => NoValue(await Use<ClearCookies>().ExecuteAsync(p.Environment, ct)))
            .Register<CapturedValuesPayload, IReadOnlyList<CapturedValueDto>>(BridgeMessageTypes.CaptureList,
                (p, ct) => Use<ListCapturedValues>().ExecuteAsync(p.Environment, ct))
            .Register<ClearCapturedValuesPayload, IReadOnlyList<CapturedValueDto>>(BridgeMessageTypes.CaptureClear,
                (p, ct) => Use<ClearCapturedValues>().ExecuteAsync(p.Environment, p.Name, ct))
            .Register<CreateFolderPayload, FolderDto>(BridgeMessageTypes.FolderCreate,
                (p, _) => Task.FromResult(Use<CreateFolder>().Execute(p.Parent, p.Name)))
            .Register<RenameFolderPayload, FolderDto>(BridgeMessageTypes.FolderRename,
                (p, _) => Task.FromResult(Use<RenameFolder>().Execute(p.Path, p.Name)))
            .Register<MoveFolderPayload, FolderDto>(BridgeMessageTypes.FolderMove,
                (p, _) => Task.FromResult(Use<MoveFolder>().Execute(p.Path, p.Parent)))
            .Register<DuplicateFolderPayload, FolderDto>(BridgeMessageTypes.FolderDuplicate,
                (p, _) => Task.FromResult(Use<DuplicateFolder>().Execute(p.Path)))
            .Register<DeleteRequestPayload, Unit?>(BridgeMessageTypes.RequestDelete,
                (p, _) => Task.FromResult(NoValue(Use<DeleteRequest>().Execute(p.Path))))
            .Register<DeleteFolderPayload, Unit?>(BridgeMessageTypes.FolderDelete,
                (p, _) => Task.FromResult(NoValue(Use<DeleteFolder>().Execute(p.Path))))
            .Register(BridgeMessageTypes.EnvironmentList,
                ct => Use<ListEnvironments>().ExecuteAsync(ct))
            .Register<EnvironmentIdPayload, EnvironmentDto>(BridgeMessageTypes.EnvironmentLoad,
                (p, ct) => Use<LoadEnvironment>().ExecuteAsync(p.Id, ct))
            .Register<CreateEnvironmentPayload, SavedEnvironmentDto>(BridgeMessageTypes.EnvironmentCreate,
                (p, ct) => Use<CreateEnvironment>().ExecuteAsync(p.Environment, ct))
            .Register<SaveEnvironmentPayload, SavedEnvironmentDto>(BridgeMessageTypes.EnvironmentSave,
                (p, ct) => Use<SaveEnvironment>().ExecuteAsync(p.Id, p.Environment, ct))
            .Register<EnvironmentIdPayload, Unit?>(BridgeMessageTypes.EnvironmentDelete,
                async (p, ct) => NoValue(await Use<DeleteEnvironment>().ExecuteAsync(p.Id, ct)))
            .Register<SelectEnvironmentPayload, Unit?>(BridgeMessageTypes.EnvironmentSelect,
                async (p, ct) => NoValue(await Use<SelectEnvironment>().ExecuteAsync(p.Id, ct)))
            .Register(BridgeMessageTypes.GitStatus,
                async ct => (await Use<GetGitStatus>().ExecuteAsync(ct)).Map(GitStatusDto.From))
            .Register(BridgeMessageTypes.GitInit,
                async ct => (await Use<InitRepository>().ExecuteAsync(ct)).Map(GitStatusDto.From))
            .Register<GitCommitPayload, GitCommitDto>(BridgeMessageTypes.GitCommit,
                async (p, ct) => (await Use<CommitChanges>().ExecuteAsync(new CommitChangesRequest(p.Message, p.Paths), ct)).Map(GitCommitDto.From))
            .Register(BridgeMessageTypes.GitPull,
                async ct => (await Use<PullChanges>().ExecuteAsync(ct)).Map(GitPullDto.From))
            .Register(BridgeMessageTypes.GitPush,
                async ct => (await Use<PushChanges>().ExecuteAsync(ct)).Map(GitStatusDto.From))
            .Register(BridgeMessageTypes.GitFetch,
                async ct => (await Use<FetchChanges>().ExecuteAsync(ct)).Map(GitStatusDto.From))
            .Register(BridgeMessageTypes.GitIdentity,
                async ct => (await Use<GetGitIdentity>().ExecuteAsync(ct)).Map(GitIdentityDto.From))
            .Register<GitSetIdentityPayload, GitIdentityDto>(BridgeMessageTypes.GitSetIdentity,
                async (p, ct) => (await Use<SetGitIdentity>().ExecuteAsync(new SetGitIdentityRequest(p.Name, p.Email, p.Global), ct)).Map(GitIdentityDto.From))
            .Register<GitConflictPayload, GitConflictDto>(BridgeMessageTypes.GitConflict,
                async (p, ct) => (await Use<GetConflict>().ExecuteAsync(p.Path, ct)).Map(GitConflictDto.From))
            .Register<GitResolvePayload, GitStatusDto>(BridgeMessageTypes.GitResolve,
                async (p, ct) => ParseResolution(p.Resolution) is { } resolution
                    ? (await Use<ResolveConflict>().ExecuteAsync(new ResolveConflictRequest(p.Path, resolution), ct)).Map(GitStatusDto.From)
                    : new Error(BridgeErrorCodes.BadPayload, $"Unknown resolution '{p.Resolution}'; use ours, theirs or edited."))
            .Register<GitDiscardPayload, GitStatusDto>(BridgeMessageTypes.GitDiscard,
                async (p, ct) => (await Use<DiscardChanges>().ExecuteAsync(p.Paths ?? [], ct)).Map(GitStatusDto.From))
            .Register(BridgeMessageTypes.GitAbortMerge,
                async ct => (await Use<AbortMerge>().ExecuteAsync(ct)).Map(GitStatusDto.From))
            .Register<GitLogPayload, GitHistoryDto>(BridgeMessageTypes.GitLog,
                async (p, ct) => (await Use<GetGitHistory>().ExecuteAsync(new GetGitHistoryRequest(p.Skip, p.Limit), ct)).Map(GitHistoryDto.From))
            .Register<GitCommitIdPayload, GitCommitDetailsDto>(BridgeMessageTypes.GitShow,
                async (p, ct) => (await Use<GetCommit>().ExecuteAsync(p.CommitId, ct)).Map(GitCommitDetailsDto.From))
            .Register<GitCommitFilePayload, GitFileRevisionDto>(BridgeMessageTypes.GitCommitFile,
                async (p, ct) => (await Use<GetCommitFile>().ExecuteAsync(new GetCommitFileRequest(p.CommitId, p.Path), ct)))
            .Register<GitDiffPayload, GitFileDiffDto>(BridgeMessageTypes.GitDiff,
                (p, ct) => Use<GetFileDiff>().ExecuteAsync(p.Path, ct))
            .Register(BridgeMessageTypes.GitRemoteList,
                async ct => (await Use<ListRemotes>().ExecuteAsync(ct)).Map(GitRemoteDto.From))
            .Register<GitAddRemotePayload, IReadOnlyList<GitRemoteDto>>(BridgeMessageTypes.GitRemoteAdd,
                async (p, ct) => (await Use<AddRemote>().ExecuteAsync(new AddRemoteRequest(p.Name, p.Url), ct)).Map(GitRemoteDto.From))
            .Register<GitUpdateRemotePayload, IReadOnlyList<GitRemoteDto>>(BridgeMessageTypes.GitRemoteUpdate,
                async (p, ct) => (await Use<UpdateRemote>().ExecuteAsync(new UpdateRemoteRequest(p.Name, p.NewName, p.Url), ct)).Map(GitRemoteDto.From))
            .Register<GitRemoteNamePayload, IReadOnlyList<GitRemoteDto>>(BridgeMessageTypes.GitRemoteRemove,
                async (p, ct) => (await Use<RemoveRemote>().ExecuteAsync(p.Name, ct)).Map(GitRemoteDto.From))
            .Register(BridgeMessageTypes.SettingsGet,
                ct => Use<GetAppSettings>().ExecuteAsync(ct))
            .Register<SetThemePayload, AppSettingsDto>(BridgeMessageTypes.SettingsSetTheme,
                (p, ct) => Use<SetTheme>().ExecuteAsync(p.Theme, ct))
            .Register(BridgeMessageTypes.UpdateCheck,
                ct => Use<CheckForUpdate>().ExecuteAsync(ct))
            .Register(BridgeMessageTypes.UpdateRestart,
                _ => Task.FromResult(NoValue(Use<RestartToUpdate>().Execute())));
    }

    /// <summary>The files the import dialog offers: Postman, Insomnia and HAR files are JSON; OpenAPI can be YAML.</summary>
    private static readonly IReadOnlyList<FileFilter> ImportFilters =
    [
        new("Collections and API descriptions", ["*.json", "*.har", "*.yaml", "*.yml"]),
        new("All files", ["*"]),
    ];

    /// <summary>The save dialog's file types: the suggested name's extension first, then anything.</summary>
    private static IReadOnlyList<FileFilter> SaveFilters(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        var extension = dot > 0 && dot < fileName.Length - 1 ? fileName[(dot + 1)..] : null;
        return extension is null
            ? [new FileFilter("All files", ["*"])]
            : [new FileFilter($"{extension.ToUpperInvariant()} file", [$"*.{extension}"]), new FileFilter("All files", ["*"])];
    }

    private static ConflictResolution? ParseResolution(string? value) =>
        value switch
        {
            "ours" => ConflictResolution.Ours,
            "theirs" => ConflictResolution.Theirs,
            "edited" => ConflictResolution.Edited,
            _ => null,
        };

    /// <summary>Results without a value are sent as a <c>null</c> payload.</summary>
    private static Result<Unit?> NoValue(Result<Unit> result) =>
        result.IsSuccess ? Result<Unit?>.Success(null) : Result<Unit?>.Failure(result.Error!);
}
