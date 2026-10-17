using System.Windows;
using BetterVnc.App.Models;
using BetterVnc.App.Views;

namespace BetterVnc.App.Services;

/// <summary>
/// Registry of the session windows that are currently open, so the main window can show which
/// connections are live, bring an existing session to the front, cascade new windows and close
/// everything at once. All members must be used from the UI thread.
/// </summary>
public static class SessionManager
{
    private const double CascadeOffset = 30;

    private static readonly List<SessionWindow> _windows = new();
    private static SessionWindow? _lastPlaced;

    /// <summary>Open session windows in the order they were opened.</summary>
    public static IReadOnlyList<SessionWindow> Windows => _windows;

    public static int Count => _windows.Count;

    /// <summary>Raised whenever a session opens, closes, connects or disconnects.</summary>
    public static event Action? Changed;

    public static void Register(SessionWindow window)
    {
        if (_windows.Contains(window)) return;
        _windows.Add(window);
        Refresh(window.Profile);
    }

    public static void Unregister(SessionWindow window)
    {
        if (!_windows.Remove(window)) return;
        if (_lastPlaced == window) _lastPlaced = _windows.LastOrDefault();
        Refresh(window.Profile);
    }

    /// <summary>Call when a window's connection state changes so the badge in the main window follows.</summary>
    public static void NotifyStateChanged(SessionWindow window)
    {
        if (_windows.Contains(window)) Refresh(window.Profile);
    }

    private static void Refresh(ConnectionProfile profile)
    {
        profile.OpenSessions = _windows.Count(w => w.Profile == profile);
        profile.ConnectedSessions = _windows.Count(w => w.Profile == profile && w.IsConnected);
        Changed?.Invoke();
    }

    /// <summary>The most recently opened window for a profile, or null when none is open.</summary>
    public static SessionWindow? Find(ConnectionProfile profile) =>
        _windows.LastOrDefault(w => w.Profile.Id == profile.Id);

    /// <summary>Closes every open session window.</summary>
    public static void CloseAll()
    {
        foreach (var w in _windows.ToArray()) w.Close();
    }

    /// <summary>
    /// Picks a start position for a window about to be shown: centred in the work area for the first
    /// session, then offset diagonally from the last opened one so windows do not stack exactly on top
    /// of each other. The window's own size (its XAML default; FitWindowToRemote adjusts it later) is used
    /// to keep the window inside the work area.
    /// </summary>
    public static void PlaceNewWindow(SessionWindow window)
    {
        var work = SystemParameters.WorkArea;
        double width = double.IsNaN(window.Width) ? 0 : window.Width;
        double height = double.IsNaN(window.Height) ? 0 : window.Height;

        double left, top;
        var last = _lastPlaced != null && _windows.Contains(_lastPlaced) ? _lastPlaced : _windows.LastOrDefault();
        Rect? anchor = last == null ? null
            : last.WindowState == WindowState.Normal ? new Rect(last.Left, last.Top, last.ActualWidth, last.ActualHeight)
            : last.RestoreBounds.IsEmpty ? null : last.RestoreBounds;

        if (anchor is { } a && !double.IsNaN(a.Left) && !double.IsNaN(a.Top))
        {
            left = a.Left + CascadeOffset;
            top = a.Top + CascadeOffset;
            // Wrap back to the top-left corner once the cascade would run off the work area.
            if (left + width > work.Right || top + height > work.Bottom)
            {
                left = work.Left + CascadeOffset;
                top = work.Top + CascadeOffset;
            }
        }
        else
        {
            left = work.Left + (work.Width - width) / 2;
            top = work.Top + (work.Height - height) / 2;
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = Math.Max(work.Left, Math.Min(left, work.Right - width));
        window.Top = Math.Max(work.Top, Math.Min(top, work.Bottom - height));
        _lastPlaced = window;
    }
}
