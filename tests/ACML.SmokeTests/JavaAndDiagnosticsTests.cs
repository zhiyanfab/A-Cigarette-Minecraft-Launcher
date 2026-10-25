using ACML.Core;

public static class JavaAndDiagnosticsTests
{
    public static void Run(Action<string, Action> check, string root)
    {
        void Assert(bool value) { if (!value) throw new Exception("Assertion failed"); }
        check("User-requested stops are never crash reports, including forced termination before startup is ready",()=>
        {
            foreach(var ready in new[]{true,false})foreach(var code in new[]{0,1,-1,-1073741510,137})Assert(GameExitClassifier.Classify(true,ready,code)==GameExitStatus.Stopped);
            Assert(GameExitClassifier.Classify(false,false,0)==GameExitStatus.Failed && GameExitClassifier.Classify(false,true,1)==GameExitStatus.Failed && GameExitClassifier.Classify(false,true,0)==GameExitStatus.Exited);
        });
        check("Java choice labels include the full version, bitness and absolute path", () =>
        {
            var java = new JavaRuntime("C:\\Program Files\\Java\\jdk-21\\bin\\java.exe", 21, true, "test", "21.0.9");
            Assert(java.ToString().Contains("21.0.9") && java.ToString().Contains("64 位") && java.ToString().Contains(java.Path));
            Assert(JavaDiscovery.Matches(java, 21) && !JavaDiscovery.Matches(java, 17) && !JavaDiscovery.Matches(java with { Is64Bit = false }, 21));
        });
        check("Java detection includes managed and bundled game runtimes and deduplicates java/javaw aliases", () =>
        {
            var game = Path.Combine(root, "java-discovery"); var managed = Path.Combine(game, ".acml", "java", "temurin", "jre", "bin"); var bundled = Path.Combine(game, "runtime", "java-runtime-delta", "bin");
            Directory.CreateDirectory(managed); Directory.CreateDirectory(bundled);
            foreach (var folder in new[] { managed, bundled }) { File.WriteAllText(Path.Combine(folder, "java.exe"), "fixture, never executed"); File.WriteAllText(Path.Combine(folder, "javaw.exe"), "fixture, never executed"); }
            var paths = JavaDiscovery.CandidatePaths(Path.Combine(managed, "javaw.exe"), [game, game]);
            Assert(paths.Count(x => x.Equals(Path.Combine(managed, "java.exe"), StringComparison.OrdinalIgnoreCase)) == 1 && paths.Contains(Path.Combine(bundled, "java.exe")) && !paths.Contains(Path.Combine(managed, "javaw.exe")));
        });
        check("NeoForge duplicate-key diagnostics distinguish duplicate JAR paths from unrelated dictionary errors", () =>
        {
            Assert(LaunchDiagnostics.Analyze("IllegalStateException: Duplicate key C:\\gson.jar at cpw.mods.bootstraplauncher.BootstrapLauncher").Contains("运行库路径重复"));
            Assert(!LaunchDiagnostics.Analyze("Duplicate key player at example.mod").Contains("运行库路径重复"));
        });
        check("Java class-version errors explain the required version and do not confuse ASM compatibility with JVM age", () =>
        {
            Assert(LaunchDiagnostics.Analyze("UnsupportedClassVersionError: class file version 65.0, only recognizes up to 61.0").Contains("Java 21"));
            Assert(LaunchDiagnostics.Analyze("IllegalArgumentException: Unsupported class file major version 65").Contains("字节码工具"));
        });
        check("Common memory, dependency, mixin and graphics errors have actionable analyses", () =>
        {
            Assert(LaunchDiagnostics.Analyze("OutOfMemoryError: Java heap space").Contains("堆内存不足"));
            Assert(LaunchDiagnostics.Analyze("Could not reserve enough space").Contains("系统或本机内存不足"));
            Assert(LaunchDiagnostics.Analyze("Missing or unsupported mandatory dependencies").Contains("前置不匹配"));
            Assert(LaunchDiagnostics.Analyze("MixinApplyError").Contains("单凭此提示不能确定"));
            Assert(LaunchDiagnostics.Analyze("GLFW error 65542").Contains("显卡驱动"));
        });
        check("Unrecognized crashes do not invent causes from exit codes or harmless version-check timeouts", () =>
        {
            Assert(LaunchDiagnostics.Analyze("Exited 1; version checker HTTP connect timed out").Contains("未匹配"));
        });
        check("Error reports redact CLI, Bearer, JSON credentials and legacy token sessions", () =>
        {
            var text = LaunchDiagnostics.Redact("--accessToken cli-secret Bearer bearer-secret {\"access_token\":\"json-secret\",\"refresh_token\":\"refresh-secret\"} token:session-secret:0123456789abcdef0123456789abcdef");
            foreach (var secret in new[] { "cli-secret", "bearer-secret", "json-secret", "refresh-secret", "session-secret" }) Assert(!text.Contains(secret));
        });
    }
}
