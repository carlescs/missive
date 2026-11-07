using Missive.Application.Common;
using Missive.Application.Workspaces;
using Missive.Domain.Environments;

namespace Missive.Application.Environments;

/// <summary>The variables requests are sent with.</summary>
/// <param name="EnvironmentName">The selected environment's name, or <c>null</c> when none is selected.</param>
/// <param name="Values">The variables' resolved values, by name.</param>
/// <param name="UnsetSecrets">
/// Secret variables the environment declares but that have no value on this computer (for
/// example after cloning the workspace). They are not in <see cref="Values"/>.
/// </param>
/// <param name="EnvironmentId">The selected environment's file name, or <c>null</c> when none is selected.</param>
/// <param name="SecretValues">The resolved values of the secret variables, to hide them where requests are shown.</param>
/// <param name="CookieJar">Whether the selected environment keeps cookies between requests.</param>
public sealed record ActiveVariables(
    string? EnvironmentName,
    IReadOnlyDictionary<string, string> Values,
    IReadOnlySet<string> UnsetSecrets,
    string? EnvironmentId = null,
    IReadOnlyList<string>? SecretValues = null,
    bool CookieJar = false)
{
    public static ActiveVariables None { get; } =
        new(null, new Dictionary<string, string>(), new HashSet<string>());
}

/// <summary>Reads the variables of the selected environment of the open workspace.</summary>
public sealed class ActiveEnvironment(
    IEnvironmentStore store,
    ISecretStore secrets,
    ICapturedValueStore captures,
    IWorkspaceStateStore state,
    CurrentWorkspace current)
{
    /// <summary>
    /// The selected environment's variables, with the values requests captured for it taking
    /// precedence over the file's values. Without a workspace or a selected environment (or
    /// when the selected file no longer exists) there are none. An environment that can't be read
    /// or whose variables refer to each other in a loop is an error.
    /// </summary>
    public async Task<Result<ActiveVariables>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (current.Root is not { } root)
        {
            return ActiveVariables.None;
        }

        var id = (await state.LoadAsync(root, cancellationToken)).ActiveEnvironment;
        if (id is null || !EnvironmentMapper.IsValidId(id) || !store.Exists(root, id))
        {
            return ActiveVariables.None;
        }

        var environment = await store.ReadAsync(root, id, cancellationToken);
        if (!environment.IsSuccess)
        {
            return environment.Error!.Code == ErrorCodes.NotFound
                ? ActiveVariables.None
                : new Error(environment.Error.Code, $"The selected environment can't be used. {environment.Error.Message}");
        }

        var stored = await secrets.LoadAsync(root, cancellationToken);
        if (!stored.IsSuccess)
        {
            return stored.Error!;
        }

        var secretValues = stored.Value.GetValueOrDefault(id);
        var unset = new SortedSet<string>(StringComparer.Ordinal);
        var variables = new List<EnvironmentVariable>();
        foreach (var variable in environment.Value.Variables)
        {
            if (!variable.Secret)
            {
                variables.Add(variable);
            }
            else if (secretValues?.GetValueOrDefault(variable.Name) is { Length: > 0 } value)
            {
                variables.Add(variable with { Value = value });
            }
            else
            {
                unset.Add(variable.Name);
            }
        }

        var captured = await captures.LoadAsync(root, cancellationToken);
        if (!captured.IsSuccess)
        {
            return captured.Error!;
        }

        if (captured.Value.GetValueOrDefault(id) is { Count: > 0 } capturedValues)
        {
            variables.RemoveAll(variable => capturedValues.ContainsKey(variable.Name));
            unset.ExceptWith(capturedValues.Keys);
            variables.AddRange(capturedValues
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new EnvironmentVariable(pair.Key, pair.Value)));
        }

        var resolved = VariableSet.Resolve(variables);
        if (resolved.Cycle.Count > 0)
        {
            return new Error(
                ErrorCodes.InvalidRequest,
                $"{VariableSet.CycleMessage(resolved.Cycle)} Fix the environment '{environment.Value.Name}'.");
        }

        var resolvedSecrets = environment.Value.Variables
            .Where(v => v.Secret && resolved.Values.ContainsKey(v.Name))
            .Select(v => resolved.Values[v.Name])
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return new ActiveVariables(environment.Value.Name, resolved.Values, unset, id, resolvedSecrets, environment.Value.CookieJar);
    }
}
