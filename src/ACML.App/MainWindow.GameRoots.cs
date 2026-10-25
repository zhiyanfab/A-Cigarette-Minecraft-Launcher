using ACML.Core;
using System.Windows;
using System.Windows.Controls;

namespace ACML.App;

public partial class MainWindow
{
    private string? renamingGameRoot;
    private void RenameGameRoot_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (!Vm.CanInteract || sender is not MenuItem { CommandParameter: GameRootEntry entry }) return;
        renamingGameRoot = entry.Path; Vm.RootRenameInput = entry.Name;
        Vm.Notify(nameof(Vm.RootRenameInput)); Vm.DialogMessage = ""; Vm.Overlay = "RootRename";
    });
    private void ConfirmRootRename_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (!Vm.CanInteract || renamingGameRoot is null) return;
        GameRootManager.Rename(Vm.Settings, renamingGameRoot, Vm.RootRenameInput);
        Vm.Save(); Vm.RefreshGameRoots(); renamingGameRoot = null; Vm.Overlay = "";
    });
    private void ClearRootContext()
    {
        Vm.Overlay = ""; Vm.DialogMessage = "";
        Vm.Versions.Clear(); VersionList.SelectedItem = null;
        Vm.ManagedVersion = null; Vm.InstalledMods.Clear();
        Vm.ModDetail = false; Vm.ModProjects.Clear(); Vm.ModReleases.Clear(); Vm.ModDependencies.Clear();
        Vm.SelectedModProject = null; Vm.SelectedModRelease = null; Vm.ModStatus = "";
        modHistory.Clear(); allModReleases = []; pendingMods = null; pendingInstallVersion = null;
        loaderCatalogCancellation?.Cancel();
        Vm.Notify(nameof(Vm.HasModDependencies)); Vm.RefreshLabels();
    }
    private async Task ActivateGameRootAsync(string path, string? name = null)
    {
        var changed = !GameRootManager.Same(Vm.Settings.GameRoot, path);
        GameRootManager.Activate(Vm.Settings, path, name);
        if (changed) ClearRootContext();
        Vm.Save(); Vm.RefreshGameRoots();
        await ScanAsync(); VersionList.SelectedItem = Vm.ActiveVersion;
    }
    private async void GameRoot_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || !Vm.CanInteract || sender is not ComboBox { SelectedItem: GameRootEntry entry }
            || GameRootManager.Same(entry.Path, Vm.Settings.GameRoot)) return;
        await RunAsync(() => ActivateGameRootAsync(entry.Path));
    }
    private async void AddGameRoot_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(() => ActivateGameRootAsync(Vm.RootInput, Vm.RootNameInput));
    private async void RemoveGameRoot_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        GameRootManager.Remove(Vm.Settings, Vm.Settings.GameRoot);
        ClearRootContext(); Vm.Save(); Vm.RefreshGameRoots();
        await ScanAsync(); VersionList.SelectedItem = Vm.ActiveVersion;
        Vm.Status = "已从列表移除目录，游戏文件已保留。";
    });
}
