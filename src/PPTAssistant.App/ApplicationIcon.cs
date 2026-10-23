using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;

namespace PPTAssistant.App;

internal static class ApplicationIcon
{
    private static readonly Lazy<BitmapSource> CachedWindowIcon = new(() =>
    {
        var icon = BitmapFrame.Create(new Uri("pack://application:,,,/PPTAssistant;component/Assets/PPTA.ico"));
        icon.Freeze();
        return icon;
    });
    private static readonly Lazy<BitmapSource> CachedBrandImage = new(() =>
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 96;
        image.UriSource = new Uri("pack://application:,,,/PPTAssistant;component/Assets/PPTA.png");
        image.EndInit();
        image.Freeze();
        return image;
    });

    public static BitmapSource WindowIcon => CachedWindowIcon.Value;

    /// <summary>侧栏品牌图，缓存 96 px 透明位图以适配高 DPI 显示，不改动源图构图。</summary>
    public static BitmapSource BrandImage => CachedBrandImage.Value;

    public static void SetWindowDefault()
    {
        var icon = WindowIcon;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window window && window.Icon is null)
                {
                    window.SetCurrentValue(Window.IconProperty, icon);
                }
            }));
    }

    public static IconHandle CreateTrayIcon()
    {
        var icons = new IntPtr[1];
        var count = ExtractIconEx(typeof(ApplicationIcon).Assembly.Location, 0, null, icons, 1);
        if (count > 0 && icons[0] != IntPtr.Zero)
        {
            return new IconHandle(icons[0], ownsHandle: true);
        }

        AppLog.Warn("无法加载应用托盘图标，使用系统图标");
        return new IconHandle(LoadIcon(IntPtr.Zero, new IntPtr(32512)), ownsHandle: false);
    }

    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, [Out] IntPtr[]? largeIcons, [Out] IntPtr[]? smallIcons, uint count);

    [DllImport("user32.dll", EntryPoint = "LoadIconW")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);
}

internal sealed class IconHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal IconHandle(IntPtr handle, bool ownsHandle) : base(ownsHandle)
        => SetHandle(handle);

    protected override bool ReleaseHandle() => DestroyIcon(handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
