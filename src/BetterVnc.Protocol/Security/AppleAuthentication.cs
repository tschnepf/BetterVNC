using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace BetterVnc.Protocol.Security;

/// <summary>
/// Apple Remote Desktop authentication (security type 30), used by macOS Screen Sharing and
/// Remote Management. Diffie-Hellman key agreement, MD5 of the shared secret as an AES-128 key,
/// AES-ECB encryption of a 128-byte username/password block.
/// </summary>
public static class AppleAuthentication
{
    public sealed record ServerParameters(ushort Generator, ushort KeyLength, byte[] Prime, byte[] ServerPublicKey);

    public sealed record ClientResponse(byte[] EncryptedCredentials, byte[] ClientPublicKey);

    public static ServerParameters ReadServerParameters(RfbReader r)
    {
        var generator = r.ReadU16();
        var keyLength = r.ReadU16();
        if (keyLength == 0 || keyLength > 1024) throw new VncException($"Unexpected Apple DH key length {keyLength}");
        var prime = r.ReadBytes(keyLength);
        var serverPub = r.ReadBytes(keyLength);
        return new ServerParameters(generator, keyLength, prime, serverPub);
    }

    public static ClientResponse CreateResponse(ServerParameters p, string username, string password)
    {
        var privateKey = new byte[p.KeyLength];
        RandomNumberGenerator.Fill(privateKey);
        return CreateResponse(p, username, password, privateKey);
    }

    /// <summary>Deterministic variant used by tests; <paramref name="privateKey"/> is interpreted as an unsigned big-endian number.</summary>
    public static ClientResponse CreateResponse(ServerParameters p, string username, string password, byte[] privateKey)
    {
        var prime = new BigInteger(p.Prime, isUnsigned: true, isBigEndian: true);
        var serverPub = new BigInteger(p.ServerPublicKey, isUnsigned: true, isBigEndian: true);
        var priv = new BigInteger(privateKey, isUnsigned: true, isBigEndian: true) % prime;
        if (priv < 2) priv += 2;
        var generator = new BigInteger(p.Generator);

        var clientPub = BigInteger.ModPow(generator, priv, prime);
        var shared = BigInteger.ModPow(serverPub, priv, prime);

        var sharedBytes = ToFixedBigEndian(shared, p.KeyLength);
        var aesKey = MD5.HashData(sharedBytes);

        var block = new byte[128];
        RandomNumberGenerator.Fill(block);
        WriteField(block, 0, username);
        WriteField(block, 64, password);

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = aesKey;
        using var enc = aes.CreateEncryptor();
        var cipher = new byte[128];
        enc.TransformBlock(block, 0, 128, cipher, 0);

        return new ClientResponse(cipher, ToFixedBigEndian(clientPub, p.KeyLength));
    }

    /// <summary>Server-side helper (used by tests) to recover the credentials from a response.</summary>
    public static (string Username, string Password) DecryptCredentials(ServerParameters p, BigInteger serverPrivate, ClientResponse response)
    {
        var prime = new BigInteger(p.Prime, isUnsigned: true, isBigEndian: true);
        var clientPub = new BigInteger(response.ClientPublicKey, isUnsigned: true, isBigEndian: true);
        var shared = BigInteger.ModPow(clientPub, serverPrivate, prime);
        var aesKey = MD5.HashData(ToFixedBigEndian(shared, p.KeyLength));
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = aesKey;
        using var dec = aes.CreateDecryptor();
        var plain = new byte[128];
        dec.TransformBlock(response.EncryptedCredentials, 0, 128, plain, 0);
        return (ReadField(plain, 0), ReadField(plain, 64));
    }

    private static void WriteField(byte[] block, int offset, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        int n = Math.Min(bytes.Length, 63);
        Array.Copy(bytes, 0, block, offset, n);
        block[offset + n] = 0;
    }

    private static string ReadField(byte[] block, int offset)
    {
        int end = Array.IndexOf(block, (byte)0, offset, 64);
        if (end < 0) end = offset + 64;
        return Encoding.UTF8.GetString(block, offset, end - offset);
    }

    public static byte[] ToFixedBigEndian(BigInteger value, int length)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length == length) return bytes;
        var result = new byte[length];
        if (bytes.Length > length)
            Array.Copy(bytes, bytes.Length - length, result, 0, length);
        else
            Array.Copy(bytes, 0, result, length - bytes.Length, bytes.Length);
        return result;
    }
}
