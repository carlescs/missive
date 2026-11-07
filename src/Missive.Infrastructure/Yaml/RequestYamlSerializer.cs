using Missive.Application.Requests;
using Missive.Domain.Requests;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Missive.Infrastructure.Yaml;

/// <summary>
/// Reads and writes request files. Keys are always written in the same order
/// (<c>name</c>, <c>method</c>, <c>url</c>, <c>params</c>, <c>headers</c>, <c>auth</c>,
/// <c>body</c>, <c>description</c>) and empty or default values are omitted. Disabled params and headers are written
/// with a <c>~</c> prefix on their name.
/// </summary>
public static class RequestYamlSerializer
{
    public const string DisabledPrefix = HttpRequestDefinition.DisabledPrefix;

    public static string Serialize(HttpRequestDefinition request)
    {
        var writer = new YamlWriter()
            .Scalar("name", request.Name)
            .Scalar("method", request.Method.ToName())
            .Scalar("url", request.Url);

        WriteEntries(writer, "params", request.Params);
        WriteEntries(writer, "headers", request.Headers);
        WriteAuth(writer, request.Auth);

        if (request.Body.Type != BodyType.None)
        {
            writer.Mapping("body", body =>
            {
                body.Scalar("type", RequestMapper.BodyTypeName(request.Body.Type));
                if (request.Body.Content.Length > 0)
                {
                    body.Scalar("content", request.Body.Content);
                }
            });
        }

        if (request.Description.Length > 0)
        {
            writer.Scalar("description", request.Description);
        }

        return writer.ToString();
    }

    /// <summary>
    /// Parses a request file. Throws <see cref="RequestFormatException"/> when the text is not a
    /// valid request. <paramref name="fallbackName"/> is used when the file has no <c>name</c>.
    /// </summary>
    public static HttpRequestDefinition Deserialize(string yaml, string fallbackName)
    {
        YamlMappingNode root;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count == 0)
            {
                throw new RequestFormatException("The file is empty.");
            }

