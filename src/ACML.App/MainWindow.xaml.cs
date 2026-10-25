using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ACML.Core;

namespace ACML.App;

public partial class MainWindow : Window
{
    private LauncherViewModel Vm => (LauncherViewModel)DataContext;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object logLock = new();
    private bool ready;
    private readonly MicrosoftLoginService authentication;
    private CancellationTokenSource? loginCancellation;
    private string LogDirectory => Path.Combine(Vm.Store.DirectoryPath, "logs");
    private string AppLog => Path.Combine(LogDirectory, "launcher.log");

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new LauncherViewModel();
        authentication = new MicrosoftLoginService(Vm.Store.DirectoryPath);
        ready = true;
        Vm.PropertyChanged += ManagedSetting_Changed;
    }
    public void Report(Exception error)
    {
        if (DataContext is not LauncherViewModel) return;
        Vm.Status = error.Message;
        if (Vm.OverlayVisible) Vm.DialogMessage = error.Message;
        AppendLog("错误：" + error.Message);
    }
    private string RedactLog(string text)
    {
        lock (logLock) foreach (var run in gameRuns) { var token = run.Token; if (token.Length > 0) text = text.Replace(token, "[token hidden]", StringComparison.Ordinal); }
        return LaunchDiagnostics.Redact(text);
    }
    private void AppendLog(string text)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => AppendLog(text)); return; }
        text = RedactLog(text);
        var line = $"[{DateTime.Now:HH:mm:ss}] {text}";
        lock (logLock)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                if (File.Exists(AppLog) && new FileInfo(AppLog).Length > 2_000_000) File.Move(AppLog, AppLog + ".previous", true);
                File.AppendAllText(AppLog, line + Environment.NewLine);
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() => { Vm.Logs += line + Environment.NewLine; if (Vm.Logs.Length > 180_000) Vm.Logs = Vm.Logs[^120_000..]; });
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (!Vm.CanInteract) return;
        Vm.Busy = true;
        try { await action(); }
        catch (OperationCanceledException) { Vm.Status = "操作已取消或超时。"; if (Vm.OverlayVisible) Vm.DialogMessage = Vm.Status; }
        catch (Exception ex) { Report(ex); }
        finally { Vm.Busy = false; }
    }
    private void Run(Action action) { try { action(); } catch (Exception ex) { Report(ex); } }
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        AppendLog("ACML " + LauncherInfo.Version + " 启动。");
        await RunAsync(async () =>
        {
            await ScanAsync();
            Vm.Status = "正在检测本机 Java…";
            await RefreshJavaListAsync();
        });
    }
    private async Task ScanAsync()
    {
        var root = Vm.Settings.GameRoot;
        var versions = await Task.Run(() => VersionService.Scan(root));
        Vm.Versions.Clear(); foreach (var version in versions) Vm.Versions.Add(version);
        if (!versions.Any(x => x.Id == Vm.Settings.SelectedVersion)) Vm.Settings.SelectedVersion = versions.FirstOrDefault(x => x.IsSupported)?.Id;
        Vm.Save();
        Vm.Status = versions.Count == 0 ? "当前目录没有游戏版本。" : $"已扫描 {versions.Count} 个版本，其中 {versions.Count(x => x.IsSupported)} 个可尝试启动。";
    }
    private void Home_Checked(object sender, RoutedEventArgs e) { if (ready) Vm.Page = "Home"; }
    private async void Downloads_Checked(object sender, RoutedEventArgs e)
    { if (ready) { Vm.Page = "Downloads"; await RefreshCatalogAsync(); if(ModsTab.IsSelected&&Vm.ModProjects.Count==0)await LoadModFeedAsync(); } }
    private void Settings_Checked(object sender, RoutedEventArgs e) { if (ready) Vm.Page = "Settings"; }
    private void Versions_Checked(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        Vm.Page = "Versions"; VersionList.SelectedItem = Vm.ActiveVersion;
    }
    private void ToggleSidebar_Click(object sender, RoutedEventArgs e) => Run(() => { Vm.Expanded = !Vm.Expanded; Vm.Save(); });
    private void Accounts_Click(object sender, RoutedEventArgs e) { Vm.DialogMessage = ""; Vm.Overlay = "Accounts"; AccountList.SelectedItem = Vm.ActiveAccount; }
    private void DismissOverlay_Click(object sender, RoutedEventArgs e) { if (Vm.JavaSwitchVisible) { javaSwitchRequest?.TrySetResult(false); return; } if (Vm.LaunchingVisible) { CancelLaunch_Click(sender, e); return; } loginCancellation?.Cancel(); Vm.Overlay = ""; Vm.DialogMessage = ""; }
    private void Accounts_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    { if (e.Key == System.Windows.Input.Key.Escape) { DismissOverlay_Click(sender, e); e.Handled = true; } }
    private void AddAccount_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (!Vm.CanInteract) return;
        var account = PlayerAccount.Create(AccountNameInput.Text);
        if (Vm.Accounts.Any(x => !x.IsMicrosoft && string.Equals(x.Name, account.Name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("此离线账号已存在，请直接选用。");
        Vm.Accounts.Add(account); Vm.Settings.ActiveAccountId = account.Id; Vm.AccountVerified = false; Vm.Save();
        AccountList.SelectedItem = account; AccountNameInput.Clear(); Vm.DialogMessage = "账号已添加，并设为当前账号。";
        Vm.Status = $"当前账号：{account.Name}（离线模式）。";
    });
    private async void UseAccount_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (AccountList.SelectedItem is not PlayerAccount account) throw new InvalidOperationException("请先选择一个账号。");
        if (account.IsMicrosoft)
        {
            Vm.DialogMessage = "正在验证所选正版账号…";
            var session = await authentication.RefreshAsync(account, lifetime.Token);
            account = UpdateMicrosoftAccount(account, session);
        }
        Vm.Settings.ActiveAccountId = account.Id; Vm.AccountVerified = account.IsMicrosoft; Vm.Save(); Vm.Overlay = "";
        Vm.Status = $"已切换到 {account.Name}（{(account.IsMicrosoft ? "正版" : "离线模式")}）。";
    });
    private async void RemoveAccount_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (AccountList.SelectedItem is not PlayerAccount account) throw new InvalidOperationException("请先选择要移除的账号。");
        await authentication.RemoveAsync(account);
        Vm.Accounts.Remove(account);
        if (Vm.Settings.ActiveAccountId == account.Id) { Vm.Settings.ActiveAccountId = null; Vm.AccountVerified = false; }
        Vm.Save(); Vm.DialogMessage = "已移除本地账号记录，游戏存档未修改。";
    });
    private async void Microsoft_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        cancellation.CancelAfter(TimeSpan.FromMinutes(5)); loginCancellation = cancellation; Vm.IsAuthenticating = true;
        try
        {
            Vm.DialogMessage = "请在系统浏览器中完成微软登录。"; Vm.Status = Vm.DialogMessage;
            var result = await authentication.AddAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            var account = UpdateMicrosoftAccount(result.Account, result.Session);
            Vm.Settings.ActiveAccountId = account.Id; Vm.AccountVerified = true; Vm.Save(); AccountList.SelectedItem = account;
            Vm.DialogMessage = "正版账号已验证并设为当前账号。"; Vm.Status = $"已登录 {account.Name}（正版）。";
        }
        finally { loginCancellation = null; Vm.IsAuthenticating = false; }
    });
    private void CancelLogin_Click(object sender, RoutedEventArgs e) => loginCancellation?.Cancel();
    private PlayerAccount UpdateMicrosoftAccount(PlayerAccount account, GameSession session)
    {
        var updated = account with { Name = session.Name, Uuid = session.Uuid };
        var existing = Vm.Accounts.FirstOrDefault(x => x.Id == account.Id);
        if (existing is null) Vm.Accounts.Add(updated); else Vm.Accounts[Vm.Accounts.IndexOf(existing)] = updated;
        return updated;
    }
    private void Versions_Click(object sender, RoutedEventArgs e)
    {
        if (!Vm.CanInteract) return;
        Vm.DialogMessage = ""; VersionsNav.IsChecked = true; Vm.Page = "Versions"; VersionList.SelectedItem = Vm.ActiveVersion;
    }
    private void UseVersion_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (VersionList.SelectedItem is not GameVersion version) throw new InvalidOperationException("请先选择版本。");
        Vm.Settings.SelectedVersion = version.Id; Vm.Save(); Vm.Overlay = ""; Vm.Status = version.IsSupported ? $"当前版本：{version.Id}。" : version.Detail;
        Vm.ModProjects.Clear(); Vm.ModReleases.Clear();
        HomeNav.IsChecked = true;
    });
    private async void Scan_Click(object sender, RoutedEventArgs e) => await RunAsync(ScanAsync);
    private void ImportRoot_Click(object sender, RoutedEventArgs e) { Vm.Overlay = ""; SettingsNav.IsChecked = true; BrowseRoot_Click(sender, e); }
    private void GoDownload_Click(object sender, RoutedEventArgs e) { Vm.Overlay = ""; DownloadsNav.IsChecked = true; }
    private void BrowseRoot_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var dialog = new OpenFolderDialog { Title = "选择包含 versions、libraries 和 assets 的游戏根目录" };
        if (Directory.Exists(Vm.RootInput)) dialog.InitialDirectory = Vm.RootInput;
        if (dialog.ShowDialog(this) == true) { Vm.RootInput = dialog.FolderName; Vm.Notify(nameof(Vm.RootInput)); Vm.Status = "目录已选定，点击“保存并扫描版本”后生效。"; }
    });
    private void BrowseJava_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var dialog = new OpenFileDialog { Title = "选择 64 位 Java", Filter = "Java 程序|java.exe;javaw.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) { Vm.JavaInput = dialog.FileName; Vm.Notify(nameof(Vm.JavaInput)); }
    });
    private async void DetectJava_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        Vm.Status = "正在检测本机 Java…";
        await RefreshJavaListAsync(); Vm.Status = Vm.JavaDetectionStatus;
    });
    private void Java_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ready && sender is ComboBox { SelectedItem: JavaRuntime runtime }) { Vm.JavaInput = runtime.Path; Vm.Notify(nameof(Vm.JavaInput)); }
    }
    private async void SaveSettings_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(Vm.RootInput) || !Path.IsPathFullyQualified(Vm.RootInput)) throw new InvalidOperationException("请选择完整的绝对游戏目录。");
        var root = GameRootManager.ResolveInputPath(Vm.RootInput);
        InstanceManager.ValidateGameTitle(Vm.TitleInput.Trim());
        if (!int.TryParse(Vm.DownloadThreadsInput, out var downloadThreads) || downloadThreads is < 1 or > 64) throw new InvalidOperationException("下载线程数须为 1–64 之间的整数。");
        if (!int.TryParse(Vm.MemoryInput, out var memory) || memory is < 512 or > 65536) throw new InvalidOperationException("内存须为 512–65536 MB 之间的整数。");
        if (!int.TryParse(Vm.WidthInput, out var width) || width is < 320 or > 16384 || !int.TryParse(Vm.HeightInput, out var height) || height is < 240 or > 16384) throw new InvalidOperationException("请填写有效的游戏窗口宽高（宽 320–16384，高 240–16384）。");
        if (!string.IsNullOrWhiteSpace(Vm.JavaInput)) await JavaService.InspectAsync(Vm.JavaInput);
        Directory.CreateDirectory(root);
        var probe = Path.Combine(root, ".acml-write-check-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(probe, ""); File.Delete(probe);
        var changedRoot = !GameRootManager.Same(Vm.Settings.GameRoot, root);
        GameRootManager.Activate(Vm.Settings, root, Vm.RootNameInput);
        if (changedRoot) ClearRootContext();
        Vm.Settings.JavaPath = Vm.JavaInput.Trim(); Vm.Settings.MemoryMb = memory; Vm.Settings.GameWidth = width; Vm.Settings.GameHeight = height; Vm.Settings.IsolateVersions = Vm.IsolationInput; Vm.Settings.HideOnLaunch = Vm.HideInput; Vm.Settings.DownloadSource = Vm.SourceInput; Vm.Settings.BackgroundDim = Vm.BackgroundDimInput;
        Vm.Settings.DownloadThreads = downloadThreads;
        Vm.Settings.ShowStatusBar = Vm.ShowStatusInput;
        Vm.Settings.GameWindowTitle = Vm.TitleInput.Trim();
        Vm.Save(); Vm.RefreshGameRoots(); await ScanAsync(); Vm.Status = "设置已保存。" + Vm.Status;
    });
    private void ResetSettings_Click(object sender, RoutedEventArgs e) { Vm.ResetInputs(); Vm.Status = "已恢复为上次保存的设置。"; }
    private void ChooseBackground_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (!Vm.CanInteract) return;
        var dialog = new OpenFileDialog { Title = "选择背景图片", Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        var old = Vm.Settings.BackgroundImagePath;
        var imported = BackgroundImageService.Import(dialog.FileName, Vm.Store.DirectoryPath);
        try { Vm.Settings.BackgroundImagePath = imported; Vm.Save(); Vm.ReloadBackground(); }
        catch { Vm.Settings.BackgroundImagePath = old; BackgroundImageService.RemoveManagedCopy(imported, Vm.Store.DirectoryPath); throw; }
        BackgroundImageService.RemoveManagedCopy(old, Vm.Store.DirectoryPath);
        Vm.Status = "自定义背景已保存。";
    });
    private void ResetBackground_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (!Vm.CanInteract) return;
        var old = Vm.Settings.BackgroundImagePath;
        Vm.Settings.BackgroundImagePath = null;
        try { Vm.Save(); } catch { Vm.Settings.BackgroundImagePath = old; throw; }
        Vm.ReloadBackground(); BackgroundImageService.RemoveManagedCopy(old, Vm.Store.DirectoryPath); Vm.Status = "已恢复默认背景。";
    });
    private bool catalogRefreshing;
    private async void RefreshCatalog_Click(object sender, RoutedEventArgs e) => await RefreshCatalogAsync();
    private async Task RefreshCatalogAsync()
    {
        if (catalogRefreshing) return;
        catalogRefreshing = true;
        Vm.CatalogStatus = "正在连接 " + Vm.Settings.DownloadSource + " 下载源…";
        try { var catalog = await VersionService.FetchCatalogAsync(Vm.Settings.DownloadSource, lifetime.Token); Vm.SetCatalog(catalog); }
        catch (OperationCanceledException) { Vm.CatalogStatus = "版本列表加载已取消。"; }
        catch (Exception ex) { Vm.CatalogStatus = "获取失败；可重试或切换下载源。"; AppendLog("版本列表：" + ex.Message); }
        finally { catalogRefreshing = false; }
    }
    private void LaunchMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        void Add(string title, RoutedEventHandler action) { var item = new MenuItem { Header = title }; item.Click += action; menu.Items.Add(item); }
        Add("版本管理", Versions_Click);
        Add("配置 Java 与内存", (_, _) => SettingsNav.IsChecked = true);
        Add("打开当前实例目录", (_, _) => Run(() =>
        {
            if (Vm.ActiveVersion is not GameVersion version) throw new InvalidOperationException("请先选择一个游戏版本。");
            var folder = InstanceManager.GameDirectory(Vm.Settings, version.Id);
            Directory.CreateDirectory(folder); OpenFolder(folder);
        }));
        Add("查看启动日志", Logs_Click);
        menu.PlacementTarget = (Button)sender; menu.IsOpen = true;
    }
    private void Logs_Click(object sender, RoutedEventArgs e) { Vm.Overlay = "Logs"; }
    private static void OpenFolder(string path) => Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { Path.GetFullPath(path) }, UseShellExecute = false });
    private void OpenData_Click(object sender, RoutedEventArgs e) => Run(() => { Directory.CreateDirectory(Vm.Store.DirectoryPath); OpenFolder(Vm.Store.DirectoryPath); });
    private void OpenLogs_Click(object sender, RoutedEventArgs e) => Run(() => { Directory.CreateDirectory(LogDirectory); OpenFolder(LogDirectory); });
    private static void OpenSite(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    private void CurseForge_Click(object sender, RoutedEventArgs e) => Run(() => OpenSite("https://www.curseforge.com/minecraft/search?class=mc-mods"));
    private void Modrinth_Click(object sender, RoutedEventArgs e) => Run(() => OpenSite("https://modrinth.com/mods"));
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!ready) return;
        FlushInstanceSettings();
        if (Vm.GameRunning || Vm.Busy) { e.Cancel = true; Vm.Status = "任务或游戏仍在运行，启动器已最小化；完成后可关闭。"; WindowState = WindowState.Minimized; return; }
        lifetime.Cancel(); authentication.Dispose(); downloadsClient.Dispose();
    }
}

