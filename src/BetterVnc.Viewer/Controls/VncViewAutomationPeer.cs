using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;

namespace BetterVnc.Viewer.Controls;

/// <summary>
/// Puts the remote picture into the UI Automation tree (Avalonia exposes custom controls only through a peer): an Image
/// named "Remote screen" whose read-only value says what it shows, so UI tests can find it and check its state.
/// </summary>
internal sealed class VncViewAutomationPeer : ControlAutomationPeer, IValueProvider
{
    public VncViewAutomationPeer(VncView owner) : base(owner) { }

    private VncView View => (VncView)Owner;

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
    protected override string GetClassNameCore() => nameof(VncView);
    protected override string? GetNameCore() => base.GetNameCore() is { Length: > 0 } name ? name : "Remote screen";
    protected override bool IsContentElementCore() => true;
    protected override bool IsControlElementCore() => true;

    public bool IsReadOnly => true;
    public string? Value => View.Describe();
    public void SetValue(string? value) => throw new InvalidOperationException("The remote screen's description is read-only.");
}
