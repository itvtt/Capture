using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Capture.Core;
using CaptureMode = Capture.Core.CaptureMode;
using static Capture.Core.NativeMethods;

namespace Capture;

/// <summary>
/// 영역 고르기 (모니터마다 하나): 멈춰 둔 화면 위에서 끌면 영역, 누르기만 하면 그 창, Enter 는 모니터 전체, Esc 는 취소.
/// 위의 모드 막대로 영역 · 스크롤 · GIF · 영상을 바꿈. 좌표는 이 모니터 기준 실제 화소
/// </summary>
public partial class SelectionWindow : Window
{
    private readonly SelectionSession _session;
    private bool _down, _dragging;
    private (int X, int Y) _start;
    private PixelRect _selection;

    public SelectionWindow(SelectionSession session, MonitorInfo monitor, PixelFrame frame, bool showBar)
    {
        _session = session;
        Monitor = monitor;
        Frame = frame;
        InitializeComponent();
        var bitmap = frame.ToBitmap();
        Frozen.Source = bitmap;
        LoupeImage.Fill = new ImageBrush(bitmap) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        Bar.Visibility = showBar ? Visibility.Visible : Visibility.Collapsed;
        // 창 크기는 OnSourceInitialized 에서 실제 화소로 맞춤. 그 전에 대략 (주 모니터 배율)
        Left = monitor.Bounds.X;
        Top = monitor.Bounds.Y;
        Width = monitor.Bounds.Width;
        Height = monitor.Bounds.Height;
        ShowMode();
        Loaded += (_, _) => UpdateHover(Screens.Cursor());
    }

    public MonitorInfo Monitor { get; }
    public PixelFrame Frame { get; }

