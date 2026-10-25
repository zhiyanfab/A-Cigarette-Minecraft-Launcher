using System.Text.RegularExpressions;

namespace ACML.Core;
public static class LaunchDiagnostics
{
    public static string Analyze(string log, int requiredJava = 0)
    {
        bool Has(string text) => log.Contains(text, StringComparison.OrdinalIgnoreCase);
        var findings = new List<string>();
        if (Has("Duplicate key") && Has(".jar") && (Has("BootstrapLauncher") || Has("UnionFileSystem")))
            findings.Add("运行库路径重复：同一个 JAR 被重复加入启动类路径，加载器无法初始化。请更新启动器；本版本已对类路径去重，无需删除模组。");
        if (Has("UnsupportedClassVersionError"))
        {
            var version = Regex.Match(log, @"class file version (\d+)(?:\.\d+)?", RegexOptions.IgnoreCase);
            var required = version.Success && int.TryParse(version.Groups[1].Value, out var number) && number >= 52 ? number - 44 : requiredJava;
            findings.Add("Java 版本过低：" + (required > 0 ? $"报错中的类需要 Java {required} 或相应兼容运行时。" : "当前 Java 无法读取游戏或模组的类文件。") + "请在 Java 选择中切换到游戏所需的 64 位版本。");
        }
        else if (Has("Unsupported class file major version")) findings.Add("类文件版本不兼容：可能是加载器或模组内置的字节码工具过旧。核对游戏、加载器、模组与 Java 的兼容版本。");
        if (Has("OutOfMemoryError: Java heap space") || Has("GC overhead limit exceeded")) findings.Add("游戏堆内存不足：在版本设置中适当提高内存上限，并为系统预留内存。");
        if (Has("Could not reserve enough space") || Has("Native memory allocation") || Has("OutOfMemoryError: unable to create native thread")) findings.Add("系统或本机内存不足：关闭占用内存的程序，确认使用 64 位 Java；过高的游戏内存上限也可能导致此错误。");
        if (Has("Incompatible mods found") || Has("Missing or unsupported mandatory dependencies") || Has("Missing mandatory dependencies") || Has("requires version") && Has("Currently, ")) findings.Add("模组版本或前置不匹配：查看报告中的模组名称及所需版本，安装对应游戏版本与加载器的必需前置。");
        if (Has("Mixin apply failed") || Has("MixinTransformerError") || Has("MixinApplyError")) findings.Add("模组注入失败：可能存在模组冲突或版本不兼容。结合报告中的 Mixin 配置和模组名称排查，单凭此提示不能确定具体责任模组。");
        if (Has("GLFW error 65542") || Has("GLFW error 65543") || Has("does not support OpenGL") || Has("Failed to create window")) findings.Add("图形窗口初始化失败：检查显卡驱动与游戏要求的 OpenGL 支持，笔记本可尝试使用独立显卡。");
        if (Has("Could not find or load main class") || Has("缺少游戏文件") || Has("缺少客户端 JAR") || Has("文件校验失败") || Has("文件大小不符")) findings.Add("游戏或加载器文件缺失 / 损坏：在版本管理菜单执行“补全文件”；第三方版本的本地补丁缺失时可能需要重新安装对应加载器。");
        if (Has("AccessDeniedException") || Has("UnauthorizedAccessException") || Has("拒绝访问")) findings.Add("文件访问被拒绝：检查游戏目录的写入权限，以及文件是否被其他程序占用。");
        if (findings.Count == 0) return "未匹配到明确的常见原因。请保存完整错误报告，结合首个异常与 Caused by 信息排查；退出码本身不足以确定原因。";
        return string.Join("\n\n", findings.Distinct().Take(3));
    }
    public static string Redact(string text)
    {
        text = Regex.Replace(text, @"(?i)(--accessToken\s+|Bearer\s+)[^\s]+", "$1[token hidden]");
        text = Regex.Replace(text, "(?i)((?:access_token|refresh_token|id_token|client_secret|RpsTicket)\\\"?\\s*[:=]\\s*\\\"?)[^\\\"\\s,}]+", "$1[token hidden]");
        return Regex.Replace(text, @"(?i)token:[^\s:]+:[a-f0-9]{32}", "token:[token hidden]:[uuid hidden]");
    }
}
