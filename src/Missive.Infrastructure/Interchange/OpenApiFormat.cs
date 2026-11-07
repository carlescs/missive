using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Missive.Application.Interchange;
using Missive.Domain.Environments;
using Missive.Domain.Requests;

namespace Missive.Infrastructure.Interchange;

/// <summary>
/// API descriptions in OpenAPI 3.x or Swagger 2.0 (JSON or YAML). Each operation becomes a request
/// (in a folder per tag), with example values for its parameters and body; each server becomes an
/// environment defining <c>{{baseUrl}}</c>, plus the variables the security schemes need.
/// </summary>
internal static partial class OpenApiFormat
{
    public const string BaseUrl = "baseUrl";

    private static readonly string[] Methods = ["get", "put", "post", "delete", "options", "head", "patch", "trace"];

    /// <summary>Headers OpenAPI says to ignore as parameters; they come from the body, auth and response types.</summary>
    private static readonly HashSet<string> IgnoredHeaders = new(StringComparer.OrdinalIgnoreCase) { "Accept", "Content-Type", "Authorization" };

    public static bool IsOpenApi(JsonNode root) => root.Str("openapi")?.StartsWith('3') ?? false;

    public static bool IsSwagger(JsonNode root) => root.Str("swagger")?.StartsWith('2') ?? false;

    public static ParsedCollection Import(JsonNode root, string fallbackName)
    {
        var context = new Context(root, IsSwagger(root));
        var title = root.Obj("info").Str("title") is { Length: > 0 } infoTitle ? infoTitle : Path.GetFileNameWithoutExtension(fallbackName);

        // Folders follow the order of the description's tag list, then first use.
        var tagged = new Dictionary<string, List<HttpRequestDefinition>>(StringComparer.Ordinal);
        foreach (var tag in root.Arr("tags").Select(t => t.Str("name")).OfType<string>())
        {
            tagged.TryAdd(tag, []);
        }

        var untagged = new List<HttpRequestDefinition>();
        foreach (var (path, item) in root.Obj("paths") ?? [])
        {
            if (context.Resolve(item) is not JsonObject pathItem)
            {
                continue;
            }

            foreach (var methodName in Methods)
            {
                if (context.Resolve(pathItem.Get(methodName)) is not JsonObject operation)
                {
                    continue;
                }

                if (!RequestMethods.TryParse(methodName, out var method))
                {
                    context.Warnings.Add($"Skipped {methodName.ToUpperInvariant()} {path}: Missive doesn't support the {methodName.ToUpperInvariant()} method.");
                    continue;
                }

                var request = Operation(context, path, method, pathItem, operation);
                if (operation.Arr("tags").Select(t => t.Text()).FirstOrDefault(t => !string.IsNullOrEmpty(t)) is { } tag)
                {
                    (tagged.TryGetValue(tag, out var list) ? list : tagged[tag] = []).Add(request);
                }
                else
                {
                    untagged.Add(request);
                }
            }
        }

        var folders = tagged
            .Where(t => t.Value.Count > 0)
            .Select(t => new CollectionFolder(t.Key, [], t.Value))
            .ToList();
        var collection = new CollectionFolder(title, folders, untagged);

        var servers = context.Swagger ? SwaggerServers(context) : OpenApiServers(context);
        var authVariables = context.AuthVariables.Select(name => new EnvironmentVariable(name, string.Empty, Secret: true)).ToList();
        var environments = servers
            .Select(server => new EnvironmentDefinition(
                servers.Count == 1 ? title : $"{title} ({server.Name})",
                [new EnvironmentVariable(BaseUrl, server.Url), .. authVariables]))
            .ToList();
        if (authVariables.Count > 0)
        {
            context.Warnings.Add($"Enter your credentials ({string.Join(", ", context.AuthVariables)}) as secret values in the imported environment.");
        }

        var format = context.Swagger ? "Swagger 2.0 description" : $"OpenAPI {root.Str("openapi")} description";
        return new ParsedCollection(format, new RequestCollection(collection, environments), context.Warnings);
    }

