using Missive.Application.Common;
using Missive.Application.Environments;

namespace Missive.Infrastructure.Environments;

/// <summary>
/// Keeps secret variable values in <c>.missive/secrets.yaml</c>, which is git-ignored: one
/// mapping of variable names to values per environment file name.
/// </summary>
/// <example>
/// <code>
/// dev.yaml:
///   apiToken: s3cr3t
/// </code>
/// </example>
public sealed class YamlSecretStore : ISecretStore
{
    public const string FileName = "secrets.yaml";

    private readonly YamlValuesFile _file = new(
        FileName,
        "# Secret variable values for this computer. Missive keeps this file out of git.\n",
        "secrets");

    public Task<Result<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>> LoadAsync(
        string root,
        CancellationToken cancellationToken = default) =>
        _file.LoadAsync(root, cancellationToken);

    public Task<Result<Unit>> SaveAsync(
        string root,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> secrets,
        CancellationToken cancellationToken = default) =>
        _file.SaveAsync(root, secrets, cancellationToken);
}
