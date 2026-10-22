import CryptoKit
import Foundation
import Security

/// Shared-token authentication (AgentAuth.cs). The token never crosses the wire: each side sends a random nonce and the
/// other answers with HMAC-SHA256(token, nonce). The HMAC key is the token's UTF-8 text (trimmed) and the message is the
/// nonce's base64 text, not its decoded bytes. A paired viewer's key (base64) is used as its token the same way.
public enum AgentAuth {
    /// 24 random bytes, base64url without padding (32 characters).
    public static func newToken() -> String {
        randomBytes(24).base64EncodedString()
            .replacingOccurrences(of: "=", with: "")
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
    }

    public static func newNonce() -> String { randomBytes(32).base64EncodedString() }

    public static func prove(token: String, nonce: String) -> String {
        let key = SymmetricKey(data: Data(token.trimmingCharacters(in: .whitespacesAndNewlines).utf8))
        let mac = HMAC<SHA256>.authenticationCode(for: Data(nonce.utf8), using: key)
        return Data(mac).base64EncodedString()
    }

    public static func verify(token: String, nonce: String, proof: String?) -> Bool {
        guard let proof, !proof.isEmpty, let actual = Data(base64Encoded: proof),
              let expected = Data(base64Encoded: prove(token: token, nonce: nonce)) else { return false }
        guard actual.count == expected.count else { return false }
        var difference: UInt8 = 0
        for (a, b) in zip(actual, expected) { difference |= a ^ b }
        return difference == 0
    }

    static func randomBytes(_ count: Int) -> Data {
        var bytes = [UInt8](repeating: 0, count: count)
        if SecRandomCopyBytes(kSecRandomDefault, count, &bytes) != errSecSuccess {
            var generator = SystemRandomNumberGenerator()
            for i in bytes.indices { bytes[i] = generator.next() }
        }
        return Data(bytes)
    }
}
