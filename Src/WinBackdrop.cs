using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace VcsTextureCompare;

/// <summary>
/// Windows 11 深色圆角 + Acrylic 背景。失败时保持窗口原有不透明底，避免变全透明。
/// </summary>
internal static class WinBackdrop
{
    private const int DwmwaUseImmersiveDarkModeBefore20 = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmWcpRound = 2;
    /// <summary>Acrylic，比 Mica 更像毛玻璃。</summary>
    private const int DwmsbtTransientWindow = 3;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    public static void TryApplyAcrylic(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            if (hwnd == IntPtr.Zero)
                return;
            var dark = 1;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20, ref dark, sizeof(int));
            var corner = DwmWcpRound;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));
            var backdrop = DwmsbtTransientWindow;
            if (DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int)) != 0)
                return;
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            if (DwmExtendFrameIntoClientArea(hwnd, ref margins) != 0)
                return;
            window.Background = Brushes.Transparent;
            if (PresentationSource.FromVisual(window) is HwndSource source)
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
        }
        catch
        {
            // Win10 或策略关闭时保持 XAML 里的实色底
        }
    }
}
