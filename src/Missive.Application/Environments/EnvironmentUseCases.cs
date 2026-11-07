using Missive.Application.Captures;
using Missive.Application.Common;
using Missive.Application.Cookies;
using Missive.Application.Workspaces;
using Missive.Domain.Environments;
using Missive.Domain.Workspaces;

namespace Missive.Application.Environments;

/// <summary>Lists the open workspace's environments and which one is selected.</summary>
public sealed class ListEnvironments(IEnvironmentStore store, IWorkspaceStateStore state, CurrentWorkspace current)
{
    public async Task<Result<EnvironmentListDto>> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var root = current.RequireRoot();
        if (!root.IsSuccess)
        {
            return root.Error!;
        }

        var files = await store.ListAsync(root.Value, cancellationToken);
        if (!files.IsSuccess)
        {
            return files.Error!;
        }

        var summaries = files.Value
            .Select(file => file.Environment.IsSuccess
                ? new EnvironmentSummaryDto(file.Id, file.Environment.Value.Name)
                : new EnvironmentSummaryDto(file.Id, file.Id, file.Environment.Error!.Message))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToList();

        // The selected environment may have been deleted or renamed outside Missive.
        var active = (await state.LoadAsync(root.Value, cancellationToken)).ActiveEnvironment;
        return new EnvironmentListDto(summaries.Any(e => e.Id == active) ? active : null, summaries);
    }
}

/// <summary>Reads an environment, with the values of its secrets from this computer.</summary>
public sealed class LoadEnvironment(IEnvironmentStore store, ISecretStore secrets, CurrentWorkspace current)
{
    public async Task<Result<EnvironmentDto>> ExecuteAsync(string id, CancellationToken cancellationToken = default)
    {
        var root = current.RequireRoot();
        if (!root.IsSuccess)
        {
            return root.Error!;
        }

        if (!EnvironmentMapper.IsValidId(id))
        {
            return EnvironmentMapper.InvalidId(id);
        }

        var environment = await store.ReadAsync(root.Value, id, cancellationToken);
        if (!environment.IsSuccess)
        {
            return environment.Error!;
        }

        var stored = await secrets.LoadAsync(root.Value, cancellationToken);
        return stored.IsSuccess
            ? EnvironmentMapper.ToDto(environment.Value, stored.Value.GetValueOrDefault(id))
            : stored.Error!;
    }
}

/// <summary>
/// Saves a new environment as a file named after it (<c>staging.yaml</c>, or
/// <c>staging-2.yaml</c> if that name is taken), and its secret values on this computer.
/// </summary>
public sealed class CreateEnvironment(IEnvironmentStore store, ISecretStore secrets, CurrentWorkspace current)
{
    public async Task<Result<SavedEnvironmentDto>> ExecuteAsync(EnvironmentDto dto, CancellationToken cancellationToken = default)
    {
        var root = current.RequireRoot();
        if (!root.IsSuccess)
        {
            return root.Error!;
        }

        var environment = EnvironmentMapper.ToDomain(dto);
        if (!environment.IsSuccess)
        {
            return environment.Error!;
        }

        var id = EnvironmentFiles.FreeId(store, root.Value, environment.Value.Name, current: null);
        if (id is null)
        {
            return new Error(ErrorCodes.Io, $"Could not find a free file name for '{environment.Value.Name}'.");
        }

        // Check the secrets can be stored before creating the file.
        var stored = await secrets.LoadAsync(root.Value, cancellationToken);
        if (!stored.IsSuccess)
        {
            return stored.Error!;
        }

        var written = await store.WriteAsync(root.Value, id, environment.Value, cancellationToken);
        if (!written.IsSuccess)
        {
            return written.Error!;
        }

        var saved = await EnvironmentFiles.SaveSecretsAsync(secrets, root.Value, stored.Value, id, null, environment.Value, cancellationToken);
        return saved.IsSuccess
            ? new SavedEnvironmentDto(id, EnvironmentMapper.ToDto(environment.Value, EnvironmentMapper.SecretValues(environment.Value)))
            : saved.Error!;
    }
}

