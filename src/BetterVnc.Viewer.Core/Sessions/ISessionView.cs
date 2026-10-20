using BetterVnc.Protocol;
using BetterVnc.Viewer.Core.Services;

namespace BetterVnc.Viewer.Core.Sessions;

/// <summary>
/// The session window as <see cref="SessionController"/> drives it: the controls it shows state in and the dialogs it
/// asks. Everything is called on the UI thread.
/// </summary>
public interface ISessionView
{
    /// <summary>The picture surface, which the stream player draws into.</summary>
    IVideoSurface Video { get; }

    /// <summary>Frames a second the picture surface currently shows, for the status bar.</summary>
    double FramesPerSecond { get; }

    /// <summary>Covers the picture with a message (connecting, failed, disconnected); with reconnect and close buttons.</summary>
    void ShowOverlay(string title, string detail, bool showButtons);
    void HideOverlay();

    /// <summary>The status bar's message (left).</summary>
    void SetStatus(string text);
    /// <summary>The status bar's statistics (right): size, frame rate, picture, traffic, features.</summary>
    void SetStatistics(string text);
    void SetTitle(string text);

    /// <summary>Shows <paramref name="client"/>'s framebuffer, takes its input, and gives the picture keyboard focus.</summary>
    void AttachSession(VncClient client);
    /// <summary>
    /// A session without VNC: the picture comes only from the agent's stream, and pointer and keys go to
    /// <paramref name="input"/>. Black until <see cref="SetAgentPictureSize"/> or the first frame.
    /// </summary>
    void AttachAgent(IAgentInput input);
    /// <summary>A session without VNC: the stream's picture size, known at its start.</summary>
    void SetAgentPictureSize(int width, int height);
    /// <summary>Gives the picture keyboard focus.</summary>
    void FocusPicture();
    /// <summary>Stops showing and feeding the previous connection.</summary>
    void DetachSession();
    /// <summary>Sizes the window so a remote screen of this size shows 1:1 when it fits.</summary>
    void FitToRemote(int width, int height);
    /// <summary>Who scrolls by pixels (the guest agent); null for VNC wheel clicks only.</summary>
    void SetScrollInjector(IScrollInjector? injector);
    /// <summary>The server turned out to be a Mac: map the keyboard for macOS.</summary>
    void UseMacKeyboard();
    /// <summary>Releases every key the remote thinks is held.</summary>
    void ReleaseKeys();

    /// <summary>Enables each picture mode the connection can deliver (ask <see cref="SessionController.WhyUnavailable"/>).</summary>
    void RefreshPictureMenu();
    /// <summary>Enables the audio toggle when the server can send audio.</summary>
    void SetAudioAvailable(bool available);
    void Beep();

    /// <summary>Asks for credentials (modal), suggesting <paramref name="username"/>; null when cancelled.</summary>
    /// <param name="remember">True when the person asked to save them in the profile.</param>
    VncCredentials? PromptCredentials(CredentialKind kind, string prompt, string? username, out bool remember);

    /// <summary>A message box: a warning, or information when <paramref name="warning"/> is false.</summary>
    void ShowMessage(string title, string text, bool warning);
}
