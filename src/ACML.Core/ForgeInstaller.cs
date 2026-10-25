using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ACML.Core;

public sealed class ForgeInstaller(HttpClient client, string source, int threads = DownloadEngine.DefaultThreads)
{
    public async Task<JsonObject> InstallAsync(string root, string localId, JsonObject vanilla, LoaderVersion loader, JavaRuntime java,
        IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        if (loader.Loader is not ("Forge" or "NeoForge") || loader.InstallerUrl is null) throw new InvalidOperationException("加载器安装版本无效。");
        var uri = new Uri(loader.InstallerUrl);
        if (uri.Scheme != "https" || uri.Host is not ("maven.minecraftforge.net" or "maven.neoforged.net")) throw new InvalidDataException("加载器安装包来源无效。");
        var sha = (await new DownloadEngine(client,source,threads).GetTextAsync(loader.InstallerUrl+".sha1", token)).Trim().Split(' ')[0];
        if (!Regex.IsMatch(sha,"^[a-fA-F0-9]{40}$")) throw new InvalidDataException("加载器安装包缺少有效的 SHA1。");
        var archive = $".acml/loader-installers/{sha}.jar";
        await new DownloadEngine(client,source,threads).FetchAsync(root,[new(archive,loader.InstallerUrl,sha)],"下载 "+loader.Loader+" "+loader.Version,progress,token);
        var installer = SafePaths.Under(root,archive);
        JsonObject profile;JsonObject packagedVersion;
        using(var zip=ZipFile.OpenRead(installer))
        {
            var entry=zip.GetEntry("install_profile.json") ?? throw new InvalidDataException("安装器缺少 install_profile.json。");
            using var reader=new StreamReader(entry.Open()); profile=JsonNode.Parse(await reader.ReadToEndAsync(token))!.AsObject();
            var versionEntry=zip.GetEntry((profile["json"]?.GetValue<string>()??"version.json").TrimStart('/'))??throw new InvalidDataException("安装器缺少版本清单。");
            using var versionReader=new StreamReader(versionEntry.Open());packagedVersion=JsonNode.Parse(await versionReader.ReadToEndAsync(token))!.AsObject();
        }
        if(profile["minecraft"]?.GetValue<string>() != loader.GameVersion || profile["processors"] is not JsonArray)
            throw new InvalidOperationException("当前支持 1.13 及以上的现代 Forge / NeoForge 安装器。");
        var stage=SafePaths.Under(root,".acml/loader-staging/"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var logPath=SafePaths.Under(root,".acml/loader-logs/"+loader.Loader+"-"+Guid.NewGuid().ToString("N")+".log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        try
        {
            progress?.Report(new("准备 "+loader.Loader+" 安装环境",0,1,0));
            var baseFolder=SafePaths.Under(stage,"versions/"+loader.GameVersion);Directory.CreateDirectory(baseFolder);
            var baseJson=(JsonObject)vanilla.DeepClone();baseJson["id"]=loader.GameVersion;
            await File.WriteAllTextAsync(Path.Combine(baseFolder,loader.GameVersion+".json"),baseJson.ToJsonString(),token);
            File.Copy(SafePaths.Under(root,$"versions/{localId}/{localId}.jar"),Path.Combine(baseFolder,loader.GameVersion+".jar"));
            await File.WriteAllTextAsync(Path.Combine(stage,"launcher_profiles.json"),"{\"profiles\":{}}",token);
            await CopyTreeAsync(SafePaths.Under(root,"libraries"),SafePaths.Under(stage,"libraries"),token);
            var required=new List<DownloadFile>();
            foreach(var library in profile["libraries"]!.AsArray().Concat(packagedVersion["libraries"]!.AsArray()))
            {
                if(library?["downloads"]?["artifact"] is not JsonNode artifact||artifact["url"]?.GetValue<string>() is not {Length:>0} url)continue;
                required.Add(new("libraries/"+artifact["path"]!.GetValue<string>(),url,artifact["sha1"]?.GetValue<string>(),artifact["size"]?.GetValue<long>()??0));
            }
            if(profile["data"]?["MOJMAPS"]?["client"]?.GetValue<string>() is string coordinate&&vanilla["downloads"]?["client_mappings"] is JsonNode mappings)
                required.Add(new("libraries/"+MavenPath(coordinate),mappings["url"]!.GetValue<string>(),mappings["sha1"]!.GetValue<string>(),mappings["size"]!.GetValue<long>()));
            await new DownloadEngine(client,source,threads).FetchAsync(stage,required,"下载 "+loader.Loader+" 安装依赖",progress,token);
            var start=new ProcessStartInfo(java.Path){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=stage};
            foreach(var argument in new[]{"-Djava.awt.headless=true","-Djava.net.preferIPv4Stack=true","-jar",installer,"--installClient",stage})start.ArgumentList.Add(argument);
            using var process=new Process{StartInfo=start};
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromMinutes(20));
            progress?.Report(new("安装 "+loader.Loader+" "+loader.Version,0,1,0));
            process.Start();
            using var log=new StreamWriter(logPath);
            var logGate=new SemaphoreSlim(1);
            async Task Read(StreamReader reader)
            {
                while(await reader.ReadLineAsync() is string line)
                {
                    await logGate.WaitAsync();try{await log.WriteLineAsync(line);}finally{logGate.Release();}
                    // Installer output contains no authentication tokens. Keep progress compact.
                    if(line.StartsWith("Downloading",StringComparison.OrdinalIgnoreCase)||line.StartsWith("Building",StringComparison.OrdinalIgnoreCase)||line.StartsWith("Processing",StringComparison.OrdinalIgnoreCase))
                        progress?.Report(new(loader.Loader+" · "+line[..Math.Min(line.Length,140)],0,1,0));
                }
            }
            var stdout=Read(process.StandardOutput);var stderr=Read(process.StandardError);
            try{await process.WaitForExitAsync(timeout.Token);}
            catch(OperationCanceledException){try{process.Kill(true);}catch(InvalidOperationException){}await process.WaitForExitAsync();await Task.WhenAll(stdout,stderr);token.ThrowIfCancellationRequested();throw new TimeoutException("加载器安装超时，请重试。日志："+logPath);}
            await Task.WhenAll(stdout,stderr);await log.FlushAsync(token);
            if(process.ExitCode!=0)throw new IOException(loader.Loader+" 安装失败，退出码 "+process.ExitCode+"。日志："+logPath);
            var childId=profile["version"]!.GetValue<string>();InstanceManager.ValidateName(childId);
            var child=JsonNode.Parse(await File.ReadAllTextAsync(SafePaths.Under(stage,$"versions/{childId}/{childId}.json"),token))!.AsObject();
            var merged=Merge(vanilla,child);merged["id"]=localId;merged["acmlBaseVersion"]=loader.GameVersion;
            merged["acmlLoader"]=loader.Loader;merged["acmlLoaderVersion"]=loader.Version;merged["acmlInstallerUrl"]=loader.InstallerUrl;
            var generated=new JsonArray();
            var generatedPaths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var value in profile["data"]!.AsObject())
                if(value.Value?["client"]?.GetValue<string>() is string generatedCoordinate&&generatedCoordinate.StartsWith('['))generatedPaths.Add(MavenPath(generatedCoordinate));
            foreach(var lib in packagedVersion["libraries"]!.AsArray())
                if(lib?["downloads"]?["artifact"] is JsonNode artifact&&string.IsNullOrEmpty(artifact["url"]?.GetValue<string>())&&artifact["path"]?.GetValue<string>() is string path)generatedPaths.Add(path);
            var libraryRoot=SafePaths.Under(stage,"libraries");
            foreach(var file in Directory.EnumerateFiles(libraryRoot,"*",new EnumerationOptions{RecurseSubdirectories=true,AttributesToSkip=FileAttributes.ReparsePoint}))
            {
                token.ThrowIfCancellationRequested();var relative=Path.GetRelativePath(libraryRoot,file).Replace('\\','/');
                using var stream=File.OpenRead(file);var hash=Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
                var descriptor=new DownloadFile("libraries/"+relative,"",hash,new FileInfo(file).Length);
                var destination=SafePaths.Under(root,descriptor.RelativePath);
                if(!DownloadEngine.Valid(destination,descriptor))await CopyAtomicAsync(file,destination,token);
                // Preserve generated patch outputs too, including those absent from the launch classpath.
                if(generatedPaths.Contains(relative))
                    generated.Add(new JsonObject{["path"]=descriptor.RelativePath,["sha1"]=hash,["size"]=descriptor.Size});
            }
            merged["acmlGeneratedLibraries"]=generated;
            // Generated artifacts have no download URL. Give them local integrity descriptors.
            foreach(var library in merged["libraries"]!.AsArray())
            {
                if(library?["downloads"]?["artifact"] is not JsonObject artifact)continue;
                var path=artifact["path"]?.GetValue<string>();if(path is null)continue;
                var file=SafePaths.Under(root,"libraries/"+path);
                if(!File.Exists(file))continue;
                if(string.IsNullOrEmpty(artifact["url"]?.GetValue<string>()))
                {using var stream=File.OpenRead(file);artifact["sha1"]=Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();artifact["size"]=new FileInfo(file).Length;}
            }
            progress?.Report(new(loader.Loader+" 已安装",1,1,0));return merged;
        }
        finally{if(Directory.Exists(stage))Directory.Delete(stage,true);}
    }
    public static JsonObject Merge(JsonObject vanilla,JsonObject child)
    {
        var result=(JsonObject)vanilla.DeepClone();
        foreach(var pair in child)
            if(pair.Key is not ("id" or "inheritsFrom" or "arguments" or "libraries" or "downloads" or "logging"))result[pair.Key]=pair.Value?.DeepClone();
        result.Remove("inheritsFrom");
        foreach(var kind in new[]{"game","jvm"})
            if(child["arguments"]?[kind] is JsonArray extra)
            {var args=result["arguments"]![kind]!.AsArray();foreach(var value in extra)args.Add(value?.DeepClone());}
        var libraries=result["libraries"]!.AsArray();
        static string Key(JsonNode library){var parts=library["name"]!.GetValue<string>().Split(':');return parts[0]+":"+parts[1]+(parts.Length>3 ? ":"+parts[3] : "");}
        foreach(var lib in child["libraries"]!.AsArray())
        {
            if(lib is null)continue;var key=Key(lib);
            foreach(var old in libraries.Where(x=>x is not null&&Key(x)==key).ToList())libraries.Remove(old);
            libraries.Add(lib.DeepClone());
        }
        return result;
    }
    public static string MavenPath(string coordinate)
    {
        var value=coordinate.Trim('[',']');var ext=value.Split('@');var parts=ext[0].Split(':');
        if(parts.Length is <3 or >4)throw new InvalidDataException("安装器 Maven 坐标无效。");
        return parts[0].Replace('.','/')+"/"+parts[1]+"/"+parts[2]+"/"+parts[1]+"-"+parts[2]+(parts.Length==4?"-"+parts[3]:"")+"."+(ext.Length>1?ext[1]:"jar");
    }
    private static async Task CopyTreeAsync(string source,string destination,CancellationToken token)
    {
        if(!Directory.Exists(source))return;
        foreach(var file in Directory.EnumerateFiles(source,"*",new EnumerationOptions{RecurseSubdirectories=true,AttributesToSkip=FileAttributes.ReparsePoint}))
        {token.ThrowIfCancellationRequested();await CopyAtomicAsync(file,SafePaths.Under(destination,Path.GetRelativePath(source,file)),token);}
    }
    private static async Task CopyAtomicAsync(string source,string destination,CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);var temporary=destination+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{await using(var input=File.OpenRead(source))await using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,true))await input.CopyToAsync(output,token);File.Move(temporary,destination,true);}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
