using ACML.Core;
using System.Diagnostics;
using System.IO;

namespace ACML.App;
public partial class MainWindow
{
    private sealed class GameRun(GameVersion version, LauncherSettings settings, CancellationToken lifetime) : IDisposable
    {
        public GameVersion Version { get; } = version;
        public LauncherSettings Settings { get; } = settings;
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        public DateTime StartedUtc { get; } = DateTime.UtcNow;
        public Process? Process { get; set; }
        public Task? StopTask { get; set; }
        public bool StopRequested { get; set; }
        public bool SharedDirectory { get; set; }
        public string LogPath { get; set; } = "";
        public string GameLogPath { get; set; } = "";
        public string JavaDescription { get; set; } = "尚未准备 Java";
        public string Token { get; set; } = "";
        public Queue<string> Tail { get; } = new();
        public void Dispose() { Cancellation.Dispose(); Process?.Dispose(); Token = ""; }
    }
    private readonly List<GameRun> gameRuns = [];
    private GameRun? preparingGame;
    private GameRun? failedGame;
    private readonly Queue<(GameRun Run, string Title, string Message)> pendingGameFailures = new();
    private int stoppingGames;
    private void RefreshRunningGames()
    {
        Vm.RunningGameCount = gameRuns.Count(x => x.Process is not null);
        Vm.GameRunning = Vm.RunningGameCount > 0; Vm.StoppingGame = stoppingGames > 0;
    }
    private void ReleasePreparation(GameRun run)
    {
        if (!ReferenceEquals(preparingGame, run)) return;
        preparingGame = null; Vm.Busy = false;
        if (Vm.LaunchingVisible) Vm.Overlay = "";
        if (pendingGameFailures.TryDequeue(out var pending)) ShowLaunchFailure(pending.Run, pending.Title, pending.Message);
    }
    private bool IsVersionRunning(string root, string id) => gameRuns.Any(x => x.Version.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && GameRootManager.Same(x.Settings.GameRoot, root));
    private void AppendGameLog(GameRun run, string text)
    {
        if (run.Token.Length > 0) text = text.Replace(run.Token, "[token hidden]", StringComparison.Ordinal);
        text = RedactLog(text); var line = $"[{DateTime.Now:HH:mm:ss}] {text}";
        lock (logLock)
        {
            run.Tail.Enqueue(line[..Math.Min(line.Length, 1500)]); while (run.Tail.Count > 70) run.Tail.Dequeue();
            try { if (run.LogPath.Length > 0) File.AppendAllText(run.LogPath, line + Environment.NewLine); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        AppendLog("[" + run.Version.Id + "] " + text);
    }
}
