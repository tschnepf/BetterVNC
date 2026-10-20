using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace BetterVnc.Platform;

/// <summary>
/// Windows DPAPI, scoped to the current user. Values are base64 of the protected bytes. Every build so far has written
/// them without the "dpapi:" prefix, and older viewers sharing %AppData%\BetterVNC\profiles.json cannot read a prefixed
/// value, so this store keeps writing the bare form and reads both. A bare value therefore means DPAPI to every store.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore : ISecretStore
{
    public const string Prefix = "dpapi:";

    public string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return null;
        var value = protectedValue.StartsWith(Prefix, StringComparison.Ordinal) ? protectedValue[Prefix.Length..] : protectedValue;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }
}
