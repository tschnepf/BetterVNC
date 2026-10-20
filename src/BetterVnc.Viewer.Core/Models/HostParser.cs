using System.Text.RegularExpressions;

namespace BetterVnc.Viewer.Core.Models;

/// <summary>
/// Normalises whatever a person types into a host field: strips URL schemes (including typos such
/// as "vvc://"), paths, whitespace and brackets, and splits off a ":port" suffix when present.
/// </summary>
public static class HostParser
{
    private static readonly Regex Scheme = new(@"^[A-Za-z][A-Za-z0-9+.\-]*://", RegexOptions.Compiled);

    public static (string Host, int? Port) Parse(string? input)
    {
        var s = (input ?? "").Trim();
        s = Scheme.Replace(s, "");
        int slash = s.IndexOf('/');
        if (slash >= 0) s = s[..slash];
        int at = s.LastIndexOf('@');
        if (at >= 0) s = s[(at + 1)..];

        int? port = null;
        if (s.StartsWith('['))
        {
            // [ipv6]:port
            int close = s.IndexOf(']');
            if (close > 0)
            {
                var rest = s[(close + 1)..];
                s = s[1..close];
                if (rest.StartsWith(':') && int.TryParse(rest[1..], out var p6)) port = p6;
            }
        }
        else
        {
            int colon = s.LastIndexOf(':');
            if (colon > 0 && s.IndexOf(':') == colon && int.TryParse(s[(colon + 1)..], out var p))
            {
                port = p;
                s = s[..colon];
            }
        }
        return (s.Trim(), port is > 0 and <= 65535 ? port : null);
    }
}
