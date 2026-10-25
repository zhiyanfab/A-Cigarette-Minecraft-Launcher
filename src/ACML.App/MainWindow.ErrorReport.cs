using ACML.Core;
using Microsoft.Win32;
using System.IO;
using System.Text;
using System.Windows;

namespace ACML.App;
public partial class MainWindow
{
    private string DiagnosticLogs(GameRun run)
    {
        var text = new StringBuilder();
        void Read(string path, bool currentOnly)
        {
            try
            {
                if (!File.Exists(path) || currentOnly && File.GetLastWriteTimeUtc(path) < run.StartedUtc.AddSeconds(-2)) return;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                // Read a bounded head and tail so the first exception and final cause
                // survive large modpack logs without blocking the error UI.
                text.AppendLine("\n--- " + path + " ---");
                var head = new byte[Math.Min(65536L, stream.Length)]; stream.ReadExactly(head); text.AppendLine(Encoding.UTF8.GetString(head));
                if (stream.Length > head.Length)
                {
                    stream.Seek(Math.Max(head.Length, stream.Length - 196608), SeekOrigin.Begin);
                    using var reader = new StreamReader(stream, Encoding.UTF8, true, 8192); text.AppendLine("\n[日志中段已省略]\n"); text.AppendLine(reader.ReadToEnd());
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { text.AppendLine("读取日志失败：" + ex.Message); }
        }
        Read(run.LogPath, false);
        if (run.SharedDirectory) { text.AppendLine("\n同一游戏目录有多个进程，报告使用本次独立启动输出，避免混入其他进程的 latest.log。"); return RedactLog(text.ToString()); }
        Read(run.GameLogPath, true);
        try
        {
            var game = Path.GetDirectoryName(Path.GetDirectoryName(run.GameLogPath));
            var crashes = game is null ? null : Path.Combine(game, "crash-reports");
            if (crashes is not null && Directory.Exists(crashes))
            {
                var fresh = Directory.EnumerateFiles(crashes, "*.txt").Where(x => File.GetLastWriteTimeUtc(x) >= run.StartedUtc.AddSeconds(-2)).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (fresh is not null) Read(fresh, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { text.AppendLine("读取崩溃报告失败：" + ex.Message); }
        return RedactLog(text.ToString());
    }
    private void SaveErrorReport_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (failedGame is not GameRun run) return;
        var dialog = new SaveFileDialog { Title = "保存错误报告", Filter = "错误报告|*.txt", DefaultExt = ".txt", FileName = "ACML-error-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt" };
        if (dialog.ShowDialog(this) != true) return;
        var report = $"ACML 错误报告\n启动器：{LauncherInfo.Version}\n时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}\n系统：{System.Runtime.InteropServices.RuntimeInformation.OSDescription}\n" +
            $"游戏：{run.Version.Description} · {run.Version.Id}\n需要 Java：{run.Version.JavaMajor}（64 位）\n使用 Java：{run.JavaDescription}\n\n{Vm.FailureTitle}\n\n分析：\n{Vm.FailureAnalysis}\n\n错误摘要：\n{Vm.FailureDetails}\n\n本次启动日志：{run.LogPath}\n游戏日志：{run.GameLogPath}\n" + DiagnosticLogs(run);
        File.WriteAllText(dialog.FileName, RedactLog(report), new UTF8Encoding(false));
        Vm.DialogMessage = "错误报告已保存：" + dialog.FileName;
    });
}
