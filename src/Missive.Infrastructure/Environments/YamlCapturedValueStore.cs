using Missive.Application.Common;
using Missive.Application.Environments;

namespace Missive.Infrastructure.Environments;

/// <summary>
/// Keeps the values requests captured from responses in <c>.missive/captures.yaml</c>, which is
/// git-ignored: one mapping of variable names to values per environment file name.
/// </summary>
/// <example>
/// <code>
/// dev.yaml:
///   token: eyJhbGciOi...
/// </code>
/// </example>
public sealed class YamlCapturedValueStore : ICapturedValueStore
{
    public const string FileName = "captures.yaml";

    private readonly YamlValuesFile _file = new(
        FileName,
        "# Values captured from responses on this computer. Missive keeps this file out of git.\n",
        "captured values");

    public Task<Result<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>> LoadAsync(
        string root,
        CancellationToken cancellationToken = default) =>
        _file.LoadAsync(root, cancellationToken);

    public Task<Result<Unit>> SaveAsync(
        string root,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> values,
        CancellationToken cancellationToken = default) =>
        _file.SaveAsync(root, values, cancellationToken);
}
