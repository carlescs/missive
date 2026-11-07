using Missive.Application.Common;
using Missive.Domain.Environments;

namespace Missive.Application.Environments;

/// <summary>
/// An environment file in the workspace's <c>environments</c> folder. <see cref="Environment"/>
/// holds the parsed file, or why it could not be read. Secret variables have empty values here.
/// </summary>
/// <param name="Id">The file name, e.g. <c>dev.yaml</c>.</param>
public sealed record EnvironmentFile(string Id, Result<EnvironmentDefinition> Environment);

/// <summary>Reads and writes environment files (<c>environments/*.yaml</c>) in a workspace.</summary>
/// <remarks>
/// Environments are identified by their file name. Secret values are never written to these
/// files: they go through <see cref="ISecretStore"/>.
/// </remarks>
public interface IEnvironmentStore
{
    /// <summary>Lists the environment files, sorted by file name. A missing folder is an empty list.</summary>
    Task<Result<IReadOnlyList<EnvironmentFile>>> ListAsync(string root, CancellationToken cancellationToken = default);

    bool Exists(string root, string id);

    Task<Result<EnvironmentDefinition>> ReadAsync(string root, string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes an environment file (names only for secret variables), creating the folder if
    /// needed. Leaves the file untouched when its content would not change; the result says
    /// whether the file was written.
    /// </summary>
    Task<Result<bool>> WriteAsync(string root, string id, EnvironmentDefinition environment, CancellationToken cancellationToken = default);

    /// <summary>Renames an environment file. Fails if the destination already exists.</summary>
    Result<Unit> Move(string root, string from, string to);

    Result<Unit> Delete(string root, string id);
}

/// <summary>
/// Secret variable values, by environment id and variable name. They are stored on this computer
/// only, in the workspace's git-ignored <c>.missive</c> folder.
/// </summary>
public interface ISecretStore
{
    /// <summary>Loads every stored secret; a missing store loads as empty.</summary>
    Task<Result<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>> LoadAsync(
        string root,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces every stored secret. Environments without secrets are left out.</summary>
    Task<Result<Unit>> SaveAsync(
        string root,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> secrets,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Values that requests captured from responses, by environment id and variable name. Like
/// secrets they stay on this computer, in the workspace's git-ignored <c>.missive</c> folder. They
/// take precedence over the environment's own value of a variable with the same name.
/// </summary>
public interface ICapturedValueStore
{
    /// <summary>Loads every captured value; a missing store loads as empty.</summary>
    Task<Result<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>> LoadAsync(
        string root,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces every captured value. Environments without values are left out.</summary>
    Task<Result<Unit>> SaveAsync(
        string root,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> values,
        CancellationToken cancellationToken = default);
}

/// <summary>Per-workspace UI state kept on this computer: which environment is selected.</summary>
/// <param name="ActiveEnvironment">The id of the selected environment, or <c>null</c>.</param>
public sealed record WorkspaceState(string? ActiveEnvironment = null)
{
    public static WorkspaceState Default { get; } = new();
}

/// <summary>Stores <see cref="WorkspaceState"/> in the workspace's git-ignored <c>.missive</c> folder.</summary>
public interface IWorkspaceStateStore
{
    /// <summary>Loads the state; missing or unreadable state loads as <see cref="WorkspaceState.Default"/>.</summary>
    Task<WorkspaceState> LoadAsync(string root, CancellationToken cancellationToken = default);

    Task<Result<Unit>> SaveAsync(string root, WorkspaceState state, CancellationToken cancellationToken = default);
}