/// <summary>
/// Saves changes to an environment. Like requests, the file is renamed to match a new name when
/// its file name is still the slug of the old name. Secret values are stored on this computer;
/// they, the cookies and the captured values follow a renamed file.
/// </summary>
public sealed class SaveEnvironment(
    IEnvironmentStore store,
    ISecretStore secrets,
    IWorkspaceStateStore state,
    ICookieJarStore cookieJars,
    ICapturedValueStore captures,
    CurrentWorkspace current)
{
    public async Task<Result<SavedEnvironmentDto>> ExecuteAsync(string id, EnvironmentDto dto, CancellationToken cancellationToken = default)
    {
        var root = current.RequireRoot();
        if (!root.IsSuccess)
        {
            return root.Error!;
        }

        if (!EnvironmentMapper.IsValidId(id))
        {
            return EnvironmentMapper.InvalidId(id);
        }

        var environment = EnvironmentMapper.ToDomain(dto);
        if (!environment.IsSuccess)
        {
            return environment.Error!;
        }

        var stored = await secrets.LoadAsync(root.Value, cancellationToken);
        if (!stored.IsSuccess)
        {
            return stored.Error!;
        }

        var target = await RenameTargetAsync(root.Value, id, environment.Value.Name, cancellationToken);

        var written = await store.WriteAsync(root.Value, id, environment.Value, cancellationToken);
        if (!written.IsSuccess)
        {
            return written.Error!;
        }

        var savedId = id;
        if (target is not null && target != id)
        {
            var moved = store.Move(root.Value, id, target);
            if (!moved.IsSuccess)
            {
                return moved.Error!;
            }

            savedId = target;
            var selected = await state.LoadAsync(root.Value, cancellationToken);
            if (selected.ActiveEnvironment == id)
            {
                await state.SaveAsync(root.Value, selected with { ActiveEnvironment = savedId }, cancellationToken);
            }

            await CookieJars.MoveAsync(cookieJars, root.Value, id, savedId, cancellationToken);
            await CapturedValues.MoveAsync(captures, root.Value, id, savedId, cancellationToken);
        }

        if (!environment.Value.CookieJar)
        {
            // Turning the jar off forgets its cookies, so turning it on again starts afresh.
            await CookieJars.RemoveAsync(cookieJars, root.Value, savedId, cancellationToken);
        }

        var saved = await EnvironmentFiles.SaveSecretsAsync(secrets, root.Value, stored.Value, savedId, id, environment.Value, cancellationToken);
        return saved.IsSuccess
            ? new SavedEnvironmentDto(savedId, EnvironmentMapper.ToDto(environment.Value, EnvironmentMapper.SecretValues(environment.Value)))
            : saved.Error!;
    }

    private async Task<string?> RenameTargetAsync(string root, string id, string newName, CancellationToken cancellationToken)
    {
        if (!store.Exists(root, id))
        {
            return null;
        }

        var existing = await store.ReadAsync(root, id, cancellationToken);
        if (!existing.IsSuccess || existing.Value.Name == newName)
        {
            return null;
        }

        var stem = WorkspaceLayout.FileStem(id);
        var oldSlug = WorkspaceLayout.FileStem(WorkspaceLayout.EnvironmentFileName(existing.Value.Name));
        var newSlug = WorkspaceLayout.FileStem(WorkspaceLayout.EnvironmentFileName(newName));
        return stem != oldSlug || newSlug == oldSlug
            ? null
            : EnvironmentFiles.FreeId(store, root, newName, id);
    }
}

