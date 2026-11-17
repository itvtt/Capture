using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Capture.Core;
using static Capture.Core.NativeMethods;

namespace Capture;

/// <summary>창을 실제 화소 위치에 두기 · 캡처에 안 찍히게 · 눌러도 앞으로 오지 않게 · 둥근 모서리</summary>
internal static class WindowHelpers
{
    public static IntPtr Handle(Window window) => new WindowInteropHelper(window).EnsureHandle();

    /// <summary>화면 캡처(이 앱의 녹화 포함)에 나오지 않음. Windows 10 2004 이전이면 false</summary>
    public static bool ExcludeFromCapture(Window window) => SetWindowDisplayAffinity(Handle(window), WDA_EXCLUDEFROMCAPTURE);

    /// <summary>눌러도 활성 창이 되지 않음 (녹화 중인 프로그램의 포커스를 뺏지 않게). clickThrough 면 마우스도 지나감</summary>
    public static void NoActivate(Window window, bool clickThrough = false)
    {
        var handle = Handle(window);
        long style = GetWindowLongPtr(handle, GWL_EXSTYLE).ToInt64() | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        if (clickThrough) style |= WS_EX_TRANSPARENT | WS_EX_LAYERED;
        SetWindowLongPtr(handle, GWL_EXSTYLE, new IntPtr(style));
    }

    /// <summary>Windows 11 둥근 모서리 + 얇은 테두리 (안 되는 Windows 는 그대로)</summary>
    public static void RoundCorners(Window window)
    {
        var handle = Handle(window);
        int round = 2, border = 0x00423B3A, disabled = 1;
        DwmSetWindowAttribute(handle, 33, ref round, sizeof(int));
        DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
        DwmSetWindowAttribute(handle, DWMWA_TRANSITIONS_FORCEDISABLED, ref disabled, sizeof(int));
    }

    /// <summary>실제 화소 좌표로 옮김 (크기는 그대로)</summary>
    public static void MoveTo(Window window, int x, int y) =>
        SetWindowPos(Handle(window), HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);

    /// <summary>실제 화소 좌표 · 크기로</summary>
    public static void PlaceAt(Window window, PixelRect rect) =>
        SetWindowPos(Handle(window), HWND_TOPMOST, rect.X, rect.Y, rect.Width, rect.Height, SWP_NOACTIVATE);

    /// <summary>창의 지금 크기 (실제 화소)</summary>
    public static (int Width, int Height) PixelSize(Window window)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        return ((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY));
    }

    /// <summary>
    /// 막대를 영역 바로 아래(안 되면 위, 그래도 안 되면 영역 안 아래쪽)에 가운데 맞춰 둠.
    /// 모니터 배율이 달라도 맞도록 실제 화소로 계산
    /// </summary>
    public static void PlaceNear(Window bar, PixelRect region)
    {
        var monitor = Screens.FromPoint(region.X + region.Width / 2, region.Y + region.Height / 2);
        var work = monitor.WorkArea;
        // 먼저 그 모니터로 옮겨 배율을 맞춘 뒤 크기를 잼
        MoveTo(bar, work.X, work.Y);
        bar.UpdateLayout();
        var (w, h) = PixelSize(bar);
        int gap = 10;
        int x = Math.Clamp(region.X + (region.Width - w) / 2, work.X, Math.Max(work.X, work.Right - w));
        int y = region.Bottom + gap + h <= work.Bottom ? region.Bottom + gap
              : region.Y - gap - h >= work.Y ? region.Y - gap - h
              : region.Bottom - gap - h;
        MoveTo(bar, x, y);
    }
}