    /// <summary>DIP 하나에 실제 화소 몇 개</summary>
    private double Scale => Root.ActualWidth > 0 ? Frame.Width / Root.ActualWidth : 1;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        int disabled = 1;
        DwmSetWindowAttribute(handle, DWMWA_TRANSITIONS_FORCEDISABLED, ref disabled, sizeof(int));
        Place();
        // 배율이 다른 모니터로 옮기면 Windows 가 크기를 바꾸므로 다시 맞춤
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Place);
    }

    private void Place()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var b = Monitor.Bounds;
        SetWindowPos(handle, HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, SWP_NOACTIVATE);
    }

    public void ShowMode()
    {
        RegionMode.IsChecked = _session.Mode == CaptureMode.Region;
        ScrollMode.IsChecked = _session.Mode == CaptureMode.Scroll;
        GifMode.IsChecked = _session.Mode == CaptureMode.Gif;
        VideoMode.IsChecked = _session.Mode == CaptureMode.Video;
        Hint.Text = _session.Mode switch
        {
            CaptureMode.Scroll => "스크롤할 곳을 끌거나 창을 누르세요 · Esc 취소",
            CaptureMode.Gif or CaptureMode.Video => "녹화할 곳을 끌거나 창을 누르세요 · Enter 전체 화면 · Esc 취소",
            _ => "끌면 영역 · 누르면 창 · Enter 전체 화면 · Esc 취소"
        };
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var mode = sender == ScrollMode ? CaptureMode.Scroll : sender == GifMode ? CaptureMode.Gif : sender == VideoMode ? CaptureMode.Video : CaptureMode.Region;
        if (mode != _session.Mode) _session.SetMode(mode);
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e) => _session.Complete(this, new PixelRect(0, 0, Frame.Width, Frame.Height));

    private void Cancel_Click(object sender, RoutedEventArgs e) => _session.Cancel();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) _session.Cancel();
        else if (e.Key == Key.Enter) _session.Complete(this, new PixelRect(0, 0, Frame.Width, Frame.Height));
    }

    // ───────── 마우스 ─────────

    private (int X, int Y) ToPixel(Point point) =>
        (Math.Clamp((int)Math.Round(point.X * Scale), 0, Frame.Width), Math.Clamp((int)Math.Round(point.Y * Scale), 0, Frame.Height));

    private bool OnBar(MouseEventArgs e) => Bar.IsVisible && Bar.IsMouseOver;

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (OnBar(e)) return;
        _down = true;
        _dragging = false;
        _start = ToPixel(e.GetPosition(Root));
        Root.CaptureMouse();
        e.Handled = true;
    }

    private void Root_MouseMove(object sender, MouseEventArgs e)
    {
        var p = ToPixel(e.GetPosition(Root));
        ShowLoupe(p, e.GetPosition(Root));
        if (!_down)
        {
            UpdateHover((p.X + Monitor.Bounds.X, p.Y + Monitor.Bounds.Y));
            return;
        }
        if (!_dragging && Math.Abs(p.X - _start.X) + Math.Abs(p.Y - _start.Y) > 4)
        {
            _dragging = true;
            Bar.Opacity = 0;
            Bar.IsHitTestVisible = false;
        }
        if (_dragging)
        {
            _selection = PixelRect.FromCorners(_start.X, _start.Y, p.X, p.Y);
            Draw(_selection);
        }
    }

    private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_down) return;
        _down = false;
        Root.ReleaseMouseCapture();
        if (_dragging)
        {
            _dragging = false;
            if (_selection.Width >= 4 && _selection.Height >= 4) _session.Complete(this, _selection);
            else ResetDrag();
            return;
        }
        // 누르기만 함: 마우스 아래 창 (없으면 모니터 전체)
        _session.Complete(this, _selection.IsEmpty ? new PixelRect(0, 0, Frame.Width, Frame.Height) : _selection);
    }

    private void Root_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 끄는 중이면 그것만 취소, 아니면 고르기 취소
        if (_down)
        {
            _down = false;
            Root.ReleaseMouseCapture();
            ResetDrag();
        }
        else _session.Cancel();
    }

    private void ResetDrag()
    {
        _dragging = false;
        Bar.Opacity = 1;
        Bar.IsHitTestVisible = true;
        UpdateHover(Screens.Cursor());
    }

    /// <summary>마우스 아래 창을 표시 (화면 좌표). 이 모니터 밖이면 표시 없음</summary>
    public void UpdateHover((int X, int Y) screen)
    {
        if (!Monitor.Bounds.Contains(screen.X, screen.Y))
        {
            _selection = default;
            Draw(default);
            Loupe.Visibility = Visibility.Collapsed;
            return;
        }
        var window = _session.Windows.FirstOrDefault(w => w.Contains(screen.X, screen.Y));
        var rect = window.IsEmpty ? Monitor.Bounds : window.Intersect(Monitor.Bounds);
        _selection = rect.Offset(-Monitor.Bounds.X, -Monitor.Bounds.Y);
        Draw(_selection);
    }

    // ───────── 그리기 ─────────

    /// <summary>화면 확인용 (Capture.Checks): 고른 영역과 돋보기를 그려 둠</summary>
    internal void Preview(PixelRect rect, (int X, int Y) pointer)
    {
        _selection = rect;
        Draw(rect);
        ShowLoupe(pointer, new Point(pointer.X / Scale, pointer.Y / Scale));
    }


    private void Draw(PixelRect rect)
    {
        double s = Scale, w = Root.ActualWidth, h = Root.ActualHeight;
        if (rect.IsEmpty)
        {
            Shade.Data = new RectangleGeometry(new Rect(0, 0, w, h));
            Outline.Visibility = Visibility.Collapsed;
            SizeTag.Visibility = Visibility.Collapsed;
            return;
        }
        var area = new Rect(rect.X / s, rect.Y / s, rect.Width / s, rect.Height / s);
        Shade.Data = new GeometryGroup
        {
            FillRule = FillRule.EvenOdd,
            Children = { new RectangleGeometry(new Rect(0, 0, w, h)), new RectangleGeometry(area) }
        };
        Outline.Margin = new Thickness(area.X - 2, area.Y - 2, 0, 0);
        Outline.Width = area.Width + 4;
        Outline.Height = area.Height + 4;
        Outline.Visibility = Visibility.Visible;
        SizeText.Text = rect.ToString();
        SizeTag.Visibility = Visibility.Visible;
        SizeTag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double tagY = area.Y >= SizeTag.DesiredSize.Height + 8 ? area.Y - SizeTag.DesiredSize.Height - 6 : area.Y + 6;
        double tagX = Math.Clamp(area.X, 0, Math.Max(0, w - SizeTag.DesiredSize.Width));
        SizeTag.Margin = new Thickness(tagX + (area.Y >= SizeTag.DesiredSize.Height + 8 ? 0 : 6), tagY, 0, 0);
    }

    private void ShowLoupe((int X, int Y) pixel, Point at)
    {
        if (Bar.IsVisible && Bar.IsMouseOver && !_dragging)
        {
            Loupe.Visibility = Visibility.Collapsed;
            return;
        }
        pixel = (Math.Min(pixel.X, Frame.Width - 1), Math.Min(pixel.Y, Frame.Height - 1));
        ((ImageBrush)LoupeImage.Fill).Viewbox = new Rect(pixel.X - 9, pixel.Y - 9, 19, 19);
        LoupeText.Text = _dragging ? _selection.ToString() : $"{pixel.X}, {pixel.Y}";
        Loupe.Visibility = Visibility.Visible;
        double x = at.X + 20, y = at.Y + 20;
        if (x + 130 > Root.ActualWidth) x = at.X - 20 - 124;
        if (y + 150 > Root.ActualHeight) y = at.Y - 20 - 144;
        Loupe.Margin = new Thickness(x, y, 0, 0);
    }
}

