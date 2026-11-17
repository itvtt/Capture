using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Capture;
using Capture.Core;
using Capture.Services;

/// <summary>창들을 PNG 로 (화면을 직접 보지 않고 모양 확인). 창은 화면 밖에 띄움</summary>
static class Snapshots
{
    public static int Run(string folder)
    {
        Directory.CreateDirectory(folder);
        string samples = Path.Combine(folder, "samples");
        Directory.CreateDirectory(samples);
        var desktop = FakeDesktop(1280, 720);

        // 최근 목록에 보일 파일: 그림 셋, GIF 하나, 영상 하나
        var settings = new AppSettings { SaveFolder = samples };
        for (int i = 0; i < 3; i++)
        {
            string png = settings.NewFilePath("png").Replace(".png", $"_{i}.png");
            ImageFiles.Save(new CroppedBitmap(desktop, new Int32Rect(i * 200, i * 80, 640, 400)), png);
        }
        using (var gif = new GifWriter(Path.Combine(samples, "Capture_sample.gif"), 320, 200))
        {
            gif.Write(PixelFrame.FromBitmap(new CroppedBitmap(desktop, new Int32Rect(100, 100, 320, 200))).Pixels, 0);
            gif.Finish(1_000_000);
        }
        File.WriteAllBytes(Path.Combine(samples, "Capture_sample.mp4"), [0, 0, 0, 0]);

        var controller = new CaptureController(settings);
        using var hotkeys = new Hotkeys(settings);

        var main = new MainWindow(controller, hotkeys) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -6000, Top = 0, ShowActivated = false };
        main.Show();
        Program.Pump(TimeSpan.FromSeconds(2), () => false);
        Save((FrameworkElement)main.Content, Path.Combine(folder, "main.png"));
        main.ExitApp();

        var keys = new SettingsWindow(hotkeys, controller, firstRun: true) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -6000, Top = 0, ShowActivated = false };
        keys.Show();
        Program.Pump(TimeSpan.FromMilliseconds(300), () => false);
        Save((FrameworkElement)keys.Content, Path.Combine(folder, "settings.png"));
        keys.Close();

        var editor = new EditorWindow(controller, settings.NewFilePath("png"), new CroppedBitmap(desktop, new Int32Rect(80, 60, 760, 460)))
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -6000, Top = 0, ShowActivated = false
        };
        editor.Show();
        Program.Pump(TimeSpan.FromMilliseconds(500), () => false);
        editor.PreviewMarks();
        Program.Pump(TimeSpan.FromMilliseconds(500), () => false);
        Save((FrameworkElement)editor.Content, Path.Combine(folder, "editor.png"));
        editor.Close();

        // 고르기 화면: 가짜 모니터 하나, 창 하나를 고른 모습
        var monitor = new MonitorInfo(IntPtr.Zero, new PixelRect(0, 0, 1280, 720), new PixelRect(0, 0, 1280, 680), true);
        var overlay = SelectionSession.Preview(monitor, PixelFrame.FromBitmap(desktop));
        var root = (FrameworkElement)overlay.Content;
        root.Measure(new Size(1280, 720));
        root.Arrange(new Rect(0, 0, 1280, 720));
        root.UpdateLayout();
        overlay.Preview(new PixelRect(180, 140, 620, 380), (820, 540));
        root.UpdateLayout();
        Save(root, Path.Combine(folder, "overlay.png"));

        var bar = new RecordBar("GIF", canCancel: true, Color.FromRgb(0xFF, 0x45, 0x3A));
        bar.Show();
        bar.ShowTime(TimeSpan.FromSeconds(12.2));
        Program.Pump(TimeSpan.FromMilliseconds(300), () => false);
        Save((FrameworkElement)bar.Content, Path.Combine(folder, "recordbar.png"));
        bar.Close();

        var toast = new Toast("GIF 를 저장했습니다", "Capture_20261117_143012.gif · 1.2 MB · 0:12",
            ImageFiles.Thumbnail(Path.Combine(samples, "Capture_sample.gif"), 128), null,
            [("공유", () => { }), ("복사", () => { }), ("폴더", () => { })], TimeSpan.FromSeconds(30))
        { Left = -6000, Top = 0 };
        toast.Show();
        Program.Pump(TimeSpan.FromMilliseconds(300), () => false);
        Save((FrameworkElement)toast.Content, Path.Combine(folder, "toast.png"));
        toast.Close();

        Console.WriteLine("snapshots: " + string.Join(", ", Directory.GetFiles(folder, "*.png").Select(Path.GetFileName)));
        return 0;
    }

    private static void Save(FrameworkElement root, string path)
    {
        int w = (int)Math.Ceiling(root.ActualWidth), h = (int)Math.Ceiling(root.ActualHeight);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)), null, new Rect(0, 0, w, h));
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>가짜 바탕화면: 배경 · 창 두 개 · 글자</summary>
    private static BitmapSource FakeDesktop(int w, int h)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var background = new LinearGradientBrush(Color.FromRgb(0x23, 0x4E, 0x8C), Color.FromRgb(0x5B, 0x2C, 0x83), 45);
            dc.DrawRectangle(background, null, new Rect(0, 0, w, h));
            dc.DrawRoundedRectangle(Brushes.White, null, new Rect(180, 140, 620, 380), 8, 8);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF2, 0xF3, 0xF5)), null, new Rect(180, 140, 620, 36));
            var font = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            for (int line = 0; line < 9; line++)
            {
                var text = new FormattedText($"Quarterly report line {line + 1} — revenue, cost and notes", System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, font, 15, Brushes.DimGray, 1);
                dc.DrawText(text, new Point(204, 196 + line * 34));
            }
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)), null, new Rect(860, 220, 360, 300), 8, 8);
            for (int line = 0; line < 7; line++)
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0)), null, new Rect(884, 250 + line * 32, 120 + line * 23 % 160, 10));
        }
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
