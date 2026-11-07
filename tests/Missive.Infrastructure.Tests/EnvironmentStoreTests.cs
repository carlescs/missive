using Missive.Application.Common;
using Missive.Application.Environments;
using Missive.Domain.Environments;
using Missive.Infrastructure.Environments;
using Missive.Infrastructure.Yaml;

namespace Missive.Infrastructure.Tests;

public sealed class EnvironmentStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("missive-tests-").FullName;
    private readonly FileSystemEnvironmentStore _store = new();
    private readonly YamlSecretStore _secrets = new();
    private readonly YamlWorkspaceStateStore _state = new();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static readonly EnvironmentDefinition Development = new("Development", [
        new EnvironmentVariable("baseUrl", "http://localhost:5000"),
        new EnvironmentVariable("empty", ""),
        new EnvironmentVariable("apiToken", "never written", Secret: true),
    ]);

    [Fact]
    public void Serialize_WritesNamesOnlyForSecrets()
    {
        Assert.Equal(
            """
            name: Development
            variables:
              baseUrl: http://localhost:5000
              empty: ""
            secrets:
              - apiToken

            """.ReplaceLineEndings("\n"),
            EnvironmentYamlSerializer.Serialize(Development));
        Assert.Equal("name: Empty\n", EnvironmentYamlSerializer.Serialize(new EnvironmentDefinition("Empty")));
    }

    [Fact]
    public void Deserialize_RoundTrips()
    {
        var parsed = EnvironmentYamlSerializer.Deserialize(EnvironmentYamlSerializer.Serialize(Development), "dev");

        Assert.Equal("Development", parsed.Name);
        Assert.Equal(
            [
                new EnvironmentVariable("baseUrl", "http://localhost:5000"),
                new EnvironmentVariable("empty", ""),
                new EnvironmentVariable("apiToken", "", Secret: true),
            ],
            parsed.Variables);
    }

    [Fact]
    public void CookieJar_IsWrittenOnlyWhenOnAndRoundTrips()
    {
        var withJar = new EnvironmentDefinition("Dev", [new EnvironmentVariable("a", "1")]) { CookieJar = true };

        var yaml = EnvironmentYamlSerializer.Serialize(withJar);

        Assert.Equal("name: Dev\ncookieJar: true\nvariables:\n  a: \"1\"\n", yaml);
        Assert.True(EnvironmentYamlSerializer.Deserialize(yaml, "dev").CookieJar);
        Assert.False(EnvironmentYamlSerializer.Deserialize("name: Dev\n", "dev").CookieJar);
        Assert.False(EnvironmentYamlSerializer.Deserialize("name: Dev\ncookieJar: false\n", "dev").CookieJar);
    }

    [Theory]
    [InlineData("cookieJar: maybe", "'cookieJar' must be true or false")]
    [InlineData("variables: [a]", "'variables' must be a mapping")]
    [InlineData("secrets: token", "'secrets' must be a list")]
    [InlineData("variables:\n  a: 1\nsecrets:\n  - a\n", "'a' is listed both")]
    [InlineData("variables:\n  bad name: 1\n", "'bad name' is not a valid variable name")]
    public void Deserialize_RejectsInvalidFiles(string yaml, string expected)
    {
        var error = Assert.Throws<WorkspaceFileFormatException>(() => EnvironmentYamlSerializer.Deserialize(yaml, "dev"));

        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void Deserialize_AcceptsLoopsSoTheyCanBeFixed()
    {
        var parsed = EnvironmentYamlSerializer.Deserialize("variables:\n  a: \"{{a}}\"\n", "dev");

        Assert.Equal("dev", parsed.Name);
        Assert.Single(parsed.Variables);
    }

    [Fact]
    public async Task List_ReturnsEnvironmentFilesOnly()
    {
        Write("environments/prod.yaml", "name: Production\n");
        Write("environments/broken.yml", "name: [oops");
        Write("environments/.hidden.yaml", "name: Hidden\n");
        Write("environments/notes.txt", "hi");
        Write("environments/nested/x.yaml", "name: Nested\n");

        var result = await _store.ListAsync(_root);

        Assert.Equal(["broken.yml", "prod.yaml"], result.Value.Select(f => f.Id));
        Assert.Equal(ErrorCodes.InvalidFile, result.Value[0].Environment.Error?.Code);
        Assert.Equal("Production", result.Value[1].Environment.Value.Name);
    }

    [Fact]
    public async Task List_WithoutEnvironmentsFolder_IsEmpty()
    {
        var result = await _store.ListAsync(_root);

        Assert.Empty(result.Value);
    }

    [Fact]
    public async Task Write_CreatesTheFolderAndLeavesEquivalentFilesUntouched()
    {
        Assert.True((await _store.WriteAsync(_root, "dev.yaml", Development)).Value);
        Assert.True(_store.Exists(_root, "dev.yaml"));

        var path = Path.Combine(_root, "environments", "dev.yaml");
        File.WriteAllText(path, "# mine\nname: 'Development'\nvariables: { baseUrl: 'http://localhost:5000', empty: '' }\nsecrets: [apiToken]\n");

        Assert.False((await _store.WriteAsync(_root, "dev.yaml", Development)).Value);
        Assert.StartsWith("# mine", File.ReadAllText(path));
    }

    [Fact]
    public async Task Read_MissingFile_IsNotFound()
    {
        var result = await _store.ReadAsync(_root, "dev.yaml");

        Assert.Equal(ErrorCodes.NotFound, result.Error?.Code);
    }

    [Fact]
    public async Task MoveAndDelete()
    {
        await _store.WriteAsync(_root, "dev.yaml", Development);

        Assert.True(_store.Move(_root, "dev.yaml", "local.yaml").IsSuccess);
        Assert.False(_store.Exists(_root, "dev.yaml"));
        Assert.True(_store.Delete(_root, "local.yaml").IsSuccess);
        Assert.False(_store.Exists(_root, "local.yaml"));
    }

    [Fact]
    public async Task Secrets_RoundTripAndAreGitIgnored()
    {
        var secrets = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["prod.yaml"] = new Dictionary<string, string> { ["token"] = "p: #1" },
            ["dev.yaml"] = new Dictionary<string, string> { ["token"] = "line1\nline2", ["key"] = "k" },
        };

        Assert.True((await _secrets.SaveAsync(_root, secrets)).IsSuccess);
        var loaded = await _secrets.LoadAsync(_root);

        Assert.Equal(["dev.yaml", "prod.yaml"], loaded.Value.Keys.Order());
        Assert.Equal("line1\nline2", loaded.Value["dev.yaml"]["token"]);
        Assert.Equal("p: #1", loaded.Value["prod.yaml"]["token"]);
        Assert.Equal(".missive/\n", File.ReadAllText(Path.Combine(_root, ".gitignore")));
    }

    [Fact]
    public async Task Secrets_WithoutValues_RemovesTheFile()
    {
        await _secrets.SaveAsync(_root, new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["dev.yaml"] = new Dictionary<string, string> { ["token"] = "abc" },
        });

        await _secrets.SaveAsync(_root, new Dictionary<string, IReadOnlyDictionary<string, string>>());

        Assert.False(File.Exists(Path.Combine(_root, ".missive", YamlSecretStore.FileName)));
        Assert.Empty((await _secrets.LoadAsync(_root)).Value);
    }

    [Fact]
    public async Task CapturedValues_RoundTripInTheirOwnFile()
    {
        var store = new YamlCapturedValueStore();

        Assert.True((await store.SaveAsync(_root, new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["dev.yaml"] = new Dictionary<string, string> { ["token"] = "t0k", ["etag"] = "\"v1\"" },
        })).IsSuccess);

        Assert.Equal("\"v1\"", (await store.LoadAsync(_root)).Value["dev.yaml"]["etag"]);
        Assert.Equal(
            "# Values captured from responses on this computer. Missive keeps this file out of git.\ndev.yaml:\n  etag: \"\\\"v1\\\"\"\n  token: t0k\n",
            File.ReadAllText(Path.Combine(_root, ".missive", YamlCapturedValueStore.FileName)));
        Assert.Empty((await _secrets.LoadAsync(_root)).Value);
    }

    [Fact]
    public async Task Secrets_InvalidFile_IsReported()
    {
        Write(".missive/secrets.yaml", "dev.yaml: [1, 2]\n");

        var result = await _secrets.LoadAsync(_root);

        Assert.Equal(ErrorCodes.InvalidFile, result.Error?.Code);
    }

    [Theory]
    [InlineData("bin/\r\nobj/", "bin/\r\nobj/\r\n.missive/\r\n")]
    [InlineData("bin/\n", "bin/\n.missive/\n")]
    [InlineData("/.missive\n", "/.missive\n")]
    [InlineData(".missive/\n*.log\n", ".missive/\n*.log\n")]
    public async Task State_AddsTheLocalFolderToAnExistingGitIgnoreOnce(string existing, string expected)
    {
        Write(".gitignore", existing);

        await _state.SaveAsync(_root, new WorkspaceState("dev.yaml"));
        await _state.SaveAsync(_root, new WorkspaceState("prod.yaml"));

        Assert.Equal(expected, File.ReadAllText(Path.Combine(_root, ".gitignore")));
    }

    [Fact]
    public async Task State_RoundTrips()
    {
        Assert.Equal(WorkspaceState.Default, await _state.LoadAsync(_root));

        await _state.SaveAsync(_root, new WorkspaceState("dev.yaml"));
        Assert.Equal(new WorkspaceState("dev.yaml"), await _state.LoadAsync(_root));
        Assert.Equal("environment: dev.yaml\n", File.ReadAllText(Path.Combine(_root, ".missive", "state.yaml")));

        await _state.SaveAsync(_root, WorkspaceState.Default);
        Assert.Equal(WorkspaceState.Default, await _state.LoadAsync(_root));
    }

    [Fact]
    public async Task State_UnreadableFile_LoadsAsDefault()
    {
        Write(".missive/state.yaml", "environment: [oops");

        Assert.Equal(WorkspaceState.Default, await _state.LoadAsync(_root));
    }

    private void Write(string relativePath, string content)
    {
        var fullPath = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
