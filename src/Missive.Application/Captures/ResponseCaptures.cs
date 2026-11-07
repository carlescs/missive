using System.Text.Json;
using Missive.Domain.Requests;

namespace Missive.Application.Captures;

/// <summary>
/// What a capture rule did with a response. <see cref="Value"/> is set when the value was
/// captured and stored; otherwise <see cref="Error"/> says why not.
/// </summary>
public sealed record CaptureResultDto(string Variable, string Expression, string? Value, string? Error = null);

/// <summary>Picks the values capture rules name out of a response.</summary>
public static class ResponseCaptures
{
    /// <summary>Captures apply to successful (2xx) responses only, so an error page never overwrites a token.</summary>
    public static bool Applies(int status) => status is >= 200 and < 300;

    /// <param name="headers">The response headers, in the order received.</param>
    /// <param name="body">The body as text, or <c>null</c> when it is binary.</param>
    /// <param name="truncated">Whether the body was cut off.</param>
    public static IReadOnlyList<CaptureResultDto> Evaluate(
        IReadOnlyList<CaptureRule> rules,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        string? body,
        bool truncated)
    {
        JsonDocument? document = null;
        string? jsonError = null;
        var results = new List<CaptureResultDto>();
        try
        {
            foreach (var rule in rules)
            {
                if (!CaptureExpression.TryParse(rule.Expression, out var source, out var error))
                {
                    results.Add(Failed(rule, error));
                    continue;
                }

                switch (source)
                {
                    case HeaderCaptureSource header:
                        var match = headers.FirstOrDefault(h => string.Equals(h.Key, header.Name, StringComparison.OrdinalIgnoreCase));
                        results.Add(match.Key is null
                            ? Failed(rule, $"The response has no {header.Name} header.")
                            : new CaptureResultDto(rule.Variable, rule.Expression, match.Value));
                        break;
                    case JsonCaptureSource json:
                        if (document is null && jsonError is null)
                        {
                            (document, jsonError) = Parse(body, truncated);
                        }

                        results.Add(document is null ? Failed(rule, jsonError!) : Select(rule, document.RootElement, json.Path));
                        break;
                }
            }
        }
        finally
        {
            document?.Dispose();
        }

        return results;
    }

    private static (JsonDocument? Document, string? Error) Parse(string? body, bool truncated)
    {
        if (body is null)
        {
            return (null, "The response body is not text, so it can't be read as JSON.");
        }

        if (truncated)
        {
            return (null, "The response body was too large and was cut off, so it can't be read as JSON.");
        }

        try
        {
            return (JsonDocument.Parse(body), null);
        }
        catch (JsonException)
        {
            return (null, "The response body is not valid JSON.");
        }
    }

    private static CaptureResultDto Select(CaptureRule rule, JsonElement root, IReadOnlyList<JsonPathStep> path)
    {
        var current = root;
        foreach (var step in path)
        {
            if (step.Index is { } index)
            {
                if (current.ValueKind != JsonValueKind.Array || index >= current.GetArrayLength())
                {
                    return Failed(rule, $"{rule.Expression} matches nothing in the response.");
                }

                current = current[index];
            }
            else if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(step.Property!, out current))
            {
                return Failed(rule, $"{rule.Expression} matches nothing in the response.");
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.Null => Failed(rule, $"{rule.Expression} is null in the response."),
            JsonValueKind.String => new CaptureResultDto(rule.Variable, rule.Expression, current.GetString()),
            JsonValueKind.Object or JsonValueKind.Array =>
                new CaptureResultDto(rule.Variable, rule.Expression, JsonSerializer.Serialize(current)),
            _ => new CaptureResultDto(rule.Variable, rule.Expression, current.GetRawText()),
        };
    }

    private static CaptureResultDto Failed(CaptureRule rule, string error) => new(rule.Variable, rule.Expression, null, error);
}
