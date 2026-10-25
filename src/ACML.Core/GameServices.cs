using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ACML.Core;

public sealed record JavaRuntime(string Path, int Major, bool Is64Bit, string Description, string Version = "")
{
    public override string ToString() => $"Java {(Version.Length > 0 ? Version : Major)} · {(Is64Bit ? "64 位" : "未确认 64 位")} · {Path}";
}

public static class JavaService
{
    public static async Task<JavaRuntime> InspectAsync(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path) || !new[] { "java.exe", "javaw.exe" }.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("请选择 Java 的 java.exe 或 javaw.exe。");
        var consolePath = Path.Combine(Path.GetDirectoryName(path)!, "java.exe");
        if (!File.Exists(consolePath)) consolePath = path;
        using var process = new Process { StartInfo = new(consolePath, "-version") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true } };
        process.Start();
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch { } throw new InvalidOperationException("Java 检测超时。"); }
        var description = (await error) + (await output);
        var match = Regex.Match(description, "(?:version\\s+\"|openjdk\\s+)(?<v>\\d+)(?:\\.(?<minor>\\d+))?");
        if (process.ExitCode != 0 || !match.Success) throw new InvalidOperationException("无法读取 Java 版本，请检查 Java 安装。");
        var major = int.Parse(match.Groups["v"].Value);
        if (major == 1) major = int.Parse(match.Groups["minor"].Value);
        var fullVersion = Regex.Match(description, "(?:version\\s+\"|openjdk\\s+)(?<v>\\d[^\"\\s]*)").Groups["v"].Value;
        return new(path, major, description.Contains("64-Bit", StringComparison.OrdinalIgnoreCase) || description.Contains("aarch64", StringComparison.OrdinalIgnoreCase), description.Trim(), fullVersion);
    }

    public static Task<IReadOnlyList<JavaRuntime>> DiscoverAsync(string? preferred = null, IEnumerable<string>? gameRoots = null, CancellationToken cancellationToken = default)
        => JavaDiscovery.DiscoverAsync(preferred, gameRoots, cancellationToken);
}

