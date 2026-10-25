using ACML.Core;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

public static class DownloadTests
{
    public static async Task RunAsync(Action<string, Action> check, string root)
    {
        void Assert(bool result) { if (!result) throw new Exception("Assertion failed"); }
        var data = Encoding.UTF8.GetBytes("verified fixture data"); var sha = Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();
        check("Mirror mappings preserve asset and Maven paths", () =>
        { Assert(DownloadEngine.Mirror("https://resources.download.minecraft.net/ab/hash") == "https://bmclapi2.bangbang93.com/assets/ab/hash"); Assert(DownloadEngine.Mirror("https://libraries.minecraft.net/a/b.jar") == "https://bmclapi2.bangbang93.com/maven/a/b.jar"); });
        var requests = 0;
        using var client = new HttpClient(new Handler((request, ct) =>
        { Interlocked.Increment(ref requests); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }); }));
        var engine = new DownloadEngine(client, "官方"); var file = new DownloadFile("download/item.jar", "https://test.example/item", sha, data.Length);
        await engine.FetchAsync(root, [file], "fixture", null, CancellationToken.None);
        check("Downloads write only fully verified files", () => Assert(DownloadEngine.Valid(SafePaths.Under(root, file.RelativePath), file)));
        await engine.FetchAsync(root, [file], "fixture", null, CancellationToken.None);
        check("Verified files are reused without another HTTP request", () => Assert(requests == 1));
        File.WriteAllText(SafePaths.Under(root, file.RelativePath), "corrupt");
        await engine.FetchAsync(root, [file], "fixture", null, CancellationToken.None);
        check("Corrupt file is downloaded again and repaired", () => Assert(requests == 2 && DownloadEngine.Valid(SafePaths.Under(root, file.RelativePath), file)));
        using (var wrong = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("wrong bytes") }))))
        {
            var protectedPath = Path.Combine(root, "preserve.jar"); File.WriteAllText(protectedPath, "previous contents"); var rejected = false;
            try { await new DownloadEngine(wrong, "官方").FetchAsync(root, [file with { RelativePath = "preserve.jar" }], "integrity", null, CancellationToken.None); } catch (IOException) { rejected = true; }
            check("Hash failure preserves previous file and removes partial files", () => Assert(rejected && File.ReadAllText(protectedPath) == "previous contents" && !Directory.EnumerateFiles(root, "*.part", SearchOption.AllDirectories).Any()));
        }
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel(); var rejected = false;
            try { await engine.FetchAsync(root, [file with { RelativePath = "canceled.jar" }], "cancel", null, canceled.Token); } catch (OperationCanceledException) { rejected = true; }
            check("Cancellation does not register an incomplete download", () => Assert(rejected && !File.Exists(Path.Combine(root, "canceled.jar"))));
        }
        var gameRoot = Path.Combine(root, "real-install-fixture");
        var indexData = Encoding.UTF8.GetBytes("{\"objects\":{}}"); var indexSha = Convert.ToHexString(SHA1.HashData(indexData));
        var manifest = new JsonObject
        {
            ["id"] = "fixture-game", ["type"] = "release", ["mainClass"] = "net.minecraft.client.main.Main", ["javaVersion"] = new JsonObject { ["majorVersion"] = 21 },
            ["arguments"] = new JsonObject { ["jvm"] = new JsonArray("-cp", "${classpath}"), ["game"] = new JsonArray() }, ["libraries"] = new JsonArray(),
            ["assetIndex"] = new JsonObject { ["id"] = "fixture", ["url"] = "https://test.example/index", ["size"] = indexData.Length, ["sha1"] = indexSha },
            ["downloads"] = new JsonObject { ["client"] = new JsonObject { ["url"] = "https://test.example/client", ["sha1"] = sha, ["size"] = data.Length } }
        };
        using var gameClient = new HttpClient(new Handler((request, _) =>
        {
            HttpContent content;
            var path = request.RequestUri!.AbsolutePath;
            if (request.RequestUri.Host == "meta.fabricmc.net") content = new StringContent(path.EndsWith("/profile/json")
                ? """{"mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient","arguments":{"jvm":[],"game":[]},"libraries":[{"name":"net.fabricmc:fabric-loader:fixture","url":"https://test.example/"}]}"""
                : """[{"loader":{"stable":true,"version":"fixture"}}]""");
            else content = path switch { "/metadata" => new StringContent(manifest.ToJsonString()), "/index" => new ByteArrayContent(indexData), _ => path.EndsWith(".sha1") ? new StringContent(sha) : new ByteArrayContent(data) };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }));
        var installer = new GameInstaller(gameClient, "官方");
        await installer.InstallAsync(gameRoot, new RemoteVersion("fixture-game", "release", DateTimeOffset.UtcNow, "https://test.example/metadata"), "my-instance", false, null, CancellationToken.None);
        check("Full installation commits a launchable local version", () => Assert(VersionService.Scan(gameRoot).Single().IsSupported));
        var jar = Path.Combine(gameRoot, "versions", "my-instance", "my-instance.jar"); File.WriteAllText(jar, "broken");
        File.Delete(Path.Combine(gameRoot, "versions", "my-instance", "my-instance.json"));
        await installer.RepairAsync(gameRoot, "my-instance", null, CancellationToken.None);
        check("Repair restores missing manifest from install record and repairs client", () => Assert(VersionService.Scan(gameRoot).Single().IsSupported && File.ReadAllBytes(jar).SequenceEqual(data)));
        await installer.InstallAsync(gameRoot, new RemoteVersion("fixture-game", "release", DateTimeOffset.UtcNow, "https://test.example/metadata"), "fabric-instance", true, null, CancellationToken.None);
        check("Fabric installation merges its profile and downloads loader libraries", () =>
        {
            var version = VersionService.Scan(gameRoot).Single(x => x.Id == "fabric-instance");
            Assert(version.IsSupported && version.Loader == "Fabric" && version.BaseId == "fixture-game");
            Assert(File.Exists(Path.Combine(gameRoot, "libraries", "net", "fabricmc", "fabric-loader", "fixture", "fabric-loader-fixture.jar")));
        });
        using (var curseClient = new HttpClient(new Handler((request, _) =>
        {
            Assert(request.Headers.GetValues("x-api-key").Single() == "mock-key");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":[{"modId":123,"id":456,"displayName":"Fixture","fileName":"fixture.jar","downloadUrl":null,"fileLength":42,"gameVersions":["1.20.1","Fabric"],"hashes":[{"algo":1,"value":"1234"}],"dependencies":[{"modId":789,"relationType":3}]}]}""") });
        })))
        {
            var result = await new CurseForgeProvider(curseClient, "mock-key").ReleasesAsync("123", "1.20.1", "fabric", CancellationToken.None);
            check("CurseForge API key header, compatibility, dependency and download restriction parsing", () => Assert(result.Single().Url is null && result[0].Dependencies.Single().ProjectId == "789"));
        }
        var rootMod = new ModRelease("root", "root-v1", "Root", "root.jar", "https://test.example/root", sha, data.Length, "SHA1", [new("dependency", null)]);
        var dependency = new ModRelease("dependency", "dep-v1", "Dependency", "dep.jar", "https://test.example/dep", sha, data.Length, "SHA1", []);
        var provider = new FixtureProvider(dependency);
        var resolved = await ModInstaller.ResolveAsync(provider, rootMod, "1.20.1", "fabric", CancellationToken.None);
        check("Required mod dependencies are resolved once", () => Assert(resolved.Count == 2));
        var instance = Path.Combine(gameRoot, "versions", "my-instance");
        await ModInstaller.InstallAsync(client, gameRoot, instance, "Fixture", resolved, null, CancellationToken.None);
        check("Mod files install into the chosen isolated instance", () => Assert(File.Exists(Path.Combine(instance, "mods", "root.jar")) && File.Exists(Path.Combine(instance, "mods", "dep.jar"))));
        var blocked = false;
        File.Move(Path.Combine(instance, "mods", "root.jar"), Path.Combine(instance, "mods", "root.jar.disabled"));
        try { await ModInstaller.InstallAsync(client, gameRoot, instance, "Fixture", resolved, null, CancellationToken.None); } catch (InvalidOperationException) { blocked = true; }
        check("Downloading an already disabled mod does not silently reactivate or duplicate it", () => Assert(blocked && !File.Exists(Path.Combine(instance, "mods", "root.jar"))));
        File.Move(Path.Combine(instance, "mods", "root.jar.disabled"), Path.Combine(instance, "mods", "root.jar"));
        blocked = false;
        try { await ModInstaller.ResolveAsync(provider, rootMod with { Url = null }, "1.20.1", "fabric", CancellationToken.None); } catch (InvalidOperationException) { blocked = true; }
        check("Third-party download restrictions are respected", () => Assert(blocked));
        blocked = false;
        try { await ModInstaller.ResolveAsync(provider, rootMod with { Filename = "../escape.jar" }, "1.20.1", "fabric", CancellationToken.None); } catch (InvalidDataException) { blocked = true; }
        check("Unsafe mod file paths are rejected", () => Assert(blocked));
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken); }
    private sealed class FixtureProvider(ModRelease dependency) : IModProvider
    {
        public string Name => "Fixture";
        public Task<IReadOnlyList<ModProject>> SearchAsync(string query, string version, string loader, CancellationToken token) => Task.FromResult<IReadOnlyList<ModProject>>([]);
        public Task<IReadOnlyList<ModRelease>> ReleasesAsync(string id, string version, string loader, CancellationToken token) => Task.FromResult<IReadOnlyList<ModRelease>>([dependency]);
        public Task<ModRelease> ExactAsync(string id, string version, string loader, CancellationToken token) => Task.FromResult(dependency);
    }
}

