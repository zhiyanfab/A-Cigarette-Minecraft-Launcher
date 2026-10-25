using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ACML.Core;
public sealed record PackDownload(string Path, IReadOnlyList<string> Urls, string Hash, string HashKind, long Size);
public sealed record CursePackFile(string ProjectId, string FileId);
public sealed record ModpackInfo(string Name, string Minecraft, string Loader, string? LoaderVersion, string Format,
    string Overrides, IReadOnlyList<PackDownload> Downloads, IReadOnlyList<CursePackFile> CurseFiles);

public static class ModpackService
{
    public static string SafeRelative(string value)
    {
        var path = value.Replace('\\', '/').TrimEnd('/');
        if (path.Length == 0 || Path.IsPathRooted(path) || path.Split('/').Any(x => x.Length == 0 || x is "." or ".." || x.EndsWith('.') || x.EndsWith(' ')
            || x.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Regex.IsMatch(x.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$", RegexOptions.IgnoreCase)))
            throw new InvalidDataException("整合包包含不安全的路径：" + value);
        return path;
    }
    private static JsonObject ReadJson(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidDataException("整合包缺少 " + name);
        if (entry.Length > 16 * 1024 * 1024) throw new InvalidDataException("整合包清单过大。");
        using var reader = new StreamReader(entry.Open()); return JsonNode.Parse(reader.ReadToEnd())!.AsObject();
    }
    public static ModpackInfo Inspect(string archive)
    {
        using var zip = ZipFile.OpenRead(archive); ValidateArchive(zip);
        if (zip.GetEntry("modrinth.index.json") is not null)
        {
            var index = ReadJson(zip, "modrinth.index.json");
            if (index["formatVersion"]?.GetValue<int>() != 1 || index["game"]?.GetValue<string>() != "minecraft") throw new InvalidDataException("不支持的 Modrinth 整合包格式。");
            var deps = index["dependencies"]!.AsObject(); var mc = deps["minecraft"]!.GetValue<string>(); var loader = "原版"; string? loaderVersion = null;
            foreach (var pair in new[] { ("fabric-loader", "Fabric"), ("forge", "Forge"), ("neoforge", "NeoForge"), ("quilt-loader", "Quilt") })
                if (deps[pair.Item1] is JsonNode value) { if (loader != "原版") throw new InvalidDataException("整合包指定了多个加载器。"); loader = pair.Item2; loaderVersion = value.GetValue<string>(); }
            if (loader == "Quilt") throw new InvalidOperationException("当前尚未支持 Quilt 整合包。");
            var downloads = new List<PackDownload>();
            foreach (var item in index["files"]!.AsArray())
            {
                if (item?["env"]?["client"]?.GetValue<string>() == "unsupported") continue;
                var path = SafeRelative(item!["path"]!.GetValue<string>()); var sha512 = item["hashes"]?["sha512"]?.GetValue<string>(); var sha1 = item["hashes"]?["sha1"]?.GetValue<string>();
                var hash = sha512 ?? sha1 ?? throw new InvalidDataException("整合包文件缺少哈希。");
                if (!Regex.IsMatch(hash, sha512 is null ? "^[a-fA-F0-9]{40}$" : "^[a-fA-F0-9]{128}$")) throw new InvalidDataException("整合包文件哈希无效。");
                var urls = item["downloads"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
                if (urls.Length == 0 || urls.Any(x => !Uri.TryCreate(x, UriKind.Absolute, out var u) || u.Scheme != "https")) throw new InvalidDataException("整合包下载链接须使用 HTTPS。");
                downloads.Add(new(path, urls, hash, sha512 is null ? "SHA1" : "SHA512", item["fileSize"]!.GetValue<long>()));
            }
            if (downloads.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != downloads.Count) throw new InvalidDataException("整合包文件路径重复。");
            return new(index["name"]?.GetValue<string>() ?? "Modrinth 整合包", mc, loader, loaderVersion, "Modrinth", "overrides", downloads, []);
        }
        var cf = ReadJson(zip, "manifest.json");
        if (cf["manifestType"]?.GetValue<string>() != "minecraftModpack" || cf["manifestVersion"]?.GetValue<int>() != 1) throw new InvalidDataException("不支持的 CurseForge 整合包格式。");
        var minecraft = cf["minecraft"]!; var baseVersion = minecraft["version"]!.GetValue<string>();
        var loaders = minecraft["modLoaders"]?.AsArray() ?? [];
        var primary = loaders.FirstOrDefault(x => x?["primary"]?.GetValue<bool>() == true) ?? loaders.FirstOrDefault();
        var spec = primary?["id"]?.GetValue<string>() ?? ""; var type = "原版"; string? selected = null;
        foreach (var pair in new[] { ("neoforge-", "NeoForge"), ("forge-", "Forge"), ("fabric-", "Fabric") })
            if (spec.StartsWith(pair.Item1, StringComparison.OrdinalIgnoreCase)) { type = pair.Item2; selected = spec[pair.Item1.Length..]; }
        if (spec.Length > 0 && type == "原版") throw new InvalidOperationException("整合包加载器暂未支持：" + spec);
        if (selected?.StartsWith(baseVersion + "-", StringComparison.Ordinal) == true) selected = selected[(baseVersion.Length + 1)..];
        var files = cf["files"]!.AsArray().Where(x => x?["required"]?.GetValue<bool>() != false).Select(x => new CursePackFile(x!["projectID"]!.ToString(), x["fileID"]!.ToString())).ToArray();
        return new(cf["name"]?.GetValue<string>() ?? "CurseForge 整合包", baseVersion, type, selected, "CurseForge", SafeRelative(cf["overrides"]?.GetValue<string>() ?? "overrides"), [], files);
    }
    private static void ValidateArchive(ZipArchive zip)
    {
        if (zip.Entries.Count > 100000 || zip.Entries.Sum(x => x.Length) > 64L * 1024 * 1024 * 1024) throw new InvalidDataException("整合包文件数量或解压大小超过限制。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            var path = SafeRelative(entry.FullName);
            if (!paths.Add(path) || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000) throw new InvalidDataException("整合包包含重复路径或符号链接。");
        }
    }
    public static async Task ExtractContentsAsync(HttpClient client, LauncherSettings settings, string archive, ModpackInfo info, string stage,
        IModProvider? curseForge, IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(stage); var downloads = info.Downloads.Select(x => new DownloadFile(x.Path, x.Urls[0], x.Hash, x.Size, x.HashKind, x.Urls.Skip(1).ToArray())).ToList();
        var receipts = new List<InstalledMod>();
        if (info.CurseFiles.Count > 0 && curseForge is null) throw new InvalidOperationException("导入此整合包需要先在设置中填写 CurseForge API Key。");
        foreach (var file in info.CurseFiles)
        {
            token.ThrowIfCancellationRequested(); var release = await curseForge!.ExactAsync(file.ProjectId + "/" + file.FileId, info.Minecraft, info.Loader == "原版" ? "" : info.Loader.ToLowerInvariant(), token);
            if (release.Url is null) throw new InvalidOperationException("CurseForge 禁止第三方下载此文件，请从项目原站获取：" + release.Filename);
            var filename = SafeRelative(release.Filename); if (filename.Contains('/')) throw new InvalidDataException("模组文件名称无效。");
            downloads.Add(new("mods/" + filename, release.Url, release.Hash, release.Size, release.HashKind));
            receipts.Add(new("CurseForge", release.ProjectId, release.Id, release.Filename));
        }
        if (downloads.Select(x => x.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != downloads.Count) throw new InvalidDataException("整合包包含重复的模组文件名称。");
        await new DownloadEngine(client, settings.DownloadSource, settings.DownloadThreads).FetchAsync(stage, downloads, "下载整合包文件", progress, token);
        using var zip = ZipFile.OpenRead(archive); ValidateArchive(zip);
        foreach (var prefix in info.Format == "Modrinth" ? new[] { "overrides/", "client-overrides/" } : new[] { info.Overrides.TrimEnd('/') + "/" })
        foreach (var entry in zip.Entries.Where(x => x.FullName.Replace('\\', '/').StartsWith(prefix, StringComparison.Ordinal) && !x.FullName.Replace('\\', '/').EndsWith('/')))
        {
            token.ThrowIfCancellationRequested(); var relative = SafeRelative(entry.FullName.Replace('\\', '/')[prefix.Length..]); var destination = SafePaths.Under(stage, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = entry.Open(); await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            await input.CopyToAsync(output, token);
        }
        if (receipts.Count > 0) DownloadEngine.WriteJsonAtomic(SafePaths.Under(stage, "mods/.acml-mods.json"), JsonSerializer.Serialize(receipts));
    }
    public static async Task ImportAsync(HttpClient client, LauncherSettings settings, string archive, string id, IModProvider? curseForge,
        IProgress<DownloadProgress>? progress, CancellationToken token, RemoteVersion? remote = null)
    {
        InstanceManager.ValidateName(id); var root = settings.GameRoot; var target = SafePaths.Under(root, "versions/" + id);
        if (Directory.Exists(target)) throw new InvalidOperationException("此版本目录已存在，请填写其他名称。");
        var info = Inspect(archive); var stage = SafePaths.Under(root, ".acml/pack-staging/" + Guid.NewGuid().ToString("N")); var owner = Guid.NewGuid().ToString("N");
        var marker = Path.Combine(target, ".acml-pack-import-owner"); var installed = false;
        try
        {
            await ExtractContentsAsync(client, settings, archive, info, stage, curseForge, progress, token);
            var content = Directory.GetFiles(stage, "*", SearchOption.AllDirectories);
            foreach (var file in content)
            {
                var relative = SafeRelative(Path.GetRelativePath(stage, file));
                if (relative.Equals(id + ".json", StringComparison.OrdinalIgnoreCase) || relative.Equals(id + ".jar", StringComparison.OrdinalIgnoreCase) || relative.StartsWith(".acml", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("整合包内容不能替换启动器版本清单或客户端。");
            }
            remote ??= (await VersionService.FetchCatalogAsync(settings.DownloadSource, token)).Versions.FirstOrDefault(x => x.Id == info.Minecraft)
                ?? throw new InvalidOperationException("官方版本目录没有整合包所需游戏版本：" + info.Minecraft);
            if (remote.Id != info.Minecraft) throw new InvalidDataException("整合包游戏版本不匹配。");
            if (Directory.Exists(target)) throw new InvalidOperationException("目标版本目录已被其他任务创建。");
            await new GameInstaller(client, settings.DownloadSource, settings.DownloadThreads).InstallAsync(root, remote, id, info.Loader, info.LoaderVersion, settings.JavaPath, progress, token);
            installed = true; File.WriteAllText(marker, owner);
            using (var gate = GameInstaller.AcquireLock(root))
            foreach (var file in content)
            { token.ThrowIfCancellationRequested(); var destination = SafePaths.Under(target, Path.GetRelativePath(stage, file)); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Move(file, destination); }
            using var zip = ZipFile.OpenRead(archive);
            var local = zip.GetEntry("acml.instance.json") is null ? new InstanceSettings { OverrideJava = false, OverrideMemory = false, OverrideTitle = false, OverrideWindowSize = false } : ReadJson(zip, "acml.instance.json").Deserialize<InstanceSettings>()!;
            local.Enabled = true; local.OverrideJava = false; local.JavaPath = ""; local.OverrideIsolation = true; local.Isolate = true;
            InstanceManager.SaveSettings(root, id, local); File.Delete(marker);
        }
        catch
        {
            // Roll back only the brand-new directory tagged by this exact import; shared download caches are retained.
            if (installed && Directory.Exists(target) && File.Exists(marker) && File.ReadAllText(marker) == owner && Path.GetFullPath(target) == SafePaths.Under(root, "versions/" + id))
            {
                Directory.Delete(target, true); var record = SafePaths.Under(root, ".acml/install-records/" + id + ".json"); if (File.Exists(record)) File.Delete(record);
                var local = SafePaths.Under(root, ".acml/instance-settings/" + id + ".json"); if (File.Exists(local)) File.Delete(local);
            }
            throw;
        }
        finally { if (Directory.Exists(stage) && Path.GetDirectoryName(stage) == SafePaths.Under(root, ".acml/pack-staging")) Directory.Delete(stage, true); }
    }
    public static (string Minecraft, string Loader, string? Version) InstanceDependencies(JsonObject json)
    {
        string? Argument(string key)
        { var args = json["arguments"]?["game"] as JsonArray; if (args is null) return null; for (var i = 0; i + 1 < args.Count; i++) if (args[i] is JsonValue v && v.TryGetValue<string>(out var text) && text == key) return args[i + 1]?.GetValue<string>(); return null; }
        var mc = json["acmlBaseVersion"]?.GetValue<string>() ?? json["clientVersion"]?.GetValue<string>() ?? Argument("--fml.mcVersion") ?? json["inheritsFrom"]?.GetValue<string>() ?? json["id"]!.GetValue<string>();
        var type = json["acmlLoader"]?.GetValue<string>() ?? "原版"; var version = json["acmlLoaderVersion"]?.GetValue<string>();
        if (Argument("--fml.neoForgeVersion") is string neo) { type = "NeoForge"; version ??= neo; }
        else if (Argument("--fml.forgeVersion") is string forge) { type = "Forge"; version ??= forge; }
        foreach (var library in json["libraries"]?.AsArray() ?? [])
        {
            var name = library?["name"]?.GetValue<string>() ?? ""; var parts = name.Split(':'); if (parts.Length < 3) continue;
            if (name.StartsWith("net.fabricmc:fabric-loader:")) { type = "Fabric"; version ??= parts[2]; }
            if (name.StartsWith("org.quiltmc:quilt-loader:")) { type = "Quilt"; version ??= parts[2]; }
            if (name.StartsWith("net.neoforged:neoforge:") || name.StartsWith("net.neoforged:forge:")) { type = "NeoForge"; version ??= parts[2]; }
            if (name.StartsWith("net.minecraftforge:forge:")) { if (type != "NeoForge") type = "Forge"; version ??= Argument("--fml.forgeVersion") ?? parts[2]; }
            if (name.StartsWith("net.minecraftforge:fmlloader:")) { if (type != "NeoForge") type = "Forge"; version ??= parts[2]; }
        }
        if (version?.StartsWith(mc + "-", StringComparison.Ordinal) == true) version = version[(mc.Length + 1)..];
        if (type != "原版" && string.IsNullOrWhiteSpace(version)) throw new InvalidDataException("无法确定加载器版本，不能导出可还原的整合包。");
        return (mc, type, version);
    }
    public static async Task ExportAsync(HttpClient client, LauncherSettings settings, string id, string destination, string format, bool includeSaves,
        IModProvider? curseForge, IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        if (format is not ("Modrinth" or "CurseForge")) throw new InvalidOperationException("请选择整合包格式。");
        var manifest = VersionManifestResolver.Resolve(settings.GameRoot, id).Manifest; var deps = InstanceDependencies(manifest);
        InstanceManager.PrepareGameDirectory(settings, id, token);
        var directory = InstanceManager.GameDirectory(settings, id); var archivePath = Path.GetFullPath(destination); var temporary = archivePath + "." + Guid.NewGuid().ToString("N") + ".part";
        var ignored = new HashSet<string>(new[] { "assets", "libraries", "versions", "instances", ".acml", "logs", "crash-reports", "PCL", id + "-natives" }, StringComparer.OrdinalIgnoreCase);
        if (!includeSaves) ignored.Add("saves");
        var all = Directory.EnumerateFiles(directory, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Where(file =>
        {
            var relative = Path.GetRelativePath(directory, file).Replace('\\', '/'); var first = relative.Split('/')[0];
            return !ignored.Contains(first) && !first.StartsWith(".acml", StringComparison.OrdinalIgnoreCase) && first != id + ".json" && first != id + ".jar"
                && first is not ("launcher_profiles.json" or "launcher_accounts.json") && Path.GetFullPath(file) != archivePath;
        }).ToArray();
        var portable = InstanceManager.LoadSettings(settings, id); portable.JavaPath = ""; portable.OverrideJava = false;
        var indexes = new JsonArray(); var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var receiptPath = Path.Combine(directory, "mods", ".acml-mods.json");
        var receipts = File.Exists(receiptPath) ? JsonSerializer.Deserialize<List<InstalledMod>>(await File.ReadAllTextAsync(receiptPath, token)) ?? [] : [];
        IModProvider modrinth = new ModrinthProvider(client);
        foreach (var receipt in receipts)
        {
            token.ThrowIfCancellationRequested(); if (receipt.Provider != format) continue;
            var provider = format == "CurseForge" ? curseForge : modrinth; if (provider is null) continue;
            var relative = "mods/" + SafeRelative(receipt.Filename); var path = SafePaths.Under(directory, relative); if (!File.Exists(path)) continue;
            try
            {
                var release = await provider.ExactAsync(receipt.VersionId, "", "", token);
                if (release.Url is null || !DownloadEngine.Valid(path, new("", "", release.Hash, release.Size, release.HashKind))) continue;
                if (format == "CurseForge") indexes.Add(new JsonObject { ["projectID"] = long.Parse(release.ProjectId), ["fileID"] = long.Parse(release.Id.Split('/').Last()), ["required"] = true });
                else
                {
                    using var stream = File.OpenRead(path); var sha512 = Convert.ToHexString(SHA512.HashData(stream)).ToLowerInvariant(); stream.Position = 0; var sha1 = Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
                    indexes.Add(new JsonObject { ["path"] = relative, ["hashes"] = new JsonObject { ["sha512"] = sha512, ["sha1"] = sha1 }, ["downloads"] = new JsonArray(release.Url), ["fileSize"] = new FileInfo(path).Length });
                }
                excluded.Add(relative);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException) { /* Preserve unresolvable files in overrides for local round-trip. */ }
        }
        JsonObject index;
        if (format == "Modrinth")
        {
            var dependencies = new JsonObject { ["minecraft"] = deps.Minecraft }; if (deps.Loader != "原版") dependencies[deps.Loader == "Fabric" ? "fabric-loader" : deps.Loader.ToLowerInvariant()] = deps.Version;
            index = new() { ["formatVersion"] = 1, ["game"] = "minecraft", ["versionId"] = "1.0.0", ["name"] = id, ["files"] = indexes, ["dependencies"] = dependencies };
        }
        else
        {
            var loaders = new JsonArray(); if (deps.Loader != "原版") loaders.Add(new JsonObject { ["id"] = deps.Loader.ToLowerInvariant() + "-" + deps.Version, ["primary"] = true });
            index = new() { ["manifestType"] = "minecraftModpack", ["manifestVersion"] = 1, ["name"] = id, ["version"] = "1.0.0", ["author"] = "ACML", ["minecraft"] = new JsonObject { ["version"] = deps.Minecraft, ["modLoaders"] = loaders }, ["files"] = indexes, ["overrides"] = "overrides" };
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                async Task JsonEntry(string name, string json) { await using var stream = zip.CreateEntry(name).Open(); await using var writer = new StreamWriter(stream); await writer.WriteAsync(json.AsMemory(), token); }
                await JsonEntry(format == "Modrinth" ? "modrinth.index.json" : "manifest.json", index.ToJsonString(new() { WriteIndented = true }));
                await JsonEntry("acml.instance.json", JsonSerializer.Serialize(portable));
                var completed = 0;
                foreach (var file in all)
                {
                    token.ThrowIfCancellationRequested(); var relative = SafeRelative(Path.GetRelativePath(directory, file)); if (excluded.Contains(relative)) continue;
                    await using var input = File.OpenRead(file); await using var output = zip.CreateEntry("overrides/" + relative, CompressionLevel.Optimal).Open(); await input.CopyToAsync(output, token);
                    progress?.Report(new("导出整合包", ++completed, all.Length, 0));
                }
            }
            token.ThrowIfCancellationRequested(); File.Move(temporary, archivePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
