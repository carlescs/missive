using Missive.Application.Common;
using Missive.Domain.Requests;

namespace Missive.Application.Requests;

/// <summary>Converts between the request DTOs used by the UI and the domain model.</summary>
public static class RequestMapper
{
    public static RequestDto ToDto(HttpRequestDefinition request) => new(
        request.Name,
        request.Method.ToName(),
        request.Url,
        request.Params.Select(ToDto).ToList(),
        request.Headers.Select(ToDto).ToList(),
        ToDto(request.Auth),
        new BodyDto(BodyTypeName(request.Body.Type), request.Body.Content),
        request.Description,
        request.Captures.Select(c => new CaptureDto(c.Variable, c.Expression)).ToList());

    /// <summary>
    /// Builds a valid domain request from a DTO. Rows with neither key nor value (the blank row the
    /// editor keeps at the end of a table) are dropped.
    /// </summary>
    public static Result<HttpRequestDefinition> ToDomain(RequestDto dto)
    {
        if (!RequestMethods.TryParse(dto.Method, out var method))
        {
            return Invalid($"'{dto.Method}' is not a supported HTTP method.");
        }

        var auth = ToDomain(dto.Auth);
        if (!auth.IsSuccess)
        {
            return auth.Error!;
        }

        if (!TryParseBodyType(dto.Body?.Type, out var bodyType))
        {
            return Invalid($"'{dto.Body?.Type}' is not a supported body type.");
        }

        var request = new HttpRequestDefinition(
            (dto.Name ?? string.Empty).Trim(),
            method,
            dto.Url ?? string.Empty,
            ToEntries(dto.Params),
            ToEntries(dto.Headers),
            auth.Value,
            bodyType == BodyType.None ? RequestBody.None : new RequestBody(bodyType, dto.Body!.Content ?? string.Empty))
        {
            Description = NormalizeDescription(dto.Description),
            Captures = (dto.Captures ?? [])
                .Where(c => !string.IsNullOrWhiteSpace(c.Variable) || !string.IsNullOrWhiteSpace(c.Expression))
                .Select(c => new CaptureRule((c.Variable ?? string.Empty).Trim(), (c.Expression ?? string.Empty).Trim()))
                .ToList(),
        };

        var errors = request.Validate();
        return errors.Count == 0 ? request : Invalid(string.Join(" ", errors));
    }

    public static string BodyTypeName(BodyType type) => type switch
    {
        BodyType.None => "none",
        BodyType.Json => "json",
        BodyType.Text => "text",
        BodyType.Form => "form",
        BodyType.UrlEncoded => "urlencoded",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static bool TryParseBodyType(string? name, out BodyType type)
    {
        foreach (var candidate in Enum.GetValues<BodyType>())
        {
            if (string.Equals(BodyTypeName(candidate), name, StringComparison.OrdinalIgnoreCase))
            {
                type = candidate;
                return true;
            }
        }

        type = BodyType.None;
        return false;
    }

    /// <summary>
    /// Line endings become LF (files are LF-only), and text that is only whitespace counts as no
    /// description, so an emptied Docs tab leaves the key out of the file.
    /// </summary>
    private static string NormalizeDescription(string? description)
    {
        var text = (description ?? string.Empty).ReplaceLineEndings("\n");
        return string.IsNullOrWhiteSpace(text) ? string.Empty : text;
    }

    private static KeyValueDto ToDto(KeyValueEntry entry) => new(entry.Key, entry.Value, entry.Enabled);

    private static AuthDto ToDto(RequestAuth auth) => auth switch
    {
        BasicAuth basic => new AuthDto("basic", Username: basic.Username, Password: basic.Password),
        BearerAuth bearer => new AuthDto("bearer", Token: bearer.Token),
        ApiKeyAuth apiKey => new AuthDto("apiKey", Key: apiKey.Key, Value: apiKey.Value, In: apiKey.In == ApiKeyLocation.Query ? "query" : "header"),
        _ => new AuthDto("none"),
    };

    private static Result<RequestAuth> ToDomain(AuthDto? auth) => auth?.Type?.ToLowerInvariant() switch
    {
        null or "" or "none" => RequestAuth.None,
        "basic" => new BasicAuth(auth.Username ?? string.Empty, auth.Password ?? string.Empty),
        "bearer" => new BearerAuth(auth.Token ?? string.Empty),
        "apikey" => auth.In?.ToLowerInvariant() switch
        {
            null or "" or "header" => new ApiKeyAuth(auth.Key ?? string.Empty, auth.Value ?? string.Empty, ApiKeyLocation.Header),
            "query" => new ApiKeyAuth(auth.Key ?? string.Empty, auth.Value ?? string.Empty, ApiKeyLocation.Query),
            _ => Result<RequestAuth>.Failure(ErrorCodes.InvalidRequest, $"'{auth.In}' is not a supported API key location."),
        },
        _ => Result<RequestAuth>.Failure(ErrorCodes.InvalidRequest, $"'{auth.Type}' is not a supported auth type."),
    };

    private static List<KeyValueEntry> ToEntries(IReadOnlyList<KeyValueDto>? rows) =>
        (rows ?? [])
            .Where(row => !string.IsNullOrEmpty(row.Key) || !string.IsNullOrEmpty(row.Value))
            .Select(row => new KeyValueEntry((row.Key ?? string.Empty).Trim(), row.Value ?? string.Empty, row.Enabled))
            .ToList();

    private static Error Invalid(string message) => new(ErrorCodes.InvalidRequest, message);
}
