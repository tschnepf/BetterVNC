using System.Buffers.Binary;
using System.Net;
using System.Text;
using BetterVnc.Agent.Protocol;
using BetterVnc.Protocol;
using BetterVnc.Protocol.Tests;
using BetterVnc.Viewer.Core.Models;
using BetterVnc.Viewer.Core.Services;
using BetterVnc.Viewer.Core.Sessions;
using Xunit;

namespace BetterVnc.Viewer.Core.Tests;

/// <summary>
/// The session controller against the fake RFB server and, where a test needs one, an in-process guest agent; the window
/// is a <see cref="FakeSessionView"/> and the profiles live in a scratch folder, never in %AppData%.
/// </summary>
public sealed class SessionControllerTests : IAsyncDisposable
{
    private const string AgentToken = "agent-secret";

    private readonly TestUiThread _ui = new();
    private readonly FakeSessionView _view = new();
    private readonly FakeLocalClipboard _clipboard = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bettervnc-tests", Guid.NewGuid().ToString("N"));
    private readonly ProfileStore _profiles;
    private readonly FakeRfbServer _server = new();
    private AgentServer? _agentServer;
    private SessionController? _controller;
    private int _connectionChanges;

    public SessionControllerTests() => _profiles = new ProfileStore(new TestSecretStore(), _dir);

