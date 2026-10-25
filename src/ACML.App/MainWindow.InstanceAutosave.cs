using ACML.Core;
using System.ComponentModel;
using System.Windows.Threading;

namespace ACML.App;
public partial class MainWindow
{
    private bool loadingManagedVersion;
    private DispatcherTimer? instanceSaveTimer;
    private (string Root, string Id, InstanceSettings Settings)? pendingInstanceSave;
    private void ManagedSetting_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (loadingManagedVersion || Vm.ManagedVersion is null || e.PropertyName is not
            ("ManagedSettingsEnabled" or "ManagedJavaInput" or "ManagedTitleInput" or "ManagedMemoryInput" or "ManagedWidthInput" or "ManagedHeightInput" or "ManagedIsolationInput"
            or "ManagedUseGlobalJava" or "ManagedUseGlobalMemory" or "ManagedUseGlobalTitle" or "ManagedUseGlobalIsolation" or "ManagedUseGlobalWindow")) return;
        instanceSaveTimer?.Stop(); pendingInstanceSave = null;
        try
        {
            var settings = ReadManagedSettings(); InstanceManager.ValidateGameTitle(settings.GameWindowTitle);
            if (settings.MemoryMb is < 512 or > 65536 || settings.GameWidth is < 320 or > 16384 || settings.GameHeight is < 240 or > 16384) throw new InvalidOperationException("请输入有效的内存和窗口尺寸。");
            pendingInstanceSave = (Vm.Settings.GameRoot, Vm.ManagedVersion.Id, settings); Vm.InstanceSaveStatus = "正在保存…";
            if (instanceSaveTimer is null)
            { instanceSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) }; instanceSaveTimer.Tick += (_, _) => { try { FlushInstanceSettings(); } catch (Exception ex) { Vm.InstanceSaveStatus = ex.Message; } }; }
            instanceSaveTimer.Start();
        }
        catch (Exception ex) { Vm.InstanceSaveStatus = ex.Message; }
    }
    private void FlushInstanceSettings()
    {
        instanceSaveTimer?.Stop();
        if (pendingInstanceSave is not { } pending) return;
        InstanceManager.SaveSettings(pending.Root, pending.Id, pending.Settings); pendingInstanceSave = null;
        Vm.InstanceSaveStatus = "已自动保存"; Vm.Notify(nameof(Vm.ManagedDirectoryLabel));
    }
}
