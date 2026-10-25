using ACML.Core;
using Microsoft.Identity.Client;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace ACML.App;

public sealed class MicrosoftLoginService : IDisposable
{
    private static readonly string[] Scopes = ["XboxLive.signin"];
    private readonly IPublicClientApplication application;
    private readonly RegistryTokenStore tokens;
    private readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(30) };
    public MicrosoftLoginService(string dataDirectory)
    {
        tokens = new(dataDirectory);
        application = PublicClientApplicationBuilder.Create(LauncherInfo.MicrosoftClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, AadAuthorityAudience.PersonalMicrosoftAccount)
            .WithRedirectUri("http://localhost")
            .Build();
        application.UserTokenCache.SetBeforeAccess(args =>
        {
            if (tokens.Read("msal-cache") is byte[] bytes) args.TokenCache.DeserializeMsalV3(bytes, shouldClearExistingCache: true);
        });
        application.UserTokenCache.SetAfterAccess(args =>
        {
            if (args.HasStateChanged) tokens.Write("msal-cache", args.TokenCache.SerializeMsalV3());
        });
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ACML/" + LauncherInfo.Version);
    }
    public async Task<(PlayerAccount Account, GameSession Session)> AddAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await application.AcquireTokenInteractive(Scopes)
                .WithPrompt(Prompt.SelectAccount).WithUseEmbeddedWebView(false).ExecuteAsync(cancellationToken);
            var session = await new MinecraftAuthentication(client).ExchangeAsync(result.AccessToken, cancellationToken);
            var account = new PlayerAccount("ms-" + session.Uuid, session.Name, session.Uuid, true, result.Account.HomeAccountId.Identifier);
            tokens.SaveSession(account.Id, session);
            return (account, session);
        }
        catch (MsalException ex) { throw Translate(ex); }
    }
    public async Task<GameSession> RefreshAsync(PlayerAccount account, CancellationToken cancellationToken)
    {
        if (!account.IsMicrosoft) throw new InvalidOperationException("此账号不是微软账号。");
        try
        {
            var cached = (await application.GetAccountsAsync()).FirstOrDefault(x => x.HomeAccountId.Identifier == account.MicrosoftAccountId)
                ?? throw new InvalidOperationException("该账号的微软授权缓存已丢失，请重新进行微软登录。");
            var result = await application.AcquireTokenSilent(Scopes, cached).ExecuteAsync(cancellationToken);
            var session = await new MinecraftAuthentication(client).ExchangeAsync(result.AccessToken, cancellationToken);
            if (session.Uuid != account.Uuid) throw new InvalidOperationException("返回的玩家身份与所选账号不一致，请重新登录。");
            tokens.SaveSession(account.Id, session);
            return session;
        }
        catch (MsalUiRequiredException) { throw new InvalidOperationException("该账号需要重新授权。请点击“微软登录”并选择该账号。"); }
        catch (MsalException ex) { throw Translate(ex); }
    }
    public async Task RemoveAsync(PlayerAccount account)
    {
        if (!account.IsMicrosoft) return;
        var cached = (await application.GetAccountsAsync()).FirstOrDefault(x => x.HomeAccountId.Identifier == account.MicrosoftAccountId);
        if (cached is not null) await application.RemoveAsync(cached);
        tokens.Delete("session-" + account.Id);
    }
    private static Exception Translate(MsalException error)
    {
        if (error.ErrorCode is "authentication_canceled" or "user_canceled") return new OperationCanceledException("微软登录已取消。");
        var aadCode = Regex.Match(error.Message, "AADSTS[0-9]+").Value;
        return new InvalidOperationException($"微软授权失败（{(aadCode.Length > 0 ? aadCode : error.ErrorCode)}）。请确认应用支持个人 Microsoft 账号，并在“移动和桌面应用程序”平台登记 http://localhost 回调。若为账号或租户权限问题，请在微软官方页面处理。");
    }
    public void Dispose() => client.Dispose();
}
