using Missive.Application.Captures;
using Missive.Application.Requests;

namespace Missive.Application.Http;

/// <summary>A response as shown in the UI.</summary>
/// <param name="StatusText">The reason phrase, e.g. <c>Not Found</c>; may be empty.</param>
/// <param name="ContentType">The media type without parameters, e.g. <c>application/json</c>.</param>
/// <param name="Body">The body as text, or base64 when <paramref name="BodyEncoding"/> is <c>base64</c>.</param>
/// <param name="BodyEncoding"><c>text</c> or <c>base64</c> (for binary content).</param>
/// <param name="Size">Size of the received body in bytes.</param>
/// <param name="Truncated">Whether the body was too large and was cut off.</param>
/// <param name="FileName">The file name suggested when saving the body.</param>
/// <param name="Timings">Where the time went, in milliseconds; <c>null</c> when it wasn't measured.</param>
/// <param name="Redirects">The redirects followed before this response, in order.</param>
/// <param name="Sent">The request as sent (after redirects), with secrets hidden; <c>null</c> when unknown.</param>
/// <param name="Cookies">The cookies set by this response and by the redirects before it.</param>
/// <param name="Captures">What the request's capture rules did; <c>null</c> when it has none.</param>
public sealed record ResponseDto(
    int Status,
    string StatusText,
    IReadOnlyList<KeyValueDto> Headers,
    string? ContentType,
    string Body,
    string BodyEncoding,
    long Size,
    bool Truncated,
    long DurationMs,
    string FileName = "response",
    TimingsDto? Timings = null,
    IReadOnlyList<RedirectDto>? Redirects = null,
    SentRequestDto? Sent = null,
    IReadOnlyList<ResponseCookieDto>? Cookies = null,
    IReadOnlyList<CaptureResultDto>? Captures = null);

/// <summary>The phases of an exchange in milliseconds; connection phases are null when no connection was opened.</summary>
public sealed record TimingsDto(double? Dns, double? Connect, double? Tls, double Wait, double Download);

/// <summary>A redirect that was followed.</summary>
/// <param name="Url">The URL that answered with the redirect, with secrets hidden.</param>
/// <param name="Location">Where it redirected to, with secrets hidden.</param>
public sealed record RedirectDto(string Method, string Url, int Status, string StatusText, string Location, long DurationMs);

/// <summary>A request as it went on the wire, with secrets hidden.</summary>
public sealed record SentRequestDto(string Method, string Url, IReadOnlyList<KeyValueDto> Headers);

/// <summary>A cookie a response set.</summary>
/// <param name="Expires">The expiry it gave (from <c>Expires</c> or <c>Max-Age</c>); <c>null</c> for a session cookie.</param>
public sealed record ResponseCookieDto(
    string Name,
    string Value,
    string? Domain,
    string? Path,
    DateTimeOffset? Expires,
    bool Secure,
    bool HttpOnly,
    string? SameSite);
