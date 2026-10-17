using System.Security.Cryptography;
using System.Text;

namespace BetterVnc.Protocol.Security;

/// <summary>
/// Classic VNC authentication (security type 2): the server sends a 16-byte challenge and the
/// client DES-encrypts it with the password. VNC uses the password bytes with the bit order of
/// every byte reversed as the DES key, a quirk inherited from the original AT&amp;T implementation.
/// </summary>
public static class VncAuthentication
{
    public static byte[] EncryptChallenge(ReadOnlySpan<byte> challenge, string password)
    {
        if (challenge.Length != 16) throw new ArgumentException("Challenge must be 16 bytes", nameof(challenge));
        var key = new byte[8];
        var pw = Encoding.Latin1.GetBytes(password);
        for (int i = 0; i < 8 && i < pw.Length; i++)
            key[i] = ReverseBits(pw[i]);

        using var des = DES.Create();
        des.Mode = CipherMode.ECB;
        des.Padding = PaddingMode.None;
        des.Key = key;
        using var enc = des.CreateEncryptor();
        var output = new byte[16];
        enc.TransformBlock(challenge.ToArray(), 0, 16, output, 0);
        return output;
    }

    private static byte ReverseBits(byte b)
    {
        int r = 0;
        for (int i = 0; i < 8; i++)
        {
            r = (r << 1) | (b & 1);
            b >>= 1;
        }
        return (byte)r;
    }
}
