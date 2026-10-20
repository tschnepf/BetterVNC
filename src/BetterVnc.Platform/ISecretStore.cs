namespace BetterVnc.Platform;

/// <summary>
/// Protects the secrets saved in the viewer's profiles (VNC, SSH and SMB passwords, agent tokens and pair keys) with the
/// OS's per-user secret store: DPAPI on Windows; the Keychain and the Secret Service are to follow. Each store marks the
/// values it writes with its own prefix ("dpapi:", "keychain:", "secret-service:"), so a profile file copied to another
/// OS can be told apart from one with no password.
/// </summary>
public interface ISecretStore
{
    /// <summary>The protected form of <paramref name="plain"/>; null for null or an empty string.</summary>
    string? Protect(string? plain);

    /// <summary>The secret, or null when there is none or this store cannot read it (another user, another OS).</summary>
    string? Unprotect(string? protectedValue);
}
