using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ACML.Core;

public sealed class GameInstaller(HttpClient client, string source, int threads = DownloadEngine.DefaultThreads)
{
    private readonly DownloadEngine downloads = new(client, source, threads);
    public Task<string> InstallAsync(string root, RemoteVersion version, string localId, bool fabric, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
        => InstallAsync(root,version,localId,fabric ? "Fabric" : "原版",null,null,progress,cancellationToken);
    public async Task<string> InstallAsync(string root, RemoteVersion version, string localId, string loader, string? loaderVersion, string? javaPath, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        InstanceManager.ValidateName(localId);
        if (File.Exists(SafePaths.Under(root, $"versions/{localId}/{localId}.json"))) throw new InvalidOperationException("此名称已存在，请换一个名称或对已有版本使用补全功能。");
        Directory.CreateDirectory(root);
        var metadataPath = $".acml/metadata/{version.Id}.json";
        await downloads.FetchAsync(root, [new(metadataPath, version.Url, version.Sha1)], "获取游戏清单", progress, cancellationToken);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(SafePaths.Under(root, metadataPath), cancellationToken))!.AsObject();
        if (json["arguments"] is null) throw new InvalidOperationException("此 Demo 先支持 1.13 及以上的现代版本清单。");
        var baseId = json["id"]!.GetValue<string>();
        if (baseId != version.Id) throw new InvalidDataException("下载的版本清单 ID 不匹配。");
        JavaRuntime? installerJava=null; LoaderVersion? selected=null;
        if(loader is "Forge" or "NeoForge")
        {
            var versions=await new LoaderCatalog(client,source).FetchAsync(baseId,loader,cancellationToken);
            selected=versions.FirstOrDefault(x=>loaderVersion is null||x.Version==loaderVersion)??throw new InvalidOperationException("所选加载器版本与游戏版本不兼容。");
            installerJava=await new JavaInstaller(client, threads).EnsureAsync(root,Math.Max(17,json["javaVersion"]?["majorVersion"]?.GetValue<int>()??8),javaPath,progress,cancellationToken);
        }
        else if(loader is not ("原版" or "Fabric"))throw new InvalidOperationException("未知的模组加载器。");
        using var gate=AcquireLock(root);
        if (File.Exists(SafePaths.Under(root, $"versions/{localId}/{localId}.json"))) throw new InvalidOperationException("此版本名称已存在。");
        if (loader=="Fabric") await AddFabricAsync(json, baseId, loaderVersion, cancellationToken);
        json["id"] = localId; json["acmlBaseVersion"] = baseId;
        await CompleteFilesAsync(root, localId, json, progress, cancellationToken);
        if(selected is not null)
        {
            json=await new ForgeInstaller(client,source,threads).InstallAsync(root,localId,json,selected,installerJava!,progress,cancellationToken);
            await CompleteFilesAsync(root,localId,json,progress,cancellationToken);
        }
        DownloadEngine.WriteJsonAtomic(SafePaths.Under(root, $".acml/install-records/{localId}.json"), json.ToJsonString());
        DownloadEngine.WriteJsonAtomic(SafePaths.Under(root, $"versions/{localId}/{localId}.json"), json.ToJsonString());
        return localId;
    }
    public async Task RepairAsync(string root, string id, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        JsonObject json;
        try { json = VersionService.Read(root, id); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException)
        {
            var backup = SafePaths.Under(root, $".acml/install-records/{id}.json");
            if (File.Exists(backup)) json = JsonNode.Parse(await File.ReadAllTextAsync(backup, cancellationToken))!.AsObject();
            else
            {
                var catalog = await VersionService.FetchCatalogAsync(source, cancellationToken);
                var remote = catalog.Versions.FirstOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("此自定义实例的清单已损坏，且没有安装记录；请重新安装此实例。");
                var path = $".acml/metadata/{id}.json";
                await downloads.FetchAsync(root, [new(path, remote.Url, remote.Sha1)], "恢复版本清单", progress, cancellationToken);
                json = JsonNode.Parse(await File.ReadAllTextAsync(SafePaths.Under(root, path), cancellationToken))!.AsObject();
            }
        }
        // Foreign launcher manifests remain untouched. Resolve inheritance and shared
        // clients only in memory, then repair the paths actually used at launch.
        if (!File.Exists(SafePaths.Under(root, $".acml/install-records/{id}.json")))
        {
            var resolved = VersionManifestResolver.Resolve(root, id);
            using var foreignGate = AcquireLock(root);
            await CompleteFilesAsync(root, id, resolved.Manifest, progress, cancellationToken, resolved.ClientRelativePath);
            return;
        }
        JavaRuntime? java=null;
        var rebuild=json["acmlGeneratedLibraries"] is JsonArray generated&&generated.Any(x=>!DownloadEngine.Valid(SafePaths.Under(root,x!["path"]!.GetValue<string>()),new("", "",x["sha1"]!.GetValue<string>(),x["size"]!.GetValue<long>())));
        if(rebuild)java=await new JavaInstaller(client, threads).EnsureAsync(root,Math.Max(17,json["javaVersion"]?["majorVersion"]?.GetValue<int>()??8),null,progress,cancellationToken);
        using var gate = AcquireLock(root);
        if(rebuild)
        {
            var baseId=json["acmlBaseVersion"]!.GetValue<string>();
            var catalog=await VersionService.FetchCatalogAsync(source,cancellationToken);var remote=catalog.Versions.First(x=>x.Id==baseId);
            var metadata=$".acml/metadata/{baseId}.json";
            await downloads.FetchAsync(root,[new(metadata,remote.Url,remote.Sha1)],"获取补丁基础清单",progress,cancellationToken);
            var vanilla=JsonNode.Parse(await File.ReadAllTextAsync(SafePaths.Under(root,metadata),cancellationToken))!.AsObject();
            await CompleteFilesAsync(root,id,vanilla,progress,cancellationToken);
            var loader=new LoaderVersion(json["acmlLoader"]!.GetValue<string>(),json["acmlLoaderVersion"]!.GetValue<string>(),baseId,json["acmlInstallerUrl"]!.GetValue<string>());
            json=await new ForgeInstaller(client,source,threads).InstallAsync(root,id,vanilla,loader,java!,progress,cancellationToken);
        }
        await CompleteFilesAsync(root, id, json, progress, cancellationToken);
        DownloadEngine.WriteJsonAtomic(SafePaths.Under(root, $".acml/install-records/{id}.json"), json.ToJsonString());
        DownloadEngine.WriteJsonAtomic(SafePaths.Under(root, $"versions/{id}/{id}.json"), json.ToJsonString());
    }
    public static FileStream AcquireLock(string root)
    {
        var directory = SafePaths.Under(root, ".acml"); Directory.CreateDirectory(directory);
        try { return new FileStream(Path.Combine(directory, "install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new IOException("此游戏目录正在被另一个安装任务使用。"); }
    }
    private async Task CompleteFilesAsync(string root, string id, JsonObject json, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken, string? clientRelativePath = null)
    {
        var index = json["assetIndex"] ?? throw new InvalidDataException("清单缺少资源索引。");
        var indexId = index["id"]!.GetValue<string>();
        await downloads.FetchAsync(root, [Item(index, "assets/indexes/" + indexId + ".json")], "获取资源索引", progress, cancellationToken);
        var assets = JsonNode.Parse(await File.ReadAllTextAsync(SafePaths.Under(root, "assets/indexes/" + indexId + ".json"), cancellationToken))!;
        if (assets["virtual"]?.GetValue<bool>() == true || assets["map_to_resources"]?.GetValue<bool>() == true) throw new InvalidOperationException("旧版虚拟资源目录尚未适配。");
        var files = new List<DownloadFile> { Item(json["downloads"]?["client"] ?? throw new InvalidDataException("缺少客户端下载地址。"), clientRelativePath ?? $"versions/{id}/{id}.jar") };
        foreach (var lib in json["libraries"]!.AsArray())
        {
            if (lib is null || !ManifestRules.Allowed(lib["rules"])) continue;
            if (lib["downloads"]?["artifact"] is JsonNode artifact)
            {
                var item=Item(artifact,"libraries/"+artifact["path"]!.GetValue<string>());
                if(string.IsNullOrEmpty(item.Url)) {if(!DownloadEngine.Valid(SafePaths.Under(root,item.RelativePath),item))throw new InvalidDataException("加载器补丁文件损坏，请在版本管理中补全："+item.RelativePath);}
                else files.Add(item);
            }
            if (lib["natives"]?["windows"] is JsonNode classifier)
            {
                var native = lib["downloads"]?["classifiers"]?[classifier.GetValue<string>().Replace("${arch}", "64")] ?? throw new InvalidDataException("缺少 native 地址。");
                files.Add(Item(native, "libraries/" + native["path"]!.GetValue<string>()));
            }
        }
        if (json["logging"]?["client"]?["file"] is JsonNode logging) files.Add(Item(logging, "assets/log_configs/" + logging["id"]!.GetValue<string>()));
        foreach (var entry in assets["objects"]!.AsObject())
        {
            var hash = entry.Value!["hash"]!.GetValue<string>();
            if (!Regex.IsMatch(hash, "^[0-9a-f]{40}$")) throw new InvalidDataException("资源哈希无效。");
            files.Add(new("assets/objects/" + hash[..2] + "/" + hash, "https://resources.download.minecraft.net/" + hash[..2] + "/" + hash, hash, entry.Value["size"]!.GetValue<long>()));
        }
        await downloads.FetchAsync(root, files, "下载 / 校验游戏文件（" + source + "）", progress, cancellationToken);
    }
    private static DownloadFile Item(JsonNode node, string path) => new(path, node["url"]?.GetValue<string>()??"", node["sha1"]?.GetValue<string>(), node["size"]?.GetValue<long>() ?? 0);
    private async Task AddFabricAsync(JsonObject json, string baseId, string? selectedVersion, CancellationToken token)
    {
        async Task<JsonNode> Get(string url)
        {
            var target = source == "BMCLAPI" ? DownloadEngine.Mirror(url) ?? url : url;
            try { return JsonNode.Parse(await client.GetStringAsync(target, token))!; }
            catch (HttpRequestException) when (source == "自动" && DownloadEngine.Mirror(url) is string mirror) { return JsonNode.Parse(await client.GetStringAsync(mirror, token))!; }
        }
        var loaders = await Get("https://meta.fabricmc.net/v2/versions/loader/" + Uri.EscapeDataString(baseId));
        var selected = selectedVersion is null ? loaders.AsArray().FirstOrDefault(x => x?["loader"]?["stable"]?.GetValue<bool>() == true) ?? loaders.AsArray().FirstOrDefault()
            : loaders.AsArray().FirstOrDefault(x=>x?["loader"]?["version"]?.GetValue<string>()==selectedVersion);
        if(selected is null)throw new InvalidOperationException("此游戏版本没有所选的 Fabric Loader。");
        var loader = selected["loader"]!["version"]!.GetValue<string>();
        var profile = await Get($"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(baseId)}/{Uri.EscapeDataString(loader)}/profile/json");
        json["mainClass"] = profile["mainClass"]!.DeepClone(); json["acmlLoader"] = "Fabric"; json["acmlLoaderVersion"] = loader;
        foreach (var kind in new[] { "jvm", "game" })
        {
            if (profile["arguments"]?[kind] is not JsonArray extra) continue;
            var args = json["arguments"]![kind]!.AsArray(); foreach (var value in extra) args.Add(value!.DeepClone());
        }
        var libraries = json["libraries"]!.AsArray();
        foreach (var original in profile["libraries"]!.AsArray())
        {
            var lib = original!.DeepClone(); var name = lib["name"]!.GetValue<string>(); var parts = name.Split(':');
            if (parts.Length is < 3 or > 4) throw new InvalidDataException("Fabric Maven 坐标无法解析。");
            var relative = parts[0].Replace('.', '/') + "/" + parts[1] + "/" + parts[2] + "/" + parts[1] + "-" + parts[2] + (parts.Length == 4 ? "-" + parts[3] : "") + ".jar";
            var url = (lib["url"]?.GetValue<string>() ?? "https://maven.fabricmc.net/").TrimEnd('/') + "/" + relative;
            string? sha = null;
            try { sha = (await client.GetStringAsync(source == "BMCLAPI" ? DownloadEngine.Mirror(url + ".sha1") ?? url + ".sha1" : url + ".sha1", token)).Trim().Split(' ')[0]; } catch (HttpRequestException) { }
            if (sha is not null && !Regex.IsMatch(sha, "^[0-9a-fA-F]{40}$")) sha = null;
            lib["downloads"] = new JsonObject { ["artifact"] = new JsonObject { ["path"] = relative, ["url"] = url, ["sha1"] = sha } };
            libraries.Add(lib);
        }
    }
}

