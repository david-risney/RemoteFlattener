using System;
using RemoteFlattener.Hotkeys;
using Xunit;

namespace RemoteFlattener.Tests.Hotkeys;

public class HotkeyManagerTests
{
    // Standard Windows virtual-key codes (from WinUser.h)
    private const int VK_TAB      = 0x09;
    private const int VK_LEFT     = 0x25;
    private const int VK_RIGHT    = 0x27;
    private const int VK_F1       = 0x70; // unrelated key
    private const int WM_KEYDOWN  = 0x0100;
    private const int WM_KEYUP    = 0x0101;

    // ── DecideAction: early-exit conditions ───────────────────────────────

    [Fact]
    public void DecideAction_NCodeNegative_ReturnsPassThrough()
    {
        var result = HotkeyManager.DecideAction(
            nCode: -1, isInjected: false, vkCode: VK_TAB,
            isDown: true, isUp: false, isWinDown: true, isCtrlDown: false);
        Assert.Equal(HotkeyAction.PassThrough, result);
    }

    [Fact]
    public void DecideAction_IsInjected_ReturnsPassThrough()
    {
        var result = HotkeyManager.DecideAction(
            nCode: 0, isInjected: true, vkCode: VK_TAB,
            isDown: true, isUp: false, isWinDown: true, isCtrlDown: false);
        Assert.Equal(HotkeyAction.PassThrough, result);
    }

    // ── DecideAction: Tab keyup while Win held ────────────────────────────

    [Fact]
    public void DecideAction_TabKeyupWinDown_ReturnsEatTabUp()
    {
        var result = HotkeyManager.DecideAction(
            nCode: 0, isInjected: false, vkCode: VK_TAB,
            isDown: false, isUp: true, isWinDown: true, isCtrlDown: false);
        Assert.Equal(HotkeyAction.EatTabUp, result);
    }

    [Fact]
    public void DecideAction_TabKeyupWinNotDown_ReturnsPassThrough()
    {
        var result = HotkeyManager.DecideAction(
            nCode: 0, isInjected: false, vkCode: VK_TAB,
            isDown: false, isUp: true, isWinDown: false, isCtrlDown: false);
        Assert.Equal(HotkeyAction.PassThrough, result);
    }

    // ── DecideAction: Win+Tab → overlay ──────────────────────────────────

    [Fact]
    public void DecideAction_WinTabDown_ReturnsWinTab()
    {
        var result = HotkeyManager.DecideAction(
            nCode: 0, isInjected: false, vkCode: VK_TAB,
            isDown: true, isUp: false, isWinDown: true, isCtrlDown: false);
        Assert.Equal(HotkeyAction.WinTab, result);
    }

    [Fact]
    public void DecideAction_TabDownWinNotDown_ReturnsPassThrough()
    {
        var result = HotkeyManager.DecideAction(
            nCode: 0, isInjected: false, vkCode: VK_TAB,
            isDown: true, isUp: false, isWinDown: false, isCtrlDown: false);
        Assert.Equal(HotkeyAction.PassThrough, result);
    }

    // ── DecideAction: Ctrl+Win+Left ───────────────────────────────────────

    [Fact]
    public void DecideAction_CtrlWinLeft_ReturnsCtrlWinLeft()
    {
        var result = HotkeyManager.DecideAction(
            nCode: 0, isInjected: false, vkCode: VK_LEFT,
            isDown: true, isUp: false, isWinDown: true, isCtrlDown: true);
        Assert.Equal(HotkeyAction.CtrlWinLeft, result);
    }

    [Fact]
    public void DecideAction_WinLeftWithoutCtrl_ReturnsPassThrough()
    {
        // Win+Left (snap window) — Ctrl not held, so no edge check
        var result = HotkeyManager.DecideAction(
            nCode: 0, isInjected: false, vkCode: VK_LEFT,
            isDown: true, isUp: false, isWinDown: true, isCtrlDown: false);
        Assert.Equal(HotkeyAction.PassThrough, result);
    }

    // ── DecideAction: Ctrl+Win+Right ──────────────────────────────────────

