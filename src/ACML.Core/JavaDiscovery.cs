using Microsoft.Win32;
using System.Runtime.Versioning;

namespace ACML.Core;
public static class JavaDiscovery
{
    public static bool Matches(JavaRuntime runtime, int requiredMajor) => runtime.Is64Bit && runtime.Major == requiredMajor;
    public static async Task<IReadOnlyList<JavaRuntime>> DiscoverAsync(string? preferred, IEnumerable<string>? gameRoots, CancellationToken token)
    {
        var candidates = await Task.Run(() => CandidatePaths(preferred, gameRoots, token), token);
        var results = new System.Collections.Concurrent.ConcurrentBag<JavaRuntime>();
        await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (path, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            try { var java = await JavaService.InspectAsync(path); results.Add(java); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception) { }
        });
        return results.OrderByDescending(x => x.Major).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }
    public static IReadOnlyList<string> CandidatePaths(string? preferred, IEnumerable<string>? gameRoots, CancellationToken token = default)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim('"'))); if (File.Exists(full)) { var console = Path.Combine(Path.GetDirectoryName(full)!, "java.exe"); paths.Add(File.Exists(console) ? console : full); } }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { }
        }
        void Folder(string? path) { if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) folders.Add(Path.GetFullPath(path)); }
        Add(preferred);
        foreach (var name in new[] { "JAVA_HOME", "JDK_HOME", "JRE_HOME" }) { var home = Environment.GetEnvironmentVariable(name); if (!string.IsNullOrWhiteSpace(home)) Add(Path.Combine(home, "bin", "java.exe")); }
        foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)) if (!string.IsNullOrWhiteSpace(path)) { try { Add(Path.Combine(path.Trim('"'), "java.exe")); } catch (ArgumentException) { } }
        var installations = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) };
        foreach (var drive in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed && x.IsReady)) { installations.Add(Path.Combine(drive.RootDirectory.FullName, "Program Files")); installations.Add(Path.Combine(drive.RootDirectory.FullName, "Program Files (x86)")); Folder(Path.Combine(drive.RootDirectory.FullName, "Java")); }
        foreach (var baseFolder in installations.Where(x => x.Length > 0))
            foreach (var vendor in new[] { "Java", "Eclipse Adoptium", "Microsoft", "Zulu", "BellSoft", "Amazon Corretto", "Semeru", "OpenLogic", "Common Files/Oracle/Java" }) Folder(Path.Combine(baseFolder, vendor));
        Folder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".jdks"));
        Folder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Eclipse Adoptium"));
        Folder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "runtime"));
        foreach (var root in (gameRoots ?? []).Append(Path.Combine(LauncherInfo.InstallDirectory, ".minecraft")))
        {
            token.ThrowIfCancellationRequested();
            foreach (var relative in new[] { "runtime", "java", "jre", "jdk", ".acml/java" }) Folder(Path.Combine(root, relative));
            if (Path.GetDirectoryName(root) is string launcher)
                foreach (var relative in new[] { "runtime", "Java", "java", "jre", "PCL/Java" }) Folder(Path.Combine(launcher, relative));
        }
        if (OperatingSystem.IsWindows()) foreach (var home in RegistryHomes()) { Add(Path.Combine(home, "bin", "java.exe")); Folder(home); }
        foreach (var folder in folders)
        {
            token.ThrowIfCancellationRequested();
            try { foreach (var path in Directory.EnumerateFiles(folder, "java.exe", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, MaxRecursionDepth = 7 })) { token.ThrowIfCancellationRequested(); Add(path); } }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return paths.ToArray();
    }
    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<string> RegistryHomes()
    {
        var homes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                void Walk(RegistryKey key, int depth)
                {
                    foreach (var value in new[] { "JavaHome", "InstallationPath", "Path" }) if (key.GetValue(value) is string home && Directory.Exists(home)) homes.Add(home);
                    if (depth == 0) return;
                    foreach (var name in key.GetSubKeyNames()) { using var child = key.OpenSubKey(name); if (child is not null) Walk(child, depth - 1); }
                }
                foreach (var key in new[] { @"SOFTWARE\JavaSoft", @"SOFTWARE\Eclipse Adoptium", @"SOFTWARE\AdoptOpenJDK", @"SOFTWARE\Microsoft\JDK" }) { using var vendor = root.OpenSubKey(key); if (vendor is not null) Walk(vendor, 4); }
                using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is not null) foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var app = uninstall.OpenSubKey(name); var display = app?.GetValue("DisplayName") as string ?? "";
                    if (System.Text.RegularExpressions.Regex.IsMatch(display, "Java|JDK|OpenJDK|Temurin|Corretto|Zulu|Liberica", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                        && app?.GetValue("InstallLocation") is string location && Directory.Exists(location)) homes.Add(location);
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return homes.ToArray();
    }
}
