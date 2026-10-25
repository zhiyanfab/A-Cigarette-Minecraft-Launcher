using ACML.Core;
using Microsoft.Win32;
using System.IO;
using System.Windows;

namespace ACML.App;

public partial class MainWindow
{
    private void ManageVersion_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (!Vm.CanInteract) return;
        var version = VersionList.SelectedItem as GameVersion ?? Vm.ActiveVersion ?? throw new InvalidOperationException("请选择要管理的版本。");
        LoadManagedVersion(version);
        Vm.DialogMessage = ""; Vm.Overlay = ""; Vm.Page = "Instance";
    });
    private void LoadManagedVersion(GameVersion version)
    {
        FlushInstanceSettings(); loadingManagedVersion = true;
        try {
        var settings = InstanceManager.LoadSettings(Vm.Settings, version.Id);
        Vm.ManagedVersion = version; Vm.ManagedNameInput = version.Id; Vm.ManagedSettingsEnabled = settings.Enabled;
        Vm.ManagedJavaInput = settings.JavaPath; Vm.ManagedMemoryInput = settings.MemoryMb.ToString();
        Vm.ManagedWidthInput = settings.GameWidth.ToString(); Vm.ManagedHeightInput = settings.GameHeight.ToString(); Vm.ManagedIsolationInput = settings.Isolate;
        var effective = InstanceManager.EffectiveSettings(Vm.Settings, version.Id);
        if (!settings.OverrideJava) Vm.ManagedJavaInput = effective.JavaPath;
        if (!settings.OverrideMemory) Vm.ManagedMemoryInput = effective.MemoryMb.ToString();
        if (!settings.OverrideWindowSize) { Vm.ManagedWidthInput = effective.GameWidth.ToString(); Vm.ManagedHeightInput = effective.GameHeight.ToString(); }
        if (!settings.OverrideIsolation) Vm.ManagedIsolationInput = effective.IsolateVersions;
        Vm.ManagedTitleInput = settings.OverrideTitle ? settings.GameWindowTitle : effective.GameWindowTitle;
        Vm.ManagedUseGlobalJava = !settings.OverrideJava; Vm.ManagedUseGlobalMemory = !settings.OverrideMemory;
        Vm.ManagedUseGlobalTitle = !settings.OverrideTitle; Vm.ManagedUseGlobalIsolation = !settings.OverrideIsolation; Vm.ManagedUseGlobalWindow = !settings.OverrideWindowSize;
        Vm.Notify(nameof(Vm.ManagedTitleInput));
        foreach (var name in new[] { nameof(Vm.ManagedVersion), nameof(Vm.ManagedNameInput), nameof(Vm.ManagedSettingsEnabled), nameof(Vm.ManagedJavaInput), nameof(Vm.ManagedMemoryInput), nameof(Vm.ManagedWidthInput), nameof(Vm.ManagedHeightInput), nameof(Vm.ManagedIsolationInput), nameof(Vm.ManagedDirectoryLabel) }) Vm.Notify(name);
        RefreshManagedMods(); Vm.InstanceSaveStatus = "";
        } finally { loadingManagedVersion = false; }
    }
    private void RefreshManagedMods()
    {
        Vm.InstalledMods.Clear();
        if (Vm.ManagedVersion is not GameVersion version) return;
        foreach (var mod in InstanceManager.ListMods(Vm.Settings, version.Id)) Vm.InstalledMods.Add(mod);
    }
    private InstanceSettings ReadManagedSettings()
    {
        if (!int.TryParse(Vm.ManagedMemoryInput, out var memory) || !int.TryParse(Vm.ManagedWidthInput, out var width) || !int.TryParse(Vm.ManagedHeightInput, out var height)) throw new InvalidOperationException("请填写整数形式的内存和窗口尺寸。");
        return new InstanceSettings { Enabled = Vm.ManagedSettingsEnabled, JavaPath = Vm.ManagedJavaInput.Trim(), MemoryMb = memory, GameWidth = width, GameHeight = height, Isolate = Vm.ManagedIsolationInput,
            GameWindowTitle = Vm.ManagedTitleInput.Trim(), OverrideJava = !Vm.ManagedUseGlobalJava, OverrideMemory = !Vm.ManagedUseGlobalMemory,
            OverrideTitle = !Vm.ManagedUseGlobalTitle, OverrideIsolation = !Vm.ManagedUseGlobalIsolation, OverrideWindowSize = !Vm.ManagedUseGlobalWindow };
    }
    private void SaveInstanceSettings_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (!Vm.CanInteract || Vm.ManagedVersion is not GameVersion version) return;
        InstanceManager.SaveSettings(Vm.Settings.GameRoot, version.Id, ReadManagedSettings());
        Vm.Notify(nameof(Vm.ManagedDirectoryLabel)); RefreshManagedMods(); Vm.DialogMessage = "版本设置已保存。";
    });
    private void BrowseInstanceJava_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var dialog = new OpenFileDialog { Title = "选择版本专用 Java", Filter = "Java|java.exe;javaw.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) { Vm.ManagedJavaInput = dialog.FileName; Vm.Notify(nameof(Vm.ManagedJavaInput)); }
    });
    private async void RenameVersion_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        FlushInstanceSettings();
        var version = Vm.ManagedVersion ?? throw new InvalidOperationException("请选择版本。");
        if (IsVersionRunning(Vm.Settings.GameRoot, version.Id)) throw new InvalidOperationException("此版本仍有游戏进程运行，请结束该版本的进程后再更名。");
        var newId = Vm.ManagedNameInput.Trim(); var selected = Vm.Settings.SelectedVersion;
        try
        {
            InstanceManager.Rename(Vm.Settings.GameRoot, version.Id, newId, () =>
            {
                if (selected == version.Id) Vm.Settings.SelectedVersion = newId;
                Vm.Save();
            });
        }
        catch { Vm.Settings.SelectedVersion = selected; throw; }
        await ScanAsync();
        LoadManagedVersion(Vm.Versions.First(x => x.Id == newId));
        VersionList.SelectedItem = Vm.ManagedVersion;
        Vm.DialogMessage = "版本目录、JSON 和 JAR 名称已同步。";
    });
    private void RefreshInstanceMods_Click(object sender, RoutedEventArgs e) => Run(RefreshManagedMods);
    private void ToggleInstanceMod_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (!Vm.CanInteract || Vm.ManagedVersion is not GameVersion version) return;
        if (InstalledModsList.SelectedItem is not LocalModFile mod) throw new InvalidOperationException("请选择模组。");
        InstanceManager.ToggleMod(Vm.Settings, version.Id, mod); RefreshManagedMods(); Vm.DialogMessage = mod.Enabled ? "模组已停用。" : "模组已启用。";
    });
    private void OpenInstanceMods_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (Vm.ManagedVersion is not GameVersion version) return;
        var directory = Path.Combine(InstanceManager.GameDirectory(Vm.Settings, version.Id), "mods");
        Directory.CreateDirectory(directory); OpenFolder(directory);
    });
    private void DownloadInstanceMods_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (!Vm.CanInteract || Vm.ManagedVersion is not GameVersion version) return;
        Vm.Settings.SelectedVersion = version.Id; Vm.Save(); Vm.ModProjects.Clear(); Vm.ModReleases.Clear();
        Vm.Overlay = ""; DownloadsNav.IsChecked = true; ModsTab.IsSelected = true;
    });
    private async Task<JavaRuntime> EnsureJavaAsync(LauncherSettings settings, GameVersion version, CancellationToken launchToken = default)
    {
        if (!version.IsSupported) throw new InvalidOperationException(version.Detail);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, launchToken);
        downloadCancellation = cancellation; Vm.IsDownloading = true; Vm.DownloadPercent = 0;
        Vm.DownloadText = "正在检查 Java…";
        var progress = new Progress<DownloadProgress>(update =>
        {
            if (!Vm.IsDownloading) return; Vm.DownloadPercent = update.Percent; Vm.DownloadText = update.Label; Vm.Status = update.Label;
            if (Vm.LaunchingVisible) LaunchStep(update.Label, 15 + update.Percent * 0.2);
        });
        try
        {
            var preferred = await ConfirmJavaSwitchAsync(settings, version, cancellation.Token);
            var runtime = await new JavaInstaller(downloadsClient, Vm.Settings.DownloadThreads).EnsureAsync(settings.GameRoot, version.JavaMajor, preferred, progress, cancellation.Token);
            RememberJava(runtime);
            settings.JavaPath = runtime.Path;
            var local = InstanceManager.LoadSettings(Vm.Settings, version.Id);
            if (local.Enabled && local.OverrideJava) { local.JavaPath = runtime.Path; InstanceManager.SaveSettings(settings.GameRoot, version.Id, local); }
            else { Vm.Settings.JavaPath = runtime.Path; Vm.Save(); Vm.ResetInputs(); }
            return runtime;
        }
        finally { downloadCancellation = null; Vm.IsDownloading = false; if (Vm.TasksVisible) Vm.Overlay = ""; }
    }
}


