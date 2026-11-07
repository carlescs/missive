using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Missive.Application.Common;
using Missive.Application.Cookies;
using Missive.Application.Environments;
using Missive.Application.Http;
using Missive.Application.Interchange;
using Missive.Application.Workspaces;
using Missive.Desktop.Bridge;
using Missive.Domain.Http;
using NSubstitute;

namespace Missive.Desktop.Tests.Bridge;

/// <summary>The request.send and request.cancel messages, through the real router and use cases.</summary>
public sealed class SendBridgeTests : IDisposable
{
    private const string RequestJson = """
        {
          "name": "Get user", "method": "GET", "url": "https://api.example.com/users/1",
          "params": [], "headers": [{ "key": "Accept", "value": "application/json", "enabled": true }],
          "auth": { "type": "none" }, "body": { "type": "none", "content": "" }
        }
        """;

    private readonly IHttpSender _sender = Substitute.For<IHttpSender>();
    private readonly IFileDialogs _dialogs = Substitute.For<IFileDialogs>();
    private readonly IExternalFiles _files = Substitute.For<IExternalFiles>();
    private readonly ICookieJarStore _cookies = Substitute.For<ICookieJarStore>();
    private readonly CurrentWorkspace _current = new();
    private readonly ServiceProvider _services;
    private readonly BridgeRouter _router;

    public SendBridgeTests()
    {
        _services = new ServiceCollection()
            .AddSingleton(_sender)
            .AddSingleton<InFlightRequests>()
            .AddSingleton<ResponseBodies>()
            .AddSingleton(_current)
            .AddSingleton(_files)
            .AddSingleton(_cookies)
            .AddSingleton(TimeProvider.System)
            .AddSingleton(Substitute.For<IEnvironmentStore>())
            .AddSingleton(Substitute.For<ISecretStore>())
            .AddSingleton(Substitute.For<ICapturedValueStore>())
            .AddSingleton(Substitute.For<IWorkspaceStateStore>())
            .AddTransient<ActiveEnvironment>()
            .AddTransient<SendRequest>()
            .AddTransient<CancelRequest>()
            .AddTransient<SaveResponseBody>()
            .AddTransient<ListCookies>()
            .AddTransient<DeleteCookie>()
            .AddTransient<ClearCookies>()
            .BuildServiceProvider();
        _router = BridgeHandlers.Create(_services, _dialogs);
    }

    public void Dispose() => _services.Dispose();

