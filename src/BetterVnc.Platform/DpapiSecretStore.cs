using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace BetterVnc.Platform;

/// <summary>
/// Windows DPAPI, scoped to the current user. Values are base64 of the protected bytes, with no prefix: that is what
/// every build has written, and older viewers sharing %AppData%\BetterVNC\profiles.json read nothing else. A value
/// without a prefix therefore means DPAPI to every store (see <see cref="ISecretStore"/>).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore : ISecretStore
{
    public string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }
}
