namespace PPTAssistant.Overlay;

public static class InputRouting
{

    public static bool ShouldCapture(
        InputMode mode,
        InputDeviceKind device,
        bool penDeviceSeen,
        bool ignoreTouchWhenPenPresent,
        bool allowMouseDrawing,
        bool penAlwaysOurs = false)
    {

        if (device == InputDeviceKind.Pen && penAlwaysOurs)
        {
            return true;
        }

        if (mode == InputMode.Erase)
        {
            return device switch
            {
                InputDeviceKind.Pen => true,
                InputDeviceKind.Touch => !(ignoreTouchWhenPenPresent && penDeviceSeen),
                InputDeviceKind.Mouse => allowMouseDrawing,
                _ => false,
            };
        }

        if (mode != InputMode.Pen)
        {
            return false;
        }

        return device switch
        {
            InputDeviceKind.Pen => true,

            InputDeviceKind.Touch => !(ignoreTouchWhenPenPresent && penDeviceSeen),

            // 鼠标：教室场景里由笔/手指主导，但鼠标必须能画（无触摸屏的机器、以及老师用讲台鼠标时）。
            InputDeviceKind.Mouse => allowMouseDrawing,

            _ => false,
        };
    }

    public static bool WantsPenChannelRedirect(InputMode mode, bool alwaysRoutePenToInk)
        => alwaysRoutePenToInk && mode == InputMode.Mouse;

    /// <summary>
    /// 这个鼠标事件是不是「真鼠标」发出的。
    /// </summary>
    /// <remarks>
    /// WPF 会把触摸/笔提升为鼠标事件；被提升的事件带 <c>StylusDevice</c>。
    /// 若不去重，同一笔会被 Stylus 与 Mouse 两条路径各画一遍（表现是笔迹重影、笔画计数翻倍）。
    /// </remarks>
    public static bool IsRealMouse(bool hasStylusDevice) => !hasStylusDevice;
}
