using ACML.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ACML.App;
public partial class MainWindow
{
    private static bool IsWithinButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button) return true;
            source = source is FrameworkElement element ? element.Parent ?? System.Windows.Media.VisualTreeHelper.GetParent(element) : null;
        }
        return false;
    }
    private void VersionItem_LeftClick(object sender, MouseButtonEventArgs e) => Run(() =>
    {
        if (!Vm.CanInteract || IsWithinButton(e.OriginalSource as DependencyObject)) return;
        if (ItemsControl.ContainerFromElement(VersionList, e.OriginalSource as DependencyObject) is not ListBoxItem { DataContext: GameVersion version }) return;
        Vm.Settings.SelectedVersion = version.Id; Vm.Save(); Vm.ModDetail = false; Vm.ModProjects.Clear(); Vm.ModReleases.Clear();
    });
    private void VersionItem_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (!Vm.CanInteract || ItemsControl.ContainerFromElement(VersionList, e.OriginalSource as DependencyObject) is not ListBoxItem { DataContext: GameVersion version } item) return;
        e.Handled = true; ShowVersionMenu(version, item);
    }
    private void VersionMenu_Click(object sender, RoutedEventArgs e)
    { if (sender is Button { DataContext: GameVersion version } button) ShowVersionMenu(version, button); }
    private void ShowVersionMenu(GameVersion version, FrameworkElement placement)
    {
        if (!Vm.CanInteract) return;
        var menu = new ContextMenu();
        void Add(string title, Action action) { var item = new MenuItem { Header = title }; item.Click += (_, _) => Run(action); menu.Items.Add(item); }
        Add("版本设置", () => { VersionList.SelectedItem = version; ManageVersion_Click(placement, new()); });
        Add("导出整合包", () => OpenExportPack(version));
        Add("补全文件", () => { VersionList.SelectedItem = version; RepairVersion_Click(placement, new()); });
        Add("打开版本目录", () => OpenFolder(SafePaths.Under(Vm.Settings.GameRoot, "versions/" + version.Id)));
        menu.PlacementTarget = placement; menu.IsOpen = true;
    }
    private void BackVersionSettings_Click(object sender, RoutedEventArgs e)
    { FlushInstanceSettings(); VersionsNav.IsChecked = true; Vm.Page = "Versions"; }
}
