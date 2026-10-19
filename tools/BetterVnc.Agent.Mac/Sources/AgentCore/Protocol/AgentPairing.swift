import CryptoKit
import Foundation

/// Click-to-approve pairing (AgentPairing.cs). The viewer sends an ephemeral ECDH P-256 public key (base64
/// SubjectPublicKeyInfo), the agent answers with its own after the person at the screen clicked Allow, and both derive
/// SHA-256(shared secret ‖ "bettervnc-agent-pairing-v1"), which .NET's DeriveKeyFromHash computes. That key, as base64,
/// is the viewer's token from then on.
public enum AgentPairing {
    static let label = Data("bettervnc-agent-pairing-v1".utf8)

    public static func newKeyPair() -> P256.KeyAgreement.PrivateKey { P256.KeyAgreement.PrivateKey() }

    public static func exportPublicKey(_ key: P256.KeyAgreement.PrivateKey) -> String {
        key.publicKey.derRepresentation.base64EncodedString()
    }

    public static func deriveKey(_ mine: P256.KeyAgreement.PrivateKey, peerPublicKey: String) throws -> String {
        guard let spki = Data(base64Encoded: peerPublicKey) else {
            throw AgentProtocolError("The pairing public key is not valid base64.")
        }
        let peer: P256.KeyAgreement.PublicKey
        do { peer = try P256.KeyAgreement.PublicKey(derRepresentation: spki) }
        catch { throw AgentProtocolError("The pairing public key is not a valid P-256 key.") }
        let shared = try mine.sharedSecretFromKeyAgreement(with: peer)
        var hash = SHA256()
        shared.withUnsafeBytes { hash.update(bufferPointer: $0) }
        hash.update(data: label)
        return Data(hash.finalize()).base64EncodedString()
    }
}