/// <summary>모니터마다 고르기 창을 띄우고 고른 결과를 돌려줌</summary>
public sealed class SelectionSession
{
    private readonly TaskCompletionSource<Selection?> _done = new();
    private readonly List<SelectionWindow> _windows = [];

    private SelectionSession(CaptureMode mode, List<PixelRect> windows)
    {
        Mode = mode;
        Windows = windows;
    }

    public CaptureMode Mode { get; private set; }

    /// <summary>화면 확인용 (Capture.Checks): 띄우지 않은 고르기 창 하나</summary>
    internal static SelectionWindow Preview(MonitorInfo monitor, PixelFrame frame) =>
        new(new SelectionSession(CaptureMode.Region, []), monitor, frame, showBar: true);

    /// <summary>화면에 보이던 창들 (위에 있는 것부터)</summary>
    public List<PixelRect> Windows { get; }

    /// <summary>지금 화면을 멈춰 두고 고르게 함. 취소하면 null. 화면을 읽지 못하면 Win32Exception</summary>
    public static Task<Selection?> RunAsync(CaptureMode mode)
    {
        var monitors = Screens.All();
        var frames = monitors.Select(m => ScreenGrabber.GrabOnce(m.Bounds)).ToList();
        var session = new SelectionSession(mode, WindowFinder.VisibleWindows());
        var cursor = Screens.Cursor();
        var barMonitor = monitors.FirstOrDefault(m => m.Bounds.Contains(cursor.X, cursor.Y)) ?? monitors.First(m => m.IsPrimary);
        SelectionWindow? active = null;
        for (int i = 0; i < monitors.Count; i++)
        {
            var window = new SelectionWindow(session, monitors[i], frames[i], monitors[i] == barMonitor);
            session._windows.Add(window);
            window.Show();
            if (monitors[i] == barMonitor) active = window;
        }
        if (active is not null)
        {
            active.Activate();
            SetForegroundWindow(new WindowInteropHelper(active).Handle);
            active.Focus();
        }
        return session._done.Task;
    }

    public void SetMode(CaptureMode mode)
    {
        Mode = mode;
        foreach (var window in _windows) window.ShowMode();
    }

    public void Complete(SelectionWindow window, PixelRect local)
    {
        if (_done.Task.IsCompleted) return;
        var global = local.Offset(window.Monitor.Bounds.X, window.Monitor.Bounds.Y);
        var image = Mode == CaptureMode.Region ? window.Frame.Crop(local) : null;
        CloseAll();
        _done.TrySetResult(new Selection(Mode, global, image));
    }

    public void Cancel()
    {
        CloseAll();
        _done.TrySetResult(null);
    }

    private void CloseAll()
    {
        foreach (var window in _windows) window.Close();
        _windows.Clear();
    }
}
