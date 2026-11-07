using Missive.Application.Common;
using Missive.Application.Environments;
using Missive.Application.Workspaces;

namespace Missive.Application.Captures;

/// <summary>Lists the values requests captured for an environment, sorted by name.</summary>
public sealed class ListCapturedValues(ICapturedValueStore store, CurrentWorkspace current)
{
    public async Task<Result<IReadOnlyList<CapturedValueDto>>> ExecuteAsync(string environmentId, CancellationToken cancellationToken = default)
    {
        var root = current.RequireRoot();
        if (!root.IsSuccess)
        {
            return root.Error!;
        }

        if (!EnvironmentMapper.IsValidId(environmentId))
        {
            return EnvironmentMapper.InvalidId(environmentId);
        }

        var values = await CapturedValues.LoadAsync(store, root.Value, environmentId, cancellationToken);
        return values.Map(CapturedValues.ToDtos);
    }
}

/// <summary>
/// Forgets one captured value of an environment, or all of them, and returns what is left. The
/// environment's own value of the variable, if it has one, applies again.
/// </summary>
public sealed class ClearCapturedValues(ICapturedValueStore store, CurrentWorkspace current)
{
    /// <param name="name">The variable to forget, or <c>null</c> for every captured value.</param>
    public async Task<Result<IReadOnlyList<CapturedValueDto>>> ExecuteAsync(
        string environmentId,
        string? name,
        CancellationToken cancellationToken = default)
    {
        var root = current.RequireRoot();
        if (!root.IsSuccess)
        {
            return root.Error!;
        }

        if (!EnvironmentMapper.IsValidId(environmentId))
        {
            return EnvironmentMapper.InvalidId(environmentId);
        }

        var remaining = await CapturedValues.UpdateAsync(
            store,
            root.Value,
            environmentId,
            values => name is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : values.Where(pair => pair.Key != name).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            cancellationToken);
        return remaining.Map(CapturedValues.ToDtos);
    }
}
