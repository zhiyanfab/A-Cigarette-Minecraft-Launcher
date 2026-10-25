using ACML.Core;
using System.Net;
using System.Text.Json.Nodes;

static class LoaderAndModsTests
{
    public static async Task RunAsync(Action<string,Action> check)
    {
        static void Assert(bool value){if(!value)throw new Exception("Assertion failed");}
        using var client=new HttpClient(new Handler((r,_)=>
        {
            var path=r.RequestUri!.AbsolutePath;
            var content=r.RequestUri.Host=="meta.fabricmc.net"?"""[{"loader":{"version":"0.16.14","stable":true}},{"loader":{"version":"0.15.11","stable":true}}]"""
                :path.Contains("net/minecraftforge")?"<metadata><versioning><versions><version>1.20.1-47.4.2</version><version>1.20.1-47.4.28</version><version>1.21.1-52.0.1</version></versions></versioning></metadata>"
                :path.Contains("/forge/")?"<metadata><versioning><versions><version>1.20.1-47.1.106</version></versions></versioning></metadata>"
                :"<metadata><versioning><versions><version>21.0.10</version><version>21.1.233</version><version>21.1.234-beta</version><version>26.3.0.98</version><version>26.3.1.1</version></versions></versioning></metadata>";
            return Task.FromResult(Json(content));
        }));
        var forge=await new LoaderCatalog(client).FetchAsync("1.20.1","Forge",default);
        check("Forge catalog filters exact Minecraft version, sorts numeric releases and constructs official installer URL",()=>Assert(forge.Count==2&&forge[0].Version=="47.4.28"&&forge[0].InstallerUrl!.EndsWith("forge-1.20.1-47.4.28-installer.jar")));
        var neo=await new LoaderCatalog(client).FetchAsync("1.21.1","NeoForge",default);var neoOld=await new LoaderCatalog(client).FetchAsync("1.20.1","NeoForge",default);var neoNew=await new LoaderCatalog(client).FetchAsync("26.3","NeoForge",default);
        check("NeoForge catalog maps old Forge group, 1.21 patch numbering and modern 26.x numbering",()=>Assert(neo.Count==2&&neoOld[0].InstallerUrl!.Contains("net/neoforged/forge/")&&neoNew.Count==1&&neoNew[0].Version=="26.3.0.98"));
        var fabric=await new LoaderCatalog(client).FetchAsync("1.20.1","Fabric",default);
        check("Fabric catalog exposes explicit loader versions",()=>Assert(fabric.Select(x=>x.Version).SequenceEqual(new[]{"0.16.14","0.15.11"})));
        check("Forge manifest merge preserves vanilla assets, replaces library versions and combines launch arguments",()=>
        {
            var vanilla=JsonNode.Parse("""{"id":"1.20.1","mainClass":"vanilla.Main","downloads":{"client":{"url":"client"}},"arguments":{"jvm":["-cp","${classpath}"],"game":["--username","${auth_player_name}"]},"libraries":[{"name":"org.ow2.asm:asm:9.2"},{"name":"org.lwjgl:lwjgl:3.3.1:natives-windows"}],"assetIndex":{"id":"5"}}""")!.AsObject();
            var child=JsonNode.Parse("""{"inheritsFrom":"1.20.1","mainClass":"cpw.mods.bootstraplauncher.BootstrapLauncher","arguments":{"jvm":["-p","${library_directory}"],"game":["--launchTarget","forgeclient"]},"libraries":[{"name":"org.ow2.asm:asm:9.9"}]}""")!.AsObject();
            var merged=ForgeInstaller.Merge(vanilla,child);Assert(merged["inheritsFrom"] is null&&merged["mainClass"]!.GetValue<string>().Contains("BootstrapLauncher")&&merged["assetIndex"]!["id"]!.GetValue<string>()=="5");
            Assert(merged["arguments"]!["game"]!.AsArray().Count==4&&merged["libraries"]!.AsArray().Count==2&&merged["libraries"]!.AsArray().Any(x=>x!["name"]!.GetValue<string>()=="org.ow2.asm:asm:9.9"));
            Assert(vanilla["mainClass"]!.GetValue<string>()=="vanilla.Main");
        });
        check("Forge processor Maven coordinates support classifier and non-JAR mappings",()=>Assert(ForgeInstaller.MavenPath("[net.minecraft:client:1.20.1:mappings@txt]")=="net/minecraft/client/1.20.1/client-1.20.1-mappings.txt"));
        using var mods=new HttpClient(new Handler((request,_)=>
        {
            var path=request.RequestUri!.AbsolutePath;
            if(path.EndsWith("/search"))return Task.FromResult(Json("""{"hits":[{"project_id":"project","title":"Test Mod","description":"Test","slug":"test","icon_url":"https://cdn.modrinth.com/icon.png","versions":["1.20.1","1.21.1"],"categories":["fabric","neoforge","technology"]}]}"""));
            if(path.EndsWith("/project/project"))return Task.FromResult(Json("""{"id":"project","title":"Test Mod","description":"Test","slug":"test","icon_url":"https://cdn.modrinth.com/icon.png","game_versions":["1.20.1","1.21.1"],"loaders":["fabric","neoforge"]}"""));
            var release="""{"project_id":"project","id":"v1","name":"Test 1.0","version_type":"release","date_published":"2026-10-25T00:00:00Z","game_versions":["1.20.1"],"loaders":["fabric"],"files":[{"primary":true,"filename":"test.jar","url":"https://cdn.modrinth.com/test.jar","size":42,"hashes":{"sha512":"abcd"}}],"dependencies":[{"dependency_type":"required","project_id":"required","version_id":null},{"dependency_type":"optional","project_id":"optional","version_id":null}]}""";
            return Task.FromResult(Json(path.EndsWith("/version")?"["+release+"]":release));
        }));
        var provider=new ModrinthProvider(mods);var recommend=await provider.SearchAsync("","","",default);var detail=await provider.ProjectAsync("project",default);var releases=await provider.ReleasesAsync("project","","",default);
        check("Empty mod search works without an installed instance and returns icon, game versions and loaders",()=>Assert(recommend.Single().IconUrl!.StartsWith("https:")&&recommend[0].GameVersions!.Count==2&&recommend[0].Loaders!.Count==2));
        check("Mod detail and file list retain metadata and required/optional dependency distinctions",()=>Assert(detail.Loaders!.Contains("neoforge")&&releases.Single().Dependencies.Count==2&&releases[0].Dependencies.Single(x=>x.Kind=="optional").ProjectId=="optional"&&releases[0].GameVersions!.Contains("1.20.1")));
        var rejected=false;try{await provider.ExactAsync("v1","1.21.1","neoforge",default);}catch(InvalidOperationException){rejected=true;}
        check("Browsing a file does not allow installing it into an incompatible game or loader",()=>Assert(rejected));
        var releaseRoot=new ModRelease("root","v1","Root","root.jar","https://test.example/root.jar","hash",42,"SHA1",[new("optional",null,"optional")]);
        var resolved=await ModInstaller.ResolveAsync(provider,releaseRoot,"1.20.1","fabric",default);
        check("Optional dependency is displayed in metadata but not installed automatically",()=>Assert(resolved.Count==1));
        check("Mod icons reject local-file and insecure URLs",()=>Assert(ModMetadata.Icon("file:///C:/secret.png") is null&&ModMetadata.Icon("http://example.com/icon.png") is null));
    }
    private static HttpResponseMessage Json(string text)=>new(HttpStatusCode.OK){Content=new StringContent(text)};
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> handler):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>handler(request,token);}
}
