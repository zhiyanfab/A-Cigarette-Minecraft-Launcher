using System.Text.Json;
using System.Text.Json.Nodes;

namespace ACML.Core;

public sealed record ModProject(string Id, string Title, string Description, string PageUrl,
    string? IconUrl = null, IReadOnlyList<string>? GameVersions = null, IReadOnlyList<string>? Loaders = null)
{
    public string VersionsLabel => GameVersions is { Count: > 0 } ? string.Join(" · ",GameVersions.Reverse().Take(8))+(GameVersions.Count>8 ? $" … 共 {GameVersions.Count} 个版本" : "") : "";
    public string LoadersLabel => Loaders is { Count: > 0 } ? string.Join(" / ",Loaders) : "";
    public override string ToString() => Title;
}
public sealed record ModDependency(string? ProjectId,string? VersionId,string Kind="required");
public sealed record ModRelease(string ProjectId,string Id,string Name,string Filename,string? Url,string Hash,long Size,string HashKind,IReadOnlyList<ModDependency> Dependencies,
    IReadOnlyList<string>? GameVersions=null,IReadOnlyList<string>? Loaders=null,string ReleaseType="release",DateTimeOffset Published=default)
{
    public string Label=>Name+(Url is null?" · 需从原站下载":"");
    public string CompatibilityLabel=>string.Join(" / ",Loaders??[])+" · "+string.Join(", ",GameVersions??[]);
    public string DateLabel=>Published==default?"":Published.ToLocalTime().ToString("yyyy-MM-dd")+" · "+ReleaseType;
    public override string ToString()=>Label;
}
public interface IModProvider
{
    string Name{get;}
    Task<IReadOnlyList<ModProject>> SearchAsync(string query,string gameVersion,string loader,CancellationToken token);
    Task<IReadOnlyList<ModRelease>> ReleasesAsync(string projectId,string gameVersion,string loader,CancellationToken token);
    Task<ModRelease> ExactAsync(string versionId,string gameVersion,string loader,CancellationToken token);
    Task<ModProject> ProjectAsync(string id,CancellationToken token)=>throw new NotSupportedException("此来源不支持项目详情。");
}
public static class ModMetadata
{
    public static string? Icon(string? value)=>Uri.TryCreate(value,UriKind.Absolute,out var uri)&&uri.Scheme=="https"?value:null;
    public static string[] Strings(JsonNode? node)=>node is JsonArray array?array.Select(x=>x!.GetValue<string>()).Distinct().ToArray():[];
    public static readonly string[] KnownLoaders=["fabric","forge","neoforge","quilt","liteloader","rift","modloader"];
    public static string[] LoaderTags(JsonNode? node)=>Strings(node).Where(x=>KnownLoaders.Contains(x)).ToArray();
    public static bool Matches(string game,string loader,IReadOnlyList<string> games,IReadOnlyList<string> loaders)=>(game.Length==0||games.Contains(game))&&(loader.Length==0||loaders.Any(x=>x.Equals(loader,StringComparison.OrdinalIgnoreCase)));
}
public sealed class ModrinthProvider(HttpClient client):IModProvider
{
    public string Name=>"Modrinth";
    public async Task<IReadOnlyList<ModProject>> SearchAsync(string query,string gameVersion,string loader,CancellationToken token)
    {
        var facets=new List<string[]>{new[]{"project_type:mod"}};
        if(gameVersion.Length>0)facets.Add(["versions:"+gameVersion]);if(loader.Length>0)facets.Add(["categories:"+loader]);
        var recommend=string.IsNullOrWhiteSpace(query);
        var path="search?limit="+(recommend?"60":"30")+"&query="+Uri.EscapeDataString(query)+"&facets="+Uri.EscapeDataString(JsonSerializer.Serialize(facets));
        if(recommend)path+="&index=downloads&offset="+Random.Shared.Next(0,4)*20;
        var json=await Get(path,token);
        var projects=json["hits"]!.AsArray().Select(x=>new ModProject(x!["project_id"]!.GetValue<string>(),x["title"]!.GetValue<string>(),x["description"]!.GetValue<string>(),"https://modrinth.com/mod/"+x["slug"]!.GetValue<string>(),ModMetadata.Icon(x["icon_url"]?.GetValue<string>()),ModMetadata.Strings(x["versions"]),ModMetadata.LoaderTags(x["categories"]))).ToList();
        return recommend?projects.OrderBy(_=>Random.Shared.Next()).Take(20).ToList():projects;
    }
    public async Task<ModProject> ProjectAsync(string id,CancellationToken token)
    {
        var x=await Get("project/"+Uri.EscapeDataString(id),token);
        return new(x["id"]!.GetValue<string>(),x["title"]!.GetValue<string>(),x["description"]!.GetValue<string>(),"https://modrinth.com/mod/"+(x["slug"]?.GetValue<string>()??id),ModMetadata.Icon(x["icon_url"]?.GetValue<string>()),ModMetadata.Strings(x["game_versions"]),ModMetadata.Strings(x["loaders"]));
    }
    public async Task<IReadOnlyList<ModRelease>> ReleasesAsync(string projectId,string gameVersion,string loader,CancellationToken token)
    {
        var query="project/"+Uri.EscapeDataString(projectId)+"/version?";
        if(gameVersion.Length>0)query+="game_versions="+Uri.EscapeDataString(JsonSerializer.Serialize(new[]{gameVersion}))+"&";
        if(loader.Length>0)query+="loaders="+Uri.EscapeDataString(JsonSerializer.Serialize(new[]{loader}));
        var json=await Get(query,token);return json.AsArray().Select(x=>Parse(x!,gameVersion,loader)).ToList();
    }
    public async Task<ModRelease> ExactAsync(string id,string game,string loader,CancellationToken token)=>Parse(await Get("version/"+Uri.EscapeDataString(id),token),game,loader);
    private static ModRelease Parse(JsonNode json,string game,string loader)
    {
        var games=ModMetadata.Strings(json["game_versions"]);var loaders=ModMetadata.Strings(json["loaders"]);
        if(!ModMetadata.Matches(game,loader,games,loaders))throw new InvalidOperationException("模组文件与游戏版本或加载器不兼容。");
        var files=json["files"]!.AsArray();var file=files.FirstOrDefault(x=>x?["primary"]?.GetValue<bool>()==true)??files.FirstOrDefault()??throw new InvalidDataException("模组没有可用文件。");
        var hash=file["hashes"]?["sha512"]?.GetValue<string>()??file["hashes"]?["sha1"]?.GetValue<string>()??throw new InvalidDataException("模组文件没有可校验的哈希。");
        var deps=json["dependencies"]!.AsArray().Where(x=>x?["dependency_type"]?.GetValue<string>() is "required" or "optional")
            .Select(x=>new ModDependency(x!["project_id"]?.GetValue<string>(),x["version_id"]?.GetValue<string>(),x["dependency_type"]!.GetValue<string>())).ToList();
        return new(json["project_id"]!.GetValue<string>(),json["id"]!.GetValue<string>(),json["name"]!.GetValue<string>(),file["filename"]!.GetValue<string>(),file["url"]!.GetValue<string>(),hash,file["size"]!.GetValue<long>(),file["hashes"]?["sha512"] is not null?"SHA512":"SHA1",deps,games,loaders,json["version_type"]?.GetValue<string>()??"release",json["date_published"] is null?default:DateTimeOffset.Parse(json["date_published"]!.GetValue<string>()));
    }
    private async Task<JsonNode> Get(string path,CancellationToken token)
    {
        using var response=await client.GetAsync("https://api.modrinth.com/v2/"+path,token);
        if(!response.IsSuccessStatusCode)throw new HttpRequestException("Modrinth 请求失败（HTTP "+(int)response.StatusCode+"），请稍后重试。");
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(token))!;
    }
}
public sealed class CurseForgeProvider(HttpClient client,string apiKey):IModProvider
{
    public string Name=>"CurseForge";
    private static int Loader(string loader)=>loader.ToLowerInvariant() switch{"fabric"=>4,"neoforge"=>6,"forge"=>1,"quilt"=>5,_=>throw new InvalidOperationException("此加载器暂未适配。")};
    private static string LoaderName(int? id)=>id switch{1=>"forge",4=>"fabric",5=>"quilt",6=>"neoforge",_=>""};
    public async Task<IReadOnlyList<ModProject>> SearchAsync(string query,string gameVersion,string loader,CancellationToken token)
    {
        var path="mods/search?gameId=432&classId=6&pageSize=50&searchFilter="+Uri.EscapeDataString(query);
        if(gameVersion.Length>0)path+="&gameVersion="+Uri.EscapeDataString(gameVersion);if(loader.Length>0)path+="&modLoaderType="+Loader(loader);
        if(string.IsNullOrWhiteSpace(query))path+="&sortField=2&sortOrder=desc&index="+Random.Shared.Next(0,4)*20;
        var json=await Get(path,token);var list=json.AsArray().Select(x=>Project(x!)).ToList();
        return string.IsNullOrWhiteSpace(query)?list.OrderBy(_=>Random.Shared.Next()).Take(20).ToList():list;
    }
    private static ModProject Project(JsonNode x)
    {
        var files=x["latestFilesIndexes"] as JsonArray;
        var games=files?.Select(f=>f?["gameVersion"]?.GetValue<string>()).Where(v=>v is not null).Cast<string>().Distinct().ToArray()??[];
        var loaders=files?.Select(f=>LoaderName(f?["modLoader"]?.GetValue<int>())).Where(v=>v.Length>0).Distinct().ToArray()??[];
        return new(x["id"]!.ToString(),x["name"]!.GetValue<string>(),x["summary"]!.GetValue<string>(),x["links"]!["websiteUrl"]!.GetValue<string>(),ModMetadata.Icon(x["logo"]?["thumbnailUrl"]?.GetValue<string>()),games,loaders);
    }
    public async Task<ModProject> ProjectAsync(string id,CancellationToken token)=>Project(await Get("mods/"+Uri.EscapeDataString(id),token));
    public async Task<IReadOnlyList<ModRelease>> ReleasesAsync(string projectId,string gameVersion,string loader,CancellationToken token)
    {
        var all=new List<ModRelease>();
        for(var page=0;page<20;page++)
        {
            var path=$"mods/{Uri.EscapeDataString(projectId)}/files?pageSize=50&index={page*50}";
            if(gameVersion.Length>0)path+="&gameVersion="+Uri.EscapeDataString(gameVersion);if(loader.Length>0)path+="&modLoaderType="+Loader(loader);
            var json=(await Get(path,token)).AsArray();all.AddRange(json.Select(x=>Parse(x!,gameVersion,loader)));if(json.Count<50)break;
        }
        return all;
    }
    public async Task<ModRelease> ExactAsync(string versionId,string gameVersion,string loader,CancellationToken token)
    {var parts=versionId.Split('/');if(parts.Length!=2)throw new InvalidDataException("CurseForge 文件标识无效。");return Parse(await Get($"mods/{Uri.EscapeDataString(parts[0])}/files/{Uri.EscapeDataString(parts[1])}",token),gameVersion,loader);}
    private static ModRelease Parse(JsonNode json,string game,string loader)
    {
        var tags=ModMetadata.Strings(json["gameVersions"]);var loaders=tags.Where(x=>ModMetadata.KnownLoaders.Contains(x.ToLowerInvariant())).Select(x=>x.ToLowerInvariant()).ToArray();
        var games=tags.Where(x=>!ModMetadata.KnownLoaders.Contains(x.ToLowerInvariant())&&char.IsDigit(x.FirstOrDefault())).ToArray();
        if(!ModMetadata.Matches(game,loader,games,loaders))throw new InvalidOperationException("CurseForge 文件与目标版本或加载器不兼容。");
        var hash=json["hashes"]!.AsArray().FirstOrDefault(x=>x?["algo"]?.GetValue<int>()==1)??json["hashes"]!.AsArray().FirstOrDefault()??throw new InvalidDataException("文件缺少哈希。");
        var deps=json["dependencies"]!.AsArray().Where(x=>x?["relationType"]?.GetValue<int>() is 2 or 3).Select(x=>new ModDependency(x!["modId"]!.ToString(),null,x["relationType"]!.GetValue<int>()==3?"required":"optional")).ToList();
        var project=json["modId"]!.ToString();
        return new(project,project+"/"+json["id"]!,json["displayName"]!.GetValue<string>(),json["fileName"]!.GetValue<string>(),json["downloadUrl"]?.GetValue<string>(),hash["value"]!.GetValue<string>(),json["fileLength"]!.GetValue<long>(),hash["algo"]!.GetValue<int>()==1?"SHA1":"MD5",deps,games,loaders,json["releaseType"]?.GetValue<int>() switch{2=>"beta",3=>"alpha",_=>"release"},json["fileDate"] is null?default:DateTimeOffset.Parse(json["fileDate"]!.GetValue<string>()));
    }
    private async Task<JsonNode> Get(string path,CancellationToken token)
    {
        if(string.IsNullOrWhiteSpace(apiKey))throw new InvalidOperationException("请先在设置中配置 CurseForge API Key；也可直接使用 Modrinth。");
        using var request=new HttpRequestMessage(HttpMethod.Get,"https://api.curseforge.com/v1/"+path);request.Headers.Add("x-api-key",apiKey);
        using var response=await client.SendAsync(request,token);
        if(!response.IsSuccessStatusCode)throw new HttpRequestException("CurseForge 请求失败（HTTP "+(int)response.StatusCode+"），请检查 API Key 或稍后重试。");
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(token))!["data"]!;
    }
}

