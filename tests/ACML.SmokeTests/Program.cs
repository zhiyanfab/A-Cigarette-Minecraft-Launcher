using ACML.Core;
using System.IO.Compression;
using System.Text.Json.Nodes;

var root = Path.Combine(Path.GetTempPath(), "acml-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
var failed = 0;
void Check(string name, Action action)
{
    try { action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
void Assert(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
try
{
    Check("Default game directory follows the actual executable, not the bundle cache", () => Assert(new LauncherSettings().GameRoot == Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, ".minecraft")));
    Check("Minecraft-compatible offline UUID", () => Assert(PlayerAccount.Create("Notch").Uuid == "b50ad385829d3141a2167e7d7539ba7f"));
    Check("Account names reject invalid input", () => { Throws<ArgumentException>(() => PlayerAccount.Create("ab")); Throws<ArgumentException>(() => PlayerAccount.Create("玩家 名称")); });
    Check("Multiple accounts and active identity persist atomically", () =>
    {
        var store = new SettingsStore(Path.Combine(root, "data"));
        var first = PlayerAccount.Create("PlayerOne"); var second = PlayerAccount.Create("PlayerTwo");
        store.Save(new LauncherSettings { Accounts = [first, second], ActiveAccountId = second.Id });
        var loaded = store.Load(); Assert(loaded.Accounts.Count == 2 && loaded.ActiveAccountId == second.Id);
        Assert(!File.Exists(Path.Combine(root, "data", "settings.json.tmp")));
        File.WriteAllText(Path.Combine(root, "data", "settings.json"), "broken");
        Throws<InvalidDataException>(() => store.Load());
        Assert(File.ReadAllText(Path.Combine(root, "data", "settings.json")) == "broken");
    });
    Check("Path traversal rejected", () => { Throws<InvalidDataException>(() => SafePaths.Under(root, "../escape")); Throws<InvalidDataException>(() => SafePaths.Under(root, Path.GetPathRoot(root)! + "escape")); Assert(SafePaths.Under(root, "assets/indexes/test.json").StartsWith(root)); });
    Check("OS and feature rules", () =>
    {
        Assert(ManifestRules.Allowed(null));
        Assert(!ManifestRules.Allowed(JsonNode.Parse("""[{"action":"allow","os":{"name":"linux"}}]""")));
        Assert(ManifestRules.Allowed(JsonNode.Parse("""[{"action":"allow"},{"action":"disallow","os":{"name":"osx"}}]""")));
        Assert(!ManifestRules.Allowed(JsonNode.Parse("""[{"action":"allow","os":{"arch":"x86"}}]""")));
        Assert(!ManifestRules.Allowed(JsonNode.Parse("""[{"action":"allow","features":{"is_demo_user":true}}]""")));
        Assert(ManifestRules.Allowed(JsonNode.Parse("""[{"action":"allow","features":{"has_custom_resolution":true}}]""")));
    });
    Check("Conditional arguments and unknown variable rejection", () =>
    {
        var args = ManifestRules.Arguments(JsonNode.Parse("""["--username", "${auth_player_name}", {"rules":[{"action":"allow","features":{"is_demo_user":true}}],"value":"--demo"}]""")).ToList();
        Assert(args.Count == 2); Assert(ManifestRules.Expand(args[1], new Dictionary<string, string> { ["auth_player_name"] = "PlayerOne" }) == "PlayerOne");
        Throws<InvalidDataException>(() => ManifestRules.Expand("${missing}", new Dictionary<string, string>()));
    });
    Check("Legacy account data migrates without turning offline accounts into Microsoft accounts", () =>
    {
        var old = System.Text.Json.JsonSerializer.Deserialize<LauncherSettings>("""{"SchemaVersion":1,"Accounts":[{"Id":"old","Name":"LegacyPlayer","Uuid":"b50ad385829d3141a2167e7d7539ba7f"}]}""")!;
        Assert(!old.Accounts[0].IsMicrosoft && old.Accounts[0].MicrosoftAccountId is null);
    });
    Check("Background choice and dim level persist", () =>
    {
        var store = new SettingsStore(Path.Combine(root, "background-data"));
        store.Save(new LauncherSettings { BackgroundImagePath = "C:\\test image.png", BackgroundDim = 0.42 });
        Assert(store.Load().BackgroundImagePath == "C:\\test image.png" && store.Load().BackgroundDim == 0.42);
    });
    Check("Official Microsoft-Xbox-Minecraft exchange and entitlement checks", () =>
    {
        var step = 0;
        using var http = new HttpClient(new MockHandler(async (request, cancellation) =>
        {
            step++;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellation);
            var host = request.RequestUri!.Host;
            if (step == 1)
            {
                var payload = JsonNode.Parse(body)!.AsObject();
                Assert(host == "user.auth.xboxlive.com" && payload["Properties"]?["AuthMethod"]?.GetValue<string>() == "RPS", "Xbox requires exact PascalCase JSON field names");
                Assert(payload["Properties"]?["SiteName"]?.GetValue<string>() == "user.auth.xboxlive.com");
                Assert(payload["Properties"]?["RpsTicket"]?.GetValue<string>() == "d=mock-ms-token");
                Assert(payload["RelyingParty"]?.GetValue<string>() == "http://auth.xboxlive.com" && payload["TokenType"]?.GetValue<string>() == "JWT");
                Assert(request.Headers.GetValues("x-xbl-contract-version").Single() == "1");
                Assert(request.Headers.Accept.Any(x => x.MediaType == "application/json"));
                return Json("""{"Token":"mock-xbox-token"}""");
            }
            if (step == 2)
            {
                var payload = JsonNode.Parse(body)!.AsObject();
                Assert(host == "xsts.auth.xboxlive.com" && payload["Properties"]?["SandboxId"]?.GetValue<string>() == "RETAIL");
                Assert(payload["Properties"]?["UserTokens"]?[0]?.GetValue<string>() == "mock-xbox-token");
                Assert(payload["RelyingParty"]?.GetValue<string>() == "rp://api.minecraftservices.com/" && payload["TokenType"]?.GetValue<string>() == "JWT");
                return Json("""{"Token":"mock-xsts-token","DisplayClaims":{"xui":[{"uhs":"userhash","xid":"1234"}]}}""");
            }
            if (step == 3)
            {
                var payload = JsonNode.Parse(body)!.AsObject();
                Assert(host == "api.minecraftservices.com" && payload["identityToken"]?.GetValue<string>() == "XBL3.0 x=userhash;mock-xsts-token");
                Assert(payload["ensureLegacyEnabled"]?.GetValue<bool>() == true);
                return Json("""{"access_token":"mock-minecraft-token","expires_in":86400}""");
            }
            Assert(request.Headers.Authorization?.Parameter == "mock-minecraft-token");
            if (step == 4) return Json("""{"items":[{"name":"game_minecraft"}]}""");
            Assert(step == 5); return Json("""{"id":"b50ad385829d3141a2167e7d7539ba7f","name":"Notch"}""");
        }));
        var session = new MinecraftAuthentication(http).ExchangeAsync("mock-ms-token", CancellationToken.None).GetAwaiter().GetResult();
        Assert(step == 5 && session.IsValid && session.Name == "Notch" && session.Xuid == "1234");
        Assert(!session.ToString().Contains("mock-minecraft-token"));
    });
    Check("Xbox error response is translated without leaking response secrets", () =>
    {
        using var http = new HttpClient(new MockHandler((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized) { Content = new StringContent("""{"XErr":2148916233,"debug":"mock-secret-should-not-leak"}""") })));
        try { new MinecraftAuthentication(http).ExchangeAsync("mock-ms-token", CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("Expected rejection"); }
        catch (InvalidOperationException ex) { Assert(ex.Message.Contains("Xbox 档案") && !ex.Message.Contains("mock-secret")); }
    });
    Check("Xbox HTTP 400 reports only its numeric error code and hides response secrets", () =>
    {
        using var http = new HttpClient(new MockHandler((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest) { Content = new StringContent("""{"XErr":1234567890,"Message":"mock-secret-token"}""") })));
        try { new MinecraftAuthentication(http).ExchangeAsync("mock-ms-token", CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("Expected rejection"); }
        catch (InvalidOperationException ex) { Assert(ex.Message.Contains("HTTP 400") && ex.Message.Contains("1234567890") && !ex.Message.Contains("mock-secret-token") && !ex.Message.Contains("mock-ms-token")); }
    });
    Check("Xbox HTTP 400 with an empty or non-object response still gives a safe authentication error", () =>
    {
        foreach (var response in new[] { "", "[]", "<html>mock-secret</html>", "{\"XErr\":\"mock-secret\"}" })
        {
            using var http = new HttpClient(new MockHandler((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest) { Content = new StringContent(response) })));
            try { new MinecraftAuthentication(http).ExchangeAsync("mock-ms-token", CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("Expected rejection"); }
            catch (InvalidOperationException ex) { Assert(ex.Message.Contains("HTTP 400") && !ex.Message.Contains("mock-secret") && !ex.Message.Contains("mock-ms-token")); }
        }
    });
    Check("Missing entitlement rejects account before profile becomes usable", () =>
    {
        var step = 0;
        using var http = new HttpClient(new MockHandler((_, _) => Task.FromResult(++step switch
        {
            1 => Json("""{"Token":"xbox"}"""), 2 => Json("""{"Token":"xsts","DisplayClaims":{"xui":[{"uhs":"h"}]}}"""),
            3 => Json("""{"access_token":"mc","expires_in":86400}"""), _ => Json("""{"items":[]}""")
        })));
        Throws<InvalidOperationException>(() => new MinecraftAuthentication(http).ExchangeAsync("ms", CancellationToken.None).GetAwaiter().GetResult()); Assert(step == 4);
    });
    Check("Authentication cancellation propagates", () =>
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var http = new HttpClient(new MockHandler((_, ct) => Task.FromCanceled<HttpResponseMessage>(ct)));
        Throws<OperationCanceledException>(() => new MinecraftAuthentication(http).ExchangeAsync("ms", cancellation.Token).GetAwaiter().GetResult());
    });
    var gameRoot = Path.Combine(root, "game files with spaces");
    Directory.CreateDirectory(Path.Combine(gameRoot, "versions", "fixture"));
    Directory.CreateDirectory(Path.Combine(gameRoot, "assets", "indexes"));
    File.WriteAllText(Path.Combine(gameRoot, "versions", "fixture", "fixture.jar"), "fixture");
    File.WriteAllText(Path.Combine(gameRoot, "assets", "indexes", "fixture.json"), """{"objects":{}}""");
    var manifest = JsonNode.Parse("""{"id":"fixture","type":"release","mainClass":"net.minecraft.client.main.Main","javaVersion":{"majorVersion":21},"assetIndex":{"id":"fixture"},"libraries":[],"arguments":{"jvm":["-Djava.library.path=${natives_directory}","-cp","${classpath}"],"game":["--username","${auth_player_name}","--uuid","${auth_uuid}","--accessToken","${auth_access_token}","--gameDir","${game_directory}",{"rules":[{"action":"allow","features":{"has_custom_resolution":true}}],"value":["--width","${resolution_width}","--height","${resolution_height}"]}]}}""")!;
    var manifestPath = Path.Combine(gameRoot, "versions", "fixture", "fixture.json");
    File.WriteAllText(manifestPath, manifest.ToJsonString());
    var settings = new LauncherSettings { GameRoot = gameRoot, MemoryMb = 4096 };
    var java = new JavaRuntime("C:\\fixture\\java.exe", 21, true, "test double; not executed");
    var account = PlayerAccount.Create("DemoPlayer");
    GameVersion Version() => VersionService.Scan(gameRoot).First(x => x.Id == "fixture");
    Check("Empty version directory yields no fake entries", () => Assert(VersionService.Scan(Path.Combine(root, "empty")).Count == 0));
    Check("Vanilla manifest scan and Java requirement", () => Assert(Version().IsSupported && Version().JavaMajor == 21));
    Check("Launch plan uses argument list and isolated game directory", () =>
    {
        var plan = GameLauncher.Prepare(settings, account, Version(), java);
        try
        {
            Assert(plan.StartInfo.ArgumentList.Contains("-Xmx4096M")); Assert(plan.StartInfo.ArgumentList.Contains(account.Uuid));
            Assert(plan.StartInfo.ArgumentList.Contains(Path.Combine(gameRoot, "versions", "fixture", "fixture.jar")));
            Assert(plan.GameDirectory == Path.Combine(gameRoot, "versions", "fixture"));
            Assert(!plan.StartInfo.UseShellExecute && plan.StartInfo.Arguments == "");
        }
        finally { GameLauncher.CleanupNatives(plan.NativeDirectory); }
    });
    Check("Shared directory mode", () =>
    {
        settings.IsolateVersions = false; var plan = GameLauncher.Prepare(settings, account, Version(), java);
        try { Assert(plan.GameDirectory == gameRoot); } finally { GameLauncher.CleanupNatives(plan.NativeDirectory); settings.IsolateVersions = true; }
    });
    Check("Game preparation reports file validation and finishes at full progress", () =>
    {
        var reports = new List<GamePreparationProgress>();
        var plan = GameLauncher.Prepare(settings, account, Version(), java, progress: new InlineProgress<GamePreparationProgress>(reports.Add));
        try { Assert(reports.First().Percent == 0 && reports.Last().Percent == 100 && reports.Any(x => x.Stage == "验证游戏客户端")); }
        finally { GameLauncher.CleanupNatives(plan.NativeDirectory); }
    });
    Check("Canceling preparation during native setup cleans the generated native directory", () =>
    {
        using var cancel = new CancellationTokenSource();
        Throws<OperationCanceledException>(() => GameLauncher.Prepare(settings, account, Version(), java,
            progress: new InlineProgress<GamePreparationProgress>(x => { if (x.Percent >= 90) cancel.Cancel(); }), cancellationToken: cancel.Token));
        var nativeRoot = Path.Combine(gameRoot, ".acml", "natives");
        Assert(!Directory.Exists(nativeRoot) || !Directory.EnumerateDirectories(nativeRoot).Any());
    });
    Check("Microsoft launch requires a matching unexpired session", () =>
    {
        var online = account with { IsMicrosoft = true, MicrosoftAccountId = "mock-account" };
        Throws<InvalidOperationException>(() => GameLauncher.Prepare(settings, online, Version(), java));
        var session = new GameSession { Name = online.Name, Uuid = online.Uuid, AccessToken = "mock-minecraft-token", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
        var plan = GameLauncher.Prepare(settings, online, Version(), java, session);
        try { Assert(plan.StartInfo.ArgumentList.Contains("mock-minecraft-token") && !plan.StartInfo.ArgumentList.Contains("0")); }
        finally { GameLauncher.CleanupNatives(plan.NativeDirectory); }
        var expired = new GameSession { Name = online.Name, Uuid = online.Uuid, AccessToken = "mock", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        Throws<InvalidOperationException>(() => GameLauncher.Prepare(settings, online, Version(), java, expired));
    });
    Check("Incompatible Java and memory rejected", () => { Throws<InvalidOperationException>(() => GameLauncher.Prepare(settings, account, Version(), java with { Major = 17 })); Throws<InvalidOperationException>(() => GameLauncher.Prepare(settings, account, Version(), java with { Is64Bit = false })); settings.MemoryMb = 100; Throws<InvalidOperationException>(() => GameLauncher.Prepare(settings, account, Version(), java)); settings.MemoryMb = 4096; });
    Check("Missing assets stop launch", () =>
    {
        File.WriteAllText(Path.Combine(gameRoot, "assets", "indexes", "fixture.json"), """{"objects":{"test":{"hash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","size":1}}}""");
        Throws<FileNotFoundException>(() => GameLauncher.Prepare(settings, account, Version(), java));
        File.WriteAllText(Path.Combine(gameRoot, "assets", "indexes", "fixture.json"), """{"objects":{}}""");
    });
    Check("Corrupt client digest stops launch", () =>
    {
        manifest["downloads"] = JsonNode.Parse("""{"client":{"sha1":"0000000000000000000000000000000000000000"}}"""); File.WriteAllText(manifestPath, manifest.ToJsonString());
        Throws<InvalidDataException>(() => GameLauncher.Prepare(settings, account, Version(), java));
        manifest.AsObject().Remove("downloads"); File.WriteAllText(manifestPath, manifest.ToJsonString());
    });
    Check("Loader versions are not falsely launchable", () =>
    {
        manifest["inheritsFrom"] = "base"; File.WriteAllText(manifestPath, manifest.ToJsonString()); Assert(!Version().IsSupported);
        manifest.AsObject().Remove("inheritsFrom"); File.WriteAllText(manifestPath, manifest.ToJsonString());
    });
    Check("Native ZIP traversal rejected and temporary directory cleaned", () =>
    {
        Directory.CreateDirectory(Path.Combine(gameRoot, "libraries"));
        using (var zip = ZipFile.Open(Path.Combine(gameRoot, "libraries", "native.jar"), ZipArchiveMode.Create)) using (var writer = new StreamWriter(zip.CreateEntry("../escape.dll").Open())) writer.Write("test");
        manifest["libraries"] = JsonNode.Parse("""[{"natives":{"windows":"natives-windows"},"downloads":{"classifiers":{"natives-windows":{"path":"native.jar"}}}}]"""); File.WriteAllText(manifestPath, manifest.ToJsonString());
        Throws<InvalidDataException>(() => GameLauncher.Prepare(settings, account, Version(), java));
        Assert(!File.Exists(Path.Combine(gameRoot, ".acml", "natives", "escape.dll")));
        Assert(!Directory.EnumerateDirectories(Path.Combine(gameRoot, ".acml", "natives")).Any());
    });
    await DownloadTests.RunAsync(Check, root);
    await InstanceTests.RunAsync(Check, root);
    await LoaderAndModsTests.RunAsync(Check);
    await RootsAndParallelTests.RunAsync(Check, root);
    await StartupMonitorTests.RunAsync(Check);
    await LaunchLayoutTests.RunAsync(Check, root);
    await ModpackTests.RunAsync(Check, root);
    await ForeignVersionTests.RunAsync(Check, root);
    JavaAndDiagnosticsTests.Run(Check, root);
    if (args.Contains("--online"))
    {
        var catalog = await VersionService.FetchCatalogAsync("自动", CancellationToken.None);
        Check("Live version catalog returns latest release and snapshot", () => Assert(catalog.Versions.Any(x => x.Id == catalog.LatestRelease) && catalog.Versions.Any(x => x.Id == catalog.LatestSnapshot)));
        Console.WriteLine("Catalog source: " + catalog.Source + "; count: " + catalog.Versions.Count);
    }
}
finally { Directory.Delete(root, true); }
Console.WriteLine($"RESULT {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

static HttpResponseMessage Json(string value) => new(System.Net.HttpStatusCode.OK) { Content = new StringContent(value, System.Text.Encoding.UTF8, "application/json") };
sealed class MockHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken);
}
sealed class InlineProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }

