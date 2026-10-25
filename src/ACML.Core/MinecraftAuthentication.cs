using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ACML.Core;

public sealed class GameSession
{
    public required string Name { get; init; }
    public required string Uuid { get; init; }
    public required string AccessToken { get; init; }
    public string Xuid { get; init; } = "";
    public required DateTimeOffset ExpiresAt { get; init; }
    public bool IsValid => ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2);
    public override string ToString() => $"Minecraft session for {Name} (token hidden)";
}

/// <summary>Only official Xbox and Minecraft HTTPS endpoints are used. No passwords are collected.</summary>
public sealed class MinecraftAuthentication(HttpClient client)
{
    // Xbox requires PascalCase keys; JsonContent's default Web options change them to camelCase.
    // Minecraft payload members already use their required camelCase names.
    private static readonly JsonSerializerOptions AuthenticationJson = new() { PropertyNamingPolicy = null };
    public async Task<GameSession> ExchangeAsync(string microsoftAccessToken, CancellationToken cancellationToken)
    {
        var xbox = await PostAsync("https://user.auth.xboxlive.com/user/authenticate", new
        {
            Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = "d=" + microsoftAccessToken },
            RelyingParty = "http://auth.xboxlive.com", TokenType = "JWT"
        }, "Xbox Live 身份验证", cancellationToken);
        var xsts = await PostAsync("https://xsts.auth.xboxlive.com/xsts/authorize", new
        {
            Properties = new { SandboxId = "RETAIL", UserTokens = new[] { Required(xbox, "Token") } },
            RelyingParty = "rp://api.minecraftservices.com/", TokenType = "JWT"
        }, "Xbox XSTS 授权", cancellationToken);
        var claim = xsts["DisplayClaims"]?["xui"]?[0] ?? throw new InvalidDataException("Xbox 授权未返回玩家身份。");
        var login = await PostAsync("https://api.minecraftservices.com/authentication/login_with_xbox", new
        {
            identityToken = $"XBL3.0 x={Required(claim, "uhs")};{Required(xsts, "Token")}", ensureLegacyEnabled = true
        }, "Minecraft 服务登录", cancellationToken);
        var token = Required(login, "access_token");
        var entitlements = await GetAsync("https://api.minecraftservices.com/entitlements/mcstore", token, "Java 版游戏权限检查", cancellationToken);
        if (entitlements["items"] is not JsonArray { Count: > 0 }) throw new InvalidOperationException("此账号未返回 Minecraft Java 版游戏权限。请确认购买或订阅资格，并使用拥有游戏的账号。");
        var profile = await GetAsync("https://api.minecraftservices.com/minecraft/profile", token, "Minecraft 玩家档案", cancellationToken);
        var uuid = Required(profile, "id");
        if (!System.Text.RegularExpressions.Regex.IsMatch(uuid, "^[0-9a-fA-F]{32}$")) throw new InvalidDataException("Minecraft 玩家 UUID 格式无效。");
        var seconds = login["expires_in"]?.GetValue<int>() ?? 0;
        if (seconds <= 0) throw new InvalidDataException("Minecraft 服务返回的令牌已失效。");
        return new() { Name = Required(profile, "name"), Uuid = uuid.ToLowerInvariant(), AccessToken = token, ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds), Xuid = claim["xid"]?.GetValue<string>() ?? "" };
    }
    private async Task<JsonNode> PostAsync(string url, object body, string stage, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, options: AuthenticationJson) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (url.Contains("xboxlive.com", StringComparison.Ordinal)) request.Headers.Add("x-xbl-contract-version", "1");
        return await SendAsync(request, stage, cancellationToken);
    }
    private async Task<JsonNode> GetAsync(string url, string token, string stage, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await SendAsync(request, stage, cancellationToken);
    }
    private async Task<JsonNode> SendAsync(HttpRequestMessage request, string stage, CancellationToken cancellationToken)
    {
        using var response = await client.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        JsonNode? json;
        try { json = JsonNode.Parse(text); } catch (System.Text.Json.JsonException) { json = null; }
        if (!response.IsSuccessStatusCode)
        {
            var xerr = (json as JsonObject)?["XErr"]?.ToString();
            if (!System.Text.RegularExpressions.Regex.IsMatch(xerr ?? "", "^[0-9]{1,20}$")) xerr = null;
            var detail = xerr switch
            {
                "2148916233" => "该微软账号尚未建立 Xbox 档案，请先在 xbox.com 完成档案设置。",
                "2148916235" => "此地区的 Xbox 服务暂不可用。",
                "2148916236" or "2148916237" => "Xbox 要求完成年龄或身份验证，请在官方页面处理。",
                "2148916238" => "该账号受 Xbox 家庭设置限制，需要家庭组织者在官方页面处理。",
                _ => response.StatusCode == HttpStatusCode.Forbidden ? "请核实此 Client ID 的 Minecraft 服务访问资格，以及账号的游戏权限。"
                    : response.StatusCode == HttpStatusCode.Unauthorized ? "授权已失效，请重新进行微软登录。"
                    : response.StatusCode == HttpStatusCode.NotFound ? "尚未找到 Java 版玩家档案，请先在 Minecraft 官方页面完成档案设置。"
                    : response.StatusCode == HttpStatusCode.BadRequest && request.RequestUri?.Host == "user.auth.xboxlive.com" ? "Xbox 未接受登录请求，请重新进行微软登录；若仍失败，请提供此错误编号以便继续定位。" : "请稍后重试。"
            };
            // Never put an upstream response body or token in an exception or log.
            var code = xerr is null ? "" : $"，Xbox 错误码 {xerr}";
            throw new InvalidOperationException($"{stage}失败（HTTP {(int)response.StatusCode}{code}）。{detail}");
        }
        return json ?? throw new InvalidDataException(stage + "返回的数据无法解析。");
    }
    private static string Required(JsonNode node, string name) => node[name]?.GetValue<string>() is { Length: > 0 } value ? value : throw new InvalidDataException("认证响应缺少 " + name + "。");
}