public sealed record InstalledMod(string Provider, string ProjectId, string VersionId, string Filename);
public static class ModInstaller
{
    public static async Task<IReadOnlyList<ModRelease>> ResolveAsync(IModProvider provider, ModRelease selected, string gameVersion, string loader, CancellationToken token)
    {
        var resolved = new Dictionary<string, ModRelease>();
        async Task Visit(ModRelease release)
        {
            token.ThrowIfCancellationRequested();
            if (resolved.TryGetValue(release.ProjectId, out var existing)) { if (existing.Id != release.Id) throw new InvalidOperationException("模组必需依赖存在版本冲突，请选择其他版本。"); return; }
            if (resolved.Count >= 100) throw new InvalidOperationException("依赖数量过多，已停止自动解析。");
            if (release.Url is null) throw new InvalidOperationException("此文件不允许第三方下载，请从原站下载后手动导入。");
            if (Path.GetFileName(release.Filename) != release.Filename || release.Filename.Contains('\\') || !release.Filename.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("模组文件名不安全或不是 JAR 文件。");
            resolved[release.ProjectId] = release;
            foreach (var dependency in release.Dependencies.Where(x => x.Kind == "required"))
            {
                var next = dependency.VersionId is not null ? await provider.ExactAsync(dependency.VersionId, gameVersion, loader, token)
                    : dependency.ProjectId is not null ? (await provider.ReleasesAsync(dependency.ProjectId, gameVersion, loader, token)).FirstOrDefault() ?? throw new InvalidOperationException("没有找到兼容的必需依赖。")
                    : throw new InvalidOperationException("此模组有需要手动安装的外部依赖。");
                await Visit(next);
            }
        }
        await Visit(selected); return resolved.Values.ToList();
    }
    public static Task InstallAsync(HttpClient client, string root, string gameDirectory, string provider, IReadOnlyList<ModRelease> releases, IProgress<DownloadProgress>? progress, CancellationToken token, int threads = DownloadEngine.DefaultThreads)
        => InstallToDirectoryAsync(client, root, Path.Combine(gameDirectory, "mods"), provider, releases, progress, token, threads);
    public static async Task InstallToDirectoryAsync(HttpClient client, string root, string mods, string provider, IReadOnlyList<ModRelease> releases, IProgress<DownloadProgress>? progress, CancellationToken token, int threads = DownloadEngine.DefaultThreads)
    {
        if (!Path.IsPathFullyQualified(mods)) throw new InvalidOperationException("请选择完整的模组保存目录。");
        mods = Path.GetFullPath(mods);
        using var gate = GameInstaller.AcquireLock(root);
        Directory.CreateDirectory(mods);
        var indexPath = Path.Combine(mods, ".acml-mods.json");
        var index = File.Exists(indexPath) ? JsonSerializer.Deserialize<List<InstalledMod>>(await File.ReadAllTextAsync(indexPath, token)) ?? [] : [];
        if (releases.GroupBy(x => x.Filename, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1)) throw new InvalidOperationException("依赖清单中有重复文件名。");
        foreach (var release in releases)
        {
            if (index.Any(x => x.Provider == provider && x.ProjectId == release.ProjectId && x.VersionId != release.Id && (File.Exists(SafePaths.Under(mods, x.Filename)) || File.Exists(SafePaths.Under(mods, x.Filename + ".disabled"))))) throw new InvalidOperationException("该模组或依赖已安装其他版本。请先在实例 mods 目录移除旧版本后重试。");
            var file = new DownloadFile(release.Filename, release.Url!, release.Hash, release.Size, release.HashKind);
            var destination = SafePaths.Under(mods, release.Filename);
            if (File.Exists(destination + ".disabled")) throw new InvalidOperationException("该模组已停用，请在版本管理中启用，或先处理停用的文件：" + release.Filename);
            if (File.Exists(destination) && !DownloadEngine.Valid(destination, file)) throw new InvalidOperationException("mods 目录已有同名但内容不同的文件，请先处理：" + release.Filename);
        }
        var staging = SafePaths.Under(root, ".acml/mod-downloads/" + Guid.NewGuid().ToString("N"));
        var added = new List<string>();
        try
        {
            await new DownloadEngine(client, "官方", threads).FetchAsync(staging, releases.Select(x => new DownloadFile(x.Filename, x.Url!, x.Hash, x.Size, x.HashKind)), "下载模组及必需依赖（" + provider + "）", progress, token);
            token.ThrowIfCancellationRequested();
            foreach (var release in releases)
            {
                var destination = SafePaths.Under(mods, release.Filename);
                if (!File.Exists(destination)) { File.Move(SafePaths.Under(staging, release.Filename), destination); added.Add(destination); }
                index.RemoveAll(x => x.Provider == provider && x.ProjectId == release.ProjectId);
                index.Add(new(provider, release.ProjectId, release.Id, release.Filename));
            }
            DownloadEngine.WriteJsonAtomic(indexPath, JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { foreach (var path in added) File.Delete(path); throw; }
        finally { if (Directory.Exists(staging) && Path.GetDirectoryName(staging) == SafePaths.Under(root, ".acml/mod-downloads")) Directory.Delete(staging, true); }
    }
}