public static class VersionService
{
    public static IReadOnlyList<GameVersion> Scan(string root)
    {
        var directory = Path.Combine(root, "versions");
        if (!Directory.Exists(directory)) return [];
        var result = new List<GameVersion>();
        foreach (var folder in Directory.EnumerateDirectories(directory))
        {
            var id = Path.GetFileName(folder);
            try
            {
                var resolved = VersionManifestResolver.Resolve(root, id); var json = resolved.Manifest;
                var main = json["mainClass"]?.GetValue<string>() ?? "";
                var text = json.ToJsonString();
                var loader = json["acmlLoader"]?.GetValue<string>() ?? (text.Contains("quiltmc", StringComparison.OrdinalIgnoreCase) ? "Quilt" : text.Contains("neoforged", StringComparison.OrdinalIgnoreCase) ? "NeoForge"
                    : text.Contains("fabricmc", StringComparison.OrdinalIgnoreCase) ? "Fabric"
                    : main.Contains("launchwrapper", StringComparison.OrdinalIgnoreCase) ? "Forge"
                    : text.Contains("minecraftforge", StringComparison.OrdinalIgnoreCase) ? "Forge"
                    : json["inheritsFrom"] is not null ? "自定义" : "原版");
                string? error = null;
                if (loader is not ("原版" or "Fabric" or "Forge" or "NeoForge" or "Quilt")) error = "暂不支持此加载器。";
                else if (json["arguments"] is null) error = "此 Demo 先支持使用现代参数清单的原版版本（通常为 1.13 及以上）。";
                else if (!File.Exists(SafePaths.Under(root, resolved.ClientRelativePath))) error = "缺少客户端 JAR，请先补全游戏文件。";
                var baseId = json["acmlBaseVersion"]?.GetValue<string>() ?? json["inheritsFrom"]?.GetValue<string>() ?? id;
                var loaderVersion = json["acmlLoaderVersion"]?.GetValue<string>();
                try { var metadata = ModpackService.InstanceDependencies(json); baseId = metadata.Minecraft; loaderVersion ??= metadata.Version; } catch (InvalidDataException) { }
                result.Add(new(id, json["type"]?.GetValue<string>() ?? "unknown", loader, json["javaVersion"]?["majorVersion"]?.GetValue<int>() ?? 8, error, baseId, loaderVersion));
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException or InvalidDataException)
            { result.Add(new(id, "unknown", "未知", 0, "清单无法读取：" + ex.Message)); }
        }
        return result.OrderByDescending(x => x.IsSupported).ThenByDescending(x => x.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static JsonObject Read(string root, string id)
    {
        var file = SafePaths.Under(root, $"versions/{id}/{id}.json");
        var json = JsonNode.Parse(File.ReadAllText(file))?.AsObject() ?? throw new InvalidDataException("游戏清单为空。");
        if (json["id"]?.GetValue<string>() is string original && original != id) { json["acmlOriginalId"] = original; json["id"] = id; }
        return json;
    }

    public static async Task<VersionCatalog> FetchCatalogAsync(string source, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ACML/" + LauncherInfo.Version);
        var sources = source == "BMCLAPI" ? new[] { ("BMCLAPI", "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json") }
            : source == "官方" ? new[] { ("Minecraft 官方", "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json") }
            : new[] { ("Minecraft 官方", "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"), ("BMCLAPI", "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json") };
        var errors = new List<string>();
        foreach (var (name, url) in sources)
        {
            try
            {
                var json = JsonNode.Parse(await client.GetStringAsync(url, cancellationToken))!;
                var versions = json["versions"]!.AsArray().Select(x => new RemoteVersion(x!["id"]!.GetValue<string>(), x["type"]!.GetValue<string>(), DateTimeOffset.Parse(x["releaseTime"]!.GetValue<string>()), x["url"]!.GetValue<string>(), x["sha1"]?.GetValue<string>())).ToList();
                return new(json["latest"]!["release"]!.GetValue<string>(), json["latest"]!["snapshot"]!.GetValue<string>(), versions, name);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
            { errors.Add(name + "：" + ex.Message); }
        }
        throw new IOException("版本列表获取失败。" + string.Join("；", errors));
    }
}

public static class ManifestRules
{
    public static bool Allowed(JsonNode? rules)
    {
        if (rules is null) return true;
        var allowed = false;
        foreach (var rule in rules.AsArray())
        {
            if (rule is null) continue;
            var matches = true;
            if (rule["os"] is JsonNode os)
            {
                if (os["name"] is not null && os["name"]!.GetValue<string>() != "windows") matches = false;
                if (os["arch"] is not null && !Regex.IsMatch(RuntimeInformation.OSArchitecture == Architecture.X64 ? "amd64" : "aarch64", os["arch"]!.GetValue<string>(), RegexOptions.None, TimeSpan.FromSeconds(1))) matches = false;
                if (os["version"] is not null && !Regex.IsMatch(Environment.OSVersion.Version.ToString(), os["version"]!.GetValue<string>(), RegexOptions.None, TimeSpan.FromSeconds(1))) matches = false;
            }
            if (rule["features"] is JsonObject features)
                foreach (var pair in features)
                {
                    var actual = pair.Key == "has_custom_resolution";
                    if (pair.Value?.GetValue<bool>() != actual) matches = false;
                }
            if (matches) allowed = rule["action"]?.GetValue<string>() == "allow";
        }
        return allowed;
    }
    public static IEnumerable<string> Arguments(JsonNode? arguments)
    {
        if (arguments is null) yield break;
        foreach (var arg in arguments.AsArray())
        {
            if (arg is JsonValue value) { yield return value.GetValue<string>(); continue; }
            if (arg is null || !Allowed(arg["rules"])) continue;
            if (arg["value"] is JsonArray array) { foreach (var item in array) yield return item!.GetValue<string>(); }
            else yield return arg["value"]!.GetValue<string>();
        }
    }
    public static string Expand(string argument, IReadOnlyDictionary<string, string> values) => Regex.Replace(argument, @"\$\{([^}]+)\}", match => values.TryGetValue(match.Groups[1].Value, out var replacement) ? replacement : throw new InvalidDataException("暂不支持的启动参数：" + match.Value));
}

public sealed record LaunchPlan(ProcessStartInfo StartInfo, string NativeDirectory, string GameDirectory);

public static class GameLauncher
{
    private static void Verify(string path, JsonNode? metadata)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("缺少游戏文件，请使用完整的原版游戏目录：" + path);
        if (metadata?["size"] is not null && new FileInfo(path).Length != metadata["size"]!.GetValue<long>()) throw new InvalidDataException("文件大小不符，请修复：" + path);
        if (metadata?["sha1"] is not null)
        {
            using var stream = File.OpenRead(path);
            if (!Convert.ToHexString(SHA1.HashData(stream)).Equals(metadata["sha1"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("文件校验失败，请修复：" + path);
        }
    }
    public static LaunchPlan Prepare(LauncherSettings settings, PlayerAccount account, GameVersion version, JavaRuntime java, GameSession? session = null, IProgress<GamePreparationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); progress?.Report(new("检查游戏清单", 0));
        if (account.IsMicrosoft && (session is null || !session.IsValid || !string.Equals(session.Uuid, account.Uuid, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("正版账号需要有效的 Minecraft 授权，请重新登录。");
        if (!version.IsSupported) throw new InvalidOperationException(version.Detail);
        if (RuntimeInformation.OSArchitecture != Architecture.X64) throw new InvalidOperationException("此 Demo 的游戏启动目前仅验证 Windows x64。");
        if (java.Major < version.JavaMajor) throw new InvalidOperationException($"此版本至少需要 Java {version.JavaMajor}，当前是 Java {java.Major}。");
        if (!java.Is64Bit) throw new InvalidOperationException("请选择 64 位 Java；当前 Java 未确认是 64 位。");
        if (settings.MemoryMb is < 512 or > 65536) throw new InvalidOperationException("游戏内存须在 512–65536 MB 之间。");
        var root = Path.GetFullPath(settings.GameRoot);
        var resolved = VersionManifestResolver.Resolve(root, version.Id); var json = resolved.Manifest;
        if(json["acmlGeneratedLibraries"] is JsonArray generated)
            foreach(var file in generated)Verify(SafePaths.Under(root,file!["path"]!.GetValue<string>()),file);
        var mainClass = json["mainClass"]?.GetValue<string>() ?? throw new InvalidDataException("清单缺少主类。");
        if (json["arguments"] is null) throw new InvalidDataException("游戏清单缺少可用的启动参数。");
        var gameDirectory = settings.IsolateVersions ? SafePaths.Under(root, "versions/" + version.Id) : root;
        var nativesDirectory = SafePaths.Under(root, ".acml/natives/" + Guid.NewGuid().ToString("N"));
        var classpath = new List<string>();
        var nativeFiles = new List<(string File, JsonNode Library)>();
        var libraryIndex = 0; var libraries = json["libraries"]!.AsArray();
        foreach (var library in libraries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new("验证运行库", 5 + 30.0 * ++libraryIndex / Math.Max(1, libraries.Count)));
            if (library is null || !ManifestRules.Allowed(library["rules"])) continue;
            if (library["downloads"]?["artifact"] is JsonNode artifact)
            {
                var path = SafePaths.Under(root, "libraries/" + artifact["path"]!.GetValue<string>());
                Verify(path, artifact); classpath.Add(path);
            }
            if (library["natives"]?["windows"] is JsonNode classifier)
            {
                var key = classifier.GetValue<string>().Replace("${arch}", "64");
                var native = library["downloads"]?["classifiers"]?[key] ?? throw new InvalidDataException("缺少 Windows native 下载信息。");
                var path = SafePaths.Under(root, "libraries/" + native["path"]!.GetValue<string>());
                Verify(path, native); nativeFiles.Add((path, library));
            }
        }
        var client = SafePaths.Under(root, resolved.ClientRelativePath);
        cancellationToken.ThrowIfCancellationRequested(); progress?.Report(new("验证游戏客户端", 40));
        Verify(client, json["downloads"]?["client"]); classpath.Add(client);
        var assetId = json["assetIndex"]?["id"]?.GetValue<string>() ?? throw new InvalidDataException("缺少资源索引。");
        var assetIndex = SafePaths.Under(root, "assets/indexes/" + assetId + ".json");
        Verify(assetIndex, json["assetIndex"]);
        var assets = JsonNode.Parse(File.ReadAllText(assetIndex))!;
        if (assets["virtual"]?.GetValue<bool>() == true || assets["map_to_resources"]?.GetValue<bool>() == true) throw new InvalidDataException("此 Demo 尚不支持旧版虚拟资源目录。");
        var assetIndexNumber = 0; var objects = assets["objects"]!.AsObject();
        foreach (var entry in objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++assetIndexNumber % 128 == 0 || assetIndexNumber == objects.Count) progress?.Report(new("验证游戏资源", 45 + 40.0 * assetIndexNumber / Math.Max(1, objects.Count)));
            var hash = entry.Value!["hash"]!.GetValue<string>();
            if (!Regex.IsMatch(hash, "^[0-9a-f]{40}$")) throw new InvalidDataException("无效的资源哈希。");
            var path = SafePaths.Under(root, "assets/objects/" + hash[..2] + "/" + hash);
            Verify(path, new JsonObject { ["size"] = entry.Value["size"]!.GetValue<long>() });
        }
        InstanceManager.PrepareGameDirectory(settings, version.Id, cancellationToken);
        Directory.CreateDirectory(gameDirectory);
        Directory.CreateDirectory(nativesDirectory);
        try
        {
            cancellationToken.ThrowIfCancellationRequested(); progress?.Report(new("准备本地运行库", 90));
            foreach (var (file, library) in nativeFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var zip = ZipFile.OpenRead(file);
                var excludes = library["extract"]?["exclude"]?.AsArray().Select(x => x!.GetValue<string>()).ToArray() ?? ["META-INF/"];
                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName.EndsWith('/') || excludes.Any(x => entry.FullName.StartsWith(x, StringComparison.Ordinal))) continue;
                    var destination = SafePaths.Under(nativesDirectory, entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination, true);
                }
            }
            var values = new Dictionary<string, string>
            {
                ["auth_player_name"] = account.Name, ["auth_uuid"] = account.Uuid, ["auth_access_token"] = account.IsMicrosoft ? session!.AccessToken : "0", ["user_type"] = account.IsMicrosoft ? "msa" : "legacy",
                ["version_name"] = version.Id, ["version_type"] = version.Type, ["game_directory"] = gameDirectory,
                ["assets_root"] = Path.Combine(root, "assets"), ["assets_index_name"] = assetId,
                ["natives_directory"] = nativesDirectory, ["launcher_name"] = "ACML", ["launcher_version"] = LauncherInfo.Version,
                ["classpath"] = string.Join(Path.PathSeparator, classpath.Distinct(StringComparer.OrdinalIgnoreCase)), ["classpath_separator"] = Path.PathSeparator.ToString(),
                ["library_directory"] = Path.Combine(root, "libraries"), ["resolution_width"] = settings.GameWidth.ToString(), ["resolution_height"] = settings.GameHeight.ToString(),
                ["clientid"] = account.IsMicrosoft ? LauncherInfo.MicrosoftClientId : "", ["auth_xuid"] = account.IsMicrosoft ? session!.Xuid : "", ["user_properties"] = "{}",
                ["auth_session"] = account.IsMicrosoft ? "token:" + session!.AccessToken + ":" + account.Uuid : "0"
            };
            var start = new ProcessStartInfo(java.Path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = gameDirectory };
            start.ArgumentList.Add("-Xmx" + settings.MemoryMb + "M");
            foreach (var argument in ManifestRules.Arguments(json["arguments"]!["jvm"])) start.ArgumentList.Add(ManifestRules.Expand(argument, values));
            if (json["logging"]?["client"] is JsonNode logging)
            {
                var file = logging["file"]!;
                var path = SafePaths.Under(root, "assets/log_configs/" + file["id"]!.GetValue<string>());
                Verify(path, file);
                start.ArgumentList.Add(logging["argument"]!.GetValue<string>().Replace("${path}", path));
            }
            start.ArgumentList.Add(mainClass);
            foreach (var argument in ManifestRules.Arguments(json["arguments"]!["game"])) start.ArgumentList.Add(ManifestRules.Expand(argument, values));
            cancellationToken.ThrowIfCancellationRequested(); progress?.Report(new("游戏文件验证完成", 100));
            return new(start, nativesDirectory, gameDirectory);
        }
        catch { CleanupNatives(nativesDirectory); throw; }
    }

    public static void CleanupNatives(string directory)
    {
        // Only remove a generated native extraction directory, never an instance or user data.
        if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _) || Path.GetFileName(Path.GetDirectoryName(directory)) != "natives") return;
        try { Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
