using ACML.Core;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

public static class LaunchLayoutTests
{
    public static async Task RunAsync(Action<string, Action> check, string root)
    {
        void Assert(bool value) { if (!value) throw new Exception("Assertion failed"); }
        check("Root selection accepts the Minecraft root, versions folder, version folder or containing launcher folder", () =>
        {
            var home = Path.Combine(root, "launcher-layout"); var game = Path.Combine(home, ".minecraft");
            var version = Path.Combine(game, "versions", "test"); Directory.CreateDirectory(version); File.WriteAllText(Path.Combine(version, "test.json"), "{}");
            Assert(GameRootManager.ResolveInputPath(home) == game && GameRootManager.ResolveInputPath(game) == game);
            Assert(GameRootManager.ResolveInputPath(Path.Combine(game, "versions")) == game && GameRootManager.ResolveInputPath(version) == game);
        });
        check("Legacy isolation imports saves and mods into versions while preserving backups and metadata", () =>
        {
            var game = Path.Combine(root, "legacy-layout"); var version = Path.Combine(game, "versions", "test"); Directory.CreateDirectory(version);
            File.WriteAllText(Path.Combine(version, "test.json"), "original manifest");
            var old = Path.Combine(game, "instances", "test"); Directory.CreateDirectory(Path.Combine(old, "saves", "world")); Directory.CreateDirectory(Path.Combine(old, "mods"));
            File.WriteAllText(Path.Combine(old, "saves", "world", "level.dat"), "world"); File.WriteAllText(Path.Combine(old, "mods", "mod.jar"), "mod");
            var settings = new LauncherSettings { GameRoot = game }; InstanceManager.PrepareGameDirectory(settings, "test");
            Assert(InstanceManager.GameDirectory(settings, "test") == version && File.ReadAllText(Path.Combine(version, "test.json")) == "original manifest");
            Assert(File.ReadAllText(Path.Combine(version, "saves", "world", "level.dat")) == "world" && File.Exists(Path.Combine(old, "saves", "world", "level.dat")));
            File.Move(Path.Combine(version, "mods", "mod.jar"), Path.Combine(version, "mods", "mod.jar.disabled"));
            InstanceManager.PrepareGameDirectory(settings, "test"); Assert(!File.Exists(Path.Combine(version, "mods", "mod.jar")));
        });
        check("Conflicting old and version-folder data is retained without silently overwriting either copy", () =>
        {
            var game = Path.Combine(root, "layout-conflict"); var version = Path.Combine(game, "versions", "test"); var old = Path.Combine(game, "instances", "test");
            Directory.CreateDirectory(version); Directory.CreateDirectory(old);
            File.WriteAllText(Path.Combine(version, "options.txt"), "new"); File.WriteAllText(Path.Combine(old, "options.txt"), "old");
            var rejected = false; try { InstanceManager.PrepareGameDirectory(new() { GameRoot = game }, "test"); } catch (IOException) { rejected = true; }
            Assert(rejected && File.ReadAllText(Path.Combine(version, "options.txt")) == "new" && File.ReadAllText(Path.Combine(old, "options.txt")) == "old");
        });
        var prerequisiteRoot = Path.Combine(root, "logging-layout"); var folder = Path.Combine(prerequisiteRoot, "versions", "test"); Directory.CreateDirectory(folder);
        var bytes = Encoding.UTF8.GetBytes("verified-log4j-config"); var hash = Convert.ToHexString(SHA1.HashData(bytes));
        var manifest = new JsonObject { ["id"] = "test", ["logging"] = new JsonObject { ["client"] = new JsonObject { ["file"] = new JsonObject
        { ["id"] = "client-1.12.xml", ["url"] = "https://test.example/log.xml", ["sha1"] = hash, ["size"] = bytes.Length } } } };
        File.WriteAllText(Path.Combine(folder, "test.json"), manifest.ToJsonString());
        var requests = 0;
        using var client = new HttpClient(new Handler((_, _) => { requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }); }));
        var settingsForLogging = new LauncherSettings { GameRoot = prerequisiteRoot };
        await LaunchResources.EnsureLoggingAsync(client, settingsForLogging, "test", null, default);
        await LaunchResources.EnsureLoggingAsync(client, settingsForLogging, "test", null, default);
        var loggingFile = Path.Combine(prerequisiteRoot, "assets", "log_configs", "client-1.12.xml");
        check("Missing client-1.12.xml is downloaded into the shared root assets and validated once", () => Assert(requests == 1 && File.ReadAllBytes(loggingFile).SequenceEqual(bytes)));
        File.WriteAllText(loggingFile, "corrupt"); await LaunchResources.EnsureLoggingAsync(client, settingsForLogging, "test", null, default);
        check("A corrupt logging configuration is replaced only by a verified complete file", () => Assert(requests == 2 && File.ReadAllBytes(loggingFile).SequenceEqual(bytes)));
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => callback(request, token); }
}