    private static HttpRequestDefinition Operation(Context context, string path, RequestMethod method, JsonObject pathItem, JsonObject operation)
    {
        var name = operation.Str("summary") is { Length: > 0 } summary
            ? summary
            : operation.Str("operationId") is { Length: > 0 } operationId ? operationId : $"{method.ToName()} {path}";

        // Operation parameters override path-level ones with the same name and location.
        var parameters = new Dictionary<(string In, string Name), JsonNode>();
        foreach (var parameter in pathItem.Arr("parameters").Concat(operation.Arr("parameters")).Select(context.Resolve).OfType<JsonObject>())
        {
            if (parameter.Str("name") is { } parameterName && parameter.Str("in") is { } location)
            {
                parameters[(location, parameterName)] = parameter;
            }
        }

        var pathValues = parameters
            .Where(p => p.Key.In == "path")
            .ToDictionary(p => p.Key.Name, p => ExampleText(context, p.Value), StringComparer.Ordinal);
        var url = "{{" + BaseUrl + "}}" + PathParameter().Replace(path, match =>
        {
            var parameterName = match.Groups["name"].Value;
            return pathValues.GetValueOrDefault(parameterName) is { Length: > 0 } value
                ? Uri.EscapeDataString(value)
                : "{{" + parameterName + "}}";
        });

        var query = parameters
            .Where(p => p.Key.In == "query")
            .Select(p => new KeyValueEntry(p.Key.Name, ExampleText(context, p.Value), p.Value.Bool("required")))
            .ToList();
        var headers = parameters
            .Where(p => p.Key.In == "header" && !IgnoredHeaders.Contains(p.Key.Name))
            .Select(p => new KeyValueEntry(p.Key.Name, ExampleText(context, p.Value), p.Value.Bool("required")))
            .ToList();

        var (body, contentType) = context.Swagger
            ? SwaggerBody(context, operation, parameters)
            : OpenApiBody(context, context.Resolve(operation.Get("requestBody")) as JsonObject);
        if (contentType is not null && !InterchangeText.IsImpliedContentType(body.Type, contentType) && body.Type != BodyType.Form)
        {
            headers.Add(new KeyValueEntry("Content-Type", contentType));
        }

        var auth = Security(context, operation.Get("security") ?? context.Root.Get("security"), name);
        return new HttpRequestDefinition(name, method, url, query, headers, auth, body)
        {
            Description = operation.Str("description") ?? string.Empty,
        };
    }

    // ---- Bodies ----

    private static (RequestBody Body, string? ContentType) OpenApiBody(Context context, JsonObject? requestBody)
    {
        var content = requestBody.Obj("content");
        if (content is null || content.Count == 0)
        {
            return (RequestBody.None, null);
        }

        var (mediaType, media) = content.FirstOrDefault(c => InterchangeText.IsJson(c.Key));
        if (media is null)
        {
            (mediaType, media) = content.FirstOrDefault(c => c.Key.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase));
        }

