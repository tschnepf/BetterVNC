using System.Security.Cryptography;
using System.Text;

namespace BetterVnc.Agent.Protocol;

/// <summary>
/// Shared-token authentication. The token itself never crosses the wire: each side sends a random nonce and
/// the other answers with HMAC-SHA256(token, nonce), so a passive observer learns nothing reusable.
/// </summary>
public static class AgentAuth
{
    public static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string NewNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static string Prove(string token, string nonce)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(token.Trim()), Encoding.UTF8.GetBytes(nonce));
        return Convert.ToBase64String(mac);
    }

    public static bool Verify(string token, string nonce, string? proof)
    {
        if (string.IsNullOrEmpty(proof)) return false;
        byte[] expected = Convert.FromBase64String(Prove(token, nonce));
        byte[] actual;
        try { actual = Convert.FromBase64String(proof); }
        catch (FormatException) { return false; }
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
