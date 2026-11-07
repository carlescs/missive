using Missive.Application.Captures;
using Missive.Domain.Requests;

namespace Missive.Application.Tests.Http;

public class ResponseCapturesTests
{
    private const string Body = """
        { "data": { "items": [ { "id": 7, "tags": ["a"], "ok": true }, { "name": null } ] }, "odd key": "x", "count": 2.5 }
        """;

    private static string Capture(string expression, string? body = Body, bool truncated = false)
    {
        var result = Assert.Single(ResponseCaptures.Evaluate(
            [new CaptureRule("v", expression)],
            [KeyValuePair.Create("ETag", "\"abc\""), KeyValuePair.Create("Set-Cookie", "a=1"), KeyValuePair.Create("set-cookie", "b=2")],
            body,
            truncated));
        return result.Value ?? "!" + result.Error;
    }

    [Theory]
    [InlineData("$.data.items[0].id", "7")]
    [InlineData("$['data'][\"items\"][0].ok", "true")]
    [InlineData("$['odd key']", "x")]
    [InlineData("$.count", "2.5")]
    [InlineData("$.data.items[0].tags", "[\"a\"]")]
    [InlineData("header:etag", "\"abc\"")]
    [InlineData("header:Set-Cookie", "a=1")]
    public void Evaluate_PicksTheNamedValue(string expression, string expected) =>
        Assert.Equal(expected, Capture(expression));

    [Theory]
    [InlineData("$.data.items[5].id", "matches nothing")]
    [InlineData("$.data.items[1].name", "is null")]
    [InlineData("$.data.id", "matches nothing")]
    [InlineData("header:Location", "no Location header")]
    public void Evaluate_ExplainsMisses(string expression, string reason) =>
        Assert.Contains(reason, Capture(expression));

    [Fact]
    public void Evaluate_NeedsAJsonBody()
    {
        Assert.Contains("not valid JSON", Capture("$.a", "<html/>"));
        Assert.Contains("not text", Capture("$.a", null));
        Assert.Contains("cut off", Capture("$.a", "{}", truncated: true));
        Assert.Equal("{}", Capture("$", "{}"));
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(204, true)]
    [InlineData(302, false)]
    [InlineData(401, false)]
    public void Applies_OnlyToSuccessfulResponses(int status, bool applies) =>
        Assert.Equal(applies, ResponseCaptures.Applies(status));
}
