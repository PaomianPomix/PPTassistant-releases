using PPTAssistant.Overlay;

namespace PPTAssistant.Tests;

public class InputRoutingTests
{
    [Fact]
    public void MouseModeNeverDraws()
    {

        foreach (var device in Enum.GetValues<InputDeviceKind>())
        {
            Assert.False(InputRouting.ShouldCapture(InputMode.Mouse, device, false, true, true));
        }
    }

    [Fact]
    public void PenAlwaysDrawsInPenMode()
        => Assert.True(InputRouting.ShouldCapture(InputMode.Pen, InputDeviceKind.Pen, false, true, true));

    [Fact]
    public void TouchDrawsWhenNoPenDevicePresent()
        => Assert.True(InputRouting.ShouldCapture(InputMode.Pen, InputDeviceKind.Touch, penDeviceSeen: false, true, true));

    [Fact]
    public void TouchIsIgnoredOnceAPenDeviceIsPresent()
    {

        Assert.False(InputRouting.ShouldCapture(InputMode.Pen, InputDeviceKind.Touch, penDeviceSeen: true, true, true));

        // 老师显式关掉这个策略时，触摸恢复可用。
        Assert.True(InputRouting.ShouldCapture(InputMode.Pen, InputDeviceKind.Touch, penDeviceSeen: true, false, true));
    }

    [Fact]
    public void MouseDrawsInPenModeWhenAllowed()
        => Assert.True(InputRouting.ShouldCapture(InputMode.Pen, InputDeviceKind.Mouse, false, true, allowMouseDrawing: true));

    [Fact]
    public void MouseIsRejectedWhenMouseDrawingIsDisabled()
        => Assert.False(InputRouting.ShouldCapture(InputMode.Pen, InputDeviceKind.Mouse, false, true, allowMouseDrawing: false));

    [Fact]
    public void UnknownDeviceNeverDraws()
        => Assert.False(InputRouting.ShouldCapture(InputMode.Pen, InputDeviceKind.Unknown, false, true, true));

    /// <summary>去重规则：被触摸/笔提升来的鼠标事件必须被识别出来（否则同一笔会画两遍）。</summary>
    [Fact]
    public void PromotedMouseEventsAreNotRealMouse()
    {
        Assert.True(InputRouting.IsRealMouse(hasStylusDevice: false));
        Assert.False(InputRouting.IsRealMouse(hasStylusDevice: true));
    }

    [Fact]
    public void PenCapturedInMouseModeWhenPenAlwaysOurs()
        => Assert.True(InputRouting.ShouldCapture(InputMode.Mouse, InputDeviceKind.Pen, penDeviceSeen: false, ignoreTouchWhenPenPresent: true, allowMouseDrawing: true, penAlwaysOurs: true));

    [Fact]
    public void EscapeHatchReturnsPenToModeSemantics()
        => Assert.False(InputRouting.ShouldCapture(InputMode.Mouse, InputDeviceKind.Pen, penDeviceSeen: false, ignoreTouchWhenPenPresent: true, allowMouseDrawing: true, penAlwaysOurs: false));

    [Fact]
    public void PenChannelExceptionIsOnlyForPen()
    {
        foreach (var device in new[] { InputDeviceKind.Touch, InputDeviceKind.Mouse, InputDeviceKind.Unknown })
        {
            Assert.False(InputRouting.ShouldCapture(InputMode.Mouse, device, penDeviceSeen: false, ignoreTouchWhenPenPresent: true, allowMouseDrawing: true, penAlwaysOurs: true));
        }
    }