    [Fact]
    public void DecideAction_CtrlWinRight_ReturnsCtrlWinRight()
    {
        var result = HotkeyManager.DecideAction(
            nCode: 0, isInjected: false, vkCode: VK_RIGHT,
            isDown: true, isUp: false, isWinDown: true, isCtrlDown: true);
        Assert.Equal(HotkeyAction.CtrlWinRight, result);
    }

    [Fact]
    public void DecideAction_WinRightWithoutCtrl_ReturnsPassThrough()
    {
        var result = HotkeyManager.DecideAction(
            nCode: 0, isInjected: false, vkCode: VK_RIGHT,
            isDown: true, isUp: false, isWinDown: true, isCtrlDown: false);
        Assert.Equal(HotkeyAction.PassThrough, result);
    }

    // ── DecideAction: unrelated keys ──────────────────────────────────────

    [Fact]
    public void DecideAction_UnrelatedKeyWithModifiers_ReturnsPassThrough()
    {
        var result = HotkeyManager.DecideAction(
            nCode: 0, isInjected: false, vkCode: VK_F1,
            isDown: true, isUp: false, isWinDown: true, isCtrlDown: true);
        Assert.Equal(HotkeyAction.PassThrough, result);
    }

    [Fact]
    public void DecideAction_NeitherDownNorUp_ReturnsPassThrough()
    {
        // Defensive: neither isDown nor isUp (shouldn't happen but shouldn't crash)
        var result = HotkeyManager.DecideAction(
            nCode: 0, isInjected: false, vkCode: VK_TAB,
            isDown: false, isUp: false, isWinDown: true, isCtrlDown: false);
        Assert.Equal(HotkeyAction.PassThrough, result);
    }

    // ── HandleEdgeCheck: desktop DID change ───────────────────────────────

    [Fact]
    public void HandleEdgeCheck_DesktopChanged_DoesNotFireAnyEvent()
    {
        var nm = new HotkeyManager();
        bool leftFired = false, rightFired = false;
        nm.SwitchDesktopLeft  += () => leftFired  = true;
        nm.SwitchDesktopRight += () => rightFired = true;

        nm.HandleEdgeCheck(left: true, before: Guid.NewGuid(), after: Guid.NewGuid());

        Assert.False(leftFired);
        Assert.False(rightFired);
    }

    // ── HandleEdgeCheck: desktop did NOT change (at edge) ─────────────────

    [Fact]
    public void HandleEdgeCheck_DesktopUnchanged_Left_FiresSwitchDesktopLeft()
    {
        var nm = new HotkeyManager();
        bool leftFired = false;
        nm.SwitchDesktopLeft += () => leftFired = true;

        var guid = Guid.NewGuid();
        nm.HandleEdgeCheck(left: true, before: guid, after: guid);

        Assert.True(leftFired);
    }

    [Fact]
    public void HandleEdgeCheck_DesktopUnchanged_Right_FiresSwitchDesktopRight()
    {
        var nm = new HotkeyManager();
        bool rightFired = false;
        nm.SwitchDesktopRight += () => rightFired = true;

        var guid = Guid.NewGuid();
        nm.HandleEdgeCheck(left: false, before: guid, after: guid);

        Assert.True(rightFired);
    }

    [Fact]
    public void HandleEdgeCheck_DesktopUnchanged_Left_DoesNotFireRight()
    {
        var nm = new HotkeyManager();
        bool rightFired = false;
        nm.SwitchDesktopRight += () => rightFired = true;

        var guid = Guid.NewGuid();
        nm.HandleEdgeCheck(left: true, before: guid, after: guid);

        Assert.False(rightFired);
    }

    [Fact]
    public void HandleEdgeCheck_DesktopUnchanged_Right_DoesNotFireLeft()
    {
        var nm = new HotkeyManager();
        bool leftFired = false;
        nm.SwitchDesktopLeft += () => leftFired = true;

        var guid = Guid.NewGuid();
        nm.HandleEdgeCheck(left: false, before: guid, after: guid);

        Assert.False(leftFired);
    }

    [Fact]
    public void HandleEdgeCheck_NoSubscribers_DoesNotThrow()
    {
        // Events with no subscribers should not throw.
        var nm   = new HotkeyManager();
        var guid = Guid.NewGuid();
        var ex   = Record.Exception(() => nm.HandleEdgeCheck(left: true, before: guid, after: guid));
        Assert.Null(ex);
    }
}