        if (media is null)
        {
            (mediaType, media) = content.FirstOrDefault(c => c.Key.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase));
        }

        if (media is null)
        {
            (mediaType, media) = content.First();
        }

        var type = InterchangeText.BodyTypeFor(mediaType);
        var schema = media.Get("schema");
        var example = media.Get("example") ?? FirstExample(context, media.Obj("examples"));

        return type switch
        {
            BodyType.Json => (new RequestBody(BodyType.Json, InterchangeText.Write(example?.DeepClone() ?? Generate(context, schema, 0, []) ?? new JsonObject())), mediaType),
            BodyType.UrlEncoded => (new RequestBody(type, InterchangeText.UrlEncodedBody(Fields(context, example, schema))), mediaType),
            BodyType.Form => (new RequestBody(type, InterchangeText.FormBody(Fields(context, example, schema))), mediaType),
            _ => (new RequestBody(BodyType.Text, example.AnyText()), mediaType),
        };
    }

    private static (RequestBody Body, string? ContentType) SwaggerBody(
        Context context,
        JsonObject operation,
        Dictionary<(string In, string Name), JsonNode> parameters)
    {
        var consumes = operation.Arr("consumes").Concat(context.Root.Arr("consumes")).Select(c => c.Text()).OfType<string>().ToList();
        var bodyParameter = parameters.FirstOrDefault(p => p.Key.In == "body").Value;
        if (bodyParameter is not null)
        {
            var mediaType = consumes.FirstOrDefault(InterchangeText.IsJson) ?? consumes.FirstOrDefault() ?? "application/json";
            var schema = bodyParameter.Get("schema");
            var example = bodyParameter.Get("x-example") ?? Generate(context, schema, 0, []);
            return InterchangeText.IsJson(mediaType)
                ? (new RequestBody(BodyType.Json, InterchangeText.Write(example?.DeepClone() ?? new JsonObject())), mediaType)
                : (new RequestBody(BodyType.Text, example.AnyText()), mediaType);
        }

        var formFields = parameters.Where(p => p.Key.In == "formData").ToList();
        if (formFields.Count == 0)
        {
            return (RequestBody.None, null);
        }

        var multipart = consumes.Any(c => c.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            || formFields.Any(f => f.Value.Str("type") == "file");
        var fields = formFields
            .Where(f => f.Value.Str("type") != "file")
            .Select(f => KeyValuePair.Create(f.Key.Name, ExampleText(context, f.Value)))
            .ToList();
        return multipart
            ? (new RequestBody(BodyType.Form, InterchangeText.FormBody(fields)), "multipart/form-data")
            : (new RequestBody(BodyType.UrlEncoded, InterchangeText.UrlEncodedBody(fields)), "application/x-www-form-urlencoded");
    }

    private static List<KeyValuePair<string, string>> Fields(Context context, JsonNode? example, JsonNode? schema)
    {
        if (example is JsonObject values)
        {
            return values.Select(v => KeyValuePair.Create(v.Key, v.Value.AnyText())).ToList();
        }

        var generated = Generate(context, schema, 0, []) as JsonObject;
        return generated?.Select(v => KeyValuePair.Create(v.Key, v.Value.AnyText())).ToList() ?? [];
    }

    private static JsonNode? FirstExample(Context context, JsonObject? examples) =>
        examples?.Select(e => context.Resolve(e.Value).Get("value")).FirstOrDefault(v => v is not null);

    /// <summary>A parameter's example value as text, or empty when it has none.</summary>
    private static string ExampleText(Context context, JsonNode parameter)
    {
        var schema = context.Resolve(parameter.Get("schema")) ?? parameter;
        var example = parameter.Get("example")
            ?? FirstExample(context, parameter.Obj("examples"))
            ?? parameter.Get("x-example")
            ?? schema.Get("example")
            ?? schema.Get("default")
            ?? schema.Arr("enum").FirstOrDefault();
        return example switch
        {
            JsonArray items => string.Join(',', items.Select(i => i.AnyText())),
            _ => example.AnyText(),
        };
    }

    /// <summary>An example value for a schema, built from its examples, defaults and types.</summary>
    private static JsonNode? Generate(Context context, JsonNode? node, int depth, HashSet<string> visiting)
    {
        if (node is not JsonObject || depth > 12)
        {
            return null;
        }

        var reference = node.Str("$ref");
        if (reference is not null && !visiting.Add(reference))
        {
            return null;
        }

        try
        {
            var schema = context.Resolve(node);
            if (schema is not JsonObject)
            {
                return null;
            }

            if ((schema.Get("example") ?? schema.Get("default")) is { } given)
            {
                return given.DeepClone();
            }

            if (schema.Arr("examples").FirstOrDefault() is { } first)
            {
                return first.DeepClone();
            }

            if (schema.Get("allOf") is JsonArray allOf)
            {
                var merged = new JsonObject();
                foreach (var part in allOf.Select(p => Generate(context, p, depth + 1, visiting)).OfType<JsonObject>())
                {
                    foreach (var (key, value) in part)
                    {
                        merged[key] = value?.DeepClone();
                    }
                }

                return merged;
            }

            if ((schema.Arr("oneOf").FirstOrDefault() ?? schema.Arr("anyOf").FirstOrDefault()) is { } option)
            {
                return Generate(context, option, depth + 1, visiting);
            }

            if (schema.Arr("enum").FirstOrDefault() is { } choice)
            {
                return choice.DeepClone();
            }

            var type = schema.Get("type") is JsonArray types
                ? types.Select(t => t.Text()).FirstOrDefault(t => t != "null")
                : schema.Str("type");
            if (type is null && schema.Get("properties") is JsonObject)
            {
                type = "object";
            }

            switch (type)
            {
                case "object":
                    var result = new JsonObject();
                    foreach (var (key, value) in schema.Obj("properties") ?? [])
                    {
                        if (context.Resolve(value).Bool("readOnly"))
                        {
                            continue;
                        }

                        result[key] = Generate(context, value, depth + 1, visiting);
                    }

                    return result;
                case "array":
                    var item = Generate(context, schema.Get("items"), depth + 1, visiting);
                    return item is null ? new JsonArray() : new JsonArray(item);
                case "integer" or "number":
                    return JsonValue.Create(0);
                case "boolean":
                    return JsonValue.Create(false);
                case "string":
                    return JsonValue.Create(schema.Str("format") switch
                    {
                        "date-time" => "2024-01-01T00:00:00Z",
                        "date" => "2024-01-01",
                        "email" => "user@example.com",
                        "uuid" => "00000000-0000-0000-0000-000000000000",
                        "uri" or "url" => "https://example.com",
                        _ => "string",
                    });
                default:
                    return null;
            }
        }
        finally
        {
            if (reference is not null)
            {
                visiting.Remove(reference);
            }
        }
    }

    // ---- Servers and security ----

    private static List<(string Name, string Url)> OpenApiServers(Context context)
    {
        var servers = new List<(string Name, string Url)>();
        foreach (var server in context.Root.Arr("servers"))
        {
            var url = ServerVariable().Replace(server.Str("url") ?? string.Empty, match =>
                server.Obj("variables").Obj(match.Groups["name"].Value).Str("default") ?? match.Value);
            if (url.StartsWith('/') || url.Length == 0)
            {
                context.Warnings.Add($"The server '{(url.Length == 0 ? "/" : url)}' is relative; its base URL was set to http://localhost{url}. Change it in the environment.");
                url = "http://localhost" + url;
            }

            servers.Add((server.Str("description") is { Length: > 0 } description ? description : url, url.TrimEnd('/')));
        }

        if (servers.Count == 0)
        {
            context.Warnings.Add("The description lists no server; the base URL was set to http://localhost. Change it in the environment.");
            servers.Add(("localhost", "http://localhost"));
        }

        return servers.DistinctBy(s => s.Url).ToList();
    }

    private static List<(string Name, string Url)> SwaggerServers(Context context)
    {
        var root = context.Root;
        var host = root.Str("host");
        if (host is null)
        {
            context.Warnings.Add("The description has no host; the base URL was set to http://localhost. Change it in the environment.");
            host = "localhost";
        }

        var schemes = root.Arr("schemes").Select(s => s.Text()).OfType<string>().ToList();
        var scheme = schemes.Contains("https") ? "https" : schemes.FirstOrDefault() ?? "https";
        var url = $"{scheme}://{host}{root.Str("basePath")}".TrimEnd('/');
        return [(host, url)];
    }

    /// <summary>The auth for the first security requirement Missive can use.</summary>
    private static RequestAuth Security(Context context, JsonNode? requirements, string owner)
    {
        var schemes = context.Swagger ? context.Root.Obj("securityDefinitions") : context.Root.Obj("components").Obj("securitySchemes");
        foreach (var requirement in requirements is JsonArray array ? array.OfType<JsonObject>() : [])
        {
            foreach (var (schemeName, _) in requirement)
            {
                if (context.Resolve(schemes.Get(schemeName)) is not JsonObject scheme)
                {
                    continue;
                }

                var type = scheme.Str("type");
                var httpScheme = scheme.Str("scheme")?.ToLowerInvariant();
                if (type == "basic" || (type == "http" && httpScheme == "basic"))
                {
                    context.UseAuthVariable("username");
                    context.UseAuthVariable("password");
                    return new BasicAuth("{{username}}", "{{password}}");
                }

                if (type == "http" && httpScheme == "bearer")
                {
                    context.UseAuthVariable("token");
                    return new BearerAuth("{{token}}");
                }

                if (type == "apiKey" && scheme.Str("in") is "header" or "query" && scheme.Str("name") is { } keyName)
                {
                    context.UseAuthVariable("apiKey");
                    return new ApiKeyAuth(keyName, "{{apiKey}}", scheme.Str("in") == "query" ? ApiKeyLocation.Query : ApiKeyLocation.Header);
                }

                if (type is "oauth2" or "openIdConnect")
                {
                    context.UseAuthVariable("token");
                    if (context.WarnedOAuth.Add(schemeName))
                    {
                        context.Warnings.Add($"'{schemeName}' uses {type}; Missive sends the token you enter as {{{{token}}}} as a bearer token but can't obtain one.");
                    }

                    return new BearerAuth("{{token}}");
                }
            }
        }

        return RequestAuth.None;
    }

    /// <summary>The description being read, with what the import has found so far.</summary>
    private sealed class Context(JsonNode root, bool swagger)
    {
        private const int MaxHops = 32;
        private readonly HashSet<string> _externalReferences = new(StringComparer.Ordinal);

        public JsonNode Root { get; } = root;

        public bool Swagger { get; } = swagger;

        public List<string> Warnings { get; } = [];

        /// <summary>Variables the security schemes use, in the order first used.</summary>
        public List<string> AuthVariables { get; } = [];

        public HashSet<string> WarnedOAuth { get; } = new(StringComparer.Ordinal);

        public void UseAuthVariable(string name)
        {
            if (!AuthVariables.Contains(name))
            {
                AuthVariables.Add(name);
            }
        }

        /// <summary>Follows <c>$ref</c>s within the document; references to other files resolve to <c>null</c>.</summary>
        public JsonNode? Resolve(JsonNode? node)
        {
            for (var hop = 0; hop < MaxHops && node.Str("$ref") is { } reference; hop++)
            {
                if (!reference.StartsWith("#/", StringComparison.Ordinal))
                {
                    if (_externalReferences.Add(reference))
                    {
                        Warnings.Add($"Missive can't follow the reference '{reference}' to another file; that part was left out.");
                    }

                    return null;
                }

                node = Root;
                foreach (var segment in reference[2..].Split('/'))
                {
                    node = node.Get(Uri.UnescapeDataString(segment).Replace("~1", "/").Replace("~0", "~"));
                }
            }

            return node;
        }
    }

    [GeneratedRegex(@"\{(?<name>[^{}/]+)\}")]
    private static partial Regex PathParameter();

    [GeneratedRegex(@"\{(?<name>[^{}]+)\}")]
    private static partial Regex ServerVariable();
}
