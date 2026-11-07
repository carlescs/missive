using Missive.Domain.Requests;

namespace Missive.Domain.Tests;

public class CaptureExpressionTests
{
    [Fact]
    public void TryParse_ReadsJsonPathSteps()
    {
        Assert.True(CaptureExpression.TryParse(" $.data['the key'][2].id ", out var source, out _));

        Assert.Equal(
            new JsonCaptureSource([JsonPathStep.Member("data"), JsonPathStep.Member("the key"), JsonPathStep.At(2), JsonPathStep.Member("id")]),
            source);
    }

    [Fact]
    public void TryParse_ReadsHeaders()
    {
        Assert.True(CaptureExpression.TryParse("Header: X-Request-Id", out var source, out _));

        Assert.Equal(new HeaderCaptureSource("X-Request-Id"), source);
    }

    [Theory]
    [InlineData("")]
    [InlineData("token")]
    [InlineData("$..id")]
    [InlineData("$.items[*]")]
    [InlineData("$.items[-1]")]
    [InlineData("$.items[0")]
    [InlineData("$[?(@.id)]")]
    [InlineData("header:")]
    [InlineData("header:Two words")]
    public void TryParse_RefusesWhatItDoesNotSupport(string expression)
    {
        Assert.False(CaptureExpression.TryParse(expression, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Validate_ChecksCaptures()
    {
        var request = new HttpRequestDefinition("Login", RequestMethod.Post, "/login")
        {
            Captures = [new("token", "$.token"), new("token", "$.other"), new("bad name", "$.x"), new("etag", "ETag"), new("", "$.y")],
        };

        var errors = request.Validate();

        Assert.Contains(errors, e => e.Contains("'token' is captured more than once"));
        Assert.Contains(errors, e => e.Contains("'bad name' is not a valid variable name"));
        Assert.Contains(errors, e => e.StartsWith("Capture 'etag':"));
        Assert.Contains(errors, e => e.Contains("needs a variable name"));
        Assert.Equal(4, errors.Count);
    }
}