    [Fact]
    public async Task Send_ReturnsTheResponse()
    {
        _sender.SendAsync(Arg.Any<OutgoingRequest>(), Arg.Any<CancellationToken>()).Returns(new HttpResponseData(
            201,
            "Created",
            [KeyValuePair.Create("Content-Type", "application/json")],
            Encoding.UTF8.GetBytes("""{"id":1}"""),
            false,
            TimeSpan.FromMilliseconds(42)));

        var response = await SendAsync("request.send", $$"""{ "id": "s1", "request": {{RequestJson}} }""");

        Assert.True(response.GetProperty("ok").GetBoolean(), response.ToString());
        var payload = response.GetProperty("payload");
        Assert.Equal(201, payload.GetProperty("status").GetInt32());
        Assert.Equal("Created", payload.GetProperty("statusText").GetString());
        Assert.Equal("""{"id":1}""", payload.GetProperty("body").GetString());
        Assert.Equal("text", payload.GetProperty("bodyEncoding").GetString());
        Assert.Equal(42, payload.GetProperty("durationMs").GetInt64());
        Assert.Equal("Content-Type", payload.GetProperty("headers")[0].GetProperty("key").GetString());
        await _sender.Received(1).SendAsync(
            Arg.Is<OutgoingRequest>(r => r.Url.AbsoluteUri == "https://api.example.com/users/1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Send_NetworkError_IsReported()
    {
        _sender.SendAsync(Arg.Any<OutgoingRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result<HttpResponseData>.Failure(HttpErrors.NetworkError("api.example.com", "Connection refused")));

        var response = await SendAsync("request.send", $$"""{ "id": "s1", "request": {{RequestJson}} }""");

        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.Equal(HttpErrors.NetworkErrorCode, response.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Cancel_UnknownId_ReturnsFalse()
    {
        var response = await SendAsync("request.cancel", """{ "id": "nope" }""");

        Assert.True(response.GetProperty("ok").GetBoolean(), response.ToString());
        Assert.False(response.GetProperty("payload").GetBoolean());
    }

    [Fact]
    public async Task SaveBody_AsksWhereAndWritesTheBytesReceived()
    {
        byte[] bytes = [0x89, 0x50, 0x4e, 0x47];
        _sender.SendAsync(Arg.Any<OutgoingRequest>(), Arg.Any<CancellationToken>()).Returns(new HttpResponseData(
            200, "OK", [KeyValuePair.Create("Content-Type", "image/png")], bytes, false, TimeSpan.Zero));
        _dialogs.PickSaveFileAsync(default!, default!).ReturnsForAnyArgs("/home/me/logo.png");
        _files.WriteBytesAsync(default!, default!, default).ReturnsForAnyArgs(Unit.Value);
        var sent = await SendAsync("request.send", $$"""{ "id": "s1", "request": {{RequestJson}} }""");
        Assert.Equal("1.png", sent.GetProperty("payload").GetProperty("fileName").GetString());

        var response = await SendAsync("response.saveBody", """{ "id": "s1" }""");

        Assert.True(response.GetProperty("ok").GetBoolean(), response.ToString());
        Assert.Equal("/home/me/logo.png", response.GetProperty("payload").GetProperty("path").GetString());
        await _dialogs.Received(1).PickSaveFileAsync(
            "Save response as 1.png",
            Arg.Is<IReadOnlyList<FileFilter>>(f => f[0].Patterns.Single() == "*.png"));
        await _files.Received(1).WriteBytesAsync("/home/me/logo.png", bytes, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveBody_CancelledDialog_ReturnsNull()
    {
        _sender.SendAsync(Arg.Any<OutgoingRequest>(), Arg.Any<CancellationToken>()).Returns(new HttpResponseData(
            200, "OK", [], [1], false, TimeSpan.Zero));
        _dialogs.PickSaveFileAsync(default!, default!).ReturnsForAnyArgs((string?)null);
        await SendAsync("request.send", $$"""{ "id": "s1", "request": {{RequestJson}} }""");

        var response = await SendAsync("response.saveBody", """{ "id": "s1" }""");

        Assert.True(response.GetProperty("ok").GetBoolean(), response.ToString());
        Assert.Equal(JsonValueKind.Null, response.GetProperty("payload").ValueKind);
        await _files.DidNotReceiveWithAnyArgs().WriteBytesAsync(default!, default!, default);
    }

    [Fact]
    public async Task SaveBody_UnknownResponse_IsNotFound()
    {
        var response = await SendAsync("response.saveBody", """{ "id": "gone" }""");

        Assert.Equal(ErrorCodes.NotFound, response.GetProperty("error").GetProperty("code").GetString());
        await _dialogs.DidNotReceiveWithAnyArgs().PickSaveFileAsync(default!, default!);
    }

    [Fact]
    public async Task Cookies_AreListedDeletedAndCleared()
    {
        _current.Open("/work/api");
        IReadOnlyDictionary<string, IReadOnlyList<StoredCookie>> jars = new Dictionary<string, IReadOnlyList<StoredCookie>>
        {
            ["dev.yaml"] = [new StoredCookie("a", "1", "x.test", "/"), new StoredCookie("b", "2", "x.test", "/", Secure: true)],
        };
        _cookies.LoadAsync("/work/api", Arg.Any<CancellationToken>())
            .Returns(_ => Result<IReadOnlyDictionary<string, IReadOnlyList<StoredCookie>>>.Success(jars));
        _cookies.SaveAsync("/work/api", Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<StoredCookie>>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                jars = call.Arg<IReadOnlyDictionary<string, IReadOnlyList<StoredCookie>>>();
                return Result<Unit>.Success(Unit.Value);
            });

        var listed = await SendAsync("cookie.list", """{ "environment": "dev.yaml" }""");
        Assert.Equal(2, listed.GetProperty("payload").GetArrayLength());
        Assert.True(listed.GetProperty("payload")[1].GetProperty("secure").GetBoolean());

        var deleted = await SendAsync("cookie.delete", """{ "environment": "dev.yaml", "name": "a", "domain": "x.test", "path": "/" }""");
        Assert.Equal("b", deleted.GetProperty("payload").EnumerateArray().Single().GetProperty("name").GetString());

        var cleared = await SendAsync("cookie.clear", """{ "environment": "dev.yaml" }""");
        Assert.True(cleared.GetProperty("ok").GetBoolean(), cleared.ToString());
        Assert.Empty(jars);
    }

    private async Task<JsonElement> SendAsync(string type, string payload)
    {
        var json = await _router.HandleAsync($$"""{ "id": "1", "type": "{{type}}", "payload": {{payload}} }""");
        Assert.NotNull(json);
        return JsonDocument.Parse(json).RootElement;
    }
}