            root = stream.Documents[0].RootNode as YamlMappingNode
                ?? throw new RequestFormatException("The file must contain a YAML mapping (key: value pairs).");
        }
        catch (YamlException ex)
        {
            throw new RequestFormatException($"Invalid YAML at line {ex.Start.Line}: {ex.Message}");
        }

        var name = OptionalString(root, "name");
        var methodName = OptionalString(root, "method");
        var method = RequestMethod.Get;
        if (methodName is not null && !RequestMethods.TryParse(methodName, out method))
        {
            throw new RequestFormatException($"'{methodName}' is not a supported HTTP method.");
        }

        return new HttpRequestDefinition(
            string.IsNullOrWhiteSpace(name) ? fallbackName : name,
            method,
            OptionalString(root, "url") ?? string.Empty,
            ReadEntries(root, "params"),
            ReadEntries(root, "headers"),
            ReadAuth(root),
            ReadBody(root))
        {
            Description = OptionalString(root, "description") ?? string.Empty,
        };
    }

    private static void WriteEntries(YamlWriter writer, string key, IReadOnlyList<KeyValueEntry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        writer.Mapping(key, map =>
        {
            foreach (var entry in entries)
            {
                map.Scalar(entry.Enabled ? entry.Key : DisabledPrefix + entry.Key, entry.Value);
            }
        });
    }

    private static void WriteAuth(YamlWriter writer, RequestAuth auth)
    {
        if (auth is NoAuth)
        {
            return;
        }

        writer.Mapping("auth", map =>
        {
            switch (auth)
            {
                case BasicAuth basic:
                    map.Scalar("type", "basic");
                    ScalarIfNotEmpty(map, "username", basic.Username);
                    ScalarIfNotEmpty(map, "password", basic.Password);
                    break;
                case BearerAuth bearer:
                    map.Scalar("type", "bearer");
                    ScalarIfNotEmpty(map, "token", bearer.Token);
                    break;
                case ApiKeyAuth apiKey:
                    map.Scalar("type", "apiKey");
                    ScalarIfNotEmpty(map, "key", apiKey.Key);
                    ScalarIfNotEmpty(map, "value", apiKey.Value);
                    if (apiKey.In == ApiKeyLocation.Query)
                    {
                        map.Scalar("in", "query");
                    }

                    break;
            }
        });
    }

    private static void ScalarIfNotEmpty(YamlWriter writer, string key, string value)
    {
        if (value.Length > 0)
        {
            writer.Scalar(key, value);
        }
    }

    private static List<KeyValueEntry> ReadEntries(YamlMappingNode root, string key)
    {
        var node = Optional(root, key);
        if (node is null || IsNull(node))
        {
            return [];
        }

        if (node is not YamlMappingNode map)
        {
            throw new RequestFormatException($"'{key}' must be a mapping of names to values.");
        }

        var entries = new List<KeyValueEntry>();
        foreach (var (keyNode, valueNode) in map.Children)
        {
            var name = ScalarText(keyNode, key);
            var enabled = !name.StartsWith(DisabledPrefix, StringComparison.Ordinal);
            entries.Add(new KeyValueEntry(enabled ? name : name[DisabledPrefix.Length..], ScalarText(valueNode, $"{key}.{name}"), enabled));
        }

        return entries;
    }

    private static RequestAuth ReadAuth(YamlMappingNode root)
    {
        var node = Optional(root, "auth");
        if (node is null || IsNull(node))
        {
            return RequestAuth.None;
        }

        if (node is not YamlMappingNode map)
        {
            throw new RequestFormatException("'auth' must be a mapping.");
        }

        var type = OptionalString(map, "type") ?? "none";
        string Field(string name) => OptionalString(map, name) ?? string.Empty;

        return type.ToLowerInvariant() switch
        {
            "none" => RequestAuth.None,
            "basic" => new BasicAuth(Field("username"), Field("password")),
            "bearer" => new BearerAuth(Field("token")),
            "apikey" => new ApiKeyAuth(Field("key"), Field("value"), Field("in").ToLowerInvariant() switch
            {
                "" or "header" => ApiKeyLocation.Header,
                "query" => ApiKeyLocation.Query,
                var other => throw new RequestFormatException($"'{other}' is not a supported API key location."),
            }),
            _ => throw new RequestFormatException($"'{type}' is not a supported auth type."),
        };
    }

    private static RequestBody ReadBody(YamlMappingNode root)
    {
        var node = Optional(root, "body");
        if (node is null || IsNull(node))
        {
            return RequestBody.None;
        }

        if (node is not YamlMappingNode map)
        {
            throw new RequestFormatException("'body' must be a mapping with 'type' and 'content'.");
        }

        var typeName = OptionalString(map, "type") ?? "text";
        if (!RequestMapper.TryParseBodyType(typeName, out var type))
        {
            throw new RequestFormatException($"'{typeName}' is not a supported body type.");
        }

        return type == BodyType.None ? RequestBody.None : new RequestBody(type, OptionalString(map, "content") ?? string.Empty);
    }

    private static YamlNode? Optional(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var node) ? node : null;

    private static string? OptionalString(YamlMappingNode map, string key)
    {
        var node = Optional(map, key);
        return node is null || IsNull(node) ? null : ScalarText(node, key);
    }

    private static bool IsNull(YamlNode node) =>
        node is YamlScalarNode { Style: ScalarStyle.Plain or ScalarStyle.Any } scalar
        && scalar.Tag.IsEmpty
        && scalar.Value is null or "" or "~" or "null" or "Null" or "NULL";

    private static string ScalarText(YamlNode node, string key) => node switch
    {
        YamlScalarNode scalar when IsNull(scalar) => string.Empty,
        YamlScalarNode scalar => scalar.Value ?? string.Empty,
        _ => throw new RequestFormatException($"'{key}' must be a single value."),
    };
}

/// <summary>A request file whose content is not a valid request.</summary>
public sealed class RequestFormatException(string message) : Exception(message);
