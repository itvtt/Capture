using System.Windows;
using System.Windows.Media;
using Capture;
using Capture.Core;

/// <summary>
/// 실제 화면으로 확인 (사람이 보는 화면에 잠깐 자홍색 네모가 뜸):
/// 화면 읽기, 캡처에서 빼기(WDA_EXCLUDEFROMCAPTURE), 투명 창을 CAPTUREBLT 없이도 읽는지
/// </summary>
static class ScreenChecks
{
    public static int Run()
    {
        var primary = Screens.All().First(m => m.IsPrimary);
        Console.WriteLine($"monitors: {string.Join(", ", Screens.All().Select(m => $"{m.Bounds.X},{m.Bounds.Y} {m.Bounds}"))}");
        var rect = new PixelRect(primary.WorkArea.X + 60, primary.WorkArea.Y + 60, 160, 120);
        try
        {
            var frame = ScreenGrabber.GrabOnce(primary.Bounds);
            Console.WriteLine($"grab primary: ok {frame.Width}x{frame.Height}");
        }
        catch (Exception error)
        {
            Console.WriteLine("grab primary: FAIL " + error.Message);
            return 1;
        }

        bool plainSeen = ShowAndLook(rect, layered: false, exclude: false, captureLayered: true);
        bool plainExcluded = ShowAndLook(rect, layered: false, exclude: true, captureLayered: true);
        bool layeredNoBlt = ShowAndLook(rect, layered: true, exclude: false, captureLayered: false);
        bool layeredExcluded = ShowAndLook(rect, layered: true, exclude: true, captureLayered: false);
        Console.WriteLine($"window seen in grab: {plainSeen} (expected True)");
        Console.WriteLine($"excluded window seen: {plainExcluded} (expected False)");
        Console.WriteLine($"transparent window seen without CAPTUREBLT: {layeredNoBlt}");
        Console.WriteLine($"excluded transparent window seen: {layeredExcluded} (expected False)");
        return plainSeen && !plainExcluded && !layeredExcluded ? 0 : 1;
    }

    /// <summary>
    /// 실제로 녹화 · 스크롤 캡처: 숫자가 바뀌는 창을 GIF · MP4 로 1.5초, 120줄짜리 창을 스크롤 캡처 (마우스가 잠깐 움직임).
    /// 결과 파일은 folder 에 남김
    /// </summary>
    public static int Live(string folder)
    {
        System.IO.Directory.CreateDirectory(folder);
        var primary = Screens.All().First(m => m.IsPrimary);
        var area = new PixelRect(primary.WorkArea.X + 80, primary.WorkArea.Y + 80, 420, 320);
        int failures = 0;

        // 숫자가 바뀌는 창
        var counter = new System.Windows.Controls.TextBlock { FontSize = 64, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var window = Plain(new System.Windows.Controls.Grid { Background = new SolidColorBrush(Color.FromRgb(0x20, 0x40, 0x80)), Children = { counter } });
        window.Show();
        WindowHelpers.PlaceAt(window, area);
        var tick = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        int n = 0;
        tick.Tick += (_, _) => counter.Text = (++n).ToString();
        tick.Start();
        foreach (bool gif in new[] { true, false })
        {
            string path = System.IO.Path.Combine(folder, gif ? "live.gif" : "live.mp4");
            var recorder = new Recorder(area, gif ? 15 : 30, () => gif ? new GifWriter(path, area.Width, area.Height) : new Mp4Writer(path, area.Width, area.Height, 30));
            recorder.Start();
            Program.Pump(TimeSpan.FromMilliseconds(1500), () => false);
            recorder.Stop();
            long size = new System.IO.FileInfo(path).Length;
            Console.WriteLine($"{(gif ? "GIF" : "MP4")}: {recorder.Captured} captured, {recorder.Dropped} dropped, {size:N0} bytes");
            if (recorder.Captured < (gif ? 15 : 30)) failures++;
        }
        tick.Stop();
        window.Close();

        // 스크롤 캡처: 줄 120개 (24 DIP 씩)
        var lines = new System.Windows.Controls.StackPanel();
        for (int i = 1; i <= 120; i++)
            lines.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = $"Line {i} · 스크롤 캡처 확인 줄",
                Height = 24,
                Padding = new Thickness(10, 3, 0, 0),
                FontSize = 14,
                Background = new SolidColorBrush(i % 2 == 0 ? Color.FromRgb(0xF4, 0xF6, 0xFA) : Colors.White),
                Foreground = Brushes.Black
            });
        var scroller = new System.Windows.Controls.ScrollViewer { Content = lines, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Visible };
        var page = Plain(scroller);
        page.Show();
        WindowHelpers.PlaceAt(page, area);
        page.Activate();
        Program.Pump(TimeSpan.FromMilliseconds(500), () => false);
        double scale = VisualTreeHelper.GetDpi(page).DpiScaleY;
        var task = Task.Run(() => ScrollCapture.Run(area, CancellationToken.None));
        Program.Pump(TimeSpan.FromSeconds(60), () => task.IsCompleted);
        page.Close();
        if (!task.IsCompleted)
        {
            Console.WriteLine("scroll: did not finish");
            return 1;
        }
        var result = task.Result;
        int expected = (int)Math.Round(120 * 24 * scale);
        Console.WriteLine($"scroll: {result.Width}x{result.Height} (content {expected})");
        Capture.Services.ImageFiles.Save(result.ToBitmap(), System.IO.Path.Combine(folder, "live-scroll.png"));
        if (Math.Abs(result.Height - expected) > 4 * scale) failures++;
        return failures == 0 ? 0 : 1;
    }

    private static Window Plain(UIElement content) => new()
    {
        WindowStyle = WindowStyle.None,
        ResizeMode = ResizeMode.NoResize,
        Topmost = true,
        ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.Manual,
        Left = -10000,
        Top = -10000,
        Width = 100,
        Height = 100,
        Content = content
    };

    /// <summary>자홍색 창을 rect 에 띄우고 화면을 읽어 그 색이 보이는지</summary>
    private static bool ShowAndLook(PixelRect rect, bool layered, bool exclude, bool captureLayered)
    {
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = layered,
            ResizeMode = ResizeMode.NoResize,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0xFF)),
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
            Width = 50,
            Height = 50
        };
        window.Show();
        WindowHelpers.PlaceAt(window, rect);
        if (exclude) WindowHelpers.ExcludeFromCapture(window);
        Program.Pump(TimeSpan.FromMilliseconds(400), () => false);
        var frame = ScreenGrabber.GrabOnce(rect);
        using (var grabber = new ScreenGrabber(rect)) grabber.Grab(frame.Pixels, cursor: false, layered: captureLayered);
        window.Close();
        int center = (rect.Height / 2 * rect.Width + rect.Width / 2) * 4;
        return frame.Pixels[center] > 240 && frame.Pixels[center + 1] < 20 && frame.Pixels[center + 2] > 240;
    }
}
