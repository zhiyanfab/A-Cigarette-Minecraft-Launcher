using System.Security.Cryptography;

namespace ACML.Core;

public sealed record DownloadFile(string RelativePath, string Url, string? Hash = null, long Size = 0, string HashKind = "SHA1", IReadOnlyList<string>? FallbackUrls = null);
public sealed record DownloadProgress(string Stage, int Completed, int Total, long Bytes)
{
    public double Percent => Total == 0 ? 0 : Completed * 100.0 / Total;
    public string Label => $"{Stage} · {Completed}/{Total} 个文件 · 下载 {Bytes / 1048576.0:0.#} MB";
}

public sealed class DownloadEngine(HttpClient client, string source = "自动", int threads = 16)
{
    public const int DefaultThreads = 16;
    public static string? Mirror(string url)
    {
        var uri = new Uri(url);
        var path = uri.PathAndQuery;
        return uri.Host switch
        {
            "resources.download.minecraft.net" => "https://bmclapi2.bangbang93.com/assets" + path,
            "libraries.minecraft.net" or "maven.fabricmc.net" => "https://bmclapi2.bangbang93.com/maven" + path,
            "meta.fabricmc.net" => "https://bmclapi2.bangbang93.com/fabric-meta" + path,
            "maven.minecraftforge.net" => "https://bmclapi2.bangbang93.com/maven" + path,
            "maven.neoforged.net" when uri.AbsolutePath.StartsWith("/releases/", StringComparison.Ordinal) => "https://bmclapi2.bangbang93.com/maven/" + path[10..],
            "launchermeta.mojang.com" or "launcher.mojang.com" or "piston-meta.mojang.com" or "piston-data.mojang.com" => "https://bmclapi2.bangbang93.com" + path,
            _ => null
        };
    }
    public static IReadOnlyList<string> SourceUrls(string url, string source)
    {
        var mirror = Mirror(url);
        if (mirror is null || source == "官方") return [url];
        if (source == "BMCLAPI") return [mirror, url];
        // Prefer the mirrored NeoForge repository in automatic mode to avoid slow regional connections.
        return new Uri(url).Host == "maven.neoforged.net" ? [mirror, url] : [url, mirror];
    }
    public async Task<string> GetTextAsync(string url, CancellationToken token)
    {
        Exception? last = null;
        foreach (var candidate in SourceUrls(url, source))
        {
            token.ThrowIfCancellationRequested();
            try { return await client.GetStringAsync(candidate, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException) { last = ex; }
        }
        throw new IOException("获取下载元数据失败，请检查网络或切换下载源。", last);
    }
    public static bool Valid(string path, DownloadFile file)
    {
        if (!File.Exists(path)) return false;
        if (file.Size > 0 && new FileInfo(path).Length != file.Size) return false;
        if (string.IsNullOrEmpty(file.Hash)) return new FileInfo(path).Length > 0;
        using var stream = File.OpenRead(path);
        var digest = file.HashKind switch { "SHA512" => SHA512.HashData(stream), "SHA256" => SHA256.HashData(stream), "MD5" => MD5.HashData(stream), _ => SHA1.HashData(stream) };
        return Convert.ToHexString(digest).Equals(file.Hash, StringComparison.OrdinalIgnoreCase);
    }
    public async Task FetchAsync(string root, IEnumerable<DownloadFile> files, string stage, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var list = files.GroupBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToList();
        var completed = 0; long bytes = 0; long nextReport = 0;
        var parallelism = Math.Clamp(threads, 1, 64);
        using var slots = new SemaphoreSlim(parallelism);
        void CountBytes(int count)
        {
            Interlocked.Add(ref bytes, count);
            var now = Environment.TickCount64; var previous = Volatile.Read(ref nextReport);
            if (now >= previous && Interlocked.CompareExchange(ref nextReport, now + 250, previous) == previous)
                progress?.Report(new(stage, Volatile.Read(ref completed), list.Count, Interlocked.Read(ref bytes)));
        }
        progress?.Report(new(stage, 0, list.Count, 0));
        await Parallel.ForEachAsync(list, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken }, async (file, token) =>
        {
            var destination = SafePaths.Under(root, file.RelativePath);
            if (!Valid(destination, file))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
                try
                {
                    var urls = new[] { file.Url }.Concat(file.FallbackUrls ?? []).SelectMany(x => SourceUrls(x, source)).Distinct().ToArray();
                    Exception? last = null; var downloaded = false;
                    foreach (var url in urls)
                    {
                        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") throw new InvalidDataException("下载链接必须使用 HTTPS。");
                        for (var attempt = 0; attempt < 3; attempt++)
                        {
                            token.ThrowIfCancellationRequested();
                            try
                            {
                                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(3));
                                var segmented = parallelism > 1 && file.Size >= 8 * 1024 * 1024 && !string.IsNullOrEmpty(file.Hash)
                                    && await FetchSegmentsAsync(uri, temporary, file.Size, Math.Min(4, parallelism), slots, CountBytes, timeout.Token);
                                if (!segmented) await FetchSingleAsync(uri, temporary, slots, CountBytes, timeout.Token);
                                if (!Valid(temporary, file)) throw new InvalidDataException("下载文件校验失败。");
                                File.Move(temporary, destination, true); downloaded = true; break;
                            }
                            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                            catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or OperationCanceledException)
                            { last = ex; if (attempt < 2) await Task.Delay(TimeSpan.FromSeconds(attempt + 1), token); }
                        }
                        if (downloaded) break;
                    }
                    if (!downloaded) throw new IOException("无法下载 " + file.RelativePath + "，请检查网络或切换下载源。", last);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            var done = Interlocked.Increment(ref completed); progress?.Report(new(stage, done, list.Count, Interlocked.Read(ref bytes)));
        });
    }
    private async Task FetchSingleAsync(Uri uri, string path, SemaphoreSlim slots, Action<int> countBytes, CancellationToken token)
    {
        await slots.WaitAsync(token);
        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920]; int read;
            while ((read = await input.ReadAsync(buffer, token)) > 0) { await output.WriteAsync(buffer.AsMemory(0, read), token); countBytes(read); }
        }
        finally { slots.Release(); }
    }
    private sealed class RangeUnavailableException : IOException { }
    private async Task<bool> FetchSegmentsAsync(Uri uri, string path, long size, int count, SemaphoreSlim slots, Action<int> countBytes, CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var handle = File.OpenHandle(path, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.Asynchronous | FileOptions.RandomAccess);
        RandomAccess.SetLength(handle, size);
        var rangeUnavailable = 0;
        try
        {
            await Task.WhenAll(Enumerable.Range(0, count).Select(async part =>
            {
                var start = size * part / count; var end = size * (part + 1) / count - 1;
                await slots.WaitAsync(cancellation.Token);
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);
                    request.Headers.AcceptEncoding.ParseAdd("identity");
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
                    if (response.StatusCode is System.Net.HttpStatusCode.OK or System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
                    { Interlocked.Exchange(ref rangeUnavailable, 1); throw new RangeUnavailableException(); }
                    response.EnsureSuccessStatusCode();
                    var range = response.Content.Headers.ContentRange;
                    if (response.StatusCode != System.Net.HttpStatusCode.PartialContent || range?.Unit != "bytes" || range.From != start || range.To != end || range.Length != size)
                        throw new InvalidDataException("分段响应范围无效。");
                    await using var input = await response.Content.ReadAsStreamAsync(cancellation.Token);
                    var buffer = new byte[81920]; var position = start; int read;
                    while ((read = await input.ReadAsync(buffer, cancellation.Token)) > 0)
                    {
                        if (read > end - position + 1) throw new InvalidDataException("分段响应超过预期长度。");
                        await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), position, cancellation.Token); position += read; countBytes(read);
                    }
                    if (position != end + 1) throw new InvalidDataException("分段响应不完整。");
                }
                catch { cancellation.Cancel(); throw; }
                finally { slots.Release(); }
            }));
            return true;
        }
        catch when (Volatile.Read(ref rangeUnavailable) != 0 && !token.IsCancellationRequested) { return false; }
    }
    public static void WriteJsonAtomic(string path, string contents)
    { Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp"; try { File.WriteAllText(temp, contents); File.Move(temp, path, true); } finally { if (File.Exists(temp)) File.Delete(temp); } }
}
