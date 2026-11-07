using Missive.Domain.Environments;
using Missive.Domain.Requests;

namespace Missive.Application.Interchange;

/// <summary>
/// Adapts requests and environments read from other apps to the rules Missive files follow, so
/// they can be saved: names are filled in, and entries Missive can't represent (unnamed, repeated)
/// are dropped with a warning.
/// </summary>
internal static class ImportSanitizer
{
    public const string UntitledRequest = "Untitled request";
    public const string UntitledEnvironment = "Imported environment";
    private const int MaxNameLength = 200;

    /// <summary>The request ready to save, or <c>null</c> (with a warning) when it can't be.</summary>
    public static HttpRequestDefinition? Request(HttpRequestDefinition request, List<string> warnings)
    {
        var name = OneLine(request.Name);
        if (name.Length == 0)
        {
            name = OneLine($"{request.Method.ToName()} {request.Url}");
        }

        if (name.Length == 0)
        {
            name = UntitledRequest;
        }

        var cleaned = request with
        {
            Name = name.Length > MaxNameLength ? name[..MaxNameLength].TrimEnd() : name,
            Params = Entries(request.Params, "query parameter", StringComparer.Ordinal, name, warnings),
            Headers = Entries(request.Headers, "header", StringComparer.OrdinalIgnoreCase, name, warnings),
            Description = Description(request.Description),
        };

        var errors = cleaned.Validate();
        if (errors.Count > 0)
        {
            warnings.Add($"Skipped the request '{name}': {string.Join(" ", errors)}");
            return null;
        }

        return cleaned;
    }

    /// <summary>Descriptions are saved with LF line endings, and blank ones are left out.</summary>
    private static string Description(string description)
    {
        var text = description.ReplaceLineEndings("\n").Trim('\n');
        return string.IsNullOrWhiteSpace(text) ? string.Empty : text;
    }

    public static EnvironmentDefinition Environment(EnvironmentDefinition environment, List<string> warnings)
    {
        var name = OneLine(environment.Name);
        if (name.Length == 0)
        {
            name = UntitledEnvironment;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var variables = new List<EnvironmentVariable>();
        foreach (var variable in environment.Variables)
        {
            var variableName = variable.Name.Trim();
            if (!VariableTemplate.IsValidName(variableName))
            {
                warnings.Add($"Skipped the variable '{variable.Name}' of the environment '{name}': names can't be empty or contain spaces or braces.");
            }
            else if (!seen.Add(variableName))
            {
                warnings.Add($"Skipped a repeated variable '{variableName}' in the environment '{name}'.");
            }
            else
            {
                variables.Add(variable with { Name = variableName });
            }
        }

        // Stored order: plain variables first, then secrets (as the editor saves them).
        return new EnvironmentDefinition(name, variables.OrderBy(v => v.Secret).ToList());
    }

    private static List<KeyValueEntry> Entries(
        IReadOnlyList<KeyValueEntry> entries,
        string kind,
        StringComparer comparer,
        string requestName,
        List<string> warnings)
    {
        var seen = new HashSet<string>(comparer);
        var result = new List<KeyValueEntry>();
        foreach (var entry in entries)
        {
            var key = entry.Key.Trim().TrimStart(HttpRequestDefinition.DisabledPrefix[0]).Trim();
            if (key.Length == 0)
            {
                if (entry.Value.Length > 0)
                {
                    warnings.Add($"Skipped a {kind} without a name in '{requestName}'.");
                }
            }
            else if (!seen.Add(key))
            {
                warnings.Add($"Skipped the repeated {kind} '{key}' in '{requestName}'; Missive keeps one value per name.");
            }
            else
            {
                result.Add(entry with { Key = key });
            }
        }

        return result;
    }

    private static string OneLine(string? text) =>
        string.Join(' ', (text ?? string.Empty).Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
}
