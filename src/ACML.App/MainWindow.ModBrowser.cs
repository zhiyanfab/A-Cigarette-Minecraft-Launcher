using ACML.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ACML.App;
public partial class MainWindow
{
    private bool updatingModFilters;
    private IReadOnlyList<ModRelease> allModReleases=[];
    private readonly Stack<ModProject> modHistory=new();
    private readonly Dictionary<string,(ModProject Project,IReadOnlyList<ModRelease> Releases)> modCache=new();
    private async Task LoadModFeedAsync()=>await RunAsync(async()=>
    {
        Vm.ModDetail=false;Vm.ModStatus=string.IsNullOrWhiteSpace(Vm.ModQuery)?"正在获取推荐模组…":"正在搜索…";
        Vm.ModProjects.Clear();Vm.ModReleases.Clear();modHistory.Clear();
        var projects=await ModProvider().SearchAsync(Vm.ModQuery,"","",lifetime.Token);
        foreach(var project in projects)Vm.ModProjects.Add(project);
        Vm.ModStatus=$"{Vm.ModProviderInput} · "+(string.IsNullOrWhiteSpace(Vm.ModQuery)?"随机推荐":"搜索结果")+$" · {projects.Count} 个模组";
    });
    private async void SearchMods_Click(object sender,RoutedEventArgs e)=>await LoadModFeedAsync();
    private async void RecommendMods_Click(object sender,RoutedEventArgs e){Vm.ModQuery="";Vm.Notify(nameof(Vm.ModQuery));await LoadModFeedAsync();}
    private async void ModQuery_KeyDown(object sender,KeyEventArgs e){if(e.Key==Key.Enter){e.Handled=true;await LoadModFeedAsync();}}
    private async void DownloadsTabs_Changed(object sender,SelectionChangedEventArgs e)
    {if(ready&&ReferenceEquals(e.Source,sender)&&ModsTab.IsSelected&&Vm.Page=="Downloads"&&(Vm.ModProjects.Count==0||string.IsNullOrWhiteSpace(Vm.ModQuery)))await LoadModFeedAsync();}
    private async void ModProvider_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(!ready||sender is not ComboBox{SelectedItem:string provider})return;
        Vm.ModProviderInput=provider;Vm.ModDetail=false;Vm.ModProjects.Clear();Vm.ModReleases.Clear();modHistory.Clear();
        if(Vm.Page=="Downloads"&&ModsTab.IsSelected)await LoadModFeedAsync();
    }
    private async void ModProject_MouseUp(object sender,MouseButtonEventArgs e)
    {
        if(sender is ListBox list&&ItemsControl.ContainerFromElement(list,e.OriginalSource as DependencyObject) is ListBoxItem{DataContext:ModProject project})
            await OpenModProjectAsync(project,true);
    }
    private async Task OpenModProjectAsync(ModProject project,bool remember)=>await RunAsync(async()=>
    {
        if(remember&&Vm.ModDetail&&Vm.SelectedModProject is ModProject previous)modHistory.Push(previous);
        Vm.ModDetail=true;Vm.SelectedModProject=project;Vm.ModReleases.Clear();Vm.ModDependencies.Clear();Vm.Notify(nameof(Vm.HasModDependencies));Vm.ModStatus="正在获取模组信息…";
        var provider=ModProvider();var key=provider.Name+":"+project.Id;
        if(!modCache.TryGetValue(key,out var cached))
        {
            var detail=await provider.ProjectAsync(project.Id,lifetime.Token);
            var releases=await provider.ReleasesAsync(project.Id,"","",lifetime.Token);
            cached=(detail,releases);modCache[key]=cached;
        }
        allModReleases=cached.Releases;
        var games=(cached.Project.GameVersions??[]).Concat(allModReleases.SelectMany(x=>x.GameVersions??[])).Distinct().ToList();
        var loaders=(cached.Project.Loaders??[]).Concat(allModReleases.SelectMany(x=>x.Loaders??[])).Distinct().ToList();
        Vm.SelectedModProject=cached.Project with{GameVersions=games,Loaders=loaders};
        updatingModFilters=true;
        try
        {
            Vm.ModGameFilters.Clear();Vm.ModGameFilters.Add("全部");foreach(var game in games.AsEnumerable().Reverse())Vm.ModGameFilters.Add(game);
            Vm.ModLoaderFilters.Clear();Vm.ModLoaderFilters.Add("全部");foreach(var loader in loaders)Vm.ModLoaderFilters.Add(loader);
            var target=Vm.ActiveVersion;
            Vm.ModGameFilter=target?.BaseId is string baseId&&games.Contains(baseId)?baseId:"全部";
            var preferred=target?.Loader.ToLowerInvariant();Vm.ModLoaderFilter=preferred is not null&&loaders.Contains(preferred)?preferred:"全部";
            Vm.Notify(nameof(Vm.ModGameFilter));Vm.Notify(nameof(Vm.ModLoaderFilter));
        }
        finally{updatingModFilters=false;}
        ApplyModFilters();await LoadDependenciesAsync();
    });
    private void ApplyModFilters()
    {
        var selected=Vm.SelectedModRelease?.Id;updatingModFilters=true;
        try
        {
            Vm.ModReleases.Clear();
            foreach(var release in allModReleases.Where(x=>ModMetadata.Matches(Vm.ModGameFilter=="全部"?"":Vm.ModGameFilter,Vm.ModLoaderFilter=="全部"?"":Vm.ModLoaderFilter,x.GameVersions??[],x.Loaders??[])))Vm.ModReleases.Add(release);
            Vm.SelectedModRelease=Vm.ModReleases.FirstOrDefault(x=>x.Id==selected)??Vm.ModReleases.FirstOrDefault();Vm.Notify(nameof(Vm.SelectedModRelease));
            Vm.ModStatus=$"{Vm.ModProviderInput} · {Vm.ModReleases.Count} 个文件";
        }
        finally{updatingModFilters=false;}
    }
    private async void ModFilter_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(!ready||updatingModFilters||!Vm.ModDetail||sender is not ComboBox{SelectedItem:string value})return;
        if(ReferenceEquals(sender,ModGameFilterBox))Vm.ModGameFilter=value;else Vm.ModLoaderFilter=value;
        await RunAsync(async()=>{ApplyModFilters();await LoadDependenciesAsync();});
    }
    private void ModRelease_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(!ready||updatingModFilters||!Vm.ModDetail||sender is not ListBox{SelectedItem:ModRelease release})return;
        Vm.SelectedModRelease=release;
    }
    private async void ModRelease_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!Vm.CanInteract || sender is not ListBox list || ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) is not ListBoxItem { DataContext: ModRelease release }) return;
        Vm.SelectedModRelease = release; Vm.Notify(nameof(Vm.SelectedModRelease));
        IReadOnlyList<ModRelease>? files = null; string? destination = null; var provider = ModProvider(); var root = Vm.Settings.GameRoot;
        await RunAsync(async () =>
        {
            await LoadDependenciesAsync();
            var active = Vm.ActiveVersion;
            var defaultDirectory = System.IO.Path.Combine(active is null ? root : InstanceManager.GameDirectory(Vm.Settings, active.Id), "mods");
            System.IO.Directory.CreateDirectory(defaultDirectory);
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择模组保存目录", InitialDirectory = defaultDirectory };
            if (dialog.ShowDialog(this) != true) return;
            destination = dialog.FolderName;
            var game = Vm.ModGameFilter != "全部" ? Vm.ModGameFilter : release.GameVersions?.Contains(active?.BaseId ?? "") == true ? active!.BaseId! : release.GameVersions?.FirstOrDefault() ?? "";
            var loader = Vm.ModLoaderFilter != "全部" ? Vm.ModLoaderFilter : release.Loaders?.Contains(active?.Loader.ToLowerInvariant() ?? "") == true ? active!.Loader.ToLowerInvariant() : release.Loaders?.FirstOrDefault() ?? "";
            if (active is not null && GameRootManager.Same(defaultDirectory, destination) && active.Loader != "原版")
            { var target = ModTarget(); game = target.Version; loader = target.Loader; }
            var selected = await provider.ExactAsync(release.Id, game, loader, lifetime.Token);
            files = await ModInstaller.ResolveAsync(provider, selected, game, loader, lifetime.Token);
        });
        if (destination is null || files is null) return;
        await RunDownloadAsync(async (progress, token) =>
        {
            try { await ModInstaller.InstallToDirectoryAsync(downloadsClient, root, destination, provider.Name, files, progress, token, Vm.Settings.DownloadThreads); Vm.ModStatus = "下载完成 · " + destination; NotifyLaunch("模组下载完成"); }
            catch (Exception ex) { Vm.ModStatus = "下载失败：" + ex.Message; throw; }
        });
    }
    private async Task LoadDependenciesAsync()
    {
        Vm.ModDependencies.Clear();Vm.Notify(nameof(Vm.HasModDependencies));
        if(Vm.SelectedModRelease is not ModRelease selected)return;
        var provider=ModProvider();
        foreach(var dependency in selected.Dependencies.Distinct().Take(50))
        {
            var id=dependency.ProjectId;
            if(id is null&&dependency.VersionId is string versionId)id=(await provider.ExactAsync(versionId,"","",lifetime.Token)).ProjectId;
            if(id is null)continue;
            ModProject project;
            if(modCache.TryGetValue(provider.Name+":"+id,out var cached))project=cached.Project;
            else project=await provider.ProjectAsync(id,lifetime.Token);
            Vm.ModDependencies.Add(new(project,dependency.Kind));
        }
        Vm.Notify(nameof(Vm.HasModDependencies));
    }
    private async void ModDependency_MouseUp(object sender,MouseButtonEventArgs e)
    {
        if(sender is ListBox list&&ItemsControl.ContainerFromElement(list,e.OriginalSource as DependencyObject) is ListBoxItem{DataContext:ModDependencyCard card})
            await OpenModProjectAsync(card.Project,true);
    }
    private async void BackMods_Click(object sender,RoutedEventArgs e)
    {
        if(!Vm.CanInteract)return;
        if(modHistory.TryPop(out var previous))await OpenModProjectAsync(previous,false);
        else {Vm.ModDetail=false;Vm.ModStatus=$"{Vm.ModProviderInput} · {Vm.ModProjects.Count} 个模组";}
    }
}
