using ACML.App;
using ACML.Core;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using System.Windows;

namespace ACML.WindowsTests;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var root = Path.Combine(Path.GetTempPath(), "acml-windows-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("ACML_DEMO_DATA_DIR", root);
        var registryPath = @"Software\ACML\TestAccounts\" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root))));
        var application = new ACML.App.App(); application.InitializeComponent(); application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var failed = 0; var passed = 0;
        void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
        void Check(string name, Action action) { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); } }
        try
        {
            Check("Launcher window constructs with the new packaged icon, loader images and mod browser resources", () =>
            {
                var window = new MainWindow();
                Assert(window.Icon is not null);
                var converter = new LoaderIconConverter();
                foreach (var loader in new[] { "原版", "Fabric", "Forge", "NeoForge" }) Assert(converter.Convert(loader, typeof(System.Windows.Media.ImageSource), null!, System.Globalization.CultureInfo.InvariantCulture) is System.Windows.Media.ImageSource);
                window.Close();
            });
            Check("Catalog refresh preserves a selected visible version instead of replacing the install target", () =>
            {
                var vm = new LauncherViewModel();
                var latest = new RemoteVersion("1.21.1", "release", DateTimeOffset.UtcNow, "https://example.com/latest");
                var selected = new RemoteVersion("1.20.1", "release", DateTimeOffset.UtcNow, "https://example.com/selected");
                vm.SetCatalog(new(latest.Id, latest.Id, [latest, selected], "test"));
                vm.SelectedRemoteVersion = selected;
                vm.SetCatalog(new(latest.Id, latest.Id, [latest, selected], "refreshed"));
                Assert(vm.SelectedRemoteVersion?.Id == selected.Id);
            });
            Check("Release title and game root selectors reflect the active registered directory", () =>
            {
                var vm = new LauncherViewModel(); var original = vm.Settings.GameRoot;
                Assert(vm.WindowTitle == "ACML · 正式版 v2.0.1" && LauncherInfo.Version == "2.0.1" && vm.GameRoots.Count == 1);
                Assert(vm.ModProviders.SequenceEqual(new[]{"Modrinth"}));
                GameRootManager.Activate(vm.Settings, Path.Combine(root, "second-game-root"), "Second library"); vm.RefreshGameRoots();
                Assert(vm.GameRoots.Count == 2 && vm.SelectedGameRoot?.Name == "Second library" && vm.RootInput == vm.Settings.GameRoot);
                GameRootManager.Activate(vm.Settings, original); vm.RefreshGameRoots();
                Assert(vm.GameRootLabel == original && vm.SelectedGameRoot?.Path == original);
            });
            Check("Empty version state stays accessible and status hints default off while download tasks remain visible", () =>
            {
                var vm = new LauncherViewModel(); vm.Page = "Versions";
                Assert(vm.VersionManagementVisible && vm.NoVersions && !vm.ShowStatusInput && !vm.StatusBarVisible);
                vm.Versions.Add(new("test", "release", "原版", 17, null)); Assert(!vm.NoVersions);
                vm.Versions.Clear(); Assert(vm.NoVersions);
                vm.IsDownloading = true; Assert(vm.StatusBarVisible && !vm.ShowStatusInput);
                vm.IsDownloading = false; vm.ShowStatusInput = true; Assert(vm.StatusBarVisible);
                vm.Settings.ShowStatusBar = true; vm.Save();
                var reloaded = new LauncherViewModel(); Assert(reloaded.ShowStatusInput && reloaded.StatusBarVisible);
                reloaded.Settings.ShowStatusBar = false; reloaded.Save();
            });
            Check("Launch progress, failure details and success notices remain visible independently of the status bar", () =>
            {
                var vm = new LauncherViewModel(); vm.ShowStatusInput = false; vm.Busy = true; vm.Overlay = "Launching";
                vm.LaunchProgress = 35; vm.LaunchStage = "验证游戏文件";
                Assert(vm.LaunchingVisible && vm.OverlayVisible && !vm.CanInteract && !vm.StatusBarVisible);
                vm.Overlay = ""; vm.Busy = false; vm.GameRunning = true; vm.Notification = "启动成功";
                Assert(!vm.LaunchingVisible && vm.HasNotification && vm.CanStopGame && vm.CanInteract && vm.LaunchText == "启动游戏");
                vm.RunningGameCount = 2; Assert(vm.CanInteract && vm.StopGameText.Contains("2"));
                vm.StoppingGame = true; Assert(!vm.CanStopGame); vm.StoppingGame = false;
                vm.GameRunning = false; vm.FailureDetails = "退出码 1"; vm.Overlay = "LaunchFailure";
                Assert(vm.LaunchFailureVisible && vm.CanInteract && !vm.CanStopGame);
            });
            Check("Inline instance settings automatically persist valid edits and reject incomplete numeric edits", () =>
            {
                var window = new MainWindow(); var vm = (LauncherViewModel)window.DataContext;
                vm.Settings.GameRoot = Path.Combine(root, "autosave-game");
                void Call(string name, params object[] values) => typeof(MainWindow).GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, values);
                Call("LoadManagedVersion", new GameVersion("test", "release", "原版", 17, null)); vm.Page = "Instance";
                Assert(vm.InstanceVisible && !vm.OverlayVisible && !vm.ManagedSettingsEnabled && vm.ManagedUseGlobalJava && vm.ManagedUseGlobalMemory);
                vm.ManagedSettingsEnabled = true; vm.ManagedUseGlobalMemory = false; vm.ManagedMemoryInput = "3072";
                vm.ManagedUseGlobalTitle = false; vm.ManagedTitleInput = "Independent title"; Call("FlushInstanceSettings");
                var actual = InstanceManager.EffectiveSettings(vm.Settings, "test");
                Assert(actual.MemoryMb == 3072 && actual.GameWindowTitle == "Independent title" && vm.InstanceSaveStatus == "已自动保存");
                vm.ManagedMemoryInput = ""; Call("FlushInstanceSettings"); Assert(InstanceManager.EffectiveSettings(vm.Settings, "test").MemoryMb == 3072);
                vm.ManagedMemoryInput = "4096"; Call("FlushInstanceSettings"); Assert(InstanceManager.EffectiveSettings(vm.Settings, "test").MemoryMb == 4096);
                window.Close();
            });
            Check("Pending instance autosave retains its original root and version when the current root changes", () =>
            {
                var window = new MainWindow(); var vm = (LauncherViewModel)window.DataContext; var originalRoot = Path.Combine(root, "pending-save-root");
                vm.Settings.GameRoot = originalRoot;
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                typeof(MainWindow).GetMethod("LoadManagedVersion", flags)!.Invoke(window, [new GameVersion("pending", "release", "原版", 17, null)]);
                vm.ManagedSettingsEnabled = true; vm.ManagedUseGlobalMemory = false; vm.ManagedMemoryInput = "2560";
                vm.Settings.GameRoot = Path.Combine(root, "new-root"); typeof(MainWindow).GetMethod("FlushInstanceSettings", flags)!.Invoke(window, null);
                Assert(InstanceManager.LoadSettings(new() { GameRoot = originalRoot }, "pending").MemoryMb == 2560 && !InstanceManager.LoadSettings(vm.Settings, "pending").Enabled);
                window.Close();
            });
            Check("Completing an older game session cannot unlock or close a newer startup's progress UI", () =>
            {
                var window = new MainWindow(); var vm = (LauncherViewModel)window.DataContext;
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var type = typeof(MainWindow).GetNestedType("GameRun", System.Reflection.BindingFlags.NonPublic)!;
                object Run(string id) => Activator.CreateInstance(type, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, [new GameVersion(id, "release", "原版", 17, null), new LauncherSettings(), CancellationToken.None], null)!;
                var older = Run("first"); var newer = Run("second");
                typeof(MainWindow).GetField("preparingGame", flags)!.SetValue(window, newer); vm.Busy = true; vm.Overlay = "Launching";
                var release = typeof(MainWindow).GetMethod("ReleasePreparation", flags)!; release.Invoke(window, [older]);
                Assert(vm.Busy && vm.LaunchingVisible && !vm.CanInteract);
                release.Invoke(window, [newer]); Assert(!vm.Busy && !vm.LaunchingVisible && vm.CanInteract && vm.LaunchText == "启动游戏");
                ((IDisposable)older).Dispose(); ((IDisposable)newer).Dispose(); window.Close();
            });
            Check("Background game output writes its own log and dispatches UI updates without thread-affinity failures", () =>
            {
                var window = new MainWindow(); var vm = (LauncherViewModel)window.DataContext;
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var type = typeof(MainWindow).GetNestedType("GameRun", System.Reflection.BindingFlags.NonPublic)!;
                var run = Activator.CreateInstance(type, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, [new GameVersion("background", "release", "原版", 17, null), new LauncherSettings(), CancellationToken.None], null)!;
                var path = Path.Combine(root,"background-output.log"); type.GetProperty("LogPath")!.SetValue(run,path);
                Task.Run(()=>typeof(MainWindow).GetMethod("AppendGameLog",flags)!.Invoke(window,[run,"background output test"])).GetAwaiter().GetResult();
                window.Dispatcher.Invoke(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert(File.ReadAllText(path).Contains("background output test") && vm.Logs.Contains("background output test"));
                ((IDisposable)run).Dispose();window.Close();
            });
            Check("Ending one owned process preserves another and expected forced exits never open an error report", () =>
            {
                var window=new MainWindow();var vm=(LauncherViewModel)window.DataContext;
                var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
                var type=typeof(MainWindow).GetNestedType("GameRun",System.Reflection.BindingFlags.NonPublic)!;
                var list=(System.Collections.IList)typeof(MainWindow).GetField("gameRuns",flags)!.GetValue(window)!;
                object Run(string id,System.Diagnostics.Process process)
                {
                    var value=Activator.CreateInstance(type,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic,null,[new GameVersion(id,"release","原版",17,null),new LauncherSettings(),CancellationToken.None],null)!;
                    type.GetProperty("Process")!.SetValue(value,process);list.Add(value);return value;
                }
                var command=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe");
                System.Diagnostics.Process Start()=>System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(command){Arguments="-NoLogo -NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 120\"",UseShellExecute=false,CreateNoWindow=true})!;
                using var first=Start();using var second=Start();var a=Run("first",first);var b=Run("second",second);
                void Stop(object run)
                {
                    var task=(Task)typeof(MainWindow).GetMethod("StopGameRunAsync",flags)!.Invoke(window,[run])!;
                    if(!task.IsCompleted){var frame=new System.Windows.Threading.DispatcherFrame();task.ContinueWith(_=>window.Dispatcher.BeginInvoke(()=>frame.Continue=false));System.Windows.Threading.Dispatcher.PushFrame(frame);}
                    task.GetAwaiter().GetResult();
                }
                try
                {
                    typeof(MainWindow).GetMethod("RefreshRunningGames",flags)!.Invoke(window,null);Assert(vm.RunningGameCount==2&&vm.CanInteract);
                    Stop(a);Assert(first.HasExited&&!second.HasExited);
                    typeof(MainWindow).GetMethod("ShowLaunchFailure",flags)!.Invoke(window,[a,"启动失败","forced termination"]);
                    Assert(!vm.LaunchFailureVisible);Stop(b);Assert(second.HasExited);
                }
                finally
                {
                    if(!first.HasExited)first.Kill(true);if(!second.HasExited)second.Kill(true);
                    list.Clear();((IDisposable)a).Dispose();((IDisposable)b).Dispose();typeof(MainWindow).GetMethod("RefreshRunningGames",flags)!.Invoke(window,null);window.Close();
                }
            });
            var source = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/ACML.App/Assets/logo-original.png"));
            Check("Background import decodes and copies original image", () =>
            {
                var imported = BackgroundImageService.Import(source, root);
                Assert(imported != source && File.ReadAllBytes(source).SequenceEqual(File.ReadAllBytes(imported)));
                var brush = BackgroundImageService.Load(imported); Assert(brush.IsFrozen && brush.ImageSource.IsFrozen);
                File.Move(imported, imported + ".moved"); Assert(brush.ImageSource.Width > 0); File.Move(imported + ".moved", imported);
                BackgroundImageService.RemoveManagedCopy(source, root); Assert(File.Exists(source));
                BackgroundImageService.RemoveManagedCopy(imported, root); Assert(!File.Exists(imported));
            });
            Check("Invalid image fails before changing persisted background", () =>
            {
                var invalid = Path.Combine(root, "invalid.png"); File.WriteAllText(invalid, "invalid image");
                try { BackgroundImageService.Import(invalid, root); throw new Exception("Expected rejection"); }
                catch (Exception ex) when (ex is FileFormatException or NotSupportedException or ArgumentException) { }
            });
            Check("Default packaged forest brush loads without external assets", () => Assert(BackgroundImageService.DefaultBrush().IsFrozen));
            Check("Registry stores DPAPI ciphertext and can restore a session", () =>
            {
                var store = new RegistryTokenStore(root);
                var session = new GameSession { Name = "DemoPlayer", Uuid = "41724ea1c3c233aa9af9a2648e7e7e53", AccessToken = "mock-token-never-plaintext", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
                store.SaveSession("mock", session);
                using var key = Registry.CurrentUser.OpenSubKey(registryPath);
                var raw = (byte[])key!.GetValue("session-mock")!;
                Assert(key.GetValueKind("session-mock") == RegistryValueKind.Binary && !Encoding.UTF8.GetString(raw).Contains(session.AccessToken));
                Assert(store.ReadSession("mock")!.AccessToken == session.AccessToken);
                store.Delete("session-mock"); Assert(store.ReadSession("mock") is null);
            });
            Check("Tampered credential ciphertext is rejected", () =>
            {
                var store = new RegistryTokenStore(root); store.Write("mock-cache", Encoding.UTF8.GetBytes("mock-msal-cache"));
                using var key = Registry.CurrentUser.OpenSubKey(registryPath, true);
                var data = (byte[])key!.GetValue("mock-cache")!; data[^1] ^= 0xff; key.SetValue("mock-cache", data, RegistryValueKind.Binary);
                try { store.Read("mock-cache"); throw new Exception("Expected rejection"); } catch (InvalidOperationException) { }
                store.Delete("mock-cache");
            });
            Check("Missing saved background falls back to embedded default", () =>
            {
                new SettingsStore(root).Save(new LauncherSettings { BackgroundImagePath = Path.Combine(root, "missing.png") });
                var vm = new LauncherViewModel(); Assert(vm.HomeBackground is not null && vm.BackgroundName.Contains("丢失"));
                vm.BackgroundDimInput = double.NaN; Assert(vm.BackgroundDimInput == 0.15);
            });
        }
        finally
        {
            application.Shutdown();
            // Both targets were generated above for this exact test run.
            Registry.CurrentUser.DeleteSubKeyTree(registryPath, false);
            if (Path.GetFileName(root).StartsWith("acml-windows-tests-", StringComparison.Ordinal)) Directory.Delete(root, true);
        }
        Console.WriteLine($"RESULT {passed} passed, {failed} failed"); return failed == 0 ? 0 : 1;
    }
}
