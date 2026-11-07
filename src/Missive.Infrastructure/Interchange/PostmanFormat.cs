using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Missive.Application.Interchange;
using Missive.Domain.Environments;
using Missive.Domain.Requests;
using Missive.Domain.Workspaces;

namespace Missive.Infrastructure.Interchange;

/// <summary>
/// Postman's collection format (v2.0 and v2.1) and environment files. Postman uses the same
/// <c>{{variable}}</c> syntax as Missive, which Insomnia, Bruno, Hoppscotch and others can import
/// too.
/// </summary>
internal static partial class PostmanFormat
{
    public const string SchemaV21 = "https://schema.getpostman.com/json/collection/v2.1.0/collection.json";

    public static bool IsCollection(JsonNode root) =>
        root.Obj("info") is { } info
        && ((info.Str("schema")?.Contains("getpostman.com", StringComparison.OrdinalIgnoreCase) ?? false)
            || root.Get("item") is JsonArray);

    public static bool IsEnvironment(JsonNode root) =>
        root.Get("values") is JsonArray && root.Get("info") is null && root.Str("name") is not null;

    // ---- Import ----

    public static ParsedCollection ImportCollection(JsonNode root, string fallbackName)
    {
        var warnings = new List<string>();
        var info = root.Obj("info");
        var schema = info.Str("schema") ?? string.Empty;
        var format = schema.Contains("v2.0", StringComparison.Ordinal) ? "Postman collection v2.0" : "Postman collection v2.1";
        var name = info.Str("name") is { Length: > 0 } infoName ? infoName : fallbackName;

        var auth = ImportAuth(root.Obj("auth"), name, warnings) ?? RequestAuth.None;
        var folder = ImportFolder(name, root.Arr("item"), auth, warnings);

        var variables = root.Arr("variable")
            .Where(v => !v.Bool("disabled"))
            .Select(v => new EnvironmentVariable(v.Str("key") ?? v.Str("id") ?? string.Empty, v.Get("value").AnyText(), v.Str("type") == "secret"))
            .Where(v => v.Name.Length > 0)
            .ToList();
        IReadOnlyList<EnvironmentDefinition> environments = variables.Count > 0 ? [new EnvironmentDefinition(name, variables)] : [];
        if (variables.Count > 0)
        {
            warnings.Add($"The collection variables became the environment '{name}'; select it to use them.");
        }

        return new ParsedCollection(format, new RequestCollection(folder, environments), warnings);
    }

    public static ParsedCollection ImportEnvironment(JsonNode root)
    {
        var variables = root.Arr("values")
            .Where(v => v.Get("enabled") is not JsonValue enabled || enabled.Text() != "false")
            .Select(v => new EnvironmentVariable(v.Str("key") ?? string.Empty, v.Get("value").AnyText(), v.Str("type") == "secret"))
            .ToList();
        var environment = new EnvironmentDefinition(root.Str("name") ?? string.Empty, variables);
        return new ParsedCollection(
            "Postman environment",
            new RequestCollection(new CollectionFolder(environment.Name, [], []), [environment]),
            []);
    }

    private static CollectionFolder ImportFolder(string name, IEnumerable<JsonNode> items, RequestAuth inherited, List<string> warnings)
    {
        var folders = new List<CollectionFolder>();
        var requests = new List<HttpRequestDefinition>();
        foreach (var item in items)
        {
            var itemName = item.Str("name") ?? string.Empty;
            if (item.Get("item") is JsonArray children)
            {
                var auth = ImportAuth(item.Obj("auth"), itemName, warnings) ?? inherited;
                folders.Add(ImportFolder(itemName, children.Where(c => c is not null)!, auth, warnings));
            }
            else if (item.Get("request") is { } request)
            {
                if (ImportRequest(itemName, request, inherited, warnings) is { } imported)
                {
                    // Postman keeps a request's description on its request, older exports on the item.
                    var description = Description(request.Get("description")) ?? Description(item.Get("description"));
                    requests.Add(description is null ? imported : imported with { Description = description });
                }
            }
        }

        return new CollectionFolder(name, folders, requests);
    }

