using ACML.Core;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

public static class ForeignVersionTests
{
    public static async Task RunAsync(Action<string, Action> check, string root)
    {
        void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
        var game = Path.Combine(root, "foreign-versions");
        void Write(string id, JsonObject json) { var folder = Path.Combine(game, "versions", id); Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, id + ".json"), json.ToJsonString()); }
        byte[] clientBytes = Encoding.UTF8.GetBytes("shared vanilla client"), indexBytes = Encoding.UTF8.GetBytes("{\"objects\":{}}");
        JsonObject FileInfo(byte[] bytes, string path) => new() { ["url"] = "https://foreign.test/" + path, ["size"] = bytes.Length, ["sha1"] = Convert.ToHexString(SHA1.HashData(bytes)) };
        var parent = new JsonObject { ["id"] = "1.20.1", ["type"] = "release", ["mainClass"] = "net.minecraft.client.main.Main", ["javaVersion"] = new JsonObject { ["majorVersion"] = 17 },
            ["arguments"] = new JsonObject { ["game"] = new JsonArray("--username", "${auth_player_name}", "--gameDir", "${game_directory}"), ["jvm"] = new JsonArray("-cp", "${classpath}") },
            ["libraries"] = new JsonArray(new JsonObject { ["name"] = "example:dependency:1.0", ["url"] = "https://foreign.test/" }),
            ["downloads"] = new JsonObject { ["client"] = FileInfo(clientBytes, "client") }, ["assetIndex"] = FileInfo(indexBytes, "index") };
        parent["assetIndex"]!["id"] = "fixture";
        Write("1.20.1", parent); File.WriteAllBytes(Path.Combine(game, "versions", "1.20.1", "1.20.1.jar"), clientBytes);
        Directory.CreateDirectory(Path.Combine(game, "assets", "indexes")); File.WriteAllBytes(Path.Combine(game, "assets", "indexes", "fixture.json"), indexBytes);
        var child = new JsonObject { ["id"] = "foreign-fabric-id", ["inheritsFrom"] = "1.20.1", ["mainClass"] = "net.fabricmc.loader.impl.launch.knot.KnotClient",
            ["arguments"] = new JsonObject { ["game"] = new JsonArray("--custom", "yes") },
            ["libraries"] = new JsonArray(new JsonObject { ["name"] = "example:dependency:2.0", ["url"] = "https://foreign.test/" },
                new JsonObject { ["name"] = "net.fabricmc:fabric-loader:0.16.10", ["url"] = "https://foreign.test/" }) };
        Write("My Fabric", child); var original = File.ReadAllText(Path.Combine(game, "versions", "My Fabric", "My Fabric.json"));
        var resolved = VersionManifestResolver.Resolve(game, "My Fabric");
        foreach (var library in resolved.Manifest["libraries"]!.AsArray()) { var path = SafePaths.Under(game, "libraries/" + library!["downloads"]!["artifact"]!["path"]!.GetValue<string>()); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "cached library"); }
        var scanned = VersionService.Scan(game).Single(x => x.Id == "My Fabric");
        check("Foreign Fabric inheritance merges arguments, replaces conflicting libraries and uses the shared vanilla client", () =>
        {
            Assert(scanned.IsSupported && scanned.BaseId == "1.20.1" && scanned.LoaderVersion == "0.16.10" && scanned.JavaMajor == 17);
            Assert(resolved.ClientRelativePath == "versions/1.20.1/1.20.1.jar" && resolved.Manifest["libraries"]!.AsArray().Count == 2);
            Assert(ManifestRules.Arguments(resolved.Manifest["arguments"]!["game"]).SequenceEqual(new[] { "--username", "${auth_player_name}", "--gameDir", "${game_directory}", "--custom", "yes" }));
        });
        check("Launching a foreign inherited version uses its own game directory without changing its source manifest", () =>
        {
            var plan = GameLauncher.Prepare(new() { GameRoot = game }, PlayerAccount.Create("TestPlayer"), scanned, new("C:\\fixture\\java.exe", 17, true, "fixture"));
            Assert(plan.GameDirectory == Path.Combine(game, "versions", "My Fabric") && plan.StartInfo.ArgumentList.Contains("net.fabricmc.loader.impl.launch.knot.KnotClient"));
            Assert(plan.StartInfo.ArgumentList.Any(x => x.Contains(Path.Combine("1.20.1", "1.20.1.jar"))) && !File.Exists(Path.Combine(game, "versions", "My Fabric", "My Fabric.jar")));
            Assert(File.ReadAllText(Path.Combine(game, "versions", "My Fabric", "My Fabric.json")) == original); GameLauncher.CleanupNatives(plan.NativeDirectory);
        });
        File.WriteAllText(Path.Combine(game, "versions", "1.20.1", "1.20.1.jar"), "corrupt");
        using var http = new HttpClient(new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(request.RequestUri!.AbsolutePath == "/index" ? indexBytes : clientBytes) })));
        await new GameInstaller(http, "官方").RepairAsync(game, "My Fabric", null, default);
        check("Repair of a foreign inherited version fixes the shared client and preserves its original JSON", () =>
            Assert(File.ReadAllBytes(Path.Combine(game, "versions", "1.20.1", "1.20.1.jar")).SequenceEqual(clientBytes)
            && File.ReadAllText(Path.Combine(game, "versions", "My Fabric", "My Fabric.json")) == original && !File.Exists(Path.Combine(game, "versions", "My Fabric", "My Fabric.jar"))));
        foreach (var loader in new[] { "Forge", "NeoForge" })
        {
            var merged = (JsonObject)parent.DeepClone(); merged["id"] = "external-id"; merged["clientVersion"] = "1.20.1"; merged["mainClass"] = "cpw.mods.bootstraplauncher.BootstrapLauncher";
            merged["arguments"]!["game"]!.AsArray().Add(loader == "Forge" ? "--fml.forgeVersion" : "--fml.neoForgeVersion"); merged["arguments"]!["game"]!.AsArray().Add(loader == "Forge" ? "47.4.23" : "21.1.235");
            merged["libraries"] = new JsonArray(new JsonObject { ["name"] = loader == "Forge" ? "net.minecraftforge:fmlloader:1.20.1-47.4.23" : "net.neoforged.fancymodloader:loader:4.0.42" });
            Write("PCL " + loader, merged); File.WriteAllBytes(Path.Combine(game, "versions", "PCL " + loader, "PCL " + loader + ".jar"), clientBytes);
            var version = VersionService.Scan(game).Single(x => x.Id == "PCL " + loader);
            check("Merged " + loader + " metadata from another launcher reports real Minecraft and loader versions", () =>
                Assert(version.IsSupported && version.BaseId == "1.20.1" && version.Loader == loader && version.LoaderVersion == (loader == "Forge" ? "47.4.23" : "21.1.235") && !version.Description.Contains("Java")));
        }
        var quilt = (JsonObject)child.DeepClone(); quilt["libraries"] = new JsonArray(new JsonObject { ["name"] = "org.quiltmc:quilt-loader:0.28.1" }); Write("Quilt", quilt);
        check("Existing Quilt versions are recognized through their inherited loader library", () => { var version = VersionService.Scan(game).Single(x => x.Id == "Quilt"); Assert(version.IsSupported && version.Loader == "Quilt" && version.LoaderVersion == "0.28.1"); });
        Write("cycle-a", new() { ["id"] = "cycle-a", ["inheritsFrom"] = "cycle-b" }); Write("cycle-b", new() { ["id"] = "cycle-b", ["inheritsFrom"] = "cycle-a" });
        check("Circular inheritance is reported as an unusable version without crashing the version list", () => Assert(VersionService.Scan(game).Single(x => x.Id == "cycle-a").Error!.Contains("循环")));
        var legacy = (JsonObject)parent.DeepClone(); legacy.Remove("arguments"); legacy["minecraftArguments"] = "--username ${auth_player_name} --name \"two words\""; Write("Legacy args", legacy);
        check("Legacy quoted arguments are converted without splitting values containing spaces", () => Assert(ManifestRules.Arguments(VersionManifestResolver.Resolve(game, "Legacy args").Manifest["arguments"]!["game"]).Last() == "two words"));
        var renamed = (JsonObject)parent.DeepClone(); renamed["clientVersion"] = "1.20.1"; Write("Renamed", renamed); File.WriteAllBytes(Path.Combine(game, "versions", "Renamed", "1.20.1.jar"), clientBytes);
        check("Renamed folders can reuse an original-ID JAR without renaming existing files", () => Assert(VersionManifestResolver.Resolve(game, "Renamed").ClientRelativePath == "versions/Renamed/1.20.1.jar"));
        var duplicate = (JsonObject)resolved.Manifest.DeepClone(); duplicate["id"] = "Duplicate libraries";
        duplicate["libraries"]!.AsArray().Add(duplicate["libraries"]![0]!.DeepClone());
        Write("Duplicate libraries", duplicate); File.WriteAllBytes(Path.Combine(game, "versions", "Duplicate libraries", "Duplicate libraries.jar"), clientBytes);
        check("PCL merged manifests never emit duplicate physical JAR paths into the NeoForge classpath", () =>
        {
            var version = VersionService.Scan(game).Single(x => x.Id == "Duplicate libraries");
            var plan = GameLauncher.Prepare(new() { GameRoot = game }, PlayerAccount.Create("TestPlayer"), version, new("C:\\fixture\\java.exe", 17, true, "fixture"));
            try
            {
                var cp = plan.StartInfo.ArgumentList[plan.StartInfo.ArgumentList.IndexOf("-cp") + 1].Split(Path.PathSeparator);
                Assert(cp.Length == cp.Distinct(StringComparer.OrdinalIgnoreCase).Count() && cp.Length == 3);
                Assert(VersionService.Read(game, "Duplicate libraries")["libraries"]!.AsArray().Count == 3);
            }
            finally { GameLauncher.CleanupNatives(plan.NativeDirectory); }
        });
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => callback(request, token); }
}
