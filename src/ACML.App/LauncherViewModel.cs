using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using ACML.Core;

namespace ACML.App;

public sealed class LauncherViewModel : INotifyPropertyChanged
{
    public string DisplayVersion => LauncherInfo.DisplayVersion;
    public string WindowTitle => "ACML · " + DisplayVersion;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    private string page = "Home";
    public string Page { get => page; set { page = value; Notify(); Notify(nameof(HomeVisible)); Notify(nameof(DownloadsVisible)); Notify(nameof(SettingsVisible)); Notify(nameof(VersionManagementVisible)); Notify(nameof(InstanceVisible)); } }
    public bool HomeVisible => Page == "Home";
    public bool DownloadsVisible => Page == "Downloads";
    public bool SettingsVisible => Page == "Settings";
    public bool VersionManagementVisible => Page == "Versions";
    public bool NoVersions => Versions.Count == 0;
    public ObservableCollection<LocalModFile> InstalledMods { get; } = [];
    public GameVersion? ManagedVersion { get; set; }
    public string ManagedNameInput { get; set; } = "";
    private string instanceSaveStatus = "";
    public string InstanceSaveStatus { get => instanceSaveStatus; set { instanceSaveStatus = value; Notify(); } }
    private string fieldManagedJavaInput = ""; public string ManagedJavaInput { get => fieldManagedJavaInput; set { fieldManagedJavaInput = value; Notify(); } }
    private string fieldManagedTitleInput = ""; public string ManagedTitleInput { get => fieldManagedTitleInput; set { fieldManagedTitleInput = value; Notify(); } }
    private bool managedUseGlobalJava, managedUseGlobalMemory, managedUseGlobalTitle, managedUseGlobalIsolation, managedUseGlobalWindow;
    public bool ManagedUseGlobalJava { get => managedUseGlobalJava; set { managedUseGlobalJava = value; Notify(); Notify(nameof(ManagedJavaOverrideEnabled)); } }
    public bool ManagedUseGlobalMemory { get => managedUseGlobalMemory; set { managedUseGlobalMemory = value; Notify(); Notify(nameof(ManagedMemoryOverrideEnabled)); } }
    public bool ManagedUseGlobalTitle { get => managedUseGlobalTitle; set { managedUseGlobalTitle = value; Notify(); Notify(nameof(ManagedTitleOverrideEnabled)); } }
    public bool ManagedUseGlobalIsolation { get => managedUseGlobalIsolation; set { managedUseGlobalIsolation = value; Notify(); Notify(nameof(ManagedIsolationOverrideEnabled)); } }
    public bool ManagedUseGlobalWindow { get => managedUseGlobalWindow; set { managedUseGlobalWindow = value; Notify(); Notify(nameof(ManagedWindowOverrideEnabled)); } }
    public bool ManagedJavaOverrideEnabled => !ManagedUseGlobalJava;
    public bool ManagedMemoryOverrideEnabled => !ManagedUseGlobalMemory;
    public bool ManagedTitleOverrideEnabled => !ManagedUseGlobalTitle;
    public bool ManagedIsolationOverrideEnabled => !ManagedUseGlobalIsolation;
    public bool ManagedWindowOverrideEnabled => !ManagedUseGlobalWindow;
    private string fieldManagedMemoryInput = "4096"; public string ManagedMemoryInput { get => fieldManagedMemoryInput; set { fieldManagedMemoryInput = value; Notify(); } }
    private string fieldManagedWidthInput = "1280"; public string ManagedWidthInput { get => fieldManagedWidthInput; set { fieldManagedWidthInput = value; Notify(); } }
    private string fieldManagedHeightInput = "720"; public string ManagedHeightInput { get => fieldManagedHeightInput; set { fieldManagedHeightInput = value; Notify(); } }
    private bool managedSettingsEnabled;
    public bool ManagedSettingsEnabled { get => managedSettingsEnabled; set { managedSettingsEnabled = value; Notify(); } }
    private bool fieldManagedIsolationInput; public bool ManagedIsolationInput { get => fieldManagedIsolationInput; set { fieldManagedIsolationInput = value; Notify(); } }
    public string ManagedDirectoryLabel => ManagedVersion is null ? "" : InstanceManager.GameDirectory(Settings, ManagedVersion.Id);
    public LauncherSettings Settings { get; }
    public SettingsStore Store { get; }
    public ObservableCollection<PlayerAccount> Accounts { get; } = [];
    public ObservableCollection<GameVersion> Versions { get; } = [];
    public ObservableCollection<GameRootEntry> GameRoots { get; } = [];
    public GameRootEntry? SelectedGameRoot => GameRoots.FirstOrDefault(x => GameRootManager.Same(x.Path, Settings.GameRoot));
    public string RootNameInput { get; set; } = "";
    public void RefreshGameRoots()
    {
        GameRoots.Clear(); foreach (var entry in Settings.GameRoots) GameRoots.Add(entry);
        RootInput = Settings.GameRoot; RootNameInput = SelectedGameRoot?.Name ?? "";
        Notify(nameof(SelectedGameRoot)); Notify(nameof(GameRootLabel)); Notify(nameof(RootInput)); Notify(nameof(RootNameInput));
    }
    public ObservableCollection<RemoteVersion> RemoteVersions { get; } = [];
    public ObservableCollection<JavaRuntime> JavaRuntimes { get; } = [];
    public ObservableCollection<ModProject> ModProjects { get; } = [];
    public ObservableCollection<ModRelease> ModReleases { get; } = [];
    public RemoteVersion? SelectedRemoteVersion { get; set; }
    private ModProject? selectedModProject;
    public ModProject? SelectedModProject { get=>selectedModProject; set{selectedModProject=value;Notify();Notify(nameof(ModTitle));Notify(nameof(ModDescription));Notify(nameof(ModIcon));Notify(nameof(ModLoadersLabel));Notify(nameof(ModVersionsLabel));} }
    public string ModTitle=>SelectedModProject?.Title??"";
    public string ModDescription=>SelectedModProject?.Description??"";
    public string? ModIcon=>SelectedModProject?.IconUrl;
    public string ModLoadersLabel=>SelectedModProject?.LoadersLabel??"";
    public string ModVersionsLabel=>SelectedModProject?.VersionsLabel??"";
    private bool modDetail;
    public bool ModDetail {get=>modDetail;set{modDetail=value;Notify();Notify(nameof(ModBrowseVisible));}}
    public bool ModBrowseVisible=>!ModDetail;
    public ObservableCollection<string> ModGameFilters {get;}=["全部"];
    public ObservableCollection<string> ModLoaderFilters {get;}=["全部"];
    public string ModGameFilter {get;set;}="全部";
    public string ModLoaderFilter {get;set;}="全部";
    public ObservableCollection<ModDependencyCard> ModDependencies {get;}=[];
    public bool HasModDependencies=>ModDependencies.Count>0;
    public ObservableCollection<LoaderVersion> LoaderVersions {get;}=[];
    private LoaderVersion? selectedLoaderVersion;
    public LoaderVersion? SelectedLoaderVersion{get=>selectedLoaderVersion;set{selectedLoaderVersion=value;Notify();Notify(nameof(CanStartInstall));}}
    private bool loaderVersionsLoading;
    public bool LoaderVersionsLoading{get=>loaderVersionsLoading;set{loaderVersionsLoading=value;Notify();Notify(nameof(CanStartInstall));}}
    public bool LoaderVersionVisible=>InstallLoaderInput!="原版";
    public bool CanStartInstall=>CanInteract&&!LoaderVersionsLoading&&(InstallLoaderInput=="原版"||SelectedLoaderVersion is not null);
    public ModRelease? SelectedModRelease { get; set; }
    public string ModQuery { get; set; } = "";
    public string ModProviderInput { get; set; } = "Modrinth";
    public string[] ModProviders { get; } = ["Modrinth"];
    public string[] InstallLoaders { get; } = ["原版", "Fabric", "Forge", "NeoForge"];
    public string InstallRootInput { get; set; } = "";
    public string InstallIdInput { get; set; } = "";
    private string installLoaderInput="原版";
    public string InstallLoaderInput {get=>installLoaderInput;set{installLoaderInput=value;Notify();Notify(nameof(LoaderVersionVisible));Notify(nameof(CanStartInstall));}}
    private string modStatus = "";
    public string ModStatus { get => modStatus; set { modStatus = value; Notify(); } }
    public string ModTargetLabel => "目标：" + VersionTitle + " · " + VersionDetail;
    public string InstallVersionLabel { get; set; } = "";
    private string modPlanText = "";
    public string ModPlanText { get => modPlanText; set { modPlanText = value; Notify(); } }
    private bool downloading;
    public bool IsDownloading { get => downloading; set { downloading = value; Notify(); Notify(nameof(StatusBarVisible)); } }
    private double downloadPercent;
    public double DownloadPercent { get => downloadPercent; set { downloadPercent = value; Notify(); } }
    private string downloadText = "";
    public string DownloadText { get => downloadText; set { downloadText = value; Notify(); } }
    public string[] Sources { get; } = ["自动", "官方", "BMCLAPI"];
    public string[] VersionFilters { get; } = ["全部", "正式版", "快照版", "历史版本"];
    private bool expanded;
    public bool Expanded { get => expanded; set { expanded = value; Notify(); Notify(nameof(SidebarWidth)); Settings.ExpandedSidebar = value; } }
    public double SidebarWidth => Expanded ? 246 : 86;
    public PlayerAccount? ActiveAccount => Accounts.FirstOrDefault(x => x.Id == Settings.ActiveAccountId);
    public string AccountName => ActiveAccount?.Name ?? "尚未登录";
    public string AccountInitial => ActiveAccount?.Name[..1].ToUpperInvariant() ?? "+";
    private bool accountVerified;
    public bool AccountVerified { get => accountVerified; set { accountVerified = value; Notify(); Notify(nameof(AccountStatus)); } }
    public string AccountStatus => ActiveAccount is null ? "尚未选择账号" : ActiveAccount.IsMicrosoft ? AccountVerified ? "正版 · 已登录" : "正版 · 待验证" : "离线模式 · 当前账号";
    public GameVersion? ActiveVersion => Versions.FirstOrDefault(x => x.Id == Settings.SelectedVersion);
    public string VersionTitle => ActiveVersion?.Id ?? "选择游戏版本";
    public string VersionDetail => ActiveVersion?.Description ?? "扫描本地游戏或查看下载";
    public string VersionCount => $"{Versions.Count} 个本地版本";
    public string InstanceSummary => ActiveVersion is null ? "还没有选择版本" : ActiveVersion.IsSupported ? "本地版本就绪" : "需要处理";
    public string IsolationLabel => Settings.IsolateVersions ? "版本隔离已开启" : "使用共享游戏目录";
    public string MemoryLabel => $"{Settings.MemoryMb / 1024.0:0.#} GB 内存";
    public string JavaLabel => string.IsNullOrWhiteSpace(Settings.JavaPath) ? "尚未选择 Java" : Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(Settings.JavaPath))) ?? "已选择 Java";
    public string GameRootLabel => Settings.GameRoot;
    private string status = "";
    public string Status { get => status; set { status = value; Notify(); } }
    private bool busy;
    public bool Busy { get => busy; set { busy = value; Notify(); Notify(nameof(CanInteract)); Notify(nameof(CanStartInstall)); Notify(nameof(LaunchText)); } }
    public bool CanInteract => !Busy;
    private bool authenticating;
    public bool IsAuthenticating { get => authenticating; set { authenticating = value; Notify(); } }
    private bool gameRunning;
    public bool GameRunning { get => gameRunning; set { gameRunning = value; Notify(); Notify(nameof(CanInteract)); Notify(nameof(CanStartInstall)); Notify(nameof(LaunchText)); Notify(nameof(CanStopGame)); } }
    private bool stoppingGame;
    public bool StoppingGame { get => stoppingGame; set { stoppingGame = value; Notify(); Notify(nameof(CanStopGame)); Notify(nameof(StopGameText)); } }
    public bool CanStopGame => GameRunning && !StoppingGame;
    private int runningGameCount;
    public int RunningGameCount { get => runningGameCount; set { runningGameCount = value; Notify(); Notify(nameof(StopGameText)); } }
    public string StopGameText => StoppingGame ? "正在结束…" : RunningGameCount > 1 ? $"结束游戏（{RunningGameCount}）▾" : "结束游戏";
    private string launchStage = "", launchVersion = "", failureTitle = "", failureDetails = "", launchLogPath = "", gameLogPath = "", notification = "";
    private void SetLaunchText(ref string field, string value, [CallerMemberName] string? property = null) { field = value; Notify(property); }
    public string LaunchStage { get => launchStage; set => SetLaunchText(ref launchStage, value); }
    public string LaunchVersion { get => launchVersion; set => SetLaunchText(ref launchVersion, value); }
    public string FailureTitle { get => failureTitle; set => SetLaunchText(ref failureTitle, value); }
    public string FailureDetails { get => failureDetails; set => SetLaunchText(ref failureDetails, value); }
    private string failureAnalysis = "";
    public string FailureAnalysis { get => failureAnalysis; set => SetLaunchText(ref failureAnalysis, value); }
    public string LaunchLogPath { get => launchLogPath; set => SetLaunchText(ref launchLogPath, value); }
    public string GameLogPath { get => gameLogPath; set => SetLaunchText(ref gameLogPath, value); }
    public string Notification { get => notification; set { SetLaunchText(ref notification, value); Notify(nameof(HasNotification)); } }
    public bool HasNotification => Notification.Length > 0;
    private double launchProgress;
    public double LaunchProgress { get => launchProgress; set { launchProgress = Math.Clamp(value, 0, 100); Notify(); } }
    private bool launchIndeterminate;
    public bool LaunchIndeterminate { get => launchIndeterminate; set { launchIndeterminate = value; Notify(); } }
    public string LaunchText => Busy ? "正在准备…" : "启动游戏";
    public string LaunchVersionLabel => ActiveVersion?.Id ?? "未选择版本";
    private string overlay = "";
    public string Overlay { get => overlay; set { overlay = value; Notify(); Notify(nameof(OverlayVisible)); Notify(nameof(AccountsVisible)); Notify(nameof(VersionsVisible)); Notify(nameof(InstanceVisible)); Notify(nameof(LogsVisible)); Notify(nameof(InstallVisible)); Notify(nameof(TasksVisible)); Notify(nameof(ModConfirmVisible)); Notify(nameof(RootRenameVisible)); Notify(nameof(LaunchingVisible)); Notify(nameof(LaunchFailureVisible)); Notify(nameof(PackVisible)); Notify(nameof(JavaSwitchVisible)); } }
    public bool OverlayVisible => Overlay != "";
    public bool AccountsVisible => Overlay == "Accounts";
    public bool VersionsVisible => Overlay == "Versions";
    public bool InstanceVisible => Page == "Instance";
    public bool LogsVisible => Overlay == "Logs";
    public bool InstallVisible => Overlay == "Install";
    public bool TasksVisible => Overlay == "Tasks";
    public bool ModConfirmVisible => Overlay == "ModConfirm";
    public bool RootRenameVisible => Overlay == "RootRename";
    public bool LaunchingVisible => Overlay == "Launching";
    public bool LaunchFailureVisible => Overlay == "LaunchFailure";
    public bool JavaSwitchVisible => Overlay == "JavaSwitch";
    private string javaCurrentVersion = "", javaRequiredVersion = "";
    public string JavaCurrentVersion { get => javaCurrentVersion; set => SetLaunchText(ref javaCurrentVersion,value); }
    public string JavaRequiredVersion { get => javaRequiredVersion; set => SetLaunchText(ref javaRequiredVersion,value); }
    public bool PackVisible => Overlay == "Pack";
    private bool packIsImport;
    public bool PackIsImport { get => packIsImport; set { packIsImport = value; Notify(); Notify(nameof(PackExportVisible)); Notify(nameof(PackActionText)); } }
    public bool PackExportVisible => !PackIsImport;
    public string PackActionText => PackIsImport ? "开始导入" : "导出整合包";
    public string PackFilePath { get; set; } = "";
    public string PackRootInput { get; set; } = "";
    public string PackIdInput { get; set; } = "";
    public string[] PackFormats { get; } = ["Modrinth", "CurseForge"];
    public string PackFormat { get; set; } = "Modrinth";
    public bool PackIncludeSaves { get; set; }
    private string packSummary = "";
    public string PackSummary { get => packSummary; set { packSummary = value; Notify(); } }
    public string RootRenameInput { get; set; } = "";
    private string dialogMessage = "";
    public string DialogMessage { get => dialogMessage; set { dialogMessage = value; Notify(); } }
    private string logs = "";
    public string Logs { get => logs; set { logs = value; Notify(); } }
    private string latestRelease = "等待获取";
    public string LatestRelease { get => latestRelease; set { latestRelease = value; Notify(); } }
    private string latestSnapshot = "等待获取";
    public string LatestSnapshot { get => latestSnapshot; set { latestSnapshot = value; Notify(); } }
    private string catalogStatus = "";
    public string CatalogStatus { get => catalogStatus; set { catalogStatus = value; Notify(); } }
    private string search = "";
    public string Search { get => search; set { search = value; Notify(); FilterCatalog(); } }
    private string versionFilter = "全部";
    public string VersionFilter { get => versionFilter; set { versionFilter = value; Notify(); FilterCatalog(); } }
    private IReadOnlyList<RemoteVersion> catalog = [];
    public void SetCatalog(VersionCatalog value)
    {
        var selectedId = SelectedRemoteVersion?.Id;
        catalog = value.Versions; LatestRelease = value.LatestRelease; LatestSnapshot = value.LatestSnapshot;
        CatalogStatus = $"来源：{value.Source} · {catalog.Count} 个版本 · 获取于 {DateTime.Now:HH:mm:ss}";
        FilterCatalog();
        SelectedRemoteVersion = RemoteVersions.FirstOrDefault(x => x.Id == selectedId)
            ?? RemoteVersions.FirstOrDefault(x => x.Id == value.LatestRelease) ?? RemoteVersions.FirstOrDefault();
        Notify(nameof(SelectedRemoteVersion));
    }
    private void FilterCatalog()
    {
        RemoteVersions.Clear();
        foreach (var version in catalog.Where(x => x.Id.Contains(Search ?? "", StringComparison.OrdinalIgnoreCase) && (VersionFilter == "全部" || x.TypeLabel == VersionFilter))) RemoteVersions.Add(version);
    }
    public string RootInput { get; set; } = "";
    private string javaInput = "";
    public string JavaInput { get => javaInput; set { javaInput = value ?? ""; Notify(); } }
    private string javaDetectionStatus = "正在检测 Java…";
    public string JavaDetectionStatus { get => javaDetectionStatus; set { javaDetectionStatus = value; Notify(); } }
    public string TitleInput { get; set; } = "";
    public string MemoryInput { get; set; } = "";
    public string WidthInput { get; set; } = "";
    public string HeightInput { get; set; } = "";
    public bool IsolationInput { get; set; }
    public bool HideInput { get; set; }
    public string SourceInput { get; set; } = "自动";
    public string DownloadThreadsInput { get; set; } = "16";
    private bool showStatusInput;
    public bool ShowStatusInput { get => showStatusInput; set { showStatusInput = value; Notify(); Notify(nameof(StatusBarVisible)); } }
    public bool StatusBarVisible => ShowStatusInput || IsDownloading;
    private ImageBrush homeBackground = null!;
    public ImageBrush HomeBackground { get => homeBackground; private set { homeBackground = value; Notify(); } }
    public string BackgroundName => string.IsNullOrEmpty(Settings.BackgroundImagePath) ? "默认背景" : File.Exists(Settings.BackgroundImagePath) ? "自定义背景已保存" : "图片丢失，已显示默认背景";
    private double backgroundDim;
    public double BackgroundDimInput { get => backgroundDim; set { backgroundDim = double.IsFinite(value) ? Math.Clamp(value, 0, 0.75) : 0.15; Notify(); } }
    public void ReloadBackground()
    {
        try { HomeBackground = string.IsNullOrEmpty(Settings.BackgroundImagePath) ? BackgroundImageService.DefaultBrush() : BackgroundImageService.Load(Settings.BackgroundImagePath); }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or InvalidOperationException or System.IO.FileFormatException)
        { HomeBackground = BackgroundImageService.DefaultBrush(); Status = "背景图片无法读取，已显示默认背景。"; }
        Notify(nameof(BackgroundName));
    }
    public LauncherViewModel()
    {
        Versions.CollectionChanged += (_, _) => Notify(nameof(NoVersions));
        var overrideDirectory = Environment.GetEnvironmentVariable("ACML_DEMO_DATA_DIR");
        Store = new(string.IsNullOrEmpty(overrideDirectory) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ACML", "Demo") : Path.GetFullPath(overrideDirectory));
        Settings = Store.Load();
        RefreshGameRoots();
        foreach (var account in Settings.Accounts) Accounts.Add(account);
        expanded = Settings.ExpandedSidebar;
        ReloadBackground();
        ResetInputs();
    }
    public void ResetInputs()
    {
        RootNameInput = SelectedGameRoot?.Name ?? ""; Notify(nameof(RootNameInput));
        RootInput = Settings.GameRoot; JavaInput = Settings.JavaPath; MemoryInput = Settings.MemoryMb.ToString(); WidthInput = Settings.GameWidth.ToString(); HeightInput = Settings.GameHeight.ToString(); IsolationInput = Settings.IsolateVersions; HideInput = Settings.HideOnLaunch; SourceInput = Settings.DownloadSource;
        BackgroundDimInput = Settings.BackgroundDim;
        ShowStatusInput = Settings.ShowStatusBar;
        TitleInput = Settings.GameWindowTitle; Notify(nameof(TitleInput));
        DownloadThreadsInput = Settings.DownloadThreads.ToString(); Notify(nameof(DownloadThreadsInput));
        foreach (var name in new[] { nameof(RootInput), nameof(JavaInput), nameof(MemoryInput), nameof(WidthInput), nameof(HeightInput), nameof(IsolationInput), nameof(HideInput), nameof(SourceInput) }) Notify(name);
    }
    public void Save() { Settings.Accounts = Accounts.ToList(); Store.Save(Settings); RefreshLabels(); }
    public void RefreshLabels()
    { foreach (var name in new[] { nameof(AccountName), nameof(AccountInitial), nameof(AccountStatus), nameof(VersionTitle), nameof(VersionDetail), nameof(VersionCount), nameof(InstanceSummary), nameof(IsolationLabel), nameof(MemoryLabel), nameof(JavaLabel), nameof(GameRootLabel), nameof(ActiveAccount), nameof(ActiveVersion), nameof(ModTargetLabel), nameof(LaunchVersionLabel), nameof(NoVersions) }) Notify(name); }
}




