using ACML.Core;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

public static class RootsAndParallelTests
{
    public static async Task RunAsync(Action<string, Action> check, string root)
    {
        void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
        var first = Path.Combine(root, "root-one"); var second = Path.Combine(root, "root-two");
        check("Legacy root migrates with its selected version and canonical duplicate paths collapse", () =>
        {
            var directory = Path.Combine(root, "legacy-roots"); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "settings.json"), System.Text.Json.JsonSerializer.Serialize(new { SchemaVersion = 1, Accounts = Array.Empty<object>(), GameRoot = first, SelectedVersion = "legacy" }));
            var loaded = new SettingsStore(directory).Load();
            Assert(loaded.GameRoots.Count == 1 && loaded.GameRoots[0].SelectedVersion == "legacy");
            GameRootManager.Activate(loaded, first + Path.DirectorySeparatorChar, "Primary");
            Assert(loaded.GameRoots.Count == 1 && loaded.SelectedVersion == "legacy" && loaded.GameRoots[0].Name == "Primary");
        });
        check("Multiple roots remember independent selected versions across switching and restart", () =>
        {
            var settings = new LauncherSettings { GameRoot = first, SelectedVersion = "first-instance" };
            GameRootManager.Activate(settings, second, "Second"); Assert(settings.SelectedVersion is null);
            settings.SelectedVersion = "second-instance";
            GameRootManager.Activate(settings, first); Assert(settings.SelectedVersion == "first-instance");
            var store = new SettingsStore(Path.Combine(root, "root-persistence")); settings.DownloadThreads = 23; store.Save(settings);
            settings = store.Load(); GameRootManager.Activate(settings, second);
            Assert(settings.SelectedVersion == "second-instance" && settings.DownloadThreads == 23 && settings.GameRoots.Count == 2);
        });
        check("Removing active root selects another root, retains all files and prevents removing the final entry", () =>
        {
            Directory.CreateDirectory(first); var sentinel = Path.Combine(first, "keep.txt"); File.WriteAllText(sentinel, "save data");
            var settings = new LauncherSettings { GameRoot = first }; GameRootManager.Activate(settings, second); GameRootManager.Activate(settings, first);
            GameRootManager.Remove(settings, first); Assert(settings.GameRoot == second && File.ReadAllText(sentinel) == "save data");
            var rejected = false; try { GameRootManager.Remove(settings, second); } catch (InvalidOperationException) { rejected = true; }
            Assert(rejected && settings.GameRoots.Count == 1);
        });
        check("Identically named instances in different roots keep independent mods and settings", () =>
        {
            var settings = new LauncherSettings { GameRoot = first }; const string id = "same-name";
            InstanceManager.SaveSettings(first, id, new() { Enabled = true, MemoryMb = 1024 });
            InstanceManager.SaveSettings(second, id, new() { Enabled = true, MemoryMb = 3072 });
            var directory = Path.Combine(InstanceManager.GameDirectory(settings, id), "mods"); Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "one.jar"), "mod");
            GameRootManager.Activate(settings, second); Assert(InstanceManager.EffectiveSettings(settings, id).MemoryMb == 3072 && InstanceManager.ListMods(settings, id).Count == 0);
            GameRootManager.Activate(settings, first); Assert(InstanceManager.EffectiveSettings(settings, id).MemoryMb == 1024 && InstanceManager.ListMods(settings, id).Count == 1);
        });
        check("Relative root paths are rejected without changing the current root", () =>
        {
            var settings = new LauncherSettings { GameRoot = first }; var rejected = false;
            try { GameRootManager.Activate(settings, "relative/path"); } catch (InvalidOperationException) { rejected = true; }
            Assert(rejected && settings.GameRoot == first);
        });
        check("Renaming an inactive root changes only its display name and survives restart", () =>
        {
            var settings = new LauncherSettings { GameRoot = first, SelectedVersion = "first-version" };
            GameRootManager.Activate(settings, second, "Second"); settings.SelectedVersion = "second-version";
            GameRootManager.Activate(settings, first); GameRootManager.Rename(settings, second, "模组测试目录");
            Assert(settings.GameRoot == first && settings.SelectedVersion == "first-version");
            var store = new SettingsStore(Path.Combine(root, "root-names")); store.Save(settings); settings = store.Load();
            var renamed = settings.GameRoots.Single(x => GameRootManager.Same(x.Path, second));
            Assert(renamed.Name == "模组测试目录" && renamed.SelectedVersion == "second-version" && Directory.Exists(second));
        });
        var payload = new byte[8 * 1024 * 1024 + 31]; new Random(42).NextBytes(payload);
        var sha = Convert.ToHexString(SHA256.HashData(payload));
        var ranges = 0; var singles = 0;
        using var ranged = new HttpClient(new Handler(async (request, token) =>
        {
            await Task.Delay(15, token);
            if (request.Headers.Range?.Ranges.Single() is RangeItemHeaderValue range)
            {
                Interlocked.Increment(ref ranges); var start = range.From!.Value; var end = range.To!.Value;
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(payload, (int)start, (int)(end - start + 1)) };
                response.Content.Headers.ContentRange = new(start, end, payload.Length); return response;
            }
            Interlocked.Increment(ref singles); return new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        }));
        await new DownloadEngine(ranged, "官方", 4).FetchAsync(root, [new("segmented.bin", "https://test.example/data", sha, payload.Length, "SHA256")], "ranges", null, default);
        check("Large files download in four verified byte ranges and commit the original bytes", () => Assert(ranges == 4 && singles == 0 && File.ReadAllBytes(Path.Combine(root, "segmented.bin")).SequenceEqual(payload)));
        ranges = singles = 0;
        await new DownloadEngine(ranged, "官方", 1).FetchAsync(root, [new("single.bin", "https://test.example/data", sha, payload.Length, "SHA256")], "single", null, default);
        check("A one-thread setting disables file segmentation", () => Assert(ranges == 0 && singles == 1));
        var unsupported = 0; var full = 0;
        using var noRanges = new HttpClient(new Handler((request, _) =>
        {
            if (request.Headers.Range is null) Interlocked.Increment(ref full); else Interlocked.Increment(ref unsupported);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        }));
        await new DownloadEngine(noRanges, "官方", 4).FetchAsync(root, [new("fallback.bin", "https://test.example/data", sha, payload.Length, "SHA256")], "fallback", null, default);
        check("Servers ignoring Range fall back to a full verified download", () => Assert(unsupported > 0 && full == 1 && File.ReadAllBytes(Path.Combine(root, "fallback.bin")).SequenceEqual(payload)));
        var active = 0; var max = 0;
        using var concurrent = new HttpClient(new Handler(async (_, token) =>
        {
            var current = Interlocked.Increment(ref active); int previous;
            do { previous = Volatile.Read(ref max); } while (current > previous && Interlocked.CompareExchange(ref max, current, previous) != previous);
            try { await Task.Delay(30, token); return new(HttpStatusCode.OK) { Content = new StringContent("test") }; } finally { Interlocked.Decrement(ref active); }
        }));
        await new DownloadEngine(concurrent, "官方", 2).FetchAsync(root, Enumerable.Range(0, 6).Select(x => new DownloadFile("parallel/" + x, "https://test.example/" + x)), "parallel", null, default);
        check("Parallel file requests respect the configured global concurrency limit", () => Assert(max == 2));
        using var canceled = new CancellationTokenSource();
        using var blocked = new HttpClient(new Handler(async (_, token) => { canceled.Cancel(); await Task.Delay(Timeout.Infinite, token); return new(HttpStatusCode.OK); }));
        var canceledCorrectly = false;
        try { await new DownloadEngine(blocked, "官方", 4).FetchAsync(root, [new("canceled-ranges.bin", "https://test.example/data", sha, payload.Length, "SHA256")], "cancel", null, canceled.Token); } catch (OperationCanceledException) { canceledCorrectly = true; }
        check("Canceling segmented downloads removes partial data and never commits a file", () => Assert(canceledCorrectly && !File.Exists(Path.Combine(root, "canceled-ranges.bin")) && !Directory.EnumerateFiles(root, "*.part", SearchOption.AllDirectories).Any()));
        const string neo = "https://maven.neoforged.net/releases/net/neoforged/neoforge/21.1.1/neoforge-21.1.1-installer.jar";
        check("NeoForge mirror strips releases prefix and includes installers, checksums and Maven dependencies", () =>
        {
            Assert(DownloadEngine.Mirror(neo) == "https://bmclapi2.bangbang93.com/maven/net/neoforged/neoforge/21.1.1/neoforge-21.1.1-installer.jar");
            Assert(DownloadEngine.Mirror(neo + ".sha1")!.EndsWith("installer.jar.sha1"));
            Assert(DownloadEngine.Mirror("https://maven.neoforged.net/releases/org/example/lib/1/lib-1.jar") == "https://bmclapi2.bangbang93.com/maven/org/example/lib/1/lib-1.jar");
        });
        var hosts = new List<string>();
        using var fallback = new HttpClient(new Handler((request, _) =>
        {
            hosts.Add(request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(hosts.Count == 1 ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = new StringContent("metadata") });
        }));
        var metadata = await new DownloadEngine(fallback, "BMCLAPI").GetTextAsync(neo + ".sha1", default);
        check("Missing NeoForge mirror entries fall back to the official repository", () => Assert(metadata == "metadata" && hosts.SequenceEqual(new[] { "bmclapi2.bangbang93.com", "maven.neoforged.net" })));
        hosts.Clear();
        using var timeoutFallback = new HttpClient(new Handler((request, _) =>
        {
            hosts.Add(request.RequestUri!.Host); if (hosts.Count == 1) throw new TaskCanceledException("Timeout");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("metadata") });
        }));
        await new DownloadEngine(timeoutFallback, "自动").GetTextAsync(neo, default);
        check("Automatic NeoForge metadata requests prefer mirror and recover from source timeout", () => Assert(hosts.Count == 2 && hosts[0] == "bmclapi2.bangbang93.com" && hosts[1] == "maven.neoforged.net"));
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => callback(request, token); }
}
