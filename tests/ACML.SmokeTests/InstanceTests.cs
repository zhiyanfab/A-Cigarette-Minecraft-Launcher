using ACML.Core;
using System.IO.Compression;
using System.Security.Cryptography;

static class InstanceTests
{
    public static async Task RunAsync(Action<string, Action> check, string scratch)
    {
        static void Assert(bool value) { if (!value) throw new Exception("Assertion failed"); }
        static void Throws<T>(Action action) where T:Exception { try { action(); } catch(T) { return; } throw new Exception("Expected " + typeof(T).Name); }
        string Setup(string name)
        {
            var root = Path.Combine(scratch, name); var directory = Path.Combine(root, "versions", "old"); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "old.json"), """{"id":"old","type":"release","arguments":{"game":[],"jvm":[]},"mainClass":"net.minecraft.client.main.Main","libraries":[],"javaVersion":{"majorVersion":17}}""");
            File.WriteAllBytes(Path.Combine(directory, "old.jar"), [1,2,3]);
            Directory.CreateDirectory(Path.Combine(root, "versions", "old", "saves")); File.WriteAllText(Path.Combine(root, "versions", "old", "saves", "world.txt"), "keep-world");
            Directory.CreateDirectory(Path.Combine(root, ".acml", "install-records")); File.Copy(Path.Combine(directory, "old.json"),Path.Combine(root, ".acml", "install-records", "old.json"));
            return root;
        }
        check("Per-version settings persist, override globals and keep other instances unchanged", () =>
        {
            var root=Setup("settings"); var global=new LauncherSettings {GameRoot=root,MemoryMb=4096,IsolateVersions=false};
            InstanceManager.SaveSettings(root,"old",new() {Enabled=true,MemoryMb=2048,JavaPath="C:\\Java\\bin\\java.exe",Isolate=true});
            var effective=InstanceManager.EffectiveSettings(global,"old");Assert(effective.MemoryMb==2048 && effective.IsolateVersions && effective.JavaPath.Contains("Java"));
            Assert(InstanceManager.EffectiveSettings(global,"other").MemoryMb==4096 && global.MemoryMb==4096);
            Assert(InstanceManager.GameDirectory(global,"old")==Path.Combine(root,"versions","old"));
            InstanceManager.SaveSettings(root,"old",new() {Enabled=false});Assert(ReferenceEquals(InstanceManager.EffectiveSettings(global,"old"),global));
        });
        check("Rename synchronizes folder, JSON, JAR, manifest ID, saves, settings and repair record", () =>
        {
            var root=Setup("rename"); InstanceManager.SaveSettings(root,"old",new(){Enabled=true,MemoryMb=3072});
            var committed=false;InstanceManager.Rename(root,"old","测试 1.20.1",()=>committed=true);
            var json=VersionService.Read(root,"测试 1.20.1");Assert(committed && json["id"]!.GetValue<string>()=="测试 1.20.1" && json["acmlBaseVersion"]!.GetValue<string>()=="old");
            Assert(File.ReadAllBytes(Path.Combine(root,"versions","测试 1.20.1","测试 1.20.1.jar")).SequenceEqual(new byte[]{1,2,3}));
            Assert(File.ReadAllText(Path.Combine(root,"versions","测试 1.20.1","saves","world.txt"))=="keep-world");
            Assert(InstanceManager.LoadSettings(new(){GameRoot=root},"测试 1.20.1").MemoryMb==3072);
            Assert(File.Exists(Path.Combine(root,".acml","install-records","测试 1.20.1.json")) && !Directory.Exists(Path.Combine(root,"versions","old")));
            InstanceManager.Rename(root,"测试 1.20.1","TEST");InstanceManager.Rename(root,"TEST","test");Assert(VersionService.Read(root,"test")["id"]!.GetValue<string>()=="test");
        });
        check("Failed rename rolls back all files and never overwrites a conflicting target", () =>
        {
            var root=Setup("rollback");InstanceManager.SaveSettings(root,"old",new(){Enabled=true});
            var original=File.ReadAllBytes(Path.Combine(root,"versions","old","old.json"));
            Throws<IOException>(()=>InstanceManager.Rename(root,"old","new",()=>throw new IOException("commit failed")));
            Assert(original.SequenceEqual(File.ReadAllBytes(Path.Combine(root,"versions","old","old.json"))) && File.Exists(Path.Combine(root,"versions","old","old.jar")));
            Assert(File.Exists(Path.Combine(root,"versions","old","saves","world.txt")) && !Directory.Exists(Path.Combine(root,"versions","new")));
            Directory.CreateDirectory(Path.Combine(root,"versions","new"));File.WriteAllText(Path.Combine(root,"versions","new","keep.txt"),"keep");
            Throws<IOException>(()=>InstanceManager.Rename(root,"old","new"));Assert(File.ReadAllText(Path.Combine(root,"versions","new","keep.txt"))=="keep");
        });
        check("Instance names reject traversal, Windows devices and invalid trailing characters", () =>
        {
            foreach(var id in new[]{"../bad","a/b","a\\b",".","..","CON","nul.txt","trailing.","trailing "}) Throws<InvalidOperationException>(()=>InstanceManager.ValidateName(id));
            InstanceManager.ValidateName("我的世界 1.20.1-Fabric");
        });
        check("Mods list and enable/disable operate only on the selected instance and preserve bytes", () =>
        {
            var root=Setup("mods");var settings=new LauncherSettings{GameRoot=root};var dir=Path.Combine(root,"versions","old","mods");Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir,"sample.jar"),[4,5,6]);File.WriteAllText(Path.Combine(dir,"notes.txt"),"keep");
            var mod=InstanceManager.ListMods(settings,"old").Single();Assert(mod.Enabled);
            InstanceManager.ToggleMod(settings,"old",mod);mod=InstanceManager.ListMods(settings,"old").Single();Assert(!mod.Enabled && File.ReadAllBytes(Path.Combine(dir,"sample.jar.disabled")).SequenceEqual(new byte[]{4,5,6}));
            InstanceManager.ToggleMod(settings,"old",mod);Assert(File.Exists(Path.Combine(dir,"sample.jar")) && File.Exists(Path.Combine(dir,"notes.txt")));
            Throws<InvalidDataException>(()=>InstanceManager.ToggleMod(settings,"old",new("../outside.jar",true,1)));
        });
        check("Java SHA256 packages reject modified content", () =>
        {
            var path=Path.Combine(scratch,"java-hash.zip");File.WriteAllBytes(path,[10,20,30]);
            var file=new DownloadFile("java.zip","https://github.com/adoptium/mock.zip",Convert.ToHexString(SHA256.HashData([10,20,30])),3,"SHA256");
            Assert(DownloadEngine.Valid(path,file));File.WriteAllBytes(path,[10,20,31]);Assert(!DownloadEngine.Valid(path,file));
        });
        check("Java archive rejects zip traversal without writing outside its destination", () =>
        {
            var path=Path.Combine(scratch,"bad-java.zip");using(var zip=ZipFile.Open(path,ZipArchiveMode.Create))using(var writer=new StreamWriter(zip.CreateEntry("../escape-java.exe").Open()))writer.Write("bad");
            Throws<InvalidDataException>(()=>JavaInstaller.ExtractAsync(path,Path.Combine(scratch,"java-extracted"),CancellationToken.None).GetAwaiter().GetResult());Assert(!File.Exists(Path.Combine(scratch,"escape-java.exe")));
        });
        using var http=new HttpClient(new PackageHandler());
        var package=await new JavaInstaller(http).GetPackageAsync(17,CancellationToken.None);
        check("Java metadata selects Windows x64 package and falls back from absent JRE to JDK",()=>Assert(package.Size==123 && package.Sha256.Length==64));
    }
    private sealed class PackageHandler:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if(request.RequestUri!.Query.Contains("image_type=jre"))return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new StringContent("""[{"binary":{"os":"windows","architecture":"x64","package":{"link":"https://github.com/adoptium/test/releases/download/jre/java.zip","checksum":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","size":123}}}]""")});
        }
    }
}

