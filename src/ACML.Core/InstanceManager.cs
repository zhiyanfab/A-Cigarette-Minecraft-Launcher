using System.Text.Json;
using System.Text.Json.Nodes;

namespace ACML.Core;

public sealed class InstanceSettings
{
    public bool Enabled { get; set; }
    public string JavaPath { get; set; } = "";
    public int MemoryMb { get; set; } = 4096;
    public int GameWidth { get; set; } = 1280;
    public int GameHeight { get; set; } = 720;
    public bool Isolate { get; set; } = true;
    public string GameWindowTitle { get; set; } = "";
    // True defaults preserve previously saved independent settings.
    public bool OverrideJava { get; set; } = true;
    public bool OverrideMemory { get; set; } = true;
    public bool OverrideWindowSize { get; set; } = true;
    public bool OverrideIsolation { get; set; } = true;
    public bool OverrideTitle { get; set; }
}

public sealed record LocalModFile(string Filename, bool Enabled, long Size)
{
    public string Label => Filename + (Enabled ? "" : " · 已停用");
    public string SizeLabel => $"{Size / 1048576.0:0.##} MB";
}

public static class InstanceManager
{
    public static void ValidateName(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 100 || id is "." or ".." || id.EndsWith('.') || id.EndsWith(' ')
            || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains('/') || id.Contains('\\'))
            throw new InvalidOperationException("版本文件夹名称无效，不能包含路径符号或以点、空格结尾（最多 100 字符）。");
        var stem = id.Split('.')[0];
        if (System.Text.RegularExpressions.Regex.IsMatch(stem, "^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new InvalidOperationException("此名称是 Windows 保留名称，请换一个名称。");
    }
    private static string SettingsPath(string root, string id) { ValidateName(id); return SafePaths.Under(root, $".acml/instance-settings/{id}.json"); }
    public static InstanceSettings LoadSettings(LauncherSettings global, string id)
    {
        var path = SettingsPath(global.GameRoot, id);
        if (File.Exists(path)) return JsonSerializer.Deserialize<InstanceSettings>(File.ReadAllText(path)) ?? throw new InvalidDataException("版本设置为空。");
        return new() { JavaPath = global.JavaPath, MemoryMb = global.MemoryMb, GameWidth = global.GameWidth, GameHeight = global.GameHeight, Isolate = global.IsolateVersions,
            GameWindowTitle = global.GameWindowTitle, OverrideJava = false, OverrideMemory = false, OverrideWindowSize = false, OverrideIsolation = false };
    }
    public static void SaveSettings(string root, string id, InstanceSettings settings)
    {
        ValidateSettings(settings);
        using var gate = GameInstaller.AcquireLock(root);
        DownloadEngine.WriteJsonAtomic(SettingsPath(root, id), JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static void ValidateSettings(InstanceSettings settings)
    {
        ValidateGameTitle(settings.GameWindowTitle);
        if (settings.MemoryMb is < 512 or > 65536 || settings.GameWidth is < 320 or > 16384 || settings.GameHeight is < 240 or > 16384)
            throw new InvalidOperationException("内存须为 512–65536 MB，窗口宽度 320–16384、高度 240–16384。");
    }
    public static void ValidateGameTitle(string title)
    { if (title.Length > 128 || title.Any(char.IsControl)) throw new InvalidOperationException("游戏标题最多为 128 个字符，不能包含换行或控制字符。"); }
    public static LauncherSettings EffectiveSettings(LauncherSettings global, string id)
    {
        var local = LoadSettings(global, id);
        if (!local.Enabled) return global;
        ValidateSettings(local);
        return new LauncherSettings { GameRoot = global.GameRoot, SelectedVersion = id, JavaPath = local.OverrideJava ? local.JavaPath : global.JavaPath, MemoryMb = local.OverrideMemory ? local.MemoryMb : global.MemoryMb,
            GameWidth = local.OverrideWindowSize ? local.GameWidth : global.GameWidth, GameHeight = local.OverrideWindowSize ? local.GameHeight : global.GameHeight,
            IsolateVersions = local.OverrideIsolation ? local.Isolate : global.IsolateVersions,
            GameWindowTitle = local.OverrideTitle ? local.GameWindowTitle : global.GameWindowTitle,
            HideOnLaunch = global.HideOnLaunch, DownloadSource = global.DownloadSource, DownloadThreads = global.DownloadThreads };
    }
    public static string GameDirectory(LauncherSettings global, string id)
    { ValidateName(id); return EffectiveSettings(global, id).IsolateVersions ? SafePaths.Under(global.GameRoot, "versions/" + id) : Path.GetFullPath(global.GameRoot); }
    public static void PrepareGameDirectory(LauncherSettings settings, string id, CancellationToken token = default)
    {
        ValidateName(id);
        if (!EffectiveSettings(settings, id).IsolateVersions) return;
        var target = SafePaths.Under(settings.GameRoot, "versions/" + id);
        var legacy = SafePaths.Under(settings.GameRoot, "instances/" + id);
        var marker = SafePaths.Under(target, ".acml-legacy-isolation-imported.json");
        if (!Directory.Exists(legacy) || File.Exists(marker)) return;
        using var gate = GameInstaller.AcquireLock(settings.GameRoot);
        RejectLinks(settings.GameRoot, legacy); RejectLinks(settings.GameRoot, target);
        if (File.Exists(marker)) return;
        var copies = new List<(string Source, string Target)>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(legacy, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested(); RejectLinks(settings.GameRoot, entry);
            if (Directory.Exists(entry)) continue;
            var destination = SafePaths.Under(target, Path.GetRelativePath(legacy, entry)); RejectLinks(settings.GameRoot, destination);
            if (File.Exists(destination))
            {
                using var first = File.OpenRead(entry); using var second = File.OpenRead(destination);
                if (first.Length != second.Length || !System.Security.Cryptography.SHA256.HashData(first).SequenceEqual(System.Security.Cryptography.SHA256.HashData(second)))
                    throw new IOException("旧隔离目录与版本目录存在不同的同名文件，已保留两份原文件，请先处理冲突：" + destination + "；旧文件：" + entry);
            }
            else copies.Add((entry, destination));
        }
        foreach (var copy in copies)
        {
            token.ThrowIfCancellationRequested(); Directory.CreateDirectory(Path.GetDirectoryName(copy.Target)!);
            var temporary = copy.Target + "." + Guid.NewGuid().ToString("N") + ".part";
            try { File.Copy(copy.Source, temporary); token.ThrowIfCancellationRequested(); File.Move(temporary, copy.Target); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        DownloadEngine.WriteJsonAtomic(marker, JsonSerializer.Serialize(new { Source = legacy, ImportedAt = DateTimeOffset.UtcNow }));
        // Keep the original directory as a backup; the marker prevents reimporting disabled/removed mods later.
    }

    // Every moved path is inside the chosen .minecraft root. A failed rename rolls back all completed moves.
    public static void Rename(string root, string oldId, string newId, Action? commit = null)
    {
        ValidateName(oldId); ValidateName(newId);
        if (oldId == newId) { commit?.Invoke(); return; }
        using var gate = GameInstaller.AcquireLock(root);
        var source = SafePaths.Under(root, "versions/" + oldId);
        var target = SafePaths.Under(root, "versions/" + newId);
        RejectLinks(root, source);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("版本目录不存在。");
        if (!oldId.Equals(newId, StringComparison.OrdinalIgnoreCase) && (Directory.Exists(target) || File.Exists(target))) throw new IOException("目标版本名称已存在。");
        if (!oldId.Equals(newId, StringComparison.OrdinalIgnoreCase) &&
            (Directory.Exists(SafePaths.Under(root, "instances/" + newId)) || File.Exists(SettingsPath(root, newId)) || File.Exists(SafePaths.Under(root, $".acml/install-records/{newId}.json"))))
            throw new IOException("目标名称已有实例数据，请换一个名称。");
        var json = VersionService.Read(root, oldId);
        json["acmlBaseVersion"] ??= json["inheritsFrom"]?.DeepClone() ?? JsonValue.Create(oldId);
        json["id"] = newId;
        var undo = new Stack<Action>();
        void MoveDirectory(string from, string to)
        {
            RejectLinks(root, from); RejectLinks(root, to);
            Directory.Move(from, to); undo.Push(() => Directory.Move(to, from));
        }
        void MoveFile(string from, string to)
        {
            if (!File.Exists(from)) return;
            RejectLinks(root, from); RejectLinks(root, to);
            // An intermediate name also handles case-only changes on Windows.
            var temporary = from + "." + Guid.NewGuid().ToString("N") + ".rename";
            File.Move(from, temporary); undo.Push(() => File.Move(temporary, from));
            File.Move(temporary, to); undo.Push(() => File.Move(to, temporary));
        }
        void Rewrite(string path, string contents)
        {
            var original = File.ReadAllBytes(path);
            undo.Push(() => File.WriteAllBytes(path, original));
            DownloadEngine.WriteJsonAtomic(path, contents);
        }
        var staging = SafePaths.Under(root, "versions/.acml-rename-" + Guid.NewGuid().ToString("N"));
        try
        {
            MoveDirectory(source, staging); MoveDirectory(staging, target);
            MoveFile(Path.Combine(target, oldId + ".json"), Path.Combine(target, newId + ".json"));
            MoveFile(Path.Combine(target, oldId + ".jar"), Path.Combine(target, newId + ".jar"));
            Rewrite(Path.Combine(target, newId + ".json"), json.ToJsonString());
            var oldInstance = SafePaths.Under(root, "instances/" + oldId);
            var newInstance = SafePaths.Under(root, "instances/" + newId);
            if (Directory.Exists(oldInstance))
            {
                var tempInstance = SafePaths.Under(root, "instances/.acml-rename-" + Guid.NewGuid().ToString("N"));
                MoveDirectory(oldInstance, tempInstance); MoveDirectory(tempInstance, newInstance);
            }
            MoveFile(SettingsPath(root, oldId), SettingsPath(root, newId));
            var oldRecord = SafePaths.Under(root, $".acml/install-records/{oldId}.json");
            var newRecord = SafePaths.Under(root, $".acml/install-records/{newId}.json");
            MoveFile(oldRecord, newRecord);
            if (File.Exists(newRecord)) Rewrite(newRecord, json.ToJsonString());
            commit?.Invoke();
        }
        catch (Exception original)
        {
            var errors = new List<Exception> { original };
            while (undo.TryPop(out var action)) { try { action(); } catch (Exception ex) { errors.Add(ex); } }
            if (errors.Count > 1) throw new AggregateException("更名未完成，部分文件无法回退，请保留版本目录并查看日志。", errors);
            throw;
        }
    }
    private static void RejectLinks(string root, string path)
    {
        var boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        for (var current = Path.GetFullPath(path); current.Length >= boundary.Length; current = Path.GetDirectoryName(current) ?? "")
        {
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("版本管理不能移动符号链接或目录连接。");
            if (current.Equals(boundary, StringComparison.OrdinalIgnoreCase)) break;
        }
    }
    public static IReadOnlyList<LocalModFile> ListMods(LauncherSettings settings, string id)
    {
        var directory = Path.Combine(GameDirectory(settings, id), "mods");
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory).Where(x => x.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
            .Select(x => new LocalModFile(Path.GetFileName(x), x.EndsWith(".jar", StringComparison.OrdinalIgnoreCase), new FileInfo(x).Length)).OrderBy(x => x.Filename).ToList();
    }
    public static void ToggleMod(LauncherSettings settings, string id, LocalModFile mod)
    {
        if (Path.GetFileName(mod.Filename) != mod.Filename || !(mod.Filename.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || mod.Filename.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("无效的模组文件名。");
        using var gate = GameInstaller.AcquireLock(settings.GameRoot);
        var directory = Path.Combine(GameDirectory(settings, id), "mods");
        var source = SafePaths.Under(directory, mod.Filename);
        var destination = SafePaths.Under(directory, mod.Enabled ? mod.Filename + ".disabled" : mod.Filename[..^9]);
        RejectLinks(settings.GameRoot, source); RejectLinks(settings.GameRoot, destination);
        File.Move(source, destination);
    }
}
