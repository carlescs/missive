namespace Missive.Domain.Requests;

/// <summary>A saved HTTP request: the content of one request file in a workspace.</summary>
public sealed record HttpRequestDefinition
{
    public HttpRequestDefinition(
        string name,
        RequestMethod method,
        string url,
        IReadOnlyList<KeyValueEntry>? @params = null,
        IReadOnlyList<KeyValueEntry>? headers = null,
        RequestAuth? auth = null,
        RequestBody? body = null)
    {
        Name = name;
        Method = method;
        Url = url;
        Params = @params ?? [];
        Headers = headers ?? [];
        Auth = auth ?? RequestAuth.None;
        Body = body ?? RequestBody.None;
    }

    public string Name { get; init; }

    public RequestMethod Method { get; init; }

    public string Url { get; init; }

    public IReadOnlyList<KeyValueEntry> Params { get; init; }

    public IReadOnlyList<KeyValueEntry> Headers { get; init; }

    public RequestAuth Auth { get; init; }

    public RequestBody Body { get; init; }

    /// <summary>Values to store in variables from a successful response, in order.</summary>
    public IReadOnlyList<CaptureRule> Captures { get; init; } = [];

    /// <summary>Notes about the request, in Markdown; empty when there are none.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Request files mark disabled parameters and headers with this prefix, so names cannot start
    /// with it.
    /// </summary>
    public const string DisabledPrefix = "~";

    /// <summary>
    /// Checks the rules a request must satisfy before it can be saved. Returns an empty list
    /// when the request is valid.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add("The request needs a name.");
        }

        CheckEntries(Params, "parameter", StringComparer.Ordinal, errors);
        CheckEntries(Headers, "header", StringComparer.OrdinalIgnoreCase, errors);
        CheckCaptures(Captures, errors);

        return errors;
    }

    private static void CheckCaptures(IReadOnlyList<CaptureRule> captures, List<string> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capture in captures)
        {
            if (string.IsNullOrWhiteSpace(capture.Variable))
            {
                errors.Add("Every capture needs a variable name.");
            }
            else if (!VariableTemplate.IsValidName(capture.Variable))
            {
                errors.Add($"'{capture.Variable}' is not a valid variable name: it can't contain spaces or braces.");
            }
            else if (!seen.Add(capture.Variable))
            {
                errors.Add($"The variable '{capture.Variable}' is captured more than once.");
            }
            else if (!CaptureExpression.TryParse(capture.Expression, out _, out var error))
            {
                errors.Add($"Capture '{capture.Variable}': {error}");
            }
        }
    }

    private static void CheckEntries(
        IReadOnlyList<KeyValueEntry> entries,
        string kind,
        StringComparer comparer,
        List<string> errors)
    {
        var seen = new HashSet<string>(comparer);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
            {
                errors.Add($"Every {kind} needs a name.");
            }
            else if (entry.Key.StartsWith(DisabledPrefix, StringComparison.Ordinal))
            {
                errors.Add($"The {kind} name '{entry.Key}' cannot start with '{DisabledPrefix}'.");
            }
            else if (!seen.Add(entry.Key))
            {
                errors.Add($"The {kind} '{entry.Key}' is defined more than once.");
            }
        }
    }
}
