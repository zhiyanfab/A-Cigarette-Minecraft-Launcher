using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Text.Json;
using ACML.Core;
using Microsoft.Win32;

namespace ACML.App;

/// <summary>The registry contains only DPAPI-protected binary values, never plaintext tokens.</summary>
public sealed class RegistryTokenStore
{
    private readonly string path;
    private readonly object gate = new();
    private static readonly byte[] Entropy = SHA256.HashData(Encoding.UTF8.GetBytes("ACML:" + LauncherInfo.MicrosoftClientId));
    public RegistryTokenStore(string dataDirectory)
    {
        var testDirectory = Environment.GetEnvironmentVariable("ACML_DEMO_DATA_DIR");
        path = string.IsNullOrEmpty(testDirectory) ? @"Software\ACML\Accounts" : @"Software\ACML\TestAccounts\" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataDirectory))));
    }
    public byte[]? Read(string name)
    {
        lock (gate)
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            if (key?.GetValue(name) is not byte[] bytes) return null;
            try { return ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser); }
            catch (CryptographicException) { throw new InvalidOperationException("本机保存的微软凭据无法解密。请移除该账号后重新登录。"); }
        }
    }
    public void Write(string name, byte[] data)
    {
        var encrypted = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
        lock (gate) { using var key = Registry.CurrentUser.CreateSubKey(path); key.SetValue(name, encrypted, RegistryValueKind.Binary); }
    }
    public void Delete(string name)
    { lock (gate) { using var key = Registry.CurrentUser.OpenSubKey(path, true); key?.DeleteValue(name, false); } }
    public void SaveSession(string id, GameSession session) => Write("session-" + id, JsonSerializer.SerializeToUtf8Bytes(session));
    public GameSession? ReadSession(string id) => Read("session-" + id) is byte[] data ? JsonSerializer.Deserialize<GameSession>(data) : null;
}
