using System.Diagnostics;

namespace ACML.Core;

public sealed record GameProcessSnapshot(bool HasExited, int ExitCode, bool HasWindow);
public static class GameStartupMonitor
{
    // Creating java.exe alone is insufficient: require a stable game window while the process stays alive.
    public static async Task WaitForWindowAsync(Func<GameProcessSnapshot> probe, CancellationToken token,
        TimeSpan? timeout = null, TimeSpan? stableWindow = null, TimeSpan? pollInterval = null)
    {
        var started = Stopwatch.GetTimestamp(); long? windowSince = null;
        while (true)
        {
            token.ThrowIfCancellationRequested(); var state = probe();
            if (state.HasExited) throw new InvalidOperationException($"游戏在启动完成前退出，退出码 {state.ExitCode}。");
            if (state.HasWindow)
            {
                windowSince ??= Stopwatch.GetTimestamp();
                if (Stopwatch.GetElapsedTime(windowSince.Value) >= (stableWindow ?? TimeSpan.FromSeconds(2))) return;
            }
            else windowSince = null;
            if (Stopwatch.GetElapsedTime(started) >= (timeout ?? TimeSpan.FromMinutes(5)))
                throw new TimeoutException("等待游戏窗口超时。进程仍可能正在加载，请查看日志；也可结束当前游戏后重试。");
            await Task.Delay(pollInterval ?? TimeSpan.FromMilliseconds(250), token);
        }
    }
}

public sealed record GamePreparationProgress(string Stage, double Percent);
