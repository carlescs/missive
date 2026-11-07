using Microsoft.Extensions.DependencyInjection;
using Missive.Application.Captures;
using Missive.Application.Cookies;
using Missive.Application.Environments;
using Missive.Application.Folders;
using Missive.Application.Git;
using Missive.Application.Http;
using Missive.Application.Interchange;
using Missive.Application.Requests;
using Missive.Application.Settings;
using Missive.Application.Updates;
using Missive.Application.Workspaces;
using Missive.Desktop.Bridge;
using Missive.Desktop.Hosting;
using Missive.Infrastructure.Cookies;
using Missive.Infrastructure.Environments;
using Missive.Infrastructure.Git;
using Missive.Infrastructure.Http;
using Missive.Infrastructure.Interchange;
using Missive.Infrastructure.Settings;
using Missive.Infrastructure.Updates;
using Missive.Infrastructure.Workspaces;
using Missive.Infrastructure.Yaml;
using Photino.NET;
using Velopack;

namespace Missive.Desktop;

internal static class Program
{
    /// <summary>The GitHub repository whose releases the app updates itself from.</summary>
    private const string ReleasesRepository = "https://github.com/carlescs/missive";

    [STAThread]
    private static void Main()
    {
        // Must run first: Velopack's installer and updater start the app with special arguments,
        // and vpk checks that Main calls this directly.
        VelopackApp.Build().Run();

        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var indexPath = Path.Combine(wwwroot, "index.html");

        if (!File.Exists(indexPath))
        {
            throw new FileNotFoundException("Angular assets are missing. Run dotnet build first.", indexPath);
        }

        using var services = ConfigureServices();
        var assets = new BundledAssets(wwwroot);

        var window = new PhotinoWindow()
            .SetTitle("Missive")
            .SetUseOsDefaultSize(false)
            .SetSize(1024, 768)
            .Center()
            .RegisterCustomSchemeHandler(BundledAssets.Scheme, assets.Handle);

        // Windows title bars need an .ico; GTK loads a PNG reliably. macOS takes the
        // icon from the app bundle instead.
        var iconPath = Path.Combine(
            AppContext.BaseDirectory, "Assets", OperatingSystem.IsWindows() ? "missive.ico" : "missive.png");
        if (!OperatingSystem.IsMacOS() && File.Exists(iconPath))
        {
            window.SetIconFile(iconPath);
        }

        var bridge = new PhotinoBridge(window);
        bridge.Attach(BridgeHandlers.Create(services, bridge));

        window.Load(BundledAssets.StartUri).WaitForClose();
    }

    private static ServiceProvider ConfigureServices() =>
        new ServiceCollection()
            .AddSingleton<CurrentWorkspace>()
            .AddSingleton<IWorkspaceRepository, FileSystemWorkspaceRepository>()
            .AddSingleton<IEnvironmentStore, FileSystemEnvironmentStore>()
            .AddSingleton<ISecretStore, YamlSecretStore>()
            .AddSingleton<IWorkspaceStateStore, YamlWorkspaceStateStore>()
            .AddSingleton<ICookieJarStore, JsonCookieJarStore>()
            .AddSingleton<ICapturedValueStore, YamlCapturedValueStore>()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<IGitService, GitCliService>()
            .AddSingleton<IRequestFileFormat, YamlRequestFileFormat>()
            .AddSingleton<IHttpSender, HttpClientSender>()
            .AddSingleton<InFlightRequests>()
            .AddSingleton<ResponseBodies>()
            .AddSingleton<IExternalFiles, LocalExternalFiles>()
            .AddSingleton<ICollectionImporter, CollectionImporter>()
            .AddSingleton<ICollectionExporter, PostmanCollectionExporter>()
            .AddSingleton<ICollectionExporter, InsomniaExporter>()
            .AddSingleton<IAppSettingsStore>(new JsonAppSettingsStore(JsonAppSettingsStore.DefaultFilePath))
            .AddSingleton<IAppUpdater>(new VelopackAppUpdater(ReleasesRepository))
            .AddTransient<GetAppSettings>()
            .AddTransient<SetTheme>()
            .AddTransient<CheckForUpdate>()
            .AddTransient<RestartToUpdate>()
            .AddTransient<OpenWorkspace>()
            .AddTransient<OpenLastWorkspace>()
            .AddTransient<GetRecentWorkspaces>()
            .AddTransient<GetWorkspaceTree>()
            .AddTransient<ExportWorkspace>()
            .AddTransient<ImportCollection>()
            .AddTransient<LoadRequest>()
            .AddTransient<CreateRequest>()
            .AddTransient<SaveRequest>()
            .AddTransient<MoveRequest>()
            .AddTransient<DuplicateRequest>()
            .AddTransient<DeleteRequest>()
            .AddTransient<ActiveEnvironment>()
            .AddTransient<SendRequest>()
            .AddTransient<CancelRequest>()
            .AddTransient<SaveResponseBody>()
            .AddTransient<ListCookies>()
            .AddTransient<DeleteCookie>()
            .AddTransient<ClearCookies>()
            .AddTransient<ListCapturedValues>()
            .AddTransient<ClearCapturedValues>()
            .AddTransient<CreateFolder>()
            .AddTransient<RenameFolder>()
            .AddTransient<MoveFolder>()
            .AddTransient<DuplicateFolder>()
            .AddTransient<DeleteFolder>()
            .AddTransient<ListEnvironments>()
            .AddTransient<LoadEnvironment>()
            .AddTransient<CreateEnvironment>()
            .AddTransient<SaveEnvironment>()
            .AddTransient<DeleteEnvironment>()
            .AddTransient<SelectEnvironment>()
            .AddTransient<GetGitStatus>()
            .AddTransient<InitRepository>()
            .AddTransient<CommitChanges>()
            .AddTransient<DiscardChanges>()
            .AddTransient<PullChanges>()
            .AddTransient<PushChanges>()
            .AddTransient<FetchChanges>()
            .AddTransient<GetGitIdentity>()
            .AddTransient<SetGitIdentity>()
            .AddTransient<GetConflict>()
            .AddTransient<ResolveConflict>()
            .AddTransient<AbortMerge>()
            .AddTransient<GetGitHistory>()
            .AddTransient<GetCommit>()
            .AddTransient<GetCommitFile>()
            .AddTransient<GetFileDiff>()
            .AddTransient<ListBranches>()
            .AddTransient<SwitchBranch>()
            .AddTransient<CreateBranch>()
            .AddTransient<CheckoutRemoteBranch>()
            .AddTransient<DeleteBranch>()
            .AddTransient<ListRemotes>()
            .AddTransient<AddRemote>()
            .AddTransient<UpdateRemote>()
            .AddTransient<RemoveRemote>()
            .BuildServiceProvider();
}
