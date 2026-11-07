using Missive.Application.Common;
using Missive.Domain.Workspaces;
using Missive.Infrastructure.Yaml;
using YamlDotNet.RepresentationModel;
using static Missive.Infrastructure.Workspaces.WorkspaceFiles;
using static Missive.Infrastructure.Yaml.YamlNodes;

namespace Missive.Infrastructure.Environments;

/// <summary>
/// A git-ignored file in <c>.missive/</c> holding variable values per environment: one mapping of
/// variable names to values per environment file name, both sorted.
/// </summary>
/// <param name="fileName">The file's name in <c>.missive/</c>.</param>
/// <param name="header">A comment written at the top of the file.</param>
/// <param name="description">What the file holds, for error messages (e.g. <c>secrets</c>).</param>
internal sealed class YamlValuesFile(string fileName, string header, string description)
{
    public async Task<Result<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>> LoadAsync(
        string root,
        CancellationToken cancellationToken)
    {
        string? text;
        try
        {
            text = await ReadIfExistsAsync(LocalWorkspaceFolder.FilePath(root, fileName), cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Error(ErrorCodes.Io, $"Could not read the {description} file: {ex.Message}");
        }

        var values = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        if (text is null)
        {
            return values;
        }

        try
        {
            var map = LoadMapping(text);
            foreach (var (key, value) in map.Children)
            {
                var id = ScalarText(key, "environment");
                if (IsNull(value))
                {
                    continue;
                }

                if (value is not YamlMappingNode pairs)
                {
                    throw new WorkspaceFileFormatException($"'{id}' must be a mapping of names to values.");
                }

                values[id] = StringPairs(pairs, id).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            }
        }
        catch (WorkspaceFileFormatException ex)
        {
            return new Error(
                ErrorCodes.InvalidFile,
                $"'{WorkspaceLayout.LocalFolder}/{fileName}' is not a valid {description} file. {ex.Message}");
        }

        return values;
    }

    public async Task<Result<Unit>> SaveAsync(
        string root,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> values,
        CancellationToken cancellationToken)
    {
        var environments = values
            .Where(entry => entry.Value.Count > 0)
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .ToList();

        try
        {
            if (environments.Count == 0)
            {
                LocalWorkspaceFolder.Delete(root, fileName);
                return Unit.Value;
            }

            var writer = new YamlWriter();
            foreach (var (id, pairs) in environments)
            {
                writer.Mapping(id, map =>
                {
                    foreach (var (name, value) in pairs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    {
                        map.Scalar(name, value);
                    }
                });
            }

            await LocalWorkspaceFolder.WriteAsync(root, fileName, header + writer, cancellationToken);
            return Unit.Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Error(ErrorCodes.Io, $"Could not save the {description} file: {ex.Message}");
        }
    }
}
