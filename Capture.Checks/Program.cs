using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Capture;
using Capture.Core;
using Capture.Services;

/// <summary>
/// Capture 확인. 실패하면 종료 코드 1.
///   (인자 없음)            GIF · 스크롤 이어 붙이기 · MP4 · 설정 · 편집 창 합치기
///   --snapshot &lt;폴더&gt;    큰 창 · 편집 창 · 고르기 화면 · 녹화 막대 · 알림을 PNG 로
///   --screen               실제 화면 읽기 · 캡처에서 빼기 (원격 데스크톱 창이 최소화되어 있으면 실패)
/// </summary>
static class Program
{
    private static int _passed, _failed;

    [STAThread]
    private static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Capture;component/Theme.xaml") });
        if (args is ["--snapshot", var folder]) return Snapshots.Run(folder);
        if (args is ["--screen"]) return ScreenChecks.Run();
        if (args is ["--live", var output]) return ScreenChecks.Live(output);
        if (args is ["--e2e", var exe, var e2e]) return EndToEnd.Run(exe, e2e);
        if (args is ["--hotkey-main", var mainFolder]) return EndToEnd.FromMainWindow(mainFolder);  // CAPTURE_DATA_DIR 을 따로 정해 두고
        if (args is ["--hotkey-dialog", var keyFolder]) return EndToEnd.HotkeyDialog(keyFolder);  // CAPTURE_DATA_DIR 을 따로 정해 두고
        if (args is ["--grab", var shot])
        {
            // 주 모니터를 그대로 PNG 로 (실제 화면 확인)
            var monitor = Screens.All().First(m => m.IsPrimary);
            ImageFiles.Save(ScreenGrabber.GrabOnce(monitor.Bounds).ToBitmap(), shot);
            return 0;
        }
        if (args is ["--share", var file])
        {
            // 이 PC 의 File Shared 에 실제로 올려 봄 (링크를 출력)
            string link = FileShared.ShareAsync(file).GetAwaiter().GetResult();
            Console.WriteLine("link: " + link);
            using var http = new System.Net.Http.HttpClient();
            var page = http.GetAsync(link.Replace(FileShared.LocalAddress(), "127.0.0.1")).GetAwaiter().GetResult();
            Console.WriteLine($"page: {(int)page.StatusCode} {page.Content.Headers.ContentType}");
            return page.IsSuccessStatusCode ? 0 : 1;
        }

        string root = Path.Combine(Path.GetTempPath(), "capture-checks-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            Check("GIF: exact colors, only the changed box, still frames merged, delays", () => GifBasics(root));
            Check("GIF: a big noisy frame survives LZW table resets", () => GifNoise(root));
            Check("GIF: more than 256 colors come out close", () => GifManyColors(root));
            Check("Scroll stitch: header, content and footer come back in one piece", ScrollStitch);
            Check("Scroll stitch: nothing moved is reported as no change", ScrollNoChange);
            Check("MP4: H.264 file plays and is right side up", () => Mp4(root));
            Check("Settings: new file names never overwrite", () => FileNames(root));
            Check("Hotkeys: names, allowed keys, saved and read back", () => HotkeySettings(root));
            Check("Editor: the flattened picture keeps its size and the marks", () => EditorFlatten(root));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
        Console.WriteLine(_failed == 0 ? $"All {_passed} checks passed." : $"{_failed} of {_passed + _failed} checks failed.");
        return _failed == 0 ? 0 : 1;
    }

    private static void Check(string name, Action body)
    {
        try
        {
            body();
            _passed++;
            Console.WriteLine("  ok   " + name);
        }
        catch (Exception error)
        {
            _failed++;
            Console.WriteLine("  FAIL " + name + Environment.NewLine + "       " + error.GetType().Name + ": " + error.Message);
        }
    }

    internal static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Equal<T>(T expected, T actual, string what)
    {
        if (!Equals(expected, actual)) throw new Exception($"{what}: expected <{expected}> but was <{actual}>");
    }