/// <summary>Deletes an environment file and the secret values, cookies and captured values stored for it.</summary>
public sealed class DeleteEnvironment(
    IEnvironmentStore store,
    ISecretStore secrets,
    IWorkspaceStateStore state,
    ICookieJarStore cookieJars,
    ICapturedValueStore captures,
    CurrentWorkspace current)
{
    public async Task<Result<Unit>> ExecuteAsync(string id, CancellationToken cancellationToken = default)
    {
        var root = current.RequireRoot();
        if (!root.IsSuccess)
        {
            return root.Error!;
        }

        if (!EnvironmentMapper.IsValidId(id))
        {
            return EnvironmentMapper.InvalidId(id);
        }

        if (!store.Exists(root.Value, id))
        {
            return EnvironmentMapper.NotFound(id);
        }

        var deleted = store.Delete(root.Value, id);
        if (!deleted.IsSuccess)
        {
            return deleted.Error!;
        }

        var selected = await state.LoadAsync(root.Value, cancellationToken);
        if (selected.ActiveEnvironment == id)
        {
            await state.SaveAsync(root.Value, selected with { ActiveEnvironment = null }, cancellationToken);
        }

        // The file is gone either way; secrets that can't be cleaned up are only local leftovers.
        var stored = await secrets.LoadAsync(root.Value, cancellationToken);
        if (stored.IsSuccess && stored.Value.ContainsKey(id))
        {
            await secrets.SaveAsync(root.Value, EnvironmentFiles.Without(stored.Value, id), cancellationToken);
        }

        await CookieJars.RemoveAsync(cookieJars, root.Value, id, cancellationToken);
        await CapturedValues.RemoveAsync(captures, root.Value, id, cancellationToken);
        return Unit.Value;
    }
}

/// <summary>Selects the environment whose variables are used when sending requests, or none.</summary>
public sealed class SelectEnvironment(IEnvironmentStore store, IWorkspaceStateStore state, CurrentWorkspace current)
{
    /// <param name="id">The environment to select, or <c>null</c> for none.</param>
    public async Task<Result<Unit>> ExecuteAsync(string? id, CancellationToken cancellationToken = default)
    {
        var root = current.RequireRoot();
        if (!root.IsSuccess)
        {
            return root.Error!;
        }

        if (id is not null)
        {
            if (!EnvironmentMapper.IsValidId(id))
            {
                return EnvironmentMapper.InvalidId(id);
            }

            if (!store.Exists(root.Value, id))
            {
                return EnvironmentMapper.NotFound(id);
            }
        }

        var selected = await state.LoadAsync(root.Value, cancellationToken);
        return selected.ActiveEnvironment == id
            ? Unit.Value
            : await state.SaveAsync(root.Value, selected with { ActiveEnvironment = id }, cancellationToken);
    }
}

internal static class EnvironmentFiles
{
    private const int MaxAttempts = 1000;

    /// <summary>The first free file name for an environment called <paramref name="name"/>.</summary>
    public static string? FreeId(IEnvironmentStore store, string root, string name, string? current)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var candidate = WorkspaceLayout.EnvironmentFileName(name, attempt);
            if (candidate == current || !store.Exists(root, candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Stores the secret values of <paramref name="environment"/> under <paramref name="id"/>,
    /// dropping those stored under <paramref name="previousId"/> when it was renamed. Writes
    /// nothing when no secret changed.
    /// </summary>
    public static async Task<Result<Unit>> SaveSecretsAsync(
        ISecretStore secrets,
        string root,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> stored,
        string id,
        string? previousId,
        EnvironmentDefinition environment,
        CancellationToken cancellationToken)
    {
        var values = EnvironmentMapper.SecretValues(environment);
        var updated = Without(previousId is null ? stored : Without(stored, previousId), id);
        if (values.Count > 0)
        {
            updated = new Dictionary<string, IReadOnlyDictionary<string, string>>(updated, StringComparer.Ordinal) { [id] = values };
        }

        return SameSecrets(stored, updated) ? Unit.Value : await secrets.SaveAsync(root, updated, cancellationToken);
    }

    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Without(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> secrets,
        string id) =>
        secrets.Where(entry => entry.Key != id).ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

    private static bool SameSecrets(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> a,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> b) =>
        a.Count == b.Count
        && a.All(entry => b.TryGetValue(entry.Key, out var other)
            && entry.Value.Count == other.Count
            && entry.Value.All(value => other.TryGetValue(value.Key, out var otherValue) && otherValue == value.Value));
}
