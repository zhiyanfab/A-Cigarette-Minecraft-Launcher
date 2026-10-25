using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace ACML.Core;

public sealed record LoaderVersion(string Loader, string Version, string GameVersion, string? InstallerUrl = null)
{ public override string ToString() => Version; }

public sealed class LoaderCatalog(HttpClient client, string source = "自动")
{
    public async Task<IReadOnlyList<LoaderVersion>> FetchAsync(string gameVersion, string loader, CancellationToken token)
    {
        if (loader == "原版") return [];
        if (loader == "Fabric")
        {
            var url = "https://meta.fabricmc.net/v2/versions/loader/" + Uri.EscapeDataString(gameVersion);
            var json = JsonNode.Parse(await Get(url, token))!.AsArray();
            return json.Select(x => new LoaderVersion(loader, x!["loader"]!["version"]!.GetValue<string>(), gameVersion)).ToList();
        }
        string repository, group, artifact, prefix;
        if (loader == "Forge") { repository="https://maven.minecraftforge.net/"; group="net/minecraftforge"; artifact="forge"; prefix=gameVersion+"-"; }
        else if (loader == "NeoForge" && gameVersion == "1.20.1") { repository="https://maven.neoforged.net/releases/"; group="net/neoforged"; artifact="forge"; prefix=gameVersion+"-"; }
        else if (loader == "NeoForge")
        {
            repository="https://maven.neoforged.net/releases/"; group="net/neoforged"; artifact="neoforge";
            var parts=gameVersion.Split('.');
            if (parts[0]=="1") prefix=parts.Length>=2 ? parts[1]+"."+(parts.Length>=3 ? parts[2] : "0")+"." : "unavailable.";
            else prefix=gameVersion+(parts.Length==2 ? ".0." : ".");
        }
        else throw new InvalidOperationException("未知的模组加载器。");
        var document = XDocument.Parse(await Get(repository+group+"/"+artifact+"/maven-metadata.xml", token));
        return document.Descendants("version").Select(x=>x.Value).Where(x=>x.StartsWith(prefix,StringComparison.Ordinal))
            .Select(x=>new LoaderVersion(loader,artifact=="forge" ? x[(gameVersion.Length+1)..] : x,gameVersion,repository+group+"/"+artifact+"/"+x+"/"+artifact+"-"+x+"-installer.jar"))
            .OrderByDescending(x=>NumericVersion(x.Version)).ThenBy(x=>x.Version.Contains("beta",StringComparison.OrdinalIgnoreCase)).ToList();
    }
    private static Version NumericVersion(string value) => Version.TryParse(value.Split('-')[0],out var version) ? version : new Version(0,0);
    private async Task<string> Get(string url,CancellationToken token)
    {
        return await new DownloadEngine(client,source).GetTextAsync(url,token);
    }
}
