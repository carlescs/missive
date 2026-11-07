using Missive.Application.Common;
using Missive.Application.Environments;

namespace Missive.Application.Captures;

/// <summary>A value a request captured from a response, kept for an environment on this computer.</summary>
public sealed record CapturedValueDto(string Name, string Value);

/// <summary>Reads and changes the captured values of one environment in <see cref="ICapturedValueStore"/>.</summary>
public static class CapturedValues
{
    /// <summary>The captured values of an environment, by name.</summary>
    public static async Task<Result<IReadOnlyDictionary<string, string>>> LoadAsync(
        ICapturedValueStore store,
        string root,
        string environmentId,
        CancellationToken cancellationToken)
    {
        var all = await store.LoadAsync(root, cancellationToken);
        return all.IsSuccess
            ? Result<IReadOnlyDictionary<string, string>>.Success(
                all.Value.GetValueOrDefault(environmentId) ?? new Dictionary<string, string>(StringComparer.Ordinal))
            : all.Error!;
    }

    /// <summary>
    /// Replaces an environment's captured values with what <paramref name="change"/> makes of them,
    /// and returns the new values. Nothing is written when they didn't change.
    /// </summary>
    public static async Task<Result<IReadOnlyDictionary<string, string>>> UpdateAsync(
        ICapturedValueStore store,
        string root,
        string environmentId,
        Func<IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>> change,
        CancellationToken cancellationToken)
    {
        var all = await store.LoadAsync(root, cancellationToken);
        if (!all.IsSuccess)
        {
            return all.Error!;
        }

        var current = all.Value.GetValueOrDefault(environmentId) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var updated = change(current);
        if (Same(current, updated))
        {
            return Result<IReadOnlyDictionary<string, string>>.Success(current);
        }

        var values = new Dictionary<string, IReadOnlyDictionary<string, string>>(all.Value, StringComparer.Ordinal)
        {
            [environmentId] = updated,
        };
        var saved = await store.SaveAsync(root, values, cancellationToken);
        return saved.IsSuccess ? Result<IReadOnlyDictionary<string, string>>.Success(updated) : saved.Error!;
    }

    /// <summary>Moves an environment's captured values when it is renamed. Failures only leave local leftovers.</summary>
    public static async Task MoveAsync(ICapturedValueStore store, string root, string from, string to, CancellationToken cancellationToken)
    {
        var all = await store.LoadAsync(root, cancellationToken);
        if (!all.IsSuccess || !all.Value.TryGetValue(from, out var values))
        {
            return;
        }

        var moved = all.Value
            .Where(entry => entry.Key != from)
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        moved[to] = values;
        await store.SaveAsync(root, moved, cancellationToken);
    }

    /// <summary>Forgets an environment's captured values, e.g. when it is deleted.</summary>
    public static async Task RemoveAsync(ICapturedValueStore store, string root, string environmentId, CancellationToken cancellationToken)
    {
        var all = await store.LoadAsync(root, cancellationToken);
        if (all.IsSuccess && all.Value.ContainsKey(environmentId))
        {
            await store.SaveAsync(root, EnvironmentFiles.Without(all.Value, environmentId), cancellationToken);
        }
    }

    public static IReadOnlyList<CapturedValueDto> ToDtos(IReadOnlyDictionary<string, string> values) =>
        values
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new CapturedValueDto(pair.Key, pair.Value))
            .ToList();

    private static bool Same(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var value) && value == pair.Value);
}
