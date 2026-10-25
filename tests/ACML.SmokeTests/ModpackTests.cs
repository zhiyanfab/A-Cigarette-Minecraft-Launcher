using ACML.Core;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

public static class ModpackTests
{
    public static async Task RunAsync(Action<string, Action> check, string root)
    {
        void Assert(bool value) { if (!value) throw new Exception("Assertion failed"); }
        var source = Path.Combine(root, "pack-source"); var folder = Path.Combine(source, "versions", "Source"); Directory.CreateDirectory(Path.Combine(folder, "mods")); Directory.CreateDirectory(Path.Combine(folder, "config")); Directory.CreateDirectory(Path.Combine(folder, "saves", "world"));
        var bytes = Encoding.UTF8.GetBytes("client-fixture"); var sha = Convert.ToHexString(SHA1.HashData(bytes)); var assets = Encoding.UTF8.GetBytes("{\"objects\":{}}"); var assetHash = Convert.ToHexString(SHA1.HashData(assets));
        var manifest = new JsonObject { ["id"] = "1.20.1", ["type"] = "release", ["mainClass"] = "net.minecraft.client.main.Main", ["javaVersion"] = new JsonObject { ["majorVersion"] = 17 },
            ["arguments"] = new JsonObject { ["game"] = new JsonArray("--username", "${auth_player_name}"), ["jvm"] = new JsonArray("-cp", "${classpath}") }, ["libraries"] = new JsonArray(),
            ["assetIndex"] = new JsonObject { ["id"] = "fixture", ["url"] = "https://pack.test/index", ["sha1"] = assetHash, ["size"] = assets.Length },
            ["downloads"] = new JsonObject { ["client"] = new JsonObject { ["url"] = "https://pack.test/client", ["sha1"] = sha, ["size"] = bytes.Length } } };
        var remoteBytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        var localManifest = (JsonObject)manifest.DeepClone(); localManifest["id"] = "Source"; localManifest["acmlBaseVersion"] = "1.20.1";
        File.WriteAllText(Path.Combine(folder, "Source.json"), localManifest.ToJsonString()); File.WriteAllBytes(Path.Combine(folder, "Source.jar"), bytes);
        File.WriteAllText(Path.Combine(folder, "mods", "local-mod.jar"), "mod bytes"); File.WriteAllText(Path.Combine(folder, "config", "example.json"), "config bytes"); File.WriteAllText(Path.Combine(folder, "saves", "world", "level.dat"), "world bytes");
        var settings = new LauncherSettings { GameRoot = source, MemoryMb = 8192, GameWindowTitle = "Global" };
        InstanceManager.SaveSettings(source, "Source", new() { Enabled = true, MemoryMb = 2304, GameWindowTitle = "Pack Title", OverrideTitle = true, JavaPath = "C:\\private\\java.exe" });
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(request.RequestUri!.AbsolutePath switch { "/manifest" => remoteBytes, "/index" => assets, _ => bytes }) })));
        var remote = new RemoteVersion("1.20.1", "release", DateTimeOffset.UtcNow, "https://pack.test/manifest", Convert.ToHexString(SHA1.HashData(remoteBytes)));
        foreach (var format in new[] { "Modrinth", "CurseForge" })
        {
            var archive = Path.Combine(root, "roundtrip-" + format + (format == "Modrinth" ? ".mrpack" : ".zip"));
            await ModpackService.ExportAsync(client, settings, "Source", archive, format, false, null, null, default);
            var info = ModpackService.Inspect(archive);
            check(format + " export creates its standard manifest and excludes saves, client JAR and machine Java paths", () =>
            {
                using var zip = ZipFile.OpenRead(archive); using var reader = new StreamReader(zip.GetEntry("acml.instance.json")!.Open());
                Assert(info.Minecraft == "1.20.1" && info.Loader == "原版" && zip.GetEntry("overrides/mods/local-mod.jar") is not null);
                Assert(!zip.Entries.Any(x => x.FullName.Contains("saves/")) && zip.GetEntry("overrides/Source.jar") is null && !reader.ReadToEnd().Contains("private"));
            });
            var target = new LauncherSettings { GameRoot = Path.Combine(root, "import-" + format), MemoryMb = 4096 };
            await ModpackService.ImportAsync(client, target, archive, "Imported", null, null, default, remote);
            check(format + " one-click import installs a fresh game instance, restores mods/config and keeps portable instance settings", () =>
            {
                var directory = InstanceManager.GameDirectory(target, "Imported"); var effective = InstanceManager.EffectiveSettings(target, "Imported");
                Assert(directory == Path.Combine(target.GameRoot, "versions", "Imported") && File.ReadAllText(Path.Combine(directory, "mods", "local-mod.jar")) == "mod bytes");
                Assert(File.ReadAllText(Path.Combine(directory, "config", "example.json")) == "config bytes" && effective.MemoryMb == 2304 && effective.GameWindowTitle == "Pack Title" && effective.IsolateVersions);
                Assert(VersionService.Scan(target.GameRoot).Single().IsSupported && !Directory.EnumerateFiles(directory, "*.part", SearchOption.AllDirectories).Any());
            });
            var rejected = false; try { await ModpackService.ImportAsync(client, target, archive, "Imported", null, null, default, remote); } catch (InvalidOperationException) { rejected = true; }
            check(format + " import never overwrites an existing version", () => Assert(rejected && File.ReadAllText(Path.Combine(target.GameRoot, "versions", "Imported", "mods", "local-mod.jar")) == "mod bytes"));
        }
        var withSaves = Path.Combine(root, "with-saves.mrpack"); await ModpackService.ExportAsync(client, settings, "Source", withSaves, "Modrinth", true, null, null, default);
        check("Pack export includes worlds only when explicitly selected", () => { using var zip = ZipFile.OpenRead(withSaves); Assert(zip.GetEntry("overrides/saves/world/level.dat") is not null); });
        var unsafeArchive = Path.Combine(root, "unsafe-pack.zip");
        using (var zip = ZipFile.Open(unsafeArchive, ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry("../outside.txt").Open()); writer.Write("unsafe"); }
        var unsafeRejected = false; try { ModpackService.Inspect(unsafeArchive); } catch (InvalidDataException) { unsafeRejected = true; }
        check("Pack archive traversal is rejected before creating a version or writing outside staging", () => Assert(unsafeRejected && !File.Exists(Path.Combine(root, "outside.txt"))));
        var inherited = new LauncherSettings { GameRoot = Path.Combine(root, "per-option"), JavaPath = "global-java", MemoryMb = 8192, GameWindowTitle = "Global title", IsolateVersions = false };
        var initial = InstanceManager.LoadSettings(inherited, "test");
        check("New instance options inherit global Java, memory, title and isolation by default", () => Assert(!initial.Enabled && !initial.OverrideJava && !initial.OverrideMemory && !initial.OverrideTitle && !initial.OverrideIsolation));
        InstanceManager.SaveSettings(inherited.GameRoot, "test", new() { Enabled = true, OverrideJava = false, OverrideMemory = true, MemoryMb = 2048, OverrideTitle = true, GameWindowTitle = "Custom title", OverrideIsolation = false });
        var effectiveSettings = InstanceManager.EffectiveSettings(inherited, "test");
        check("Each overridden instance option is independent of the other global settings", () => Assert(effectiveSettings.JavaPath == "global-java" && effectiveSettings.MemoryMb == 2048 && effectiveSettings.GameWindowTitle == "Custom title" && !effectiveSettings.IsolateVersions));
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => callback(request, token); }
}
