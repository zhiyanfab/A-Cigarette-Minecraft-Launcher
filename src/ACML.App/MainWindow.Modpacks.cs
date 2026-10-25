using ACML.Core;
using Microsoft.Win32;
using System.IO;
using System.Windows;

namespace ACML.App;
public partial class MainWindow
{
    private GameVersion? exportingVersion;
    private static IModProvider? CursePackProvider() => null;
    private void OpenExportPack(GameVersion version)
    {
        FlushInstanceSettings(); exportingVersion = version; Vm.PackIsImport = false; Vm.PackIdInput = version.Id;
        Vm.PackSummary = "导出版本：" + version.Id; Vm.PackIncludeSaves = false; Vm.DialogMessage = ""; Vm.Overlay = "Pack";
        Vm.Notify(nameof(Vm.PackIdInput));
    }
    private void ImportPack_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (!Vm.CanInteract) return;
        var dialog = new OpenFileDialog { Title = "导入整合包", Filter = "Minecraft 整合包|*.mrpack;*.zip", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        Vm.PackIsImport = true; Vm.Overlay = "Pack"; Vm.DialogMessage = ""; Vm.PackFilePath = dialog.FileName;
        var info = ModpackService.Inspect(dialog.FileName); Vm.PackRootInput = Vm.Settings.GameRoot;
        if(info.CurseFiles.Count>0)throw new InvalidOperationException("CurseForge 下载源已禁用，无法下载此整合包引用的 CurseForge 文件。");
        if(info.Downloads.SelectMany(x=>x.Urls).Any(x=>new Uri(x).Host.EndsWith(".forgecdn.net",StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("此整合包包含来自已禁用 CurseForge 下载源的文件。");
        var suggested = string.Concat(info.Name.Select(x => Path.GetInvalidFileNameChars().Contains(x) ? '_' : x)).Trim().TrimEnd('.');
        Vm.PackIdInput = suggested.Length is > 0 and <= 100 ? suggested : "导入的整合包";
        Vm.PackSummary = info.Format + " · Minecraft " + info.Minecraft + " · " + info.Loader + " " + info.LoaderVersion;
        foreach (var name in new[] { nameof(Vm.PackFilePath), nameof(Vm.PackRootInput), nameof(Vm.PackIdInput) }) Vm.Notify(name);
    });
    private void BrowsePackRoot_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var dialog = new OpenFolderDialog { Title = "选择游戏根目录" };
        if (Directory.Exists(Vm.PackRootInput)) dialog.InitialDirectory = Vm.PackRootInput;
        if (dialog.ShowDialog(this) == true) { Vm.PackRootInput = dialog.FolderName; Vm.Notify(nameof(Vm.PackRootInput)); }
    });
    private async void BeginPack_Click(object sender, RoutedEventArgs e)
    {
        if (!Vm.CanInteract) return;
        var importing = Vm.PackIsImport; var file = Vm.PackFilePath; var id = Vm.PackIdInput.Trim();
        var root = Vm.PackRootInput; var format = Vm.PackFormat; var includeSaves = Vm.PackIncludeSaves; var version = exportingVersion;
        var output = "";
        if (!importing)
        {
            if (version is null) return;
            var dialog = new SaveFileDialog { Title = "导出整合包", FileName = version.Id, DefaultExt = format == "Modrinth" ? ".mrpack" : ".zip",
                Filter = format == "Modrinth" ? "Modrinth 整合包|*.mrpack" : "CurseForge 整合包|*.zip", AddExtension = true };
            if (dialog.ShowDialog(this) != true) return; output = dialog.FileName;
        }
        await RunDownloadAsync(async (progress, token) =>
        {
            try
            {
                FlushInstanceSettings();
                if (importing)
                {
                    var selectedRoot = GameRootManager.ResolveInputPath(root);
                    var targetSettings = new LauncherSettings { GameRoot = selectedRoot, JavaPath = Vm.Settings.JavaPath, DownloadSource = Vm.Settings.DownloadSource, DownloadThreads = Vm.Settings.DownloadThreads };
                    await ModpackService.ImportAsync(downloadsClient, targetSettings, file, id, CursePackProvider(), progress, token);
                    GameRootManager.Activate(Vm.Settings, selectedRoot); ClearRootContext(); Vm.Settings.SelectedVersion = id; Vm.Save(); Vm.RefreshGameRoots();
                    await ScanAsync(); VersionsNav.IsChecked = true; Vm.Page = "Versions"; NotifyLaunch("整合包导入完成 · " + id);
                }
                else
                {
                    await ModpackService.ExportAsync(downloadsClient, Vm.Settings, version!.Id, output, format, includeSaves, CursePackProvider(), progress, token);
                    NotifyLaunch("整合包已导出 · " + Path.GetFileName(output));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { Vm.Overlay = "Pack"; throw; }
        });
    }
}