    // ───────── GIF ─────────

    private static PixelFrame Solid(int w, int h, Func<int, int, (byte R, byte G, byte B)> color)
    {
        var frame = new PixelFrame(w, h);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            var (r, g, b) = color(x, y);
            int i = (y * w + x) * 4;
            frame.Pixels[i] = b;
            frame.Pixels[i + 1] = g;
            frame.Pixels[i + 2] = r;
            frame.Pixels[i + 3] = 255;
        }
        return frame;
    }

    private static PixelFrame Copy(PixelFrame frame) => new(frame.Width, frame.Height, (byte[])frame.Pixels.Clone());

    private static void Fill(PixelFrame frame, int x0, int y0, int x1, int y1, byte r, byte g, byte b)
    {
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            int i = (y * frame.Width + x) * 4;
            frame.Pixels[i] = b;
            frame.Pixels[i + 1] = g;
            frame.Pixels[i + 2] = r;
        }
    }

    private static BitmapFrame[] Decode(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return [.. decoder.Frames];
    }

    private static PixelFrame Pixels(BitmapSource source) => PixelFrame.FromBitmap(new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0));

    private static int Query(BitmapFrame frame, string query) => Convert.ToInt32(((BitmapMetadata)frame.Metadata).GetQuery(query));

    private static void SameColors(PixelFrame expected, PixelFrame actual, string what)
    {
        Equal(expected.Width, actual.Width, what + " width");
        Equal(expected.Height, actual.Height, what + " height");
        for (int i = 0; i < expected.Width * expected.Height; i++)
            for (int c = 0; c < 3; c++)
                if (expected.Pixels[i * 4 + c] != actual.Pixels[i * 4 + c])
                    throw new Exception($"{what}: pixel {i % expected.Width},{i / expected.Width} differs");
    }

    private static void GifBasics(string root)
    {
        string path = Path.Combine(root, "basic.gif");
        var first = Solid(64, 48, (x, y) => x < 32 ? (y < 24 ? ((byte)10, (byte)20, (byte)30) : ((byte)200, (byte)40, (byte)40)) : (y < 24 ? ((byte)40, (byte)200, (byte)40) : ((byte)40, (byte)40, (byte)200)));
        var second = Copy(first);
        Fill(second, 10, 5, 20, 15, 255, 255, 255);
        var third = Copy(second);
        Fill(third, 63, 47, 63, 47, 0, 0, 0);
        using (var writer = new GifWriter(path, 64, 48))
        {
            writer.Write(first.Pixels, 0);
            writer.Write(second.Pixels, 1_000_000);
            writer.Write(Copy(second).Pixels, 2_000_000);  // 그대로: 앞 그림을 더 오래
            writer.Write(third.Pixels, 3_000_000);
            writer.Finish(4_000_000);
            Equal(3, writer.FrameCount, "frames written");
        }
        var frames = Decode(path);
        Equal(3, frames.Length, "frames decoded");
        SameColors(first, Pixels(frames[0]), "first frame");
        Equal(10, Query(frames[1], "/imgdesc/Left"), "second left");
        Equal(5, Query(frames[1], "/imgdesc/Top"), "second top");
        Equal(11, frames[1].PixelWidth, "second width");
        Equal(11, frames[1].PixelHeight, "second height");
        var white = Pixels(frames[1]);
        Assert(white.Pixels[0] == 255 && white.Pixels[1] == 255 && white.Pixels[2] == 255, "second frame is white");
        Equal(63, Query(frames[2], "/imgdesc/Left"), "third left");
        Equal(1, frames[2].PixelWidth, "third width");
        Equal(10, Query(frames[0], "/grctlext/Delay"), "first delay");
        Equal(20, Query(frames[1], "/grctlext/Delay"), "second delay (merged still frame)");
        Equal(10, Query(frames[2], "/grctlext/Delay"), "third delay");
    }

    private static void GifNoise(string root)
    {
        string path = Path.Combine(root, "noise.gif");
        var random = new Random(7);
        var palette = Enumerable.Range(0, 256).Select(_ => ((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256))).Distinct().ToArray();
        var noise = Solid(400, 300, (_, _) => palette[random.Next(palette.Length)]);
        using (var writer = new GifWriter(path, 400, 300))
        {
            writer.Write(noise.Pixels, 0);
            writer.Finish(1_000_000);
        }
        SameColors(noise, Pixels(Decode(path)[0]), "noise frame");
    }

    private static void GifManyColors(string root)
    {
        string path = Path.Combine(root, "gradient.gif");
        var gradient = Solid(128, 64, (x, y) => ((byte)(x * 2), (byte)(y * 4), (byte)((x + y) % 256)));
        using (var writer = new GifWriter(path, 128, 64))
        {
            writer.Write(gradient.Pixels, 0);
            writer.Finish(1_000_000);
        }
        var decoded = Pixels(Decode(path)[0]);
        long total = 0;
        int worst = 0;
        for (int i = 0; i < 128 * 64 * 4; i++)
        {
            if (i % 4 == 3) continue;
            int error = Math.Abs(gradient.Pixels[i] - decoded.Pixels[i]);
            total += error;
            worst = Math.Max(worst, error);
        }
        double average = total / (128.0 * 64 * 3);
        Assert(average < 8, $"average error {average:0.0}");
        Assert(worst < 48, $"worst error {worst}");
    }

    // ───────── 스크롤 ─────────

    /// <summary>머리글 40줄 · 바닥글 30줄 · 오른쪽 스크롤 막대가 있는 창에 1500줄짜리 내용을 offset 부터 보여 줌</summary>
    private static PixelFrame Viewport(int offset, int content)
    {
        const int width = 320, height = 400, header = 40, footer = 30;
        int band = height - header - footer;
        return Solid(width, height, (x, y) =>
        {
            if (x >= 300)
            {
                int thumb = header + offset * band / content;
                return y >= thumb && y < thumb + 40 ? ((byte)90, (byte)90, (byte)90) : ((byte)230, (byte)230, (byte)230);
            }
            if (y < header) return ((byte)30, (byte)60, (byte)(120 + x % 50));
            if (y >= height - footer) return ((byte)(20 + x % 30), (byte)20, (byte)20);
            return ContentPixel(offset + y - header, x);
        });
    }

    private static (byte, byte, byte) ContentPixel(int row, int x)
    {
        if (row >= 500 && row < 560) return (255, 255, 255);  // 빈 줄
        int h = (row * 7919 + (x / 8) * 104729) ^ (row << 3);
        return ((byte)h, (byte)(h >> 8), (byte)(h >> 16));
    }

    private static void ScrollStitch()
    {
        const int content = 1500, band = 330;
        var stitcher = new ScrollStitcher(Viewport(0, content));
        var offsets = new List<int>();
        for (int offset = 200; offset < content - band; offset += 200) offsets.Add(offset);
        offsets.Add(content - band);
        foreach (int offset in offsets)
            Equal(StitchOutcome.Added, stitcher.Add(Viewport(offset, content)), $"offset {offset}");
        Equal(StitchOutcome.NoChange, stitcher.Add(Viewport(content - band, content)), "at the end");
        var result = stitcher.Build();
        Equal(40 + content + 30, result.Height, "height");
        for (int y = 0; y < result.Height; y++)
        for (int x = 0; x < 288; x++)
        {
            (byte R, byte G, byte B) expected = y < 40 ? ((byte)30, (byte)60, (byte)(120 + x % 50))
                : y >= 40 + content ? ((byte)(20 + x % 30), (byte)20, (byte)20)
                : ContentPixel(y - 40, x);
            int i = (y * result.Width + x) * 4;
            if (result.Pixels[i + 2] != expected.R || result.Pixels[i + 1] != expected.G || result.Pixels[i] != expected.B)
                throw new Exception($"pixel {x},{y} differs");
        }
    }

    private static void ScrollNoChange()
    {
        var frame = Viewport(0, 1500);
        var stitcher = new ScrollStitcher(frame);
        Equal(StitchOutcome.NoChange, stitcher.Add(Copy(frame)), "same frame");
        Equal(400, stitcher.Build().Height, "height");
    }

    // ───────── MP4 ─────────

    private static void Mp4(string root)
    {
        string path = Path.Combine(root, "clip.mp4");
        const int w = 320, h = 240;
        using (var writer = new Mp4Writer(path, w, h, 30))
        {
            for (int n = 0; n < 45; n++)
            {
                var frame = Solid(w, h, (x, y) => y < h / 2 ? ((byte)230, (byte)30, (byte)30) : ((byte)30, (byte)30, (byte)230));
                Fill(frame, 10 + n * 4, 100, 40 + n * 4, 140, 255, 255, 255);
                writer.Write(frame.Pixels, n * 10_000_000L / 30);
            }
            writer.Finish(45 * 10_000_000L / 30);
        }
        var bytes = File.ReadAllBytes(path);
        Assert(bytes.Length > 2000, $"file is {bytes.Length} bytes");
        Equal("ftyp", System.Text.Encoding.ASCII.GetString(bytes, 4, 4), "first box");
        Assert(System.Text.Encoding.ASCII.GetString(bytes).Contains("moov"), "no moov box");

        // Windows 재생기로 0.5초 지점을 그려 위가 빨강 · 아래가 파랑인지
        var player = new MediaPlayer { ScrubbingEnabled = true, IsMuted = true };
        bool opened = false;
        string? failure = null;
        player.MediaOpened += (_, _) => opened = true;
        player.MediaFailed += (_, e) => failure = e.ErrorException.Message;
        player.Open(new Uri(path));
        Pump(TimeSpan.FromSeconds(10), () => opened || failure is not null);
        Assert(failure is null, "cannot play: " + failure);
        Assert(opened, "player did not open the file");
        Assert(player.NaturalVideoWidth == w && player.NaturalVideoHeight == h, $"video is {player.NaturalVideoWidth}x{player.NaturalVideoHeight}");
        player.Pause();
        player.Position = TimeSpan.FromMilliseconds(500);
        Pump(TimeSpan.FromMilliseconds(1500), () => false);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawVideo(player, new Rect(0, 0, w, h));
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = PixelFrame.FromBitmap(bitmap);
        player.Close();
        int top = (20 * w + 160) * 4, bottom = (220 * w + 160) * 4;
        Assert(pixels.Pixels[top + 2] > 150 && pixels.Pixels[top] < 100, $"top is not red (B{pixels.Pixels[top]} R{pixels.Pixels[top + 2]})");
        Assert(pixels.Pixels[bottom] > 150 && pixels.Pixels[bottom + 2] < 100, $"bottom is not blue (B{pixels.Pixels[bottom]} R{pixels.Pixels[bottom + 2]})");
    }

    internal static void Pump(TimeSpan limit, Func<bool> done)
    {
        var end = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < end && !done())
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(15);
        }
    }

    // ───────── 설정 · 편집 ─────────

    private static void FileNames(string root)
    {
        var settings = new AppSettings { SaveFolder = Path.Combine(root, "names") };
        string first = settings.NewFilePath("png");
        File.WriteAllBytes(first, [1]);
        string second = settings.NewFilePath("png");
        Assert(first != second && !File.Exists(second), "second name exists");
        Assert(Path.GetFileName(first).StartsWith("Capture_"), "name starts with Capture_");
    }

    private static void HotkeySettings(string root)
    {
        Equal("PrtSc", Hotkeys.Default(CaptureMode.Region).ToString(), "region default");
        Equal("Ctrl+Shift+PrtSc", Hotkeys.Default(CaptureMode.Video).ToString(), "video default");
        Equal("없음", default(HotkeyBinding).ToString(), "none");
        var altF = HotkeyBinding.FromKey(System.Windows.Input.Key.F, System.Windows.Input.ModifierKeys.Alt | System.Windows.Input.ModifierKeys.Control, out _);
        Equal("Ctrl+Alt+F", altF?.ToString(), "Ctrl+Alt+F");
        Equal("F9", HotkeyBinding.FromKey(System.Windows.Input.Key.F9, System.Windows.Input.ModifierKeys.None, out _)?.ToString(), "F9 alone");
        // 어떤 키든 됨: 글을 쓸 때 누르는 키는 알려 주기만
        Equal("A", HotkeyBinding.FromKey(System.Windows.Input.Key.A, System.Windows.Input.ModifierKeys.None, out var typing)?.ToString(), "A alone");
        Assert(typing is not null, "A alone comes with a warning");
        Equal("Shift+A", HotkeyBinding.FromKey(System.Windows.Input.Key.A, System.Windows.Input.ModifierKeys.Shift, out typing)?.ToString(), "Shift+A");
        Assert(typing is not null, "Shift+A comes with a warning");
        Equal("Backspace", HotkeyBinding.FromKey(System.Windows.Input.Key.Back, System.Windows.Input.ModifierKeys.None, out _)?.ToString(), "Backspace");
        Equal("Ctrl+Win+.", HotkeyBinding.FromKey(System.Windows.Input.Key.OemPeriod, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Windows, out typing)?.ToString(), "Ctrl+Win+.");
        Assert(typing is null, "Ctrl+Win+. has no warning");
        HotkeyBinding.FromKey(System.Windows.Input.Key.F, System.Windows.Input.ModifierKeys.Alt | System.Windows.Input.ModifierKeys.Control, out typing);
        Assert(typing is null, "Ctrl+Alt+F has no warning");

        // 저장한 단축키를 다음 실행에서 그대로 읽음
        string folder = Path.Combine(root, "hotkey-settings");
        Directory.CreateDirectory(folder);
        var settings = new AppSettings { SaveFolder = folder, Hotkeys = new() { ["Gif"] = altF!.Value, ["Video"] = default } };
        string json = System.Text.Json.JsonSerializer.Serialize(settings);
        var read = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;
        using var hotkeys = new Hotkeys(read);
        Equal("Ctrl+Alt+F", hotkeys.LabelFor(CaptureMode.Gif), "saved GIF key");
        Equal("없음", hotkeys.LabelFor(CaptureMode.Video), "saved video key (none)");
        Equal("PrtSc", hotkeys.LabelFor(CaptureMode.Region), "unsaved region key is the default");
    }

    private static void EditorFlatten(string root)
    {
        var settings = new AppSettings { SaveFolder = Path.Combine(root, "editor") };
        var picture = Solid(600, 400, (_, _) => (128, 128, 128)).ToBitmap();
        string path = settings.NewFilePath("png");
        ImageFiles.Save(picture, path);
        var editor = new EditorWindow(new CaptureController(settings), path, picture)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -6000, Top = 0, ShowActivated = false
        };
        editor.Show();
        Pump(TimeSpan.FromMilliseconds(300), () => false);
        editor.PreviewMarks();
        Pump(TimeSpan.FromMilliseconds(300), () => false);
        var flat = PixelFrame.FromBitmap(editor.Flatten());
        Equal(600, flat.Width, "width");
        Equal(400, flat.Height, "height");
        int stroke = (60 * 600 + 31) * 4;  // 사각형 왼쪽 선
        Assert(flat.Pixels[stroke + 2] > 200 && flat.Pixels[stroke + 1] < 120, "rectangle line is red");
        int plain = (380 * 600 + 580) * 4;
        Assert(flat.Pixels[plain] == 128 && flat.Pixels[plain + 2] == 128, "untouched pixel stays gray");
        editor.Close();  // 닫으면 저장
        var saved = PixelFrame.FromBitmap(ImageFiles.Load(path));
        Assert(saved.Pixels[stroke + 2] > 200, "saved file has the marks");
    }
}
