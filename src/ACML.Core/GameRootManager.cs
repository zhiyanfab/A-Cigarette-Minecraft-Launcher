namespace ACML.Core;

public sealed class GameRootEntry
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string? SelectedVersion { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string Label => string.IsNullOrWhiteSpace(Name) ? Path : Name + " · " + Path;
    public override string ToString() => Label;
}

public static class GameRootManager
{
    public static string CanonicalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path.Trim()))
            throw new InvalidOperationException("请选择完整的绝对游戏根目录。");
        return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path.Trim()));
    }
    public static bool Same(string first, string second) => string.Equals(CanonicalPath(first), CanonicalPath(second), StringComparison.OrdinalIgnoreCase);
    public static string ResolveInputPath(string path)
    {
        var full = CanonicalPath(path);
        if (System.IO.Path.GetFileName(full).Equals("versions", StringComparison.OrdinalIgnoreCase))
            return System.IO.Path.GetDirectoryName(full) ?? full;
        var parent = System.IO.Path.GetDirectoryName(full);
        if (parent is not null && System.IO.Path.GetFileName(parent).Equals("versions", StringComparison.OrdinalIgnoreCase)
            && File.Exists(System.IO.Path.Combine(full, System.IO.Path.GetFileName(full) + ".json")))
            return System.IO.Path.GetDirectoryName(parent) ?? full;
        if (!Directory.Exists(System.IO.Path.Combine(full, "versions")) && Directory.Exists(System.IO.Path.Combine(full, ".minecraft", "versions")))
            return System.IO.Path.Combine(full, ".minecraft");
        return full;
    }
    public static void Normalize(LauncherSettings settings)
    {
        var active = ResolveInputPath(settings.GameRoot);
        var unique = new List<GameRootEntry>();
        foreach (var entry in settings.GameRoots ?? [])
        {
            if (entry is null) throw new InvalidDataException("游戏目录列表包含空项目。");
            entry.Path = ResolveInputPath(entry.Path); entry.Name = (entry.Name ?? "").Trim();
            var existing = unique.FirstOrDefault(x => Same(x.Path, entry.Path));
            if (existing is null) unique.Add(entry);
            else { existing.SelectedVersion ??= entry.SelectedVersion; if (existing.Name.Length == 0) existing.Name = entry.Name; }
        }
        var current = unique.FirstOrDefault(x => Same(x.Path, active));
        if (current is null) { current = new() { Path = active }; unique.Insert(0, current); }
        // The legacy top-level selection remains authoritative for the current root.
        current.SelectedVersion = settings.SelectedVersion;
        settings.GameRoots = unique; settings.GameRoot = current.Path;
    }
    public static void Remember(LauncherSettings settings) => Normalize(settings);
    public static GameRootEntry Activate(LauncherSettings settings, string path, string? name = null)
    {
        var canonical = ResolveInputPath(path);
        if (name is not null && name.Trim().Length > 80) throw new InvalidOperationException("目录名称最多为 80 个字符。");
        Remember(settings);
        var target = settings.GameRoots.FirstOrDefault(x => Same(x.Path, canonical));
        if (target is null) { target = new() { Path = canonical }; settings.GameRoots.Add(target); }
        if (name is not null) target.Name = name.Trim();
        settings.GameRoot = target.Path; settings.SelectedVersion = target.SelectedVersion;
        return target;
    }
    public static void Remove(LauncherSettings settings, string path)
    {
        Remember(settings);
        var target = settings.GameRoots.FirstOrDefault(x => Same(x.Path, path)) ?? throw new InvalidOperationException("游戏目录不在列表中。");
        if (settings.GameRoots.Count == 1) throw new InvalidOperationException("请至少保留一个游戏根目录。");
        if (Same(settings.GameRoot, target.Path)) Activate(settings, settings.GameRoots.First(x => !ReferenceEquals(x, target)).Path);
        settings.GameRoots.Remove(target);
    }
    public static void Rename(LauncherSettings settings, string path, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80) throw new InvalidOperationException("目录名称须为 1–80 个字符。");
        var canonical = CanonicalPath(path);
        var target = settings.GameRoots.FirstOrDefault(x => Same(x.Path, canonical)) ?? throw new InvalidOperationException("游戏目录不在列表中。");
        target.Name = name.Trim();
    }
}
