using ACML.Core;
using System.Windows;
using System.Windows.Controls;

namespace ACML.App;
public partial class MainWindow
{
    private TaskCompletionSource<bool>? javaSwitchRequest;
    private void AcceptJavaSwitch_Click(object sender,RoutedEventArgs e)=>javaSwitchRequest?.TrySetResult(true);
    private void CancelJavaSwitch_Click(object sender,RoutedEventArgs e)=>javaSwitchRequest?.TrySetResult(false);
    private async Task RefreshJavaListAsync()
    {
        var global = Vm.JavaInput; var instance = Vm.ManagedJavaInput; var wasLoading = loadingManagedVersion;
        var roots = Vm.Settings.GameRoots.Select(x => x.Path).Append(Vm.Settings.GameRoot).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var found = await JavaService.DiscoverAsync(global, roots, lifetime.Token);
        // Resetting ItemsSource can clear SelectedValue; preserve both edited paths.
        loadingManagedVersion = true;
        try
        {
            Vm.JavaRuntimes.Clear(); foreach (var runtime in found) Vm.JavaRuntimes.Add(runtime);
            Vm.JavaInput = global; Vm.ManagedJavaInput = instance;
        }
        finally { loadingManagedVersion = wasLoading; }
        Vm.JavaDetectionStatus = found.Count > 0 ? $"检测到 {found.Count} 个 Java" : "未检测到 Java，可手动选择；启动时可自动下载";
    }
    private void RememberJava(JavaRuntime runtime)
    {
        if (!Vm.JavaRuntimes.Any(x => string.Equals(x.Path, runtime.Path, StringComparison.OrdinalIgnoreCase))) Vm.JavaRuntimes.Add(runtime);
    }
    private async Task<string?> ConfirmJavaSwitchAsync(LauncherSettings settings, GameVersion version, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(settings.JavaPath)) return null;
        JavaRuntime? selected = null; string reason;
        try
        {
            selected = await JavaService.InspectAsync(settings.JavaPath);
            RememberJava(selected);
            if (JavaDiscovery.Matches(selected, version.JavaMajor)) return selected.Path;
            reason = $"Java {(selected.Version.Length > 0 ? selected.Version : selected.Major)}（{(selected.Is64Bit ? "64 位" : "非 64 位")}）";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        { reason = "当前 Java 无法使用"; }
        token.ThrowIfCancellationRequested();
        var match = Vm.JavaRuntimes.FirstOrDefault(x => JavaDiscovery.Matches(x, version.JavaMajor));
        Vm.JavaCurrentVersion = reason;
        Vm.JavaRequiredVersion = $"Java {(match is not null && match.Version.Length > 0 ? match.Version : version.JavaMajor)}（64 位）";
        var request = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); javaSwitchRequest = request;
        Vm.Overlay = "JavaSwitch"; bool accepted;
        try { accepted = await request.Task.WaitAsync(token); }
        finally { if (ReferenceEquals(javaSwitchRequest,request)) javaSwitchRequest=null; if(Vm.JavaSwitchVisible)Vm.Overlay="Launching"; }
        if (!accepted)
        {
            preparingGame?.Cancellation.Cancel(); throw new OperationCanceledException(token);
        }
        return match?.Path;
    }
}
