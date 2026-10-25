using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ACML.Core;
public sealed record ResolvedVersionManifest(JsonObject Manifest, string ClientRelativePath);
public static class VersionManifestResolver
{
    public static ResolvedVersionManifest Resolve(string root, string id) => Resolve(root, id, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    private static ResolvedVersionManifest Resolve(string root, string id, HashSet<string> seen)
    {
        InstanceManager.ValidateName(id);
        if (seen.Count >= 32 || !seen.Add(id)) throw new InvalidDataException("游戏版本继承存在循环或层级过深。");
        var child = VersionService.Read(root, id); NormalizeArguments(child);
        var parentId = child["inheritsFrom"]?.GetValue<string>(); ResolvedVersionManifest? parent = null;
        if (parentId is not null) { InstanceManager.ValidateName(parentId); parent = Resolve(root, parentId, seen); }
        var json = parent is null ? (JsonObject)child.DeepClone() : Merge(parent.Manifest, child);
        json.Remove("inheritsFrom"); json["id"] = id;
        var gameArguments = ManifestRules.Arguments(json["arguments"]?["game"]).ToList();
        var mcArgument = gameArguments.IndexOf("--fml.mcVersion");
        json["acmlBaseVersion"] ??= json["clientVersion"]?.DeepClone() ?? (mcArgument >= 0 && mcArgument + 1 < gameArguments.Count ? JsonValue.Create(gameArguments[mcArgument + 1]) : null)
            ?? parent?.Manifest["acmlBaseVersion"]?.DeepClone() ?? JsonValue.Create(parentId ?? child["acmlOriginalId"]?.GetValue<string>() ?? id);
        var jar = child["jar"]?.GetValue<string>(); if (jar is not null) InstanceManager.ValidateName(jar);
        var ownClient = $"versions/{id}/{id}.jar";
        var originalId = child["acmlOriginalId"]?.GetValue<string>();
        var candidates = new List<string> { ownClient };
        if (jar is not null) candidates.Add($"versions/{jar}/{jar}.jar");
        if (originalId is not null) { InstanceManager.ValidateName(originalId); candidates.Add($"versions/{id}/{originalId}.jar"); candidates.Add($"versions/{originalId}/{originalId}.jar"); }
        if (parent is not null) candidates.Add(parent.ClientRelativePath);
        var client = candidates.FirstOrDefault(x => File.Exists(SafePaths.Under(root, x)))
            ?? (jar is not null ? $"versions/{jar}/{jar}.jar" : parent?.ClientRelativePath ?? ownClient);
        json["libraries"] ??= new JsonArray();
        var libraries = json["libraries"]!.AsArray();
        foreach (var library in libraries.Where(x => x is not null).ToArray())
        {
            if (library!["clientreq"]?.GetValue<bool>() == false) { libraries.Remove(library); continue; }
            if (library["downloads"] is not null) continue;
            var coordinate = library["name"]?.GetValue<string>() ?? throw new InvalidDataException("运行库缺少 Maven 坐标。");
            var path = ForgeInstaller.MavenPath(coordinate); var repository = library["url"]?.GetValue<string>() ?? "https://libraries.minecraft.net/";
            var artifact = new JsonObject { ["path"] = path, ["url"] = repository.TrimEnd('/') + "/" + path };
            var checksum = library["checksums"]?.AsArray().FirstOrDefault()?.GetValue<string>(); if (checksum is not null && Regex.IsMatch(checksum, "^[a-fA-F0-9]{40}$")) artifact["sha1"] = checksum;
            library["downloads"] = new JsonObject { ["artifact"] = artifact };
        }
        return new(json, client);
    }
    private static void NormalizeArguments(JsonObject json)
    {
        if (json["arguments"] is null && json["minecraftArguments"]?.GetValue<string>() is string legacy)
        {
            var args = new JsonArray();
            foreach (Match token in Regex.Matches(legacy, "\"((?:\\\\.|[^\"])*)\"|(\\S+)")) args.Add(token.Groups[1].Success ? token.Groups[1].Value.Replace("\\\"", "\"") : token.Groups[2].Value);
            json["arguments"] = new JsonObject { ["game"] = args, ["jvm"] = new JsonArray("-Djava.library.path=${natives_directory}", "-cp", "${classpath}") };
        }
    }
    private static JsonObject Merge(JsonObject parent, JsonObject child)
    {
        var result = (JsonObject)parent.DeepClone();
        foreach (var pair in child)
            if (pair.Key is not ("inheritsFrom" or "arguments" or "libraries" or "downloads" or "logging")) result[pair.Key] = pair.Value?.DeepClone();
        foreach (var key in new[] { "downloads", "logging" })
        {
            if (child[key] is not JsonObject extra) continue; result[key] ??= new JsonObject();
            foreach (var pair in extra) result[key]![pair.Key] = pair.Value?.DeepClone();
        }
        result["arguments"] ??= new JsonObject();
        foreach (var kind in new[] { "game", "jvm" })
        {
            result["arguments"]![kind] ??= new JsonArray();
            if (child["arguments"]?[kind] is not JsonArray extra) continue;
            if (kind == "game" && extra.OfType<JsonValue>().Any(x => x.TryGetValue<string>(out var text) && text == "--username")) result["arguments"]![kind] = extra.DeepClone();
            else foreach (var value in extra) result["arguments"]![kind]!.AsArray().Add(value?.DeepClone());
        }
        result["libraries"] ??= new JsonArray(); var libraries = result["libraries"]!.AsArray();
        static string Key(JsonNode library) { var parts = (library["name"]?.GetValue<string>() ?? "").Split(':'); return string.Join(':', parts.Take(2)) + (parts.Length > 3 ? ":" + parts[3] : ""); }
        foreach (var library in child["libraries"]?.AsArray() ?? [])
        {
            if (library is null) continue; var key = Key(library);
            foreach (var existing in libraries.Where(x => x is not null && Key(x) == key).ToArray()) libraries.Remove(existing);
            libraries.Add(library.DeepClone());
        }
        return result;
    }
}
