using System.Security.Cryptography;

namespace BetterVnc.Agent.Protocol;

/// <summary>A viewer the agent has paired with. The key is the shared secret both sides derived during pairing.</summary>
public sealed class PairedViewer
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Base64 of the derived 32-byte key; used as the HMAC token for that viewer.</summary>
    public string Key { get; set; } = "";
    public DateTime PairedAt { get; set; } = DateTime.Now;
    public DateTime? LastSeen { get; set; }
    /// <summary>Address the viewer paired from (informational).</summary>
    public string? Address { get; set; }
}

/// <summary>Asks the person at the guest screen whether a viewer may pair. Implementations show a dialog.</summary>
public interface IPairingApprover
{
    /// <summary>Returns true to allow. Must return false (not throw) on timeout.</summary>
    Task<bool> ApproveAsync(string viewerName, string viewerId, string peerAddress, CancellationToken ct);
}

/// <summary>
/// Click-to-approve pairing. The viewer sends an ephemeral ECDH P-256 public key, the agent answers with its own after
/// the person at the guest screen clicked Allow, and both derive the same 32-byte key, which from then on serves as the
/// shared token for the nonce/HMAC handshake. The key never crosses the wire.
/// </summary>
public static class AgentPairing
{
    public static ECDiffieHellman NewKeyPair() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    public static string ExportPublicKey(ECDiffieHellman key) => Convert.ToBase64String(key.PublicKey.ExportSubjectPublicKeyInfo());

    /// <summary>Derives the shared key and returns it in the string form used as a token.</summary>
    public static string DeriveKey(ECDiffieHellman mine, string peerPublicKeyBase64)
    {
        byte[] spki;
        try { spki = Convert.FromBase64String(peerPublicKeyBase64); }
        catch (FormatException ex) { throw new AgentProtocolException("The pairing public key is not valid base64.", ex); }
        using var peer = ECDiffieHellman.Create();
        try { peer.ImportSubjectPublicKeyInfo(spki, out _); }
        catch (CryptographicException ex) { throw new AgentProtocolException("The pairing public key is not a valid P-256 key.", ex); }
        var secret = mine.DeriveKeyFromHash(peer.PublicKey, HashAlgorithmName.SHA256, null, "bettervnc-agent-pairing-v1"u8.ToArray());
        return Convert.ToBase64String(secret);
    }

    /// <summary>A new random viewer id (public, stable per viewer installation).</summary>
    public static string NewViewerId() => Guid.NewGuid().ToString("N");
}
