using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static Capture.Core.NativeMethods;

namespace Capture.Core;

public sealed record MonitorInfo(IntPtr Handle, PixelRect Bounds, PixelRect WorkArea, bool IsPrimary);

/// <summary>모니터 목록과 마우스 위치</summary>
public static class Screens
{
    public static List<MonitorInfo> All()
    {
        var monitors = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr handle, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(handle, ref info))
                monitors.Add(new MonitorInfo(handle, ToPixelRect(info.rcMonitor), ToPixelRect(info.rcWork), (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            return true;
        }, IntPtr.Zero);
        return monitors;
    }

    public static (int X, int Y) Cursor() => GetCursorPos(out var point) ? (point.X, point.Y) : (0, 0);

    /// <summary>점이 있는 모니터 (없으면 가장 가까운)</summary>
    public static MonitorInfo FromPoint(int x, int y)
    {
        var all = All();
        return all.FirstOrDefault(m => m.Bounds.Contains(x, y))
            ?? all.OrderBy(m => Distance(m.Bounds, x, y)).First();
    }

    private static long Distance(PixelRect r, int x, int y)
    {
        long dx = x < r.X ? r.X - x : x >= r.Right ? x - r.Right + 1 : 0;
        long dy = y < r.Y ? r.Y - y : y >= r.Bottom ? y - r.Bottom + 1 : 0;
        return dx * dx + dy * dy;
    }

    internal static PixelRect ToPixelRect(RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
}

/// <summary>
/// 화면의 한 사각형을 읽음 (GDI BitBlt). 녹화처럼 같은 곳을 여러 번 읽을 때는 하나를 만들어 계속 씀.
/// 원격 데스크톱 창이 최소화되어 있으면 화면이 없어 실패함 (Win32Exception)
/// </summary>
public sealed class ScreenGrabber : IDisposable
{
    private readonly IntPtr _screen, _memory, _bitmap, _old, _bits;

    public ScreenGrabber(PixelRect rect)
    {
        if (rect.IsEmpty) throw new ArgumentException("빈 영역");
        Rect = rect;
        _screen = GetDC(IntPtr.Zero);
        _memory = CreateCompatibleDC(_screen);
        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = rect.Width,
            biHeight = -rect.Height,  // 위에서 아래로
            biPlanes = 1,
            biBitCount = 32
        };
        _bitmap = CreateDIBSection(_screen, ref header, 0, out _bits, IntPtr.Zero, 0);
        if (_bitmap == IntPtr.Zero || _bits == IntPtr.Zero)
        {
            Dispose();
            throw new Win32Exception("화면을 읽을 준비를 하지 못했습니다.");
        }
        _old = SelectObject(_memory, _bitmap);
    }

    public PixelRect Rect { get; }

    /// <summary>
    /// 지금 화면을 target(너비 × 높이 × 4)에 읽음. cursor 면 마우스 포인터도 그림.
    /// layered 면 CAPTUREBLT (투명 창 · 메뉴까지 확실히, 대신 일부 PC 에서 포인터가 깜박일 수 있어 녹화에서는 끔)
    /// </summary>
    public void Grab(byte[] target, bool cursor = false, bool layered = true)
    {
        if (!BitBlt(_memory, 0, 0, Rect.Width, Rect.Height, _screen, Rect.X, Rect.Y, SRCCOPY | (layered ? CAPTUREBLT : 0)))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "화면을 읽지 못했습니다. (원격 데스크톱 창이 최소화되어 있으면 읽을 수 없음)");
        if (cursor) DrawCursor();
        GdiFlush();
        Marshal.Copy(_bits, target, 0, Rect.Width * Rect.Height * 4);
    }

    public PixelFrame Grab(bool cursor = false, bool layered = true)
    {
        var frame = new PixelFrame(Rect.Width, Rect.Height);
        Grab(frame.Pixels, cursor, layered);
        return frame;
    }

    public static PixelFrame GrabOnce(PixelRect rect)
    {
        using var grabber = new ScreenGrabber(rect);
        return grabber.Grab();
    }

    private void DrawCursor()
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info) || (info.flags & CURSOR_SHOWING) == 0 || info.hCursor == IntPtr.Zero) return;
        int hotX = 0, hotY = 0;
        if (GetIconInfo(info.hCursor, out var icon))
        {
            hotX = icon.xHotspot;
            hotY = icon.yHotspot;
            if (icon.hbmMask != IntPtr.Zero) DeleteObject(icon.hbmMask);
            if (icon.hbmColor != IntPtr.Zero) DeleteObject(icon.hbmColor);
        }
        int x = info.ptScreenPos.X - Rect.X - hotX, y = info.ptScreenPos.Y - Rect.Y - hotY;
        if (x > Rect.Width || y > Rect.Height || x < -64 || y < -64) return;
        DrawIconEx(_memory, x, y, info.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
    }

    public void Dispose()
    {
        if (_memory != IntPtr.Zero)
        {
            if (_old != IntPtr.Zero) SelectObject(_memory, _old);
            DeleteDC(_memory);
        }
        if (_bitmap != IntPtr.Zero) DeleteObject(_bitmap);
        if (_screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, _screen);
    }
}

/// <summary>화면에 보이는 창들의 테두리 (위에 있는 창부터). 창을 눌러 고를 때 씀</summary>
public static class WindowFinder
{
    private static readonly HashSet<string> SkippedClasses = ["Progman", "WorkerW"];

    public static List<PixelRect> VisibleWindows()
    {
        uint self = (uint)Environment.ProcessId;
        var result = new List<PixelRect>();
        var name = new char[64];
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || IsIconic(window)) return true;
            GetWindowThreadProcessId(window, out uint process);
            if (process == self) return true;
            long style = GetWindowLongPtr(window, GWL_EXSTYLE).ToInt64();
            if ((style & WS_EX_TRANSPARENT) != 0) return true;  // 클릭이 지나가는 창 (화면 위 효과 등)
            if (DwmGetWindowAttribute(window, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            int length = GetClassName(window, name, name.Length);
            if (length > 0 && SkippedClasses.Contains(new string(name, 0, length))) return true;
            if (DwmGetWindowAttribute(window, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT bounds, Marshal.SizeOf<RECT>()) != 0
                && !GetWindowRect(window, out bounds)) return true;
            var rect = Screens.ToPixelRect(bounds);
            if (rect.Width >= 24 && rect.Height >= 24) result.Add(rect);
            return true;
        }, IntPtr.Zero);
        return result;
    }
}

/// <summary>마우스 휠 보내기 (스크롤 캡처)</summary>
public static class Mouse
{
    public static void Wheel(int notches)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            mi = new MOUSEINPUT { mouseData = unchecked((uint)(-120 * notches)), dwFlags = MOUSEEVENTF_WHEEL }
        };
        if (SendInput(1, [input], Marshal.SizeOf<INPUT>()) != 1)
            Debug.WriteLine("SendInput failed: " + Marshal.GetLastWin32Error());
    }

    public static void MoveTo(int x, int y) => SetCursorPos(x, y);
}
