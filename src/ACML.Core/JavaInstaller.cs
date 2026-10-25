using System.IO.Compression;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ACML.Core;

public sealed record JavaPackage(string Url, string Sha256, long Size);

public sealed class JavaInstaller(HttpClient client, int threads = DownloadEngine.DefaultThreads)
{
    public async Task<JavaRuntime> EnsureAsync(string root, int major, string? preferred, IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        if (major is < 8 or > 50) throw new InvalidOperationException("游戏清单中的 Java 版本无效，请先补全游戏版本。");
        async Task<JavaRuntime?> Inspect(string? path)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { var runtime = await JavaService.InspectAsync(path); return runtime.Is64Bit && runtime.Major == major ? runtime : null; }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException) { return null; }
        }
        if (await Inspect(preferred) is JavaRuntime configured) return configured;
        var managed = SafePaths.Under(root, ".acml/java");
        if (Directory.Exists(managed))
            foreach (var path in Directory.EnumerateFiles(managed, "java.exe", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 5 }))
                if (await Inspect(path) is JavaRuntime cached && cached.Major == major) return cached;
        progress?.Report(new("检测本机 Java", 0, 1, 0));
        var local = await JavaService.DiscoverAsync(null, [root], token);
        token.ThrowIfCancellationRequested();
        var matching = local.FirstOrDefault(x => x.Is64Bit && x.Major == major);
        return matching ?? await InstallAsync(root, major, progress, token);
    }
    public async Task<JavaPackage> GetPackageAsync(int major, CancellationToken token)
    {
        foreach (var kind in new[] { "jre", "jdk" })
        {
            using var response = await client.GetAsync($"https://api.adoptium.net/v3/assets/latest/{major}/hotspot?architecture=x64&heap_size=normal&image_type={kind}&os=windows&vendor=eclipse", token);
            if (response.StatusCode == HttpStatusCode.NotFound) continue;
            response.EnsureSuccessStatusCode();
            var assets = JsonNode.Parse(await response.Content.ReadAsStringAsync(token))?.AsArray();
            var binary = assets?.Select(x => x?["binary"]).FirstOrDefault(x => x?["os"]?.GetValue<string>() == "windows" && x["architecture"]?.GetValue<string>() == "x64");
            var package = binary?["package"]; if (package is null) continue;
            var url = package["link"]!.GetValue<string>(); var hash = package["checksum"]!.GetValue<string>();
            var uri = new Uri(url);
            if (uri.Scheme != "https" || uri.Host != "github.com" || !uri.AbsolutePath.StartsWith("/adoptium/", StringComparison.OrdinalIgnoreCase) || !uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(hash, "^[a-fA-F0-9]{64}$"))
                throw new InvalidDataException("Java 下载元数据的来源或校验值无效。");
            var size = package["size"]!.GetValue<long>();
            if (size is <= 0 or > 1_000_000_000) throw new InvalidDataException("Java 下载包大小异常。");
            return new(url, hash, size);
        }
        throw new InvalidOperationException($"没有可用的 Java {major} Windows x64 下载包，请在版本设置中指定 Java。");
    }
    public async Task<JavaRuntime> InstallAsync(string root, int major, IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        progress?.Report(new($"获取 Java {major} 下载信息", 0, 1, 0));
        var package = await GetPackageAsync(major, token);
        using var gate = GameInstaller.AcquireLock(root);
        var relative = $".acml/java-downloads/{package.Sha256}.zip";
        await new DownloadEngine(client, "官方", threads).FetchAsync(root, [new(relative, package.Url, package.Sha256, package.Size, "SHA256")], $"下载 Java {major} x64", progress, token);
        var staging = SafePaths.Under(root, ".acml/java/.install-" + Guid.NewGuid().ToString("N"));
        var destination = SafePaths.Under(root, $".acml/java/temurin-{major}-{package.Sha256[..12]}-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        try
        {
            progress?.Report(new($"解压 Java {major}", 0, 1, 0));
            await ExtractAsync(SafePaths.Under(root, relative), staging, token);
            var java = Directory.EnumerateFiles(staging, "java.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4 }).Where(x => Path.GetFileName(Path.GetDirectoryName(x)) == "bin").SingleOrDefault()
                ?? throw new InvalidDataException("Java 下载包中没有唯一的 bin/java.exe。");
            var runtime = await JavaService.InspectAsync(java);
            if (runtime.Major != major || !runtime.Is64Bit) throw new InvalidDataException("Java 运行时的版本或架构与请求不符。");
            token.ThrowIfCancellationRequested();
            var executable = Path.GetRelativePath(staging, java);
            Directory.Move(staging, destination);
            progress?.Report(new($"Java {major} 已就绪", 1, 1, package.Size));
            return runtime with { Path = SafePaths.Under(destination, executable) };
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
    public static async Task ExtractAsync(string archive, string destination, CancellationToken token)
    {
        using var zip = ZipFile.OpenRead(archive);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            total = checked(total + entry.Length);
            if (total > 2_000_000_000 || (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000) throw new InvalidDataException("Java 压缩包超出限制或包含符号链接。");
            var path = SafePaths.Under(destination, entry.FullName);
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var input = entry.Open();
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await input.CopyToAsync(output, token);
        }
    }
}