    /// <summary>A description is text, or an object with the text in <c>content</c>.</summary>
    private static string? Description(JsonNode? description) =>
        (description is JsonObject ? description.Str("content") : description.Text()) is { Length: > 0 } text ? text : null;

    private static HttpRequestDefinition? ImportRequest(string name, JsonNode request, RequestAuth inherited, List<string> warnings)
    {
        // A request can be just its URL.
        if (request is JsonValue)
        {
            var (plainUrl, plainQuery) = InterchangeText.SplitQuery(request.Text() ?? string.Empty);
            return new HttpRequestDefinition(name, RequestMethod.Get, plainUrl, plainQuery);
        }

        var methodName = request.Str("method") ?? "GET";
        if (!RequestMethods.TryParse(methodName, out var method))
        {
            warnings.Add($"Skipped the request '{name}': Missive doesn't support the {methodName} method.");
            return null;
        }

        var (url, query) = ImportUrl(request.Get("url"));
        var headers = ImportHeaders(request.Get("header"));
        var auth = ImportAuth(request.Obj("auth"), name, warnings) ?? inherited;
        var body = ImportBody(request.Obj("body"), headers, name, warnings);

        return new HttpRequestDefinition(
            name,
            method,
            url,
            query,
            InterchangeText.WithoutImpliedContentType(headers, body.Type),
            auth,
            body);
    }

    private static (string Url, List<KeyValueEntry> Query) ImportUrl(JsonNode? url)
    {
        if (url is JsonValue)
        {
            return InterchangeText.SplitQuery(url.Text() ?? string.Empty);
        }

        if (url is not JsonObject)
        {
            return (string.Empty, []);
        }

        var raw = url.Str("raw") ?? BuildRaw(url);
        var (baseUrl, rawQuery) = InterchangeText.SplitQuery(raw);
        var query = url.Get("query") is JsonArray
            ? url.Arr("query")
                .Where(q => q.Str("key") is not null)
                .Select(q => new KeyValueEntry(
                    InterchangeText.Decode(q.Str("key")!),
                    InterchangeText.Decode(q.Str("value") ?? string.Empty),
                    !q.Bool("disabled")))
                .ToList()
            : rawQuery;

        // Path variables (/users/:id) become their value, or a {{variable}} when they have none.
        var values = url.Arr("variable")
            .Where(v => v.Str("key") is not null)
            .GroupBy(v => v.Str("key")!)
            .ToDictionary(g => g.Key, g => g.First().Str("value") ?? string.Empty, StringComparer.Ordinal);
        var resolved = PathVariable().Replace(baseUrl, match =>
        {
            var key = match.Groups["name"].Value;
            return values.TryGetValue(key, out var value) && value.Length > 0 ? value : "{{" + key + "}}";
        });

        return (resolved, query);
    }

    private static string BuildRaw(JsonNode url)
    {
        var host = url.Get("host") is JsonArray ? string.Join('.', url.Arr("host").Select(h => h.Text())) : url.Str("host") ?? string.Empty;
        var path = url.Get("path") is JsonArray ? string.Join('/', url.Arr("path").Select(p => p.Text() ?? p.Str("value"))) : url.Str("path") ?? string.Empty;
        var builder = new StringBuilder();
        if (url.Str("protocol") is { Length: > 0 } protocol)
        {
            builder.Append(protocol).Append("://");
        }

        builder.Append(host);
        if (url.Str("port") is { Length: > 0 } port)
        {
            builder.Append(':').Append(port);
        }

        if (path.Length > 0)
        {
            builder.Append('/').Append(path.TrimStart('/'));
        }

        return builder.ToString();
    }

