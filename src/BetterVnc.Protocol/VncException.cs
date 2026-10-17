namespace BetterVnc.Protocol;

public class VncException : Exception
{
    public VncException(string message) : base(message) { }
    public VncException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Raised when the server rejects the credentials; the viewer should prompt again.</summary>
public sealed class VncAuthenticationException : VncException
{
    public VncAuthenticationException(string message) : base(message) { }
}
