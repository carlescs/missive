using System.Text;
using Missive.Application.Common;
using Missive.Application.Cookies;
using Missive.Application.Environments;
using Missive.Application.Http;
using Missive.Application.Requests;
using Missive.Application.Workspaces;
using Missive.Domain.Environments;
using Missive.Domain.Http;
using NSubstitute;

namespace Missive.Application.Tests.Http;

using Jars = IReadOnlyDictionary<string, IReadOnlyList<StoredCookie>>;

public class SendRequestTests
{
    private readonly IHttpSender _sender = Substitute.For<IHttpSender>();
    private readonly InFlightRequests _inFlight = new();

    private const string Root = "/work/my-api";

    private readonly IEnvironmentStore _environments = Substitute.For<IEnvironmentStore>();
    private readonly ISecretStore _secrets = Substitute.For<ISecretStore>();
    private readonly IWorkspaceStateStore _state = Substitute.For<IWorkspaceStateStore>();
    private readonly CurrentWorkspace _current = new();
    private readonly ICookieJarStore _cookies = Substitute.For<ICookieJarStore>();
    private readonly ICapturedValueStore _captures = Substitute.For<ICapturedValueStore>();
    private readonly ResponseBodies _bodies = new();
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly TimeProvider _time = Substitute.For<TimeProvider>();