    private static List<KeyValueEntry> ImportHeaders(JsonNode? header)
    {
        if (header is JsonValue)
        {
            return (header.Text() ?? string.Empty)
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Contains(':'))
                .Select(line => new KeyValueEntry(line[..line.IndexOf(':')].Trim(), line[(line.IndexOf(':') + 1)..].Trim()))
                .ToList();
        }

        return header is JsonArray array
            ? array
                .Where(h => h is not null && h.Str("key") is not null)
                .Select(h => new KeyValueEntry(h!.Str("key")!, h.Str("value") ?? string.Empty, !h.Bool("disabled")))
                .ToList()
            : [];
    }

    /// <summary>The auth set on an item, <see cref="RequestAuth.None"/> for <c>noauth</c>, or <c>null</c> to inherit.</summary>
    private static RequestAuth? ImportAuth(JsonObject? auth, string owner, List<string> warnings)
    {
        var type = auth.Str("type");
        switch (type)
        {
            case null or "inherit":
                return null;
            case "noauth":
                return RequestAuth.None;
            case "basic":
                return new BasicAuth(AuthParam(auth!, type, "username"), AuthParam(auth!, type, "password"));
            case "bearer":
                return new BearerAuth(AuthParam(auth!, type, "token"));
            case "apikey":
                return new ApiKeyAuth(
                    AuthParam(auth!, type, "key"),
                    AuthParam(auth!, type, "value"),
                    AuthParam(auth!, type, "in") == "query" ? ApiKeyLocation.Query : ApiKeyLocation.Header);
            default:
                warnings.Add($"'{owner}' uses {type} auth, which Missive doesn't support; it was imported without auth.");
                return RequestAuth.None;
        }
    }

    /// <summary>An auth setting: v2.1 lists <c>{ key, value }</c> pairs, v2.0 uses an object.</summary>
    private static string AuthParam(JsonObject auth, string type, string key) =>
        auth.Get(type) switch
        {
            JsonArray pairs => pairs.FirstOrDefault(p => p.Str("key") == key).Get("value").AnyText(),
            JsonObject values => values.Get(key).AnyText(),
            _ => string.Empty,
        };

    private static RequestBody ImportBody(JsonObject? body, List<KeyValueEntry> headers, string owner, List<string> warnings)
    {
        if (body is null || body.Bool("disabled"))
        {
            return RequestBody.None;
        }

        switch (body.Str("mode"))
        {
            case "raw":
                var raw = body.Str("raw") ?? string.Empty;
                if (raw.Length == 0)
                {
                    return RequestBody.None;
                }

                var language = body.Obj("options").Obj("raw").Str("language");
                var contentType = headers.FirstOrDefault(h => h.Enabled && string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))?.Value;
                var json = language == "json" || (language is null && InterchangeText.IsJson(contentType));
                return new RequestBody(json ? BodyType.Json : BodyType.Text, raw);

            case "urlencoded":
                return new RequestBody(BodyType.UrlEncoded, InterchangeText.UrlEncodedBody(Fields(body.Arr("urlencoded"), owner, warnings)));

            case "formdata":
                return new RequestBody(BodyType.Form, InterchangeText.FormBody(Fields(body.Arr("formdata"), owner, warnings)));

            case "graphql":
                var graphql = new JsonObject { ["query"] = body.Obj("graphql").Str("query") ?? string.Empty };
                if (body.Obj("graphql").Str("variables") is { Length: > 0 } variables)
                {
                    try
                    {
                        graphql["variables"] = JsonNode.Parse(variables);
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        graphql["variables"] = variables;
                    }
                }

                warnings.Add($"The GraphQL body of '{owner}' was imported as a JSON body.");
                return new RequestBody(BodyType.Json, InterchangeText.Write(graphql));

            case null:
                return RequestBody.None;

            default:
                warnings.Add($"'{owner}' sends a {body.Str("mode")} body, which Missive doesn't support; it was imported without a body.");
                return RequestBody.None;
        }
    }

    private static List<KeyValuePair<string, string>> Fields(IEnumerable<JsonNode> fields, string owner, List<string> warnings)
    {
        var result = new List<KeyValuePair<string, string>>();
        foreach (var field in fields.Where(f => !f.Bool("disabled") && f.Str("key") is not null))
        {
            if (field.Str("type") == "file")
            {
                warnings.Add($"Skipped the file field '{field.Str("key")}' of '{owner}': Missive bodies can't attach files.");
                continue;
            }

            result.Add(KeyValuePair.Create(field.Str("key")!, field.Str("value") ?? string.Empty));
        }

        return result;
    }

    // ---- Export ----

    public static ExportOutput Export(RequestCollection collection)
    {
        var document = new JsonObject
        {
            ["info"] = new JsonObject
            {
                ["_postman_id"] = DeterministicId("collection:" + collection.Root.Name),
                ["name"] = collection.Root.Name,
                ["schema"] = SchemaV21,
            },
            ["item"] = ExportItems(collection.Root),
        };

        var companions = collection.Environments
            .Select(environment => new ExportedFile(
                Slug.From(environment.Name, "environment") + ".postman_environment.json",
                InterchangeText.Write(ExportEnvironment(environment))))
            .ToList();

        return new ExportOutput(InterchangeText.Write(document), companions, []);
    }

    private static JsonArray ExportItems(CollectionFolder folder)
    {
        var items = new JsonArray();
        foreach (var child in folder.Folders)
        {
            items.Add(new JsonObject { ["name"] = child.Name, ["item"] = ExportItems(child) });
        }

        foreach (var request in folder.Requests)
        {
            items.Add(new JsonObject { ["name"] = request.Name, ["request"] = ExportRequest(request) });
        }

        return items;
    }

    private static JsonObject ExportRequest(HttpRequestDefinition original)
    {
        // Postman doesn't read references with spaces inside the braces.
        var request = InterchangeText.MapText(original, text => InterchangeText.RenameVariables(text, name => name));
        var result = new JsonObject
        {
            ["method"] = request.Method.ToName(),
            ["header"] = new JsonArray(request.Headers.Select(h => (JsonNode)KeyValue("key", h.Key, h.Value, h.Enabled)).ToArray()),
        };

        if (ExportAuth(request.Auth) is { } auth)
        {
            result["auth"] = auth;
        }

        if (ExportBody(request.Body) is { } body)
        {
            result["body"] = body;
        }

        result["url"] = ExportUrl(request.Url, request.Params);
        if (original.Description.Length > 0)
        {
            result["description"] = original.Description;
        }

        return result;
    }

    private static JsonObject KeyValue(string keyName, string key, string value, bool enabled)
    {
        var entry = new JsonObject { [keyName] = key, ["value"] = value };
        if (!enabled)
        {
            entry["disabled"] = true;
        }

        return entry;
    }

    private static JsonObject? ExportAuth(RequestAuth auth)
    {
        static JsonObject Auth(string type, params (string Key, string Value)[] values) => new()
        {
            ["type"] = type,
            [type] = new JsonArray(values
                .Select(v => (JsonNode)new JsonObject { ["key"] = v.Key, ["value"] = v.Value, ["type"] = "string" })
                .ToArray()),
        };

        return auth switch
        {
            BasicAuth basic => Auth("basic", ("username", basic.Username), ("password", basic.Password)),
            BearerAuth bearer => Auth("bearer", ("token", bearer.Token)),
            ApiKeyAuth apiKey => Auth("apikey",
                ("key", apiKey.Key),
                ("value", apiKey.Value),
                ("in", apiKey.In == ApiKeyLocation.Query ? "query" : "header")),
            _ => null,
        };
    }

    private static JsonObject? ExportBody(RequestBody body)
    {
        JsonArray Pairs(bool encoded) => new(InterchangeText.BodyFields(body.Content, encoded)
            .Select(f => (JsonNode)new JsonObject { ["key"] = f.Key, ["value"] = f.Value, ["type"] = "text" })
            .ToArray());

        return body.Type switch
        {
            BodyType.Json => new JsonObject
            {
                ["mode"] = "raw",
                ["raw"] = body.Content,
                ["options"] = new JsonObject { ["raw"] = new JsonObject { ["language"] = "json" } },
            },
            BodyType.Text => new JsonObject { ["mode"] = "raw", ["raw"] = body.Content },
            BodyType.UrlEncoded => new JsonObject { ["mode"] = "urlencoded", ["urlencoded"] = Pairs(encoded: true) },
            BodyType.Form => new JsonObject { ["mode"] = "formdata", ["formdata"] = Pairs(encoded: false) },
            _ => null,
        };
    }

    /// <summary>The URL as Postman writes it: the raw text and its parts.</summary>
    private static JsonObject ExportUrl(string url, IReadOnlyList<KeyValueEntry> parameters)
    {
        var result = new JsonObject { ["raw"] = InterchangeText.JoinQuery(url, parameters) };

        var rest = url;
        var scheme = Scheme().Match(rest);
        if (scheme.Success)
        {
            result["protocol"] = scheme.Groups["scheme"].Value;
            rest = rest[scheme.Length..];
        }

        // Ignore a query written in the URL itself; the parameters below are listed separately.
        var queryStart = rest.IndexOf('?');
        if (queryStart >= 0)
        {
            rest = rest[..queryStart];
        }

        var slash = rest.IndexOf('/');
        var hostPort = slash < 0 ? rest : rest[..slash];
        var port = Port().Match(hostPort);
        if (port.Success)
        {
            result["port"] = port.Groups["port"].Value;
            hostPort = hostPort[..port.Index];
        }

        if (hostPort.Length > 0)
        {
            result["host"] = new JsonArray((hostPort.Contains("{{", StringComparison.Ordinal) ? [hostPort] : hostPort.Split('.'))
                .Select(part => (JsonNode)JsonValue.Create(part)!)
                .ToArray());
        }

        if (slash >= 0)
        {
            result["path"] = new JsonArray(rest[(slash + 1)..].Split('/').Select(part => (JsonNode)JsonValue.Create(part)!).ToArray());
        }

        if (parameters.Count > 0)
        {
            result["query"] = new JsonArray(parameters
                .Select(p => (JsonNode)KeyValue("key", InterchangeText.Encode(p.Key), InterchangeText.Encode(p.Value), p.Enabled))
                .ToArray());
        }

        return result;
    }

    private static JsonObject ExportEnvironment(EnvironmentDefinition environment) => new()
    {
        ["id"] = DeterministicId("environment:" + environment.Name),
        ["name"] = environment.Name,
        ["values"] = new JsonArray(environment.Variables
            .Select(v => (JsonNode)new JsonObject
            {
                ["key"] = v.Name,
                ["value"] = v.Secret ? string.Empty : InterchangeText.RenameVariables(v.Value, name => name),
                ["type"] = v.Secret ? "secret" : "default",
                ["enabled"] = true,
            })
            .ToArray()),
        ["_postman_variable_scope"] = "environment",
    };

    /// <summary>The same id for the same input, so exporting again gives the same file.</summary>
    internal static string DeterministicId(string seed)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("missive:" + seed));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true).ToString();
    }

    [GeneratedRegex(@"(?<=/):(?<name>[A-Za-z_][A-Za-z0-9_.-]*)")]
    private static partial Regex PathVariable();

    [GeneratedRegex(@"^(?<scheme>[A-Za-z][A-Za-z0-9+.-]*)://")]
    private static partial Regex Scheme();

    [GeneratedRegex(@":(?<port>[0-9]+|\{\{[^{}]+\}\})$")]
    private static partial Regex Port();
}
