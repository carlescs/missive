using Missive.Application.Common;
using Missive.Application.Environments;
using Missive.Application.Workspaces;
using Missive.Domain.Environments;

namespace Missive.Application.Interchange;

/// <param name="Format">The id of an <see cref="ICollectionExporter"/>, e.g. <c>postman</c>.</param>
/// <param name="Destination">The full path of the file to write, chosen by the user.</param>
/// <param name="IncludeEnvironments">Whether to export the environments too (secret values never are).</param>
public sealed record ExportWorkspaceRequest(string Format, string Destination, bool IncludeEnvironments = true);

/// <summary>
/// Exports the open workspace's requests (and environments) to a file other apps can import.
/// Request files that can't be read are skipped and reported. Values of secret variables are never
/// exported: the variables are, with empty values.
/// </summary>
public sealed class ExportWorkspace(
    IWorkspaceRepository repository,
    IEnvironmentStore environments,
    IExternalFiles files,
    IEnumerable<ICollectionExporter> exporters,
    CurrentWorkspace current)
{
    private const int MaxAttempts = 1000;

    /// <summary>The formats <see cref="ExecuteAsync"/> can write.</summary>
    public IReadOnlyList<ExportFormatDto> Formats() =>
        exporters.Select(exporter => new ExportFormatDto(exporter.Id, exporter.Name, exporter.Extension)).ToList();

    public async Task<Result<ExportResultDto>> ExecuteAsync(ExportWorkspaceRequest request, CancellationToken cancellationToken = default)
    {
        var root = current.RequireRoot();
        if (!root.IsSuccess)
        {
            return root.Error!;
        }

        var exporter = exporters.FirstOrDefault(e => string.Equals(e.Id, request.Format, StringComparison.OrdinalIgnoreCase));
        if (exporter is null)
        {
            return new Error(ErrorCodes.InvalidRequest, $"Unknown export format '{request.Format}'.");
        }

        if (string.IsNullOrWhiteSpace(request.Destination))
        {
            return new Error(ErrorCodes.InvalidPath, "Choose a file to export to.");
        }

        var warnings = new List<string>();
        var entries = await repository.ListAsync(root.Value, cancellationToken);
        if (!entries.IsSuccess)
        {
            return entries.Error!;
        }

        var name = WorkspaceTreeBuilder.WorkspaceName(root.Value);
        var folder = ToFolder(name, entries.Value, warnings);

        var exported = new List<EnvironmentDefinition>();
        if (request.IncludeEnvironments)
        {
            var list = await environments.ListAsync(root.Value, cancellationToken);
            if (!list.IsSuccess)
            {
                return list.Error!;
            }

            foreach (var file in list.Value)
            {
                if (file.Environment.IsSuccess)
                {
                    // The store reads secret variables without values; make sure of it.
                    exported.Add(file.Environment.Value with
                    {
                        Variables = file.Environment.Value.Variables
                            .Select(v => v.Secret ? v with { Value = string.Empty } : v)
                            .ToList(),
                    });
                }
                else
                {
                    warnings.Add($"Skipped the environment '{file.Id}': {file.Environment.Error!.Message}");
                }
            }
        }

        var output = exporter.Export(new RequestCollection(folder, exported));
        warnings.AddRange(output.Warnings);
        var capturing = CountCapturing(folder);
        if (capturing > 0)
        {
            warnings.Add(capturing == 1
                ? "1 request captures response values; captures are not exported."
                : $"{capturing} requests capture response values; captures are not exported.");
        }

        var destination = HasExtension(request.Destination) ? request.Destination : request.Destination + exporter.Extension;
        var written = await files.WriteTextAsync(destination, output.Content, cancellationToken);
        if (!written.IsSuccess)
        {
            return written.Error!;
        }

        var paths = new List<string> { destination };
        var directory = Path.GetDirectoryName(destination) ?? string.Empty;
        foreach (var companion in output.Companions)
        {
            var path = FreeFilePath(directory, companion.FileName, paths);
            if (path is null)
            {
                warnings.Add($"Could not find a free file name for '{companion.FileName}'.");
                continue;
            }

            var companionWritten = await files.WriteTextAsync(path, companion.Content, cancellationToken);
            if (!companionWritten.IsSuccess)
            {
                return companionWritten.Error!;
            }

            paths.Add(path);
        }

        return new ExportResultDto(destination, paths, folder.RequestCount, exported.Count, warnings);
    }

    private static int CountCapturing(CollectionFolder folder) =>
        folder.Requests.Count(r => r.Captures.Count > 0) + folder.Folders.Sum(CountCapturing);

    private static CollectionFolder ToFolder(string name, IReadOnlyList<WorkspaceEntry> entries, List<string> warnings)
    {
        var folders = new List<CollectionFolder>();
        var requests = new List<Domain.Requests.HttpRequestDefinition>();
        foreach (var entry in entries)
        {
            switch (entry)
            {
                case FolderEntry folder:
                    folders.Add(ToFolder(folder.Path.FileName, folder.Children, warnings));
                    break;
                case RequestFileEntry { Request.IsSuccess: true } file:
                    requests.Add(file.Request.Value);
                    break;
                case RequestFileEntry file:
                    warnings.Add($"Skipped '{file.Path}': {file.Request.Error!.Message}");
                    break;
            }
        }

        return new CollectionFolder(name, folders, requests);
    }

    private static bool HasExtension(string path) => Path.GetExtension(path).Length > 1;

    /// <summary>
    /// A path for <paramref name="fileName"/> in <paramref name="directory"/> that doesn't exist and
    /// isn't one of <paramref name="taken"/>: <c>dev.postman_environment.json</c>, then
    /// <c>dev-2.postman_environment.json</c>, …
    /// </summary>
    private string? FreeFilePath(string directory, string fileName, IReadOnlyCollection<string> taken)
    {
        var dot = fileName.IndexOf('.', 1);
        var stem = dot < 0 ? fileName : fileName[..dot];
        var extension = dot < 0 ? string.Empty : fileName[dot..];
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var candidate = Path.Combine(directory, attempt == 1 ? fileName : $"{stem}-{attempt}{extension}");
            if (!files.Exists(candidate) && !taken.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }
}
