using ACML.Core;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace ACML.App;
public partial class MainWindow
{
    private int notificationSequence;
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetWindowText(IntPtr window, string title);
    private async Task ApplyGameTitleAsync(GameRun run, string title, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        InstanceManager.ValidateGameTitle(title); var process = run.Process!;
        try
        {
            while (!token.IsCancellationRequested)
            {
                process.Refresh(); if (process.HasExited) return;
                if (process.MainWindowHandle != IntPtr.Zero && process.MainWindowTitle != title) SetWindowText(process.MainWindowHandle, title);
                await Task.Delay(1000, token);
            }
        }
        catch (OperationCanceledException) { } catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception ex) { AppendGameLog(run, "设置游戏标题失败：" + ex.Message); }
    }
    private void LaunchStep(string stage, double percent, bool indeterminate = false)
    { Vm.LaunchStage = stage; Vm.LaunchProgress = percent; Vm.LaunchIndeterminate = indeterminate; Vm.Status = stage; }
    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (!Vm.CanInteract) return;
        if (Vm.ActiveAccount is null) { Accounts_Click(sender, e); Vm.DialogMessage = "请先添加或选择当前账号。"; return; }
        if (Vm.ActiveVersion is null) { Versions_Click(sender, e); return; }
        var version = Vm.ActiveVersion; var account = Vm.ActiveAccount;
        // Capture settings before another session or a root switch can change them.
        var global = System.Text.Json.JsonSerializer.Deserialize<LauncherSettings>(System.Text.Json.JsonSerializer.Serialize(Vm.Settings))!;
        using var run = new GameRun(version, global, lifetime.Token);
        lock (logLock) gameRuns.Add(run); preparingGame = run;
        var cancellation = run.Cancellation; LaunchPlan? plan = null; var readyWindow = false;
        Task stdout = Task.CompletedTask, stderr = Task.CompletedTask;
        using var titleCancellation = new CancellationTokenSource(); Task titleTask = Task.CompletedTask;
        Vm.Busy = true; Vm.Notification = ""; Vm.DialogMessage = ""; Vm.LaunchVersion = version.Id; Vm.Overlay = "Launching";
        LaunchStep("准备启动", 0);
        try
        {
            FlushInstanceSettings();
            Directory.CreateDirectory(LogDirectory);
            run.LogPath = Path.Combine(LogDirectory, $"game-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
            File.WriteAllText(run.LogPath, ""); Vm.LaunchLogPath = run.LogPath;
            var settings = InstanceManager.EffectiveSettings(global, version.Id);
            run.GameLogPath = Path.Combine(InstanceManager.GameDirectory(settings, version.Id), "logs", "latest.log"); Vm.GameLogPath = run.GameLogPath;
            AppendGameLog(run, "开始启动 " + version.Id + "；游戏根目录：" + settings.GameRoot);
            GameSession? session = null; LaunchStep("验证账号", 5, true);
            if (account.IsMicrosoft)
            {
                Vm.AccountVerified = false; session = await authentication.RefreshAsync(account, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                account = UpdateMicrosoftAccount(account, session); Vm.AccountVerified = true; Vm.Save(); run.Token = session.AccessToken;
            }
            LaunchStep("检查 Java", 15, true);
            var java = await EnsureJavaAsync(settings, version, cancellation.Token); run.JavaDescription = java.ToString();
            LaunchStep("检查版本运行目录", 32, true);
            await Task.Run(() => InstanceManager.PrepareGameDirectory(settings, version.Id, cancellation.Token));
            await LaunchResources.EnsureLoggingAsync(downloadsClient, settings, version.Id,
                new Progress<DownloadProgress>(update => { if (ReferenceEquals(preparingGame, run) && Vm.LaunchingVisible) LaunchStep(update.Label, 33); }), cancellation.Token);
            LaunchStep("验证游戏文件", 35);
            var progress = new Progress<GamePreparationProgress>(update =>
            { if (ReferenceEquals(preparingGame, run) && Vm.LaunchingVisible && run.Process is null) LaunchStep(update.Stage, 35 + update.Percent * 0.45); });
            plan = await Task.Run(() => GameLauncher.Prepare(settings, account, version, java, session, progress, cancellation.Token));
            cancellation.Token.ThrowIfCancellationRequested();
            run.GameLogPath = Path.Combine(plan.GameDirectory, "logs", "latest.log"); Vm.GameLogPath = run.GameLogPath;
            foreach (var other in gameRuns.Where(x => x != run && x.Process is not null && string.Equals(x.GameLogPath, run.GameLogPath, StringComparison.OrdinalIgnoreCase))) { run.SharedDirectory = true; other.SharedDirectory = true; }
            AppendGameLog(run, $"启动 {version.Id}；Java {java.Major}；内存 {settings.MemoryMb} MB。");
            LaunchStep("创建游戏进程", 82);
            var process = new Process { StartInfo = plan.StartInfo }; run.Process = process;
            if (!process.Start()) throw new InvalidOperationException("未能创建游戏进程。");
            RefreshRunningGames(); AppendGameLog(run, "游戏进程 PID " + process.Id);
            titleTask = ApplyGameTitleAsync(run, settings.GameWindowTitle, titleCancellation.Token);
            stdout = ReadGameOutputAsync(run, process.StandardOutput); stderr = ReadGameOutputAsync(run, process.StandardError);
            LaunchStep("等待游戏窗口", 90, true); string? startupError = null;
            try
            {
                await GameStartupMonitor.WaitForWindowAsync(() =>
                {
                    process.Refresh(); return process.HasExited ? new(true, process.ExitCode, false) : new(false, 0, process.MainWindowHandle != IntPtr.Zero);
                }, cancellation.Token);
                readyWindow = true; LaunchStep("启动成功", 100); ReleasePreparation(run); NotifyLaunch("启动成功 · " + version.Id);
                if (settings.HideOnLaunch) { await Task.Delay(2000); if (preparingGame is null && !process.HasExited) WindowState = WindowState.Minimized; }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                run.StopRequested = true;
                try { await StopGameRunAsync(run); } catch (Exception ex) { ShowLaunchFailure(run, "结束游戏失败", ex.Message); }
            }
            catch (Exception ex) { startupError = ex.Message; if(!run.StopRequested&&!cancellation.IsCancellationRequested)ShowLaunchFailure(run, "启动失败", ex.Message); }
            ReleasePreparation(run);
            await process.WaitForExitAsync(); await Task.WhenAll(stdout, stderr);
            var code = process.ExitCode; AppendGameLog(run, "游戏进程退出，代码 " + code + "。");
            if (GameExitClassifier.Classify(run.StopRequested,readyWindow,code)==GameExitStatus.Stopped) ShowStoppedGame(run);
            else if (!readyWindow) ShowLaunchFailure(run, "启动失败", startupError ?? $"游戏在窗口出现前退出，退出码 {code}。");
            else if (code != 0) ShowLaunchFailure(run, "游戏异常退出", $"游戏进程退出，代码 {code}。请查看下方日志。");
            else NotifyLaunch("游戏已退出 · " + version.Id);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { if(run.Process is not null)ShowStoppedGame(run);else NotifyLaunch("启动已取消"); }
        catch (Exception ex) when (run.StopRequested) { AppendGameLog(run,"结束游戏时的输出收尾："+ex.Message);ShowStoppedGame(run); }
        catch (Exception ex) { ShowLaunchFailure(run, "启动失败", ex.Message); }
        finally
        {
            if (run.Process is Process process)
            {
                try { if (!process.HasExited) { ReleasePreparation(run); await process.WaitForExitAsync(); } await Task.WhenAll(stdout, stderr); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { AppendGameLog(run,"进程 / 输出收尾："+ex.Message); }
            }
            if (run.StopTask is { IsCompleted: false }) { try { await run.StopTask; } catch (Exception ex) { AppendGameLog(run, "结束游戏：" + ex.Message); } }
            titleCancellation.Cancel(); await titleTask;
            lock (logLock) gameRuns.Remove(run); RefreshRunningGames(); ReleasePreparation(run);
            if (plan is not null) GameLauncher.CleanupNatives(plan.NativeDirectory);
        }
    }
    private void CancelLaunch_Click(object sender, RoutedEventArgs e)
    { preparingGame?.Cancellation.Cancel(); Vm.LaunchStage = "正在取消启动…"; }
    private void ShowStoppedGame(GameRun run)
    {
        if(ReferenceEquals(failedGame,run)&&Vm.LaunchFailureVisible){Vm.Overlay="";failedGame=null;}
        NotifyLaunch("已结束当前运行中的Minecraft进程");
    }
    private Task StopGameRunAsync(GameRun run)
    {
        if (run.StopTask is not null) return run.StopTask;
        run.StopRequested = true; run.Cancellation.Cancel(); return run.StopTask = StopGameRunCoreAsync(run);
    }
    private async Task StopGameRunCoreAsync(GameRun run)
    {
        var process = run.Process; if (process is null || process.HasExited) return;
        stoppingGames++; RefreshRunningGames();
        try
        {
            AppendGameLog(run, "用户请求结束本次游戏进程，PID " + process.Id + "。");
            if (process.CloseMainWindow()) await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(10000));
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
        finally { stoppingGames--; RefreshRunningGames(); }
    }
    private async Task EndGamesAsync(GameRun[] runs)
    {
        try { await Task.WhenAll(runs.Where(gameRuns.Contains).Select(StopGameRunAsync)); }
        catch (Exception ex) { Report(ex); NotifyLaunch("结束游戏失败：" + ex.Message); }
    }
    private async void StopGame_Click(object sender, RoutedEventArgs e)
    {
        if (!Vm.CanStopGame) return;
        var runs = gameRuns.Where(x => x.Process is not null).ToArray();
        if (runs.Length == 1) { await EndGamesAsync(runs); return; }
        var menu = new ContextMenu();
        foreach (var run in runs)
        {
            var item = new MenuItem { Header = "结束 " + run.Version.Id + " · PID " + run.Process!.Id };
            item.Click += async (_, _) => await EndGamesAsync([run]); menu.Items.Add(item);
        }
        menu.Items.Add(new Separator()); var all = new MenuItem { Header = $"结束全部 Minecraft（{runs.Length}）" };
        all.Click += async (_, _) => await EndGamesAsync(runs); menu.Items.Add(all);
        menu.PlacementTarget = sender as FrameworkElement; menu.IsOpen = true;
    }
    private async Task ReadGameOutputAsync(GameRun run, StreamReader reader)
    {
        try { while (await reader.ReadLineAsync().ConfigureAwait(false) is string line) AppendGameLog(run, line); }
        catch (IOException ex) { AppendGameLog(run, "读取游戏输出：" + ex.Message); }
    }
    private void ShowLaunchFailure(GameRun run, string title, string message)
    {
        if(run.StopRequested){AppendGameLog(run,"用户主动结束，跳过启动 / 退出错误报告："+message);return;}
        message = RedactLog(message); AppendGameLog(run, title + "：" + message);
        if (preparingGame is not null && preparingGame != run)
        { if (pendingGameFailures.Count < 10) pendingGameFailures.Enqueue((run, title, message)); return; }
        failedGame = run;
        Vm.LaunchVersion = run.Version.Id; Vm.LaunchLogPath = run.LogPath; Vm.GameLogPath = run.GameLogPath;
        Vm.Notification = ""; Vm.DialogMessage = ""; Vm.FailureTitle = title + " · " + run.Version.Id;
        Vm.FailureAnalysis = LaunchDiagnostics.Analyze(message + "\n" + DiagnosticLogs(run), run.Version.JavaMajor);
        lock (logLock)
        {
            var details = message + "\n\n" + string.Join(Environment.NewLine, run.Tail.TakeLast(45));
            Vm.FailureDetails = details.Length > 14000 ? message + "\n\n…\n" + details[^12000..] : details;
        }
        Vm.Status = title + "：" + message; Vm.Overlay = "LaunchFailure"; WindowState = WindowState.Normal; Activate();
    }
    private async void NotifyLaunch(string message)
    {
        var sequence = ++notificationSequence; Vm.Notification = message; Vm.Status = message;
        try { await Task.Delay(TimeSpan.FromSeconds(6), lifetime.Token); if (sequence == notificationSequence) Vm.Notification = ""; } catch (OperationCanceledException) { }
    }
    private void OpenLaunchLog_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (!File.Exists(Vm.LaunchLogPath)) throw new FileNotFoundException("本次启动日志尚未生成。");
        Process.Start(new ProcessStartInfo(Vm.LaunchLogPath) { UseShellExecute = true });
    });
    private void OpenGameLogFolder_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var folder = Path.GetDirectoryName(Vm.GameLogPath); OpenFolder(folder is not null && Directory.Exists(folder) ? folder : LogDirectory);
    });
}
