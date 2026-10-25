using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace ACML.Core;

public static class LauncherInfo
{
    public const string MicrosoftClientId = "c9431eb5-d47e-489c-aa55-850f42fbc5aa";
    // IncludeAllContentForSelfExtract changes AppContext.BaseDirectory to the extraction cache.
    // The actual launcher location must remain the executable's directory.
    public static string InstallDirectory => Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
    public static string Version { get; } = typeof(LauncherInfo).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).Cast<System.Reflection.AssemblyInformationalVersionAttribute>().First().InformationalVersion.Split('+')[0];
    public static string DisplayVersion => "正式版 v2.0.1";
}

public sealed record PlayerAccount(string Id, string Name, string Uuid, bool IsMicrosoft = false, string? MicrosoftAccountId = null)
{
    [JsonIgnore] public string Kind => IsMicrosoft ? "微软正版账号 · 已缓存" : "离线账号";
    public static PlayerAccount Create(string name)
    {
        name = name.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9_]{3,16}$"))
            throw new ArgumentException("离线名称须为 3–16 位英文字母、数字或下划线。");
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        hash[6] = (byte)((hash[6] & 0x0f) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new(Guid.NewGuid().ToString("N"), name, Convert.ToHexString(hash).ToLowerInvariant());
    }
    public override string ToString() => Name;
}

public sealed class LauncherSettings
{
    public int SchemaVersion { get; set; } = 1;
    public List<PlayerAccount> Accounts { get; set; } = [];
    public string? ActiveAccountId { get; set; }
    public string GameRoot { get; set; } = Path.Combine(LauncherInfo.InstallDirectory, ".minecraft");
    public List<GameRootEntry> GameRoots { get; set; } = [];
    public string? SelectedVersion { get; set; }
    public string JavaPath { get; set; } = "";
    public int MemoryMb { get; set; } = 4096;
    public int GameWidth { get; set; } = 1280;
    public int GameHeight { get; set; } = 720;
    public bool IsolateVersions { get; set; } = true;
    public bool HideOnLaunch { get; set; }
    public string GameWindowTitle { get; set; } = "";
    public string DownloadSource { get; set; } = "自动";
    public int DownloadThreads { get; set; } = DownloadEngine.DefaultThreads;
    public bool ShowStatusBar { get; set; }
    public bool ExpandedSidebar { get; set; } = true;
    public string? BackgroundImagePath { get; set; }
    public double BackgroundDim { get; set; } = 0.15;
}

public sealed class SettingsStore(string directory)
{
    public string DirectoryPath { get; } = directory;
    private string FilePath => Path.Combine(DirectoryPath, "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public LauncherSettings Load()
    {
        if (!File.Exists(FilePath)) { var initial = new LauncherSettings(); GameRootManager.Normalize(initial); return initial; }
        try
        {
            var settings = JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(FilePath))
                ?? throw new InvalidDataException("配置文件为空。");
            if (settings.SchemaVersion != 1) throw new InvalidDataException("此配置来自不同版本的启动器。");
            if (settings.Accounts is null || string.IsNullOrWhiteSpace(settings.GameRoot))
                throw new InvalidDataException("配置缺少账号列表或游戏目录。");
            GameRootManager.Normalize(settings);
            return settings;
        }
        catch (JsonException ex) { throw new InvalidDataException("配置文件损坏，请在数据目录中备份并修复 settings.json。", ex); }
    }
    public void Save(LauncherSettings settings)
    {
        GameRootManager.Remember(settings);
        Directory.CreateDirectory(DirectoryPath);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options), Encoding.UTF8);
        File.Move(temporary, FilePath, true);
    }
}

public sealed record GameVersion(string Id, string Type, string Loader, int JavaMajor, string? Error, string? BaseId = null, string? LoaderVersion = null)
{
    public bool IsSupported => Loader is "原版" or "Fabric" or "Forge" or "NeoForge" or "Quilt" && Error is null;
    public string Label => $"{Id} · {Loader}";
    public string TypeLabel => Type switch { "release" => "正式版", "snapshot" => "快照版", "old_beta" => "Beta", "old_alpha" => "Alpha", _ => "未知类型" };
    public string Description => TypeLabel + " · " + (BaseId ?? Id) + (Loader == "原版" ? "" : " · " + Loader + (string.IsNullOrWhiteSpace(LoaderVersion) ? "" : " " + LoaderVersion));
    public string Detail => Description + (Error is null ? "" : " · " + Error);
    public override string ToString() => Label;
}

public sealed record RemoteVersion(string Id, string Type, DateTimeOffset ReleaseTime, string Url, string? Sha1 = null)
{
    public string TypeLabel => Type == "release" ? "正式版" : Type == "snapshot" ? "快照版" : "历史版本";
    public string DateLabel => ReleaseTime.ToLocalTime().ToString("yyyy-MM-dd");
}
public sealed record VersionCatalog(string LatestRelease, string LatestSnapshot, IReadOnlyList<RemoteVersion> Versions, string Source);

public static class SafePaths
{
    public static string Under(string root, string relative)
    {
        root = Path.GetFullPath(root);
        if (Path.IsPathRooted(relative)) throw new InvalidDataException("清单包含绝对路径。");
        var result = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("清单包含超出游戏目录的路径。");
        return result;
    }
}
