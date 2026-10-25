using System.Text.RegularExpressions;

namespace ACML.Core;

public static class LaunchResources
{
    public static async Task EnsureLoggingAsync(HttpClient client, LauncherSettings settings, string id,
        IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        var manifest = VersionManifestResolver.Resolve(settings.GameRoot, id).Manifest;
        if (manifest["logging"]?["client"]?["file"] is not System.Text.Json.Nodes.JsonNode file) return;
        var name = file["id"]?.GetValue<string>() ?? throw new InvalidDataException("日志配置文件缺少名称。");
        if (Path.GetFileName(name) != name || name.Contains('\\') || name.Contains('/')) throw new InvalidDataException("日志配置名称无效。");
        var path = "assets/log_configs/" + name;
        var hash = file["sha1"]?.GetValue<string>(); var size = file["size"]?.GetValue<long>() ?? 0;
        var url = file["url"]?.GetValue<string>() ?? "";
        var descriptor = new DownloadFile(path, url, hash, size);
        if (DownloadEngine.Valid(SafePaths.Under(settings.GameRoot, path), descriptor)) return;
        if (hash is null || !Regex.IsMatch(hash, "^[a-fA-F0-9]{40}$") || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            throw new InvalidDataException("缺少有效的日志配置下载信息，请在版本管理中补全游戏文件。");
        await new DownloadEngine(client, settings.DownloadSource, settings.DownloadThreads).FetchAsync(settings.GameRoot,
            [descriptor], "补全启动日志配置", progress, token);
    }
}