    /// <summary>penAlwaysOurs 不得改变笔态的任何行为（笔态下笔本来就画）。</summary>
    [Fact]
    public void PenAlwaysOursDoesNotChangePenMode()
    {
        Assert.True(InputRouting.ShouldCapture(InputMode.Pen, InputDeviceKind.Pen, penDeviceSeen: false, ignoreTouchWhenPenPresent: true, allowMouseDrawing: true, penAlwaysOurs: true));
        Assert.True(InputRouting.ShouldCapture(InputMode.Pen, InputDeviceKind.Pen, penDeviceSeen: false, ignoreTouchWhenPenPresent: true, allowMouseDrawing: true, penAlwaysOurs: false));
    }

    [Fact]
    public void TouchIsStillIgnoredWhenPenIsPresentUnderD7R()
    {
        Assert.False(InputRouting.ShouldCapture(InputMode.Pen, InputDeviceKind.Touch, penDeviceSeen: true, ignoreTouchWhenPenPresent: true, allowMouseDrawing: true, penAlwaysOurs: true));
        Assert.False(InputRouting.ShouldCapture(InputMode.Mouse, InputDeviceKind.Touch, penDeviceSeen: true, ignoreTouchWhenPenPresent: true, allowMouseDrawing: true, penAlwaysOurs: true));
    }

    [Fact]
    public void PenChannelIsRegisteredOnlyInMouseModeAndNeverInPenMode()
    {
        Assert.True(InputRouting.WantsPenChannelRedirect(InputMode.Mouse, alwaysRoutePenToInk: true));

        // 关键不变量：笔态绝不注册（否则工具栏点不到）。
        Assert.False(InputRouting.WantsPenChannelRedirect(InputMode.Pen, alwaysRoutePenToInk: true));

        // 逃生门打开：不注册。
        Assert.False(InputRouting.WantsPenChannelRedirect(InputMode.Mouse, alwaysRoutePenToInk: false));
        Assert.False(InputRouting.WantsPenChannelRedirect(InputMode.Pen, alwaysRoutePenToInk: false));
    }

    [Fact]
    public void EraserModeCapturesPenTouchAndMouse()
    {
        Assert.True(InputRouting.ShouldCapture(InputMode.Erase, InputDeviceKind.Pen, false, true, true));
        Assert.True(InputRouting.ShouldCapture(InputMode.Erase, InputDeviceKind.Touch, false, true, true));
        Assert.True(InputRouting.ShouldCapture(InputMode.Erase, InputDeviceKind.Mouse, false, true, true));
    }

    [Fact]
    public void EraserModeHonoursIgnoreTouchWhenPenIsPresent()
    {
        Assert.False(InputRouting.ShouldCapture(InputMode.Erase, InputDeviceKind.Touch, penDeviceSeen: true, true, true));
        Assert.True(InputRouting.ShouldCapture(InputMode.Erase, InputDeviceKind.Touch, penDeviceSeen: true, false, true));
    }

    /// <summary>鼠标绘制开关同样约束橡皮态（无触摸屏机器上关掉它 = 不能擦）。</summary>
    [Fact]
    public void EraserModeHonoursMouseDrawingSwitch()
    {
        Assert.True(InputRouting.ShouldCapture(InputMode.Erase, InputDeviceKind.Mouse, false, true, allowMouseDrawing: true));
        Assert.False(InputRouting.ShouldCapture(InputMode.Erase, InputDeviceKind.Mouse, false, true, allowMouseDrawing: false));
    }

    /// <summary>鼠标态仍不得因为枚举里新增了 Erase 就放行任何输入。</summary>
    [Fact]
    public void MouseModeStillNeverCaptures()
    {
        foreach (var device in Enum.GetValues<InputDeviceKind>())
        {
            Assert.False(InputRouting.ShouldCapture(InputMode.Mouse, device, false, true, true));
        }
    }

    [Fact]
    public void EraserModeNeverRegistersPenChannel()
    {
        Assert.False(InputRouting.WantsPenChannelRedirect(InputMode.Erase, alwaysRoutePenToInk: true));
        Assert.False(InputRouting.WantsPenChannelRedirect(InputMode.Erase, alwaysRoutePenToInk: false));
    }
}
