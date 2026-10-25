using ACML.Core;

public static class StartupMonitorTests
{
    public static async Task RunAsync(Action<string, Action> check)
    {
        void Assert(bool value) { if (!value) throw new Exception("Assertion failed"); }
        var probes = 0;
        await GameStartupMonitor.WaitForWindowAsync(() => new(false, 0, ++probes > 3), default,
            TimeSpan.FromSeconds(1), TimeSpan.Zero, TimeSpan.FromMilliseconds(1));
        check("A live Java process is not successful until its game window appears", () => Assert(probes == 4));
        foreach (var code in new[] { 0, 1, -1 })
        {
            var message = "";
            try { await GameStartupMonitor.WaitForWindowAsync(() => new(true, code, true), default, stableWindow: TimeSpan.Zero); }
            catch (InvalidOperationException ex) { message = ex.Message; }
            check("Exit before readiness is a launch failure even for exit code " + code, () => Assert(message.Contains("退出码 " + code)));
        }
        probes = 0;
        await GameStartupMonitor.WaitForWindowAsync(() => new(false, 0, ++probes != 2), default,
            TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(12), TimeSpan.FromMilliseconds(2));
        check("A disappearing startup window resets the stable-window readiness check", () => Assert(probes >= 4));
        var timedOut = false;
        try { await GameStartupMonitor.WaitForWindowAsync(() => new(false, 0, false), default, TimeSpan.Zero); }
        catch (TimeoutException) { timedOut = true; }
        check("An alive process with no game window reports startup timeout", () => Assert(timedOut));
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); var canceledCorrectly = false; probes = 0;
        try { await GameStartupMonitor.WaitForWindowAsync(() => { probes++; return new(false, 0, true); }, canceled.Token); }
        catch (OperationCanceledException) { canceledCorrectly = true; }
        check("Startup cancellation stops monitoring without reporting success", () => Assert(canceledCorrectly && probes == 0));
    }
}
