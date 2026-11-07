using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Missive.Application.Captures;
using Missive.Application.Common;
using Missive.Application.Environments;
using Missive.Application.Workspaces;
using Missive.Desktop.Bridge;
using Missive.Domain.Environments;
using NSubstitute;

namespace Missive.Desktop.Tests.Bridge;

/// <summary>The environment.* messages, through the real router and use cases.</summary>
public sealed class EnvironmentBridgeTests : IDisposable
{
    private const string Root = "/work/my-api";

    private readonly IEnvironmentStore _store = Substitute.For<IEnvironmentStore>();
    private readonly ISecretStore _secrets = Substitute.For<ISecretStore>();
    private readonly IWorkspaceStateStore _state = Substitute.For<IWorkspaceStateStore>();
    private readonly ICapturedValueStore _captures = Substitute.For<ICapturedValueStore>();
    private readonly ServiceProvider _services;
    private readonly BridgeRouter _router;

    public EnvironmentBridgeTests()
    {
        var current = new CurrentWorkspace();
        current.Open(Root);
        _state.LoadAsync(Root, Arg.Any<CancellationToken>()).Returns(new WorkspaceState("dev.yaml"));
        _state.SaveAsync(Root, Arg.Any<WorkspaceState>(), Arg.Any<CancellationToken>()).Returns(Unit.Value);
        _secrets.LoadAsync(Root, Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>.Success(
                new Dictionary<string, IReadOnlyDictionary<string, string>>
                {
                    ["dev.yaml"] = new Dictionary<string, string> { ["token"] = "abc" },
                }));

        _captures.LoadAsync(Root, Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>.Success(
                new Dictionary<string, IReadOnlyDictionary<string, string>>
                {
                    ["dev.yaml"] = new Dictionary<string, string> { ["token"] = "t", ["etag"] = "e" },
                }));
        _captures.SaveAsync(Root, Arg.Any<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>(), Arg.Any<CancellationToken>())
            .Returns(Unit.Value);

        _services = new ServiceCollection()
            .AddSingleton(current)
            .AddSingleton(_store)
            .AddSingleton(_secrets)
            .AddSingleton(_state)
            .AddSingleton(_captures)
            .AddTransient<ListCapturedValues>()
            .AddTransient<ClearCapturedValues>()
            .AddTransient<ListEnvironments>()
            .AddTransient<LoadEnvironment>()
            .AddTransient<SelectEnvironment>()
            .BuildServiceProvider();
        _router = BridgeHandlers.Create(_services, Substitute.For<IFileDialogs>());
    }

    public void Dispose() => _services.Dispose();

    [Fact]
    public async Task CapturedValues_AreListedAndCleared()
    {
        var list = await SendAsync("capture.list", """{ "environment": "dev.yaml" }""");
        Assert.True(list.GetProperty("ok").GetBoolean(), list.ToString());
        Assert.Equal(["etag", "token"], list.GetProperty("payload").EnumerateArray().Select(v => v.GetProperty("name").GetString()));

        var cleared = await SendAsync("capture.clear", """{ "environment": "dev.yaml", "name": "token" }""");
        Assert.True(cleared.GetProperty("ok").GetBoolean(), cleared.ToString());
        Assert.Equal("etag", cleared.GetProperty("payload")[0].GetProperty("name").GetString());
        await _captures.Received(1).SaveAsync(
            Root,
            Arg.Is<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>(v => v["dev.yaml"].Count == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task List_ReturnsTheActiveEnvironment()
    {
        _store.ListAsync(Root, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<EnvironmentFile>>.Success([
            new EnvironmentFile("dev.yaml", new EnvironmentDefinition("Development")),
        ]));

        var response = await SendAsync("environment.list", "null");

        Assert.True(response.GetProperty("ok").GetBoolean(), response.ToString());
        var payload = response.GetProperty("payload");
        Assert.Equal("dev.yaml", payload.GetProperty("active").GetString());
        Assert.Equal("Development", payload.GetProperty("environments")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Load_ReturnsSecretValues()
    {
        _store.ReadAsync(Root, "dev.yaml", Arg.Any<CancellationToken>()).Returns(
            new EnvironmentDefinition("Development", [new EnvironmentVariable("token", "", Secret: true)]));

        var response = await SendAsync("environment.load", """{ "id": "dev.yaml" }""");

        Assert.True(response.GetProperty("ok").GetBoolean(), response.ToString());
        var variable = response.GetProperty("payload").GetProperty("variables")[0];
        Assert.Equal("abc", variable.GetProperty("value").GetString());
        Assert.True(variable.GetProperty("secret").GetBoolean());
    }

    [Fact]
    public async Task Select_Null_ClearsTheSelection()
    {
        var response = await SendAsync("environment.select", """{ "id": null }""");

        Assert.True(response.GetProperty("ok").GetBoolean(), response.ToString());
        Assert.Equal(JsonValueKind.Null, response.GetProperty("payload").ValueKind);
        await _state.Received(1).SaveAsync(Root, WorkspaceState.Default, Arg.Any<CancellationToken>());
    }

    private async Task<JsonElement> SendAsync(string type, string payload)
    {
        var json = await _router.HandleAsync($$"""{ "id": "1", "type": "{{type}}", "payload": {{payload}} }""");
        Assert.NotNull(json);
        return JsonDocument.Parse(json).RootElement;
    }
}
