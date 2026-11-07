using Missive.Domain.Requests;
using Missive.Infrastructure.Yaml;

namespace Missive.Infrastructure.Tests;

public class RequestYamlSerializerTests
{
    [Fact]
    public void Serialize_WritesKeysInStableOrderAndOmitsDefaults()
    {
        var request = new HttpRequestDefinition("Get user", RequestMethod.Get, "https://api.example.com/users/1");

        Assert.Equal(
            """
            name: Get user
            method: GET
            url: https://api.example.com/users/1

            """.ReplaceLineEndings("\n"),
            RequestYamlSerializer.Serialize(request));
    }

    [Fact]
    public void Serialize_WritesTheDocumentedFormat()
    {
        var request = new HttpRequestDefinition(
            "Create user",
            RequestMethod.Post,
            "{{baseUrl}}/users",
            @params: [new KeyValueEntry("page", "1"), new KeyValueEntry("debug", "true", Enabled: false)],
            headers: [new KeyValueEntry("Content-Type", "application/json")],
            auth: new BearerAuth("{{apiToken}}"),
            body: new RequestBody(BodyType.Json, "{\n  \"name\": \"Ada Lovelace\"\n}\n"));

        Assert.Equal(
            """
            name: Create user
            method: POST
            url: "{{baseUrl}}/users"
            params:
              page: "1"
              ~debug: "true"
            headers:
              Content-Type: application/json
            auth:
              type: bearer
              token: "{{apiToken}}"
            body:
              type: json
              content: |
                {
                  "name": "Ada Lovelace"
                }

            """.ReplaceLineEndings("\n"),
            RequestYamlSerializer.Serialize(request));
    }

    [Fact]
    public void Serialize_WritesTheDescriptionLastAsABlock()
    {
        var request = new HttpRequestDefinition("Get user", RequestMethod.Get, "/users/1", body: new RequestBody(BodyType.Text, "x"))
        {
            Description = "# Get user\n\nReturns *one* user.\n",
        };

        var yaml = RequestYamlSerializer.Serialize(request);

        Assert.Equal(
            """
            name: Get user
            method: GET
            url: /users/1
            body:
              type: text
              content: x
            description: |
              # Get user

              Returns *one* user.

            """.ReplaceLineEndings("\n"),
            yaml);
        Assert.Equal(request.Description, RequestYamlSerializer.Deserialize(yaml, "x").Description);
        Assert.Equal(string.Empty, RequestYamlSerializer.Deserialize("name: a\n", "x").Description);
    }

    [Theory]
    [InlineData("plain text")]
    [InlineData("")]
    [InlineData("  leading and trailing  ")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("~")]
    [InlineData("0x1F")]
    [InlineData("1.5e3")]
    [InlineData("key: value")]
    [InlineData("# not a comment")]
    [InlineData("value # not a comment")]
    [InlineData("- item")]
    [InlineData("'quoted'")]
    [InlineData("\"double\" \\ backslash")]
    [InlineData("tab\there")]
    [InlineData("no trailing newline\nsecond line")]
    [InlineData("two trailing newlines\n\n")]
    [InlineData("\nleading newline")]
    [InlineData("  indented first line\nsecond")]
    [InlineData("windows\r\nline endings")]
    [InlineData("blank\n\n  lines inside\n")]
    [InlineData("ünïcödé ✓")]
    public void RoundTrip_PreservesValuesExactly(string value)
    {
        var request = new HttpRequestDefinition(
            "Round trip",
            RequestMethod.Put,
            value,
            headers: [new KeyValueEntry("X-Value", value), new KeyValueEntry(value.Length == 0 || value.StartsWith('~') ? "key" + value : value, "k")],
            auth: new BasicAuth("user", value.Length == 0 ? "p" : value),
            body: new RequestBody(BodyType.Text, value));

        var yaml = RequestYamlSerializer.Serialize(request);
        var parsed = RequestYamlSerializer.Deserialize(yaml, "fallback");

        Assert.Equal(request.Url, parsed.Url);
        Assert.Equal(request.Headers, parsed.Headers);
        Assert.Equal(request.Auth, parsed.Auth);
        Assert.Equal(request.Body, parsed.Body);
        Assert.Equal(yaml, RequestYamlSerializer.Serialize(parsed));
    }

    [Fact]
    public void Deserialize_ReadsHandWrittenFilesLeniently()
    {
        var parsed = RequestYamlSerializer.Deserialize(
            """
            # a comment
            url: http://localhost:5000/health
            method: get
            headers:
              Retry: 3
              Empty:
            auth:
              type: apiKey
              key: X-API-Key
              value: secret
              in: query
            """,
            "health");

        Assert.Equal("health", parsed.Name);
        Assert.Equal(RequestMethod.Get, parsed.Method);
        Assert.Equal([new KeyValueEntry("Retry", "3"), new KeyValueEntry("Empty", "")], parsed.Headers);
        Assert.Equal(new ApiKeyAuth("X-API-Key", "secret", ApiKeyLocation.Query), parsed.Auth);
    }

    [Theory]
    [InlineData("name: [unclosed")]
    [InlineData("- just\n- a list")]
    [InlineData("name: x\nmethod: FETCH")]
    [InlineData("name: x\nheaders: [a, b]")]
    [InlineData("name: x\nbody:\n  type: xml")]
    [InlineData("name: x\nname: y")]
    [InlineData("")]
    public void Deserialize_RejectsInvalidFiles(string yaml)
    {
        Assert.Throws<RequestFormatException>(() => RequestYamlSerializer.Deserialize(yaml, "x"));
    }
}