    public async ValueTask DisposeAsync()
    {
        if (_controller is { } c) await _ui.RunAsync(() => { c.Dispose(); return true; });
        _server.Dispose();
        if (_agentServer != null) await _agentServer.DisposeAsync();
        _ui.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private ConnectionProfile Profile(PictureMode picture = PictureMode.VncLossless)
    {
        var profile = new ConnectionProfile { Name = "test", Host = "127.0.0.1", Port = _server.Port, PictureMode = picture };
        _profiles.Connections.Add(profile);
        return profile;
    }

    /// <summary>A guest agent on loopback with a clipboard but no screen stream, as an agent with streaming turned off.</summary>
    private ConnectionProfile WithAgentThatCannotStream(ConnectionProfile profile)
    {
        _agentServer = new AgentServer(new FakeLocalClipboard(), new AgentServerOptions { Token = AgentToken, ListenAddress = IPAddress.Loopback, Port = 0 });
        _agentServer.Start();
        profile.AgentEnabled = true;
        profile.AgentAuth = AgentAuthMode.Token;
        profile.AgentProtectedToken = _profiles.Protect(AgentToken);
        profile.AgentHost = "127.0.0.1";
        profile.AgentPort = _agentServer.Port;
        return profile;
    }

    /// <summary>A guest agent that streams and takes keyboard and mouse: what a session without VNC needs.</summary>
    private ConnectionProfile WithControllableAgent(ConnectionProfile profile, FakeGuestScreen screen)
    {
        _agentServer = new AgentServer(new FakeLocalClipboard(), new AgentServerOptions { Token = AgentToken, ListenAddress = IPAddress.Loopback, Port = 0 },
            new FakeGuestInput(), screen);
        _agentServer.Start();
        profile.AgentOnly = true;
        profile.AgentEnabled = true;
        profile.AgentAuth = AgentAuthMode.Token;
        profile.AgentProtectedToken = _profiles.Protect(AgentToken);
        profile.AgentHost = "127.0.0.1";
        profile.AgentPort = _agentServer.Port;
        return profile;
    }

    private async Task<SessionController> ConnectAsync(ConnectionProfile profile)
    {
        var controller = await _ui.RunAsync(() => new SessionController(profile, _view, new SessionServices
        {
            Profiles = _profiles,
            Ui = _ui,
            Clipboard = _clipboard,
            Audio = new FakeAudioSink(),
            CreateJpegDecoder = () => new FakeJpegDecoder(),
            JpegDecoderName = "fake JPEG",
        }));
        controller.ConnectionChanged += () => _connectionChanges++;
        _controller = controller;
        await _ui.RunAsync(() => controller.ConnectAsync());
        _ui.ThrowIfFailed();
        return controller;
    }

    private static string CutText(FakeRfbServer.ClientMsg msg)
    {
        int length = BinaryPrimitives.ReadInt32BigEndian(msg.Payload);
        return Encoding.Latin1.GetString(msg.Payload, 4, length);
    }

    // ------------------------------------------------------------------ connection

    [Fact]
    public async Task Connecting_shows_the_remote_desktop_and_saves_the_profile_in_its_own_store()
    {
        var c = await ConnectAsync(Profile());

        Assert.True(c.IsConnected);
        Assert.Equal(1, _connectionChanges);
        Assert.Same(c.Client, _view.Attached);
        Assert.False(_view.OverlayVisible);
        Assert.Equal("test  ·  fake desktop", _view.Title);
        Assert.Contains(_view.StatusLog, s => s.StartsWith("Connected  ·  "));
        Assert.Contains("64×48", _view.Statistics);
        Assert.NotNull(c.Profile.LastConnected);
        Assert.True(File.Exists(Path.Combine(_dir, "profiles.json")));
        Assert.True(_ui.Timer(TimeSpan.FromSeconds(1)).IsEnabled); // the status bar's statistics
    }

    [Fact]
    public async Task A_refused_connection_shows_the_error_with_reconnect()
    {
        var profile = Profile();
        profile.Port = _server.Port;
        _server.Dispose(); // nothing listens there any more

        var c = await ConnectAsync(profile);

        Assert.False(c.IsConnected);
        Assert.Null(c.Client);
        Assert.Equal("Could not connect", _view.OverlayTitle);
        Assert.True(_view.OverlayVisible && _view.OverlayButtons);
        Assert.Equal(0, _connectionChanges);
    }

    [Fact]
    public async Task A_rejected_password_asks_again_and_cancelling_leaves_the_overlay()
    {
        _server.SecurityTypes = new[] { SecurityType.VncAuth };
        _server.VncPassword = "right";
        var profile = Profile();
        profile.ProtectedPassword = _profiles.Protect("wrong");

        var c = await ConnectAsync(profile); // the prompt is answered with Cancel

        var prompt = Assert.Single(_view.Prompts);
        Assert.StartsWith("The server rejected the password", prompt);
        Assert.Equal("Authentication failed", _view.OverlayTitle);
        Assert.True(_view.OverlayVisible && _view.OverlayButtons);
        Assert.False(c.IsConnected);
        Assert.Equal("wrong", _profiles.Unprotect(profile.ProtectedPassword));
    }

    [Fact]
    public async Task A_password_entered_at_the_prompt_is_remembered_when_asked()
    {
        _server.SecurityTypes = new[] { SecurityType.VncAuth };
        _server.VncPassword = "right";
        var profile = Profile();
        _view.PromptAnswers.Enqueue((new VncCredentials(null, "right"), true));

        var c = await ConnectAsync(profile);

        Assert.True(c.IsConnected);
        Assert.Equal("VNC password required", Assert.Single(_view.Prompts));
        Assert.Equal("right", _profiles.Unprotect(profile.ProtectedPassword));
        Assert.Contains(profile.ProtectedPassword!, File.ReadAllText(Path.Combine(_dir, "profiles.json")));
    }

    [Fact]
    public async Task Losing_the_connection_shows_it_on_the_overlay()
    {
        var c = await ConnectAsync(Profile());

        _server.Dispose();
        await _ui.UntilAsync(() => !c.IsConnected, "the disconnect");

        Assert.Equal(2, _connectionChanges);
        Assert.True(_view.OverlayVisible && _view.OverlayButtons);
        Assert.Contains(_view.OverlayTitle, new[] { "Connection lost", "Disconnected" });
        Assert.False(_ui.Timer(TimeSpan.FromSeconds(1)).IsEnabled);
    }

    // ------------------------------------------------------------------ picture modes

    [Fact]
    public async Task A_stream_mode_shows_lossless_VNC_while_the_agent_cannot_stream()
    {
        var stream = PictureModes.Get(PictureMode.StreamBalanced);
        var c = await ConnectAsync(WithAgentThatCannotStream(Profile(PictureMode.StreamBalanced)));
        await _ui.UntilAsync(() => c.Agent?.IsConnected == true, "the guest agent to connect");

        // The stream mode is wanted, but the agent has no video: the picture stays on lossless VNC, and the menu says why.
        Assert.Equal(-1, c.Client!.Options.JpegQuality);
        Assert.False(c.Player!.IsStreaming);
        Assert.Contains("does not stream its screen", await _ui.RunAsync(() => c.WhyUnavailable(stream)));
        Assert.Null(await _ui.RunAsync(() => c.WhyUnavailable(PictureModes.Get(PictureMode.VncMedium))));

        // A lossy VNC mode is honoured; going back to the stream mode falls back to lossless again, not to the last VNC mode.
        await _ui.RunAsync(() => { c.SelectPicture(PictureModes.Get(PictureMode.VncMedium)); return true; });
        Assert.Equal(6, c.Client.Options.JpegQuality);
        await _ui.RunAsync(() => { c.SelectPicture(stream); return true; });
        await _ui.UntilAsync(() => c.Client.Options.JpegQuality == -1, "lossless VNC");
        // The status line says why (the connection's own "Picture: lossless" log line may follow it).
        var why = await _ui.RunAsync(() => _view.StatusLog.LastOrDefault(s => s.StartsWith("Showing VNC lossless until the guest agent can stream: ")));
        Assert.Contains("does not stream its screen", why);
        Assert.False(c.Player.IsStreaming);

        await _ui.RunAsync(() => { c.UpdateStatus(); return true; });
        Assert.Contains("lossless  (waiting for agent stream)", _view.Statistics);
        Assert.Contains("agent", _view.Statistics);
    }

    [Fact]
    public async Task A_stream_mode_without_an_agent_says_why_and_stays_lossless()
    {
        var c = await ConnectAsync(Profile(PictureMode.StreamQuality));

        Assert.Null(c.Agent);
        Assert.Equal(-1, c.Client!.Options.JpegQuality);
        Assert.Contains("Stream mode needs the guest agent; showing VNC lossless because this connection has none configured", _view.StatusLog);
        Assert.StartsWith("This connection has no guest agent", await _ui.RunAsync(() => c.WhyUnavailable(PictureModes.Get(PictureMode.StreamQuality))));
        Assert.True(await _ui.RunAsync(() => c.CanMakePictureDefault) == false);
    }

    [Fact]
    public async Task A_VNC_mode_starts_the_connection_with_its_Tight_quality()
    {
        var c = await ConnectAsync(Profile(PictureMode.VncLow));

        Assert.Equal(3, c.Client!.Options.JpegQuality);
        Assert.Equal(6, c.Client.Options.CompressionLevel);
        await _ui.RunAsync(() => { c.UpdateStatus(); return true; });
        Assert.Contains("JPEG 3 requested (none received yet)", _view.Statistics);
    }

    [Fact]
    public async Task Making_the_picture_the_default_saves_it_in_the_profile()
    {
        var profile = Profile(PictureMode.VncLossless);
        var c = await ConnectAsync(profile);

        await _ui.RunAsync(() => { c.SelectPicture(PictureModes.Get(PictureMode.VncHigh)); return true; });
        Assert.True(await _ui.RunAsync(() => c.CanMakePictureDefault));
        await _ui.RunAsync(() => { c.MakePictureDefault(); return true; });

        Assert.Equal(PictureMode.VncHigh, profile.PictureMode);
        Assert.Equal(9, profile.JpegQuality);
        Assert.Contains("\"PictureMode\": \"VncHigh\"", File.ReadAllText(Path.Combine(_dir, "profiles.json")));
        Assert.False(await _ui.RunAsync(() => c.CanMakePictureDefault));
    }

    // ------------------------------------------------------------------ clipboard

    [Fact]
    public async Task Text_copied_in_the_remote_lands_on_the_local_clipboard_while_sync_is_on()
    {
        var c = await ConnectAsync(Profile());

        _server.SendServerCutText("from the remote");
        await _ui.UntilAsync(() => _clipboard.Text == "from the remote", "the remote text on the local clipboard");
        Assert.Contains("Clipboard received from remote", _view.StatusLog);

        await _ui.RunAsync(() => { c.SyncClipboard = false; return true; });
        _server.SendServerCutText("not this");
        await Task.Delay(200);
        await _ui.RunAsync(() => true);
        Assert.Equal("from the remote", _clipboard.Text);
    }

    [Fact]
    public async Task Text_copied_locally_goes_to_the_remote_but_remote_text_is_not_echoed_back()
    {
        await ConnectAsync(Profile());

        _clipboard.CopyText("copied here");
        Assert.Equal("copied here", CutText(_server.WaitFor(ClientMessage.ClientCutText)));

        // Text from the remote changes the local clipboard too; it must not go straight back.
        _server.SendServerCutText("from the remote");
        await _ui.UntilAsync(() => _clipboard.Text == "from the remote", "the remote text on the local clipboard");
        await Task.Delay(200);
        Assert.Throws<TimeoutException>(() => _server.WaitFor(ClientMessage.ClientCutText, timeoutMs: 300));
    }

    [Fact]
    public async Task Paste_sends_the_local_text_even_when_it_was_sent_before()
    {
        var c = await ConnectAsync(Profile());
        _clipboard.CopyText("again");
        Assert.Equal("again", CutText(_server.WaitFor(ClientMessage.ClientCutText)));

        await _ui.RunAsync(() => c.PasteToRemoteAsync());

        Assert.Equal("again", CutText(_server.WaitFor(ClientMessage.ClientCutText)));
        Assert.Contains("Clipboard text sent to remote", _view.StatusLog);
    }

    // ------------------------------------------------------------------ sessions without VNC

    [Fact]
    public async Task A_session_without_VNC_streams_through_the_agent_and_restarts_a_stream_that_ended()
    {
        var screen = new FakeGuestScreen();
        var profile = WithControllableAgent(Profile(PictureMode.VncLossless), screen);
        var c = await ConnectAsync(profile);

        // A VNC mode in the profile means the best stream; the agent's stream is the picture, with its pointer.
        Assert.Equal(PictureMode.StreamQuality, c.WantedPicture.Mode);
        await _ui.UntilAsync(() => c.IsConnected, "the agent's stream");
        Assert.Null(c.Client);
        Assert.Same(c.Agent, _view.AgentInput);
        Assert.Equal((1280, 720), _view.AgentPictureSize);
        Assert.False(_view.OverlayVisible);
        Assert.Equal("test  ·  fake through the guest agent", _view.Title);
        Assert.Equal(1, _connectionChanges);
        Assert.True(screen.CursorRequested);
        Assert.Equal("This connection has no VNC server: the picture is the guest agent's stream.",
            await _ui.RunAsync(() => c.WhyUnavailable(PictureModes.Get(PictureMode.VncLossless))));

        // The pointer's shape arrives straight from the receive thread.
        screen.SendCursor(new GuestCursor(true, new CursorImage(2, 2, 1, 1, new byte[16])));
        await _ui.UntilAsync(() => _view.Surface.RemoteCursor != null, "the pointer shape");

        // The remote stops streaming (capture failed): no picture, then a retry that brings it back.
        screen.End("capture failed");
        await _ui.UntilAsync(() => !c.IsConnected, "the stream to end");
        Assert.Equal("No picture", _view.OverlayTitle);
        Assert.Contains("capture failed", _view.OverlayDetail);
        await _ui.Timer(TimeSpan.FromSeconds(3)).FireAsync();
        await _ui.UntilAsync(() => c.IsConnected, "the stream to start again");
        Assert.Equal(2, screen.Starts);
        Assert.Equal(3, _connectionChanges);
        _ui.ThrowIfFailed();
    }

    [Fact]
    public async Task A_session_without_VNC_says_why_an_agent_that_cannot_stream_cannot_be_controlled()
    {
        var profile = WithAgentThatCannotStream(Profile());
        profile.AgentOnly = true;
        var c = await ConnectAsync(profile);

        await _ui.UntilAsync(() => _view.OverlayTitle == "Cannot control this computer", "the overlay to say why");
        Assert.Contains("does not stream its screen", _view.OverlayDetail);
        Assert.True(_view.OverlayButtons);
        Assert.False(c.IsConnected);
        Assert.Equal(0, _connectionChanges);
    }

    [Fact]
    public async Task A_session_without_VNC_or_an_agent_cannot_connect()
    {
        var profile = Profile();
        profile.AgentOnly = true;

        var c = await ConnectAsync(profile);

        Assert.Equal("Cannot connect", _view.OverlayTitle);
        Assert.True(_view.OverlayVisible && _view.OverlayButtons);
        Assert.Null(c.Agent);
        Assert.Null(c.Client);
    }

    // ------------------------------------------------------------------ files

    [Fact]
    public async Task Sending_files_without_an_agent_or_drop_folder_explains_the_choices()
    {
        var c = await ConnectAsync(Profile());

        Assert.False(c.CanSendFiles);
        await _ui.RunAsync(() => c.UploadAsync(new[] { "C:\\nothing.txt" }));

        var (title, text) = Assert.Single(_view.Messages);
        Assert.Equal("Send files", title);
        Assert.Equal(SessionController.NoFileChannelMessage, text);
    }
}