    public SendRequestTests()
    {
        _time.GetUtcNow().Returns(Now);
        _cookies.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<Jars>.Success(new Dictionary<string, IReadOnlyList<StoredCookie>>()));
        _cookies.SaveAsync(Arg.Any<string>(), Arg.Any<Jars>(), Arg.Any<CancellationToken>()).Returns(Unit.Value);
        _state.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(WorkspaceState.Default);
        _secrets.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>.Success(
                new Dictionary<string, IReadOnlyDictionary<string, string>>()));
        _captures.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>.Success(
                new Dictionary<string, IReadOnlyDictionary<string, string>>()));
        _captures.SaveAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>(), Arg.Any<CancellationToken>())
            .Returns(Unit.Value);
    }

    private SendRequest CreateSendRequest() =>
        new(_sender, _inFlight, new ActiveEnvironment(_environments, _secrets, _captures, _state, _current), _bodies, _cookies, _captures, _current, _time);

    /// <summary>Opens a workspace with <paramref name="environment"/> selected as <c>dev.yaml</c>.</summary>
    private void SelectEnvironment(EnvironmentDefinition environment, Dictionary<string, string>? secrets = null)
    {
        _current.Open(Root);
        _state.LoadAsync(Root, Arg.Any<CancellationToken>()).Returns(new WorkspaceState("dev.yaml"));
        _environments.Exists(Root, "dev.yaml").Returns(true);
        _environments.ReadAsync(Root, "dev.yaml", Arg.Any<CancellationToken>()).Returns(environment);
        if (secrets is not null)
        {
            _secrets.LoadAsync(Root, Arg.Any<CancellationToken>())
                .Returns(Result<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>.Success(
                    new Dictionary<string, IReadOnlyDictionary<string, string>> { ["dev.yaml"] = secrets }));
        }
    }

    private static RequestDto Request(string url = "https://api.example.com/users", string name = "List users") =>
        new(name, "GET", url, [new KeyValueDto("page", "2")], [], new AuthDto("bearer", Token: "abc"), new BodyDto("none", ""));

    private static HttpResponseData Response(string body, string? contentType = "application/json; charset=utf-8", int status = 200) =>
        new(
            status,
            "OK",
            contentType is null ? [] : [KeyValuePair.Create("Content-Type", contentType)],
            Encoding.UTF8.GetBytes(body),
            false,
            TimeSpan.FromMilliseconds(123.4));

    [Fact]
    public async Task Execute_SendsThePreparedRequestAndReturnsTheResponse()
    {
        _sender.SendAsync(Arg.Any<OutgoingRequest>(), Arg.Any<CancellationToken>()).Returns(Response("""{"ok":true}"""));

        var result = await CreateSendRequest().ExecuteAsync("1", Request());

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value;
        Assert.Equal(200, response.Status);
        Assert.Equal("OK", response.StatusText);
        Assert.Equal("application/json", response.ContentType);
        Assert.Equal("""{"ok":true}""", response.Body);
        Assert.Equal("text", response.BodyEncoding);
        Assert.Equal(11, response.Size);
        Assert.Equal(123, response.DurationMs);
        Assert.Equal(new KeyValueDto("Content-Type", "application/json; charset=utf-8"), Assert.Single(response.Headers));
        await _sender.Received(1).SendAsync(
            Arg.Is<OutgoingRequest>(r =>
                r.Method == "GET"
                && r.Url.AbsoluteUri == "https://api.example.com/users?page=2"
                && r.Headers.Contains(KeyValuePair.Create("Authorization", "Bearer abc"))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_SendsUnnamedRequests()
    {
        _sender.SendAsync(Arg.Any<OutgoingRequest>(), Arg.Any<CancellationToken>()).Returns(Response(""));

        var result = await CreateSendRequest().ExecuteAsync("1", Request(name: " "));

        Assert.True(result.IsSuccess, result.Error?.Message);
    }

    [Fact]
    public async Task Execute_ReportsRequestsThatCantBeSent()
    {
        var result = await CreateSendRequest().ExecuteAsync("1", Request(url: "{{baseUrl}}/users"));

        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
        Assert.Contains("baseUrl", result.Error!.Message);
        await _sender.DidNotReceiveWithAnyArgs().SendAsync(default!, default);
    }

    [Fact]
    public async Task Execute_WithoutEnvironment_SaysNoneIsSelected()
    {
        _current.Open(Root);

        var result = await CreateSendRequest().ExecuteAsync("1", Request(url: "{{baseUrl}}/users"));

        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
        Assert.Contains("No environment is selected.", result.Error!.Message);
    }

    [Fact]
    public async Task Execute_ResolvesVariablesFromTheSelectedEnvironmentAndItsSecrets()
    {
        _sender.SendAsync(Arg.Any<OutgoingRequest>(), Arg.Any<CancellationToken>()).Returns(Response(""));
        SelectEnvironment(
            new EnvironmentDefinition("Development", [
                new EnvironmentVariable("host", "localhost:5000"),
                new EnvironmentVariable("baseUrl", "http://{{host}}/api"),
                new EnvironmentVariable("token", "", Secret: true),
            ]),
            new Dictionary<string, string> { ["token"] = "s3cret" });
        var request = Request(url: "{{baseUrl}}/users") with { Auth = new AuthDto("bearer", Token: "{{token}}") };

        var result = await CreateSendRequest().ExecuteAsync("1", request);

        Assert.True(result.IsSuccess, result.Error?.Message);
        await _sender.Received(1).SendAsync(
            Arg.Is<OutgoingRequest>(r =>
                r.Url.AbsoluteUri == "http://localhost:5000/api/users?page=2"
                && r.Headers.Contains(KeyValuePair.Create("Authorization", "Bearer s3cret"))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_ExplainsSecretsWithoutALocalValue()
    {
        SelectEnvironment(new EnvironmentDefinition("Development", [
            new EnvironmentVariable("baseUrl", "http://localhost"),
            new EnvironmentVariable("token", "", Secret: true),
        ]));
        var request = Request(url: "{{baseUrl}}/users") with { Auth = new AuthDto("bearer", Token: "{{token}}") };

        var result = await CreateSendRequest().ExecuteAsync("1", request);

        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
        Assert.Contains("aren't defined: token.", result.Error!.Message);
        Assert.Contains("enter token in the environment 'Development'", result.Error.Message);
        await _sender.DidNotReceiveWithAnyArgs().SendAsync(default!, default);
    }

    [Fact]
    public async Task Execute_NamesTheEnvironmentThatLacksAVariable()
    {
        SelectEnvironment(new EnvironmentDefinition("Development", [new EnvironmentVariable("host", "localhost")]));

        var result = await CreateSendRequest().ExecuteAsync("1", Request(url: "{{baseUrl}}/users"));

        Assert.Contains("Add them to the environment 'Development'.", result.Error?.Message);
    }

    [Fact]
    public async Task Execute_RefusesEnvironmentsWhoseVariablesLoop()
    {
        SelectEnvironment(new EnvironmentDefinition("Development", [
            new EnvironmentVariable("a", "{{b}}"),
            new EnvironmentVariable("b", "{{a}}"),
        ]));

        var result = await CreateSendRequest().ExecuteAsync("1", Request());

        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
        Assert.Contains("'a', 'b' refer to each other", result.Error!.Message);
    }

    [Fact]
    public async Task Execute_IgnoresASelectedEnvironmentWhoseFileIsGone()
    {
        _sender.SendAsync(Arg.Any<OutgoingRequest>(), Arg.Any<CancellationToken>()).Returns(Response(""));
        SelectEnvironment(new EnvironmentDefinition("Development"));
        _environments.Exists(Root, "dev.yaml").Returns(false);

        var result = await CreateSendRequest().ExecuteAsync("1", Request());

        Assert.True(result.IsSuccess, result.Error?.Message);
        await _environments.DidNotReceiveWithAnyArgs().ReadAsync(default!, default!, default);
    }

    [Fact]
    public async Task Execute_ReportsAnUnreadableSelectedEnvironment()
    {
        SelectEnvironment(new EnvironmentDefinition("Development"));
        _environments.ReadAsync(Root, "dev.yaml", Arg.Any<CancellationToken>())
            .Returns(Result<EnvironmentDefinition>.Failure(ErrorCodes.InvalidFile, "'environments/dev.yaml' is not valid."));

        var result = await CreateSendRequest().ExecuteAsync("1", Request());

        Assert.Equal(ErrorCodes.InvalidFile, result.Error?.Code);
        Assert.StartsWith("The selected environment can't be used.", result.Error!.Message);
    }

    [Fact]
    public async Task Execute_PassesNetworkErrorsThrough()
    {
        _sender.SendAsync(Arg.Any<OutgoingRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result<HttpResponseData>.Failure(HttpErrors.NetworkError("api.example.com", "Connection refused")));

        var result = await CreateSendRequest().ExecuteAsync("1", Request());

        Assert.Equal(HttpErrors.NetworkErrorCode, result.Error?.Code);
    }

    [Fact]
    public async Task Execute_ReturnsBinaryBodiesAsBase64()
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00 };
        _sender.SendAsync(Arg.Any<OutgoingRequest>(), Arg.Any<CancellationToken>())
            .Returns(new HttpResponseData(200, "OK", [KeyValuePair.Create("Content-Type", "image/png")], bytes, false, TimeSpan.Zero));

        var response = (await CreateSendRequest().ExecuteAsync("1", Request())).Value;

        Assert.Equal("base64", response.BodyEncoding);
        Assert.Equal(Convert.ToBase64String(bytes), response.Body);
        Assert.Equal(5, response.Size);
    }

    [Theory]
    [InlineData("plain words", null, "text")]
    [InlineData("<p>x</p>", "text/html", "text")]
    [InlineData("{}", "application/problem+json", "text")]
    [InlineData("abc", "application/octet-stream", "base64")]
    public async Task Execute_DetectsTextBodies(string body, string? contentType, string expectedEncoding)
    {
        _sender.SendAsync(Arg.Any<OutgoingRequest>(), Arg.Any<CancellationToken>()).Returns(Response(body, contentType));

        var response = (await CreateSendRequest().ExecuteAsync("1", Request())).Value;

        Assert.Equal(expectedEncoding, response.BodyEncoding);
    }

    [Fact]
    public async Task Execute_DecodesTheDeclaredCharset()
    {
        var latin1 = Encoding.Latin1.GetBytes("café");
        _sender.SendAsync(Arg.Any<OutgoingRequest>(), Arg.Any<CancellationToken>())
            .Returns(new HttpResponseData(200, "OK", [KeyValuePair.Create("Content-Type", "text/plain; charset=ISO-8859-1")], latin1, false, TimeSpan.Zero));

        var response = (await CreateSendRequest().ExecuteAsync("1", Request())).Value;

        Assert.Equal("café", response.Body);
    }

    [Fact]
    public async Task Cancel_StopsTheRequestWithTheSameId()
    {
        var started = new TaskCompletionSource();
        _sender.SendAsync(Arg.Any<OutgoingRequest>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
            return Result<HttpResponseData>.Success(Response(""));
        });

        var sending = CreateSendRequest().ExecuteAsync("send-1", Request());
        await started.Task;

        Assert.False(new CancelRequest(_inFlight).Execute("other"));
        Assert.True(new CancelRequest(_inFlight).Execute("send-1"));
        var result = await sending;

        Assert.Equal(HttpErrors.CancelledCode, result.Error?.Code);
        Assert.False(new CancelRequest(_inFlight).Execute("send-1"));
    }

    [Fact]
    public async Task Execute_KeepsTheRawBodyWithASuggestedFileName()
    {
        var bytes = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x00, 0xff };
        _sender.SendAsync(Arg.Any<OutgoingRequest>(), Arg.Any<CancellationToken>()).Returns(new HttpResponseData(
            200,
            "OK",
            [KeyValuePair.Create("Content-Type", "application/pdf"), KeyValuePair.Create("Content-Disposition", "attachment; filename=\"report 2026.pdf\"")],
            bytes,
            false,
            TimeSpan.Zero));

        var response = (await CreateSendRequest().ExecuteAsync("send-7", Request())).Value;

        Assert.Equal("report 2026.pdf", response.FileName);
        var stored = _bodies.Find("send-7");
        Assert.NotNull(stored);
        Assert.Equal(bytes, stored.Content);
        Assert.Equal("report 2026.pdf", stored.FileName);
    }

    [Fact]
    public async Task Execute_SuggestsAFileNameFromTheUrlAndContentType()
    {
        _sender.SendAsync(Arg.Any<OutgoingRequest>(), Arg.Any<CancellationToken>()).Returns(Response("{}"));

        var response = (await CreateSendRequest().ExecuteAsync("1", Request())).Value;

        Assert.Equal("users.json", response.FileName);
    }

    [Fact]
    public async Task Execute_MapsTimingsRedirectsAndTheSentRequestWithSecretsHidden()
    {
        SelectEnvironment(
            new EnvironmentDefinition("Development", [new EnvironmentVariable("token", "", Secret: true)]),
            new Dictionary<string, string> { ["token"] = "s3cret" });
        var request = Request() with
        {
            Headers = [new KeyValueDto("X-Trace", "token=s3cret")],
            Auth = new AuthDto("apikey", Key: "X-Api-Key", Value: "literal-key"),
        };
        var finalUrl = new Uri("https://api.example.com/v2/users?page=2");
        _sender.SendAsync(Arg.Any<OutgoingRequest>(), Arg.Any<CancellationToken>()).Returns(Response("{}") with
        {
            Redirects = [new RedirectHop("GET", new Uri("https://api.example.com/users?page=2"), 301, "Moved Permanently", finalUrl, [], TimeSpan.FromMilliseconds(20))],
            Timings = new ResponseTimings(TimeSpan.FromMilliseconds(1.26), TimeSpan.FromMilliseconds(2), null, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(4)),
            Sent = new SentRequest("GET", finalUrl, [
                KeyValuePair.Create("X-Trace", "token=s3cret"),
                KeyValuePair.Create("X-Api-Key", "literal-key"),
                KeyValuePair.Create("Authorization", "Bearer s3cret"),
            ]),
        });

        var response = (await CreateSendRequest().ExecuteAsync("1", request)).Value;

        Assert.Equal(new TimingsDto(1.3, 2, null, 30, 4), response.Timings);
        var redirect = Assert.Single(response.Redirects!);
        Assert.Equal(301, redirect.Status);
        Assert.Equal(finalUrl.AbsoluteUri, redirect.Location);
        Assert.Equal(20, redirect.DurationMs);
        Assert.Equal(finalUrl.AbsoluteUri, response.Sent!.Url);
        Assert.Equal(
            [
                new KeyValueDto("X-Trace", "token=" + SecretMasking.Mask),
                new KeyValueDto("X-Api-Key", SecretMasking.Mask),
                new KeyValueDto("Authorization", "Bearer " + SecretMasking.Mask),
            ],
            response.Sent.Headers);
        Assert.Equal("users.json", response.FileName);
    }

    [Fact]
    public async Task Execute_ListsTheCookiesSetByTheResponseAndItsRedirects()
    {
        _sender.SendAsync(Arg.Any<OutgoingRequest>(), Arg.Any<CancellationToken>()).Returns(
            new HttpResponseData(200, "OK", [KeyValuePair.Create("Set-Cookie", "theme=dark; Max-Age=60; SameSite=Lax")], [], false, TimeSpan.Zero)
            {
                Redirects = [new RedirectHop("POST", new Uri("https://api.example.com/login"), 302, "Found", new Uri("https://api.example.com/"), [KeyValuePair.Create("Set-Cookie", "session=abc; Path=/; Secure; HttpOnly")], TimeSpan.Zero)],
            });

        var cookies = (await CreateSendRequest().ExecuteAsync("1", Request())).Value.Cookies!;

        Assert.Equal(
            [
                new ResponseCookieDto("session", "abc", null, "/", null, true, true, null),
                new ResponseCookieDto("theme", "dark", null, null, Now.AddSeconds(60), false, false, "Lax"),
            ],
            cookies);
    }

    [Fact]
    public async Task Execute_KeepsCookiesInTheWorkspaceTheRequestWasSentFrom()
    {
        SelectEnvironment(new EnvironmentDefinition("Development") { CookieJar = true });
        var fresh = new StoredCookie("session", "new", "api.example.com", "/");
        _sender.SendAsync(Arg.Any<OutgoingRequest>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _current.Open("/work/other");
            return Response("") with { Cookies = [fresh] };
        });

        await CreateSendRequest().ExecuteAsync("1", Request());

        await _cookies.Received(1).SaveAsync(Root, Arg.Any<Jars>(), Arg.Any<CancellationToken>());
        await _cookies.DidNotReceive().SaveAsync("/work/other", Arg.Any<Jars>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_WithoutACookieJar_SendsNoCookies()
    {
        _sender.SendAsync(Arg.Any<OutgoingRequest>(), Arg.Any<CancellationToken>()).Returns(Response(""));
        SelectEnvironment(new EnvironmentDefinition("Development"));

        await CreateSendRequest().ExecuteAsync("1", Request());

        await _sender.Received(1).SendAsync(Arg.Is<OutgoingRequest>(r => r.Cookies == null), Arg.Any<CancellationToken>());
        await _cookies.DidNotReceiveWithAnyArgs().LoadAsync(default!, default);
    }

    [Fact]
    public async Task Execute_WithACookieJar_SendsItsCookiesAndKeepsTheNewOnes()
    {
        var old = new StoredCookie("session", "old", "api.example.com", "/");
        var expired = new StoredCookie("gone", "x", "api.example.com", "/", Now.AddDays(-1));
        var fresh = new StoredCookie("session", "new", "api.example.com", "/", Now.AddDays(1));
        SelectEnvironment(new EnvironmentDefinition("Development") { CookieJar = true });
        _cookies.LoadAsync(Root, Arg.Any<CancellationToken>())
            .Returns(Result<Jars>.Success(new Dictionary<string, IReadOnlyList<StoredCookie>> { ["dev.yaml"] = [old, expired] }));
        _sender.SendAsync(Arg.Any<OutgoingRequest>(), Arg.Any<CancellationToken>()).Returns(Response("") with { Cookies = [fresh] });

        var result = await CreateSendRequest().ExecuteAsync("1", Request());

        Assert.True(result.IsSuccess, result.Error?.Message);
        await _sender.Received(1).SendAsync(Arg.Is<OutgoingRequest>(r => r.Cookies!.SequenceEqual(new[] { old })), Arg.Any<CancellationToken>());
        await _cookies.Received(1).SaveAsync(
            Root,
            Arg.Is<Jars>(j => j["dev.yaml"].SequenceEqual(new[] { fresh })),
            Arg.Any<CancellationToken>());
    }

    private static readonly IReadOnlyList<CaptureDto> TokenCaptures =
        [new CaptureDto("token", "$.access_token"), new CaptureDto("etag", "header:ETag"), new CaptureDto("missing", "$.nope")];

    private static HttpResponseData LoginResponse(int status = 200) =>
        Response("""{"access_token":"t0k"}""", status: status) with
        {
            Headers = [KeyValuePair.Create("Content-Type", "application/json"), KeyValuePair.Create("ETag", "\"v1\"")],
        };

    [Fact]
    public async Task Execute_StoresCapturedValuesForTheSelectedEnvironment()
    {
        _sender.SendAsync(Arg.Any<OutgoingRequest>(), Arg.Any<CancellationToken>()).Returns(LoginResponse());
        SelectEnvironment(new EnvironmentDefinition("Development"));

        var result = await CreateSendRequest().ExecuteAsync("1", Request() with { Captures = TokenCaptures });

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(
            ["token=t0k", "etag=\"v1\"", "missing=!$.nope matches nothing in the response."],
            result.Value.Captures!.Select(c => $"{c.Variable}={c.Value ?? "!" + c.Error}"));
        await _captures.Received(1).SaveAsync(
            Root,
            Arg.Is<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>(v =>
                v["dev.yaml"].Count == 2 && v["dev.yaml"]["token"] == "t0k" && v["dev.yaml"]["etag"] == "\"v1\""),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_CapturesNothingFromUnsuccessfulResponses()
    {
        _sender.SendAsync(Arg.Any<OutgoingRequest>(), Arg.Any<CancellationToken>()).Returns(LoginResponse(401));
        SelectEnvironment(new EnvironmentDefinition("Development"));

        var result = await CreateSendRequest().ExecuteAsync("1", Request() with { Captures = TokenCaptures });

        Assert.All(result.Value.Captures!, c => Assert.Contains("401", c.Error));
        Assert.All(result.Value.Captures!, c => Assert.Null(c.Value));
        await _captures.DidNotReceiveWithAnyArgs().SaveAsync(default!, default!, default);
    }

    [Fact]
    public async Task Execute_WithoutEnvironment_StoresNoCapturedValues()
    {
        _sender.SendAsync(Arg.Any<OutgoingRequest>(), Arg.Any<CancellationToken>()).Returns(LoginResponse());
        _current.Open(Root);

        var result = await CreateSendRequest().ExecuteAsync("1", Request() with { Captures = TokenCaptures });

        var token = result.Value.Captures![0];
        Assert.Null(token.Value);
        Assert.Contains("no environment is selected", token.Error);
        await _captures.DidNotReceiveWithAnyArgs().SaveAsync(default!, default!, default);
    }

    [Fact]
    public async Task Execute_CapturedValuesTakePrecedenceOverTheEnvironment()
    {
        _sender.SendAsync(Arg.Any<OutgoingRequest>(), Arg.Any<CancellationToken>()).Returns(Response(""));
        SelectEnvironment(new EnvironmentDefinition("Development", [
            new EnvironmentVariable("token", "placeholder"),
            new EnvironmentVariable("session", "", Secret: true),
        ]));
        _captures.LoadAsync(Root, Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>.Success(
                new Dictionary<string, IReadOnlyDictionary<string, string>>
                {
                    ["dev.yaml"] = new Dictionary<string, string> { ["token"] = "fresh", ["session"] = "s1" },
                }));
        var request = Request(url: "https://api.example.com/{{session}}") with { Auth = new AuthDto("bearer", Token: "{{token}}") };

        var result = await CreateSendRequest().ExecuteAsync("1", request);

        Assert.True(result.IsSuccess, result.Error?.Message);
        await _sender.Received(1).SendAsync(
            Arg.Is<OutgoingRequest>(r =>
                r.Url.AbsolutePath == "/s1" && r.Headers.Contains(KeyValuePair.Create("Authorization", "Bearer fresh"))),
            Arg.Any<CancellationToken>());
    }
}
