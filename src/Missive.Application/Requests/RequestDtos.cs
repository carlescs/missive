namespace Missive.Application.Requests;

public sealed record KeyValueDto(string Key, string Value, bool Enabled = true);

/// <summary>Auth settings. Only the fields used by <see cref="Type"/> are meaningful.</summary>
/// <param name="Type"><c>none</c>, <c>basic</c>, <c>bearer</c> or <c>apiKey</c>.</param>
/// <param name="In">For API keys: <c>header</c> or <c>query</c>.</param>
public sealed record AuthDto(
    string Type,
    string? Username = null,
    string? Password = null,
    string? Token = null,
    string? Key = null,
    string? Value = null,
    string? In = null);

/// <param name="Type"><c>none</c>, <c>json</c>, <c>text</c>, <c>form</c> or <c>urlencoded</c>.</param>
public sealed record BodyDto(string Type, string Content);

/// <param name="Variable">The variable the value is stored in.</param>
/// <param name="Expression">Where the value comes from: a JSONPath (<c>$.token</c>) or <c>header:Name</c>.</param>
public sealed record CaptureDto(string Variable, string Expression);

/// <param name="Captures">Values to store in variables from a successful response.</param>
/// <param name="Description">Notes about the request, in Markdown; empty when there are none.</param>
public sealed record RequestDto(
    string Name,
    string Method,
    string Url,
    IReadOnlyList<KeyValueDto> Params,
    IReadOnlyList<KeyValueDto> Headers,
    AuthDto Auth,
    BodyDto Body,
    string? Description = null,
    IReadOnlyList<CaptureDto>? Captures = null);

/// <summary>A request as stored: its (possibly new) path and its content.</summary>
public sealed record SavedRequestDto(string Path, RequestDto Request);
