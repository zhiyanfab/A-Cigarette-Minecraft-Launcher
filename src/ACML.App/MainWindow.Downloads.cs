using ACML.Core;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace ACML.App;

public partial class MainWindow
{
    private readonly HttpClient downloadsClient = CreateDownloadsClient();
    private CancellationTokenSource? downloadCancellation;
    private RemoteVersion? pendingInstallVersion;
    private IReadOnlyList<ModRelease>? pendingMods;
    private string pendingProvider = "";
    private string pendingModRoot = "";
    private string pendingModDirectory = "";
    private static HttpClient CreateDownloadsClient()
    { var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) }; client.DefaultRequestHeaders.UserAgent.ParseAdd("ACML/" + LauncherInfo.Version); return client; }
    private IModProvider ModProvider() => Vm.ModProviderInput == "Modrinth"
        ? new ModrinthProvider(downloadsClient)
        : throw new InvalidOperationException("CurseForge 下载源已禁用。");
    private (string Version, string Loader, string Directory) ModTarget()
    {
        var version = Vm.ActiveVersion ?? throw new InvalidOperationException("请先在左侧选择一个目标游戏版本。");
        var loader = version.Loader switch { "Fabric" => "fabric", "Quilt" => "quilt", "NeoForge" => "neoforge", "Forge" or "Forge / Legacy" => "forge", _ => throw new InvalidOperationException("请先选择已安装模组加载器的目标游戏版本。") };
        var directory = InstanceManager.GameDirectory(Vm.Settings, version.Id);
        return (version.BaseId ?? version.Id, loader, directory);
    }
    private void InstallVersion_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (!Vm.CanInteract) return;
        if (Vm.SelectedRemoteVersion is null) throw new InvalidOperationException("请等待版本列表加载并选择要安装的版本。");
        pendingInstallVersion = Vm.SelectedRemoteVersion;
        loaderCatalogCancellation?.Cancel();Vm.LoaderVersions.Clear();Vm.SelectedLoaderVersion=null;Vm.LoaderVersionsLoading=false;
        Vm.InstallVersionLabel = "Minecraft " + pendingInstallVersion.Id;
        Vm.InstallRootInput = Vm.Settings.GameRoot; Vm.InstallIdInput = Vm.SelectedRemoteVersion.Id; Vm.InstallLoaderInput = "原版";
        foreach (var name in new[] { nameof(Vm.InstallRootInput), nameof(Vm.InstallIdInput), nameof(Vm.InstallLoaderInput), nameof(Vm.InstallVersionLabel) }) Vm.Notify(name);
        Vm.DialogMessage = ""; Vm.Overlay = "Install";
    });
    private CancellationTokenSource? loaderCatalogCancellation;
    private async void InstallLoader_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || pendingInstallVersion is null || sender is not ComboBox { SelectedItem: string loader }) return;
        Vm.InstallLoaderInput = loader;
        var id = pendingInstallVersion.Id;
        if(Vm.InstallIdInput==id||new[]{"fabric","forge","neoforge"}.Any(x=>Vm.InstallIdInput==id+"-"+x))Vm.InstallIdInput=id+(loader=="原版"?"":"-"+loader.ToLowerInvariant());
        Vm.Notify(nameof(Vm.InstallIdInput));
        loaderCatalogCancellation?.Cancel();var cancellation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);loaderCatalogCancellation=cancellation;
        Vm.LoaderVersions.Clear();Vm.SelectedLoaderVersion=null;
        if(loader=="原版"){Vm.LoaderVersionsLoading=false;loaderCatalogCancellation=null;cancellation.Dispose();return;}
        Vm.LoaderVersionsLoading=true;Vm.DialogMessage="正在获取加载器版本…";
        try
        {
            var versions=await new LoaderCatalog(downloadsClient,Vm.Settings.DownloadSource).FetchAsync(id,loader,cancellation.Token);
            if(cancellation.IsCancellationRequested||pendingInstallVersion?.Id!=id||Vm.InstallLoaderInput!=loader)return;
            foreach(var version in versions)Vm.LoaderVersions.Add(version);Vm.SelectedLoaderVersion=versions.FirstOrDefault();
            Vm.DialogMessage=versions.Count==0?"此游戏版本没有可用的 "+loader+" 版本。":"";
        }
        catch(OperationCanceledException){}
        catch(Exception ex){if(!cancellation.IsCancellationRequested)Report(ex);}
        finally{if(ReferenceEquals(loaderCatalogCancellation,cancellation)){loaderCatalogCancellation=null;Vm.LoaderVersionsLoading=false;}cancellation.Dispose();}
    }
    private void RemoteVersion_MouseUp(object sender,MouseButtonEventArgs e)
    {
        if(sender is ListBox list&&ItemsControl.ContainerFromElement(list,e.OriginalSource as DependencyObject) is ListBoxItem{DataContext:RemoteVersion version})
        {Vm.SelectedRemoteVersion=version;Vm.Notify(nameof(Vm.SelectedRemoteVersion));InstallVersion_Click(sender,e);}
    }
    private void BrowseInstallRoot_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var dialog = new OpenFolderDialog { Title = "选择游戏安装目录" };
        if (Directory.Exists(Vm.InstallRootInput)) dialog.InitialDirectory = Vm.InstallRootInput;
        if (dialog.ShowDialog(this) == true) { Vm.InstallRootInput = dialog.FolderName; Vm.Notify(nameof(Vm.InstallRootInput)); }
    });
    private async void BeginInstall_Click(object sender, RoutedEventArgs e)
    {
        var selected = pendingInstallVersion; if (selected is null) return;
        if(!Vm.CanStartInstall)return;
        var root = GameRootManager.ResolveInputPath(Vm.InstallRootInput); var id = Vm.InstallIdInput.Trim(); var loader=Vm.InstallLoaderInput;var loaderVersion=Vm.SelectedLoaderVersion?.Version;
        await RunDownloadAsync(async (progress, token) =>
        {
            if (!Path.IsPathFullyQualified(root)) throw new InvalidOperationException("请使用完整的绝对安装目录。");
            await new GameInstaller(downloadsClient, Vm.Settings.DownloadSource, Vm.Settings.DownloadThreads).InstallAsync(root, selected, id, loader,loaderVersion,Vm.Settings.JavaPath, progress, token);
            GameRootManager.Activate(Vm.Settings, root); ClearRootContext();
            Vm.Settings.SelectedVersion = id; Vm.Save(); Vm.RefreshGameRoots(); Vm.ResetInputs();
            await ScanAsync(); HomeNav.IsChecked = true; Vm.Status = "安装完成：" + id + "。可选择账号并启动游戏。";
        });
    }
    private async void RepairVersion_Click(object sender, RoutedEventArgs e)
    {
        var version = VersionList.SelectedItem as GameVersion ?? Vm.ActiveVersion;
        if (version is null) { Vm.DialogMessage = "请选择需要补全的本地版本。"; return; }
        var root = Vm.Settings.GameRoot;
        if (IsVersionRunning(root, version.Id)) { NotifyLaunch("请先结束此版本的游戏进程，再补全文件。"); return; }
        await RunDownloadAsync(async (progress, token) =>
        { await new GameInstaller(downloadsClient, Vm.Settings.DownloadSource, Vm.Settings.DownloadThreads).RepairAsync(root, version.Id, progress, token); await ScanAsync(); Vm.Status = "补全完成：文件已检查，缺失或损坏文件已重新下载。"; });
    }
    private async Task RunDownloadAsync(Func<IProgress<DownloadProgress>, CancellationToken, Task> action)
    {
        if (!Vm.CanInteract) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        downloadCancellation = cancellation; Vm.Busy = true; Vm.IsDownloading = true; Vm.Overlay = "";
        Vm.DownloadPercent = 0; Vm.DownloadText = "准备下载…";
        var progress = new Progress<DownloadProgress>(update => { if (!Vm.IsDownloading) return; Vm.DownloadPercent = update.Percent; Vm.DownloadText = update.Label; Vm.Status = update.Label; });
        try { await action(progress, cancellation.Token); Vm.DownloadPercent = 100; Vm.DownloadText = "任务完成。"; Vm.Overlay = ""; }
        catch (OperationCanceledException) { Vm.DownloadText = "下载已取消，已完成且校验通过的游戏文件可以复用。"; Vm.Status = Vm.DownloadText; }
        catch (Exception ex) { Vm.DownloadText = ex.Message; Report(ex); }
        finally { downloadCancellation = null; Vm.Busy = false; Vm.IsDownloading = false; if (Vm.TasksVisible) Vm.Overlay = ""; }
    }
    private void CancelDownload_Click(object sender, RoutedEventArgs e) => downloadCancellation?.Cancel();
    private void Tasks_Click(object sender, RoutedEventArgs e) { if (Vm.IsDownloading) Vm.Overlay = "Tasks"; }
    private async void PrepareModInstall_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var selected = Vm.SelectedModRelease ?? throw new InvalidOperationException("请先选择一个模组项目和文件版本。");
        var provider = ModProvider(); var target = ModTarget();
        selected=await provider.ExactAsync(selected.Id,target.Version,target.Loader,lifetime.Token);
        pendingMods = await ModInstaller.ResolveAsync(provider, selected, target.Version, target.Loader, lifetime.Token);
        pendingProvider = provider.Name; pendingModRoot = Vm.Settings.GameRoot; pendingModDirectory = target.Directory;
        Vm.ModPlanText = "将安装到：" + target.Directory + "\n\n" + string.Join("\n", pendingMods.Select(x => "• " + x.Filename)) + "";
        Vm.Overlay = "ModConfirm";
    });
    private async void BeginModInstall_Click(object sender, RoutedEventArgs e)
    {
        if (pendingMods is null) return;
        await RunDownloadAsync(async (progress, token) =>
        { await ModInstaller.InstallAsync(downloadsClient, pendingModRoot, pendingModDirectory, pendingProvider, pendingMods, progress, token, Vm.Settings.DownloadThreads); Vm.Status = "模组及必需依赖已安装到所选实例的 mods 目录。"; });
    }
    private void OpenModPage_Click(object sender, RoutedEventArgs e) => Run(() =>
    { if (Vm.SelectedModProject is ModProject project) OpenSite(project.PageUrl); else throw new InvalidOperationException("请先选择一个项目。"); });
}


