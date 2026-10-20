using BetterVnc.Protocol;
using Xunit;

namespace BetterVnc.Protocol.Tests;

public class XtScancodeTests
{
    [Theory]
    [InlineData(0x45, true, 0x45u)]    // NumLock: Windows sets the extended flag, XT has none
    [InlineData(0x45, false, 0xE046u)] // Pause: Windows reports a plain 0x45, QEMU wants E0 46
    [InlineData(0x46, true, 0xE046u)]  // Ctrl+Break
    [InlineData(0x46, false, 0x46u)]   // ScrollLock
    [InlineData(0x54, false, 0x54u)]   // SysRq (Alt+PrintScreen)
    [InlineData(0x37, true, 0xE037u)]  // PrintScreen
    [InlineData(0x1D, true, 0xE01Du)]  // right Ctrl
    [InlineData(0x47, false, 0x47u)]   // keypad 7
    [InlineData(0x1E, false, 0x1Eu)]   // A
    public void Windows_scancodes_map_to_xt(int scancode, bool extended, uint xt)
    {
        Assert.Equal(xt, XtScancode.FromWindows(scancode, extended));
    }
}
