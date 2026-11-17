using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Capture.Core;

/// <summary>
/// 실제 앱을 키보드 · 마우스로 움직여 봄 (사람이 보는 화면에서 마우스가 움직임):
/// PrtSc → 끌어서 영역 → 편집 창 · PNG, Shift+PrtSc → Enter(전체 화면) → 2초 → Shift+PrtSc → GIF, Ctrl+Shift+PrtSc → 영상.
/// 앱은 CAPTURE_DATA_DIR 로 따로 띄우고 저장 폴더도 따로 (사용자 설정 · 사진 폴더를 건드리지 않음)
/// </summary>
static class EndToEnd
{
    [StructLayout(LayoutKind.Explicit)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public MouseInput Mouse;
        [FieldOffset(8)] public KeyInput Key;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public IntPtr Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyInput
    {
        public ushort Vk, Scan;
        public uint Flags, Time;
        public IntPtr Extra;
    }

    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string title);

    private const ushort VkSnapshot = 0x2C, VkShift = 0x10, VkControl = 0x11, VkReturn = 0x0D, VkEscape = 0x1B;

    public static int Run(string exe, string folder)
    {
        string data = Path.Combine(folder, "data"), saves = Path.Combine(folder, "saves");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(saves);
        // 단축키는 처음에 없으므로 시험에 쓸 것을 정해 둠: PrtSc 영역 · Shift+PrtSc GIF · Ctrl+Shift+PrtSc 영상
        var shortcuts = new Dictionary<string, object>
        {
            ["Region"] = new { Modifiers = 0, Key = 0x2C },
            ["Gif"] = new { Modifiers = 4, Key = 0x2C },
            ["Video"] = new { Modifiers = 6, Key = 0x2C },
        };
        File.WriteAllText(Path.Combine(data, "settings.json"), JsonSerializer.Serialize(new { SaveFolder = saves, StartupConfigured = true, TrayHintShown = true, HotkeysChosen = true, Hotkeys = shortcuts }));

        var start = new ProcessStartInfo(exe, "--tray") { UseShellExecute = false };
        start.Environment["CAPTURE_DATA_DIR"] = data;
        using var app = Process.Start(start)!;
        int failures = 0;
        try
        {
            Thread.Sleep(2500);

            // 1. PrtSc → (300,300)~(700,600) 끌기
            Keys(VkSnapshot);
            Thread.Sleep(1200);
            Drag(300, 300, 700, 600);
            Thread.Sleep(2500);
            var png = Directory.GetFiles(saves, "*.png");
            if (png.Length == 1)
            {
                var image = BitmapFrame.Create(new Uri(png[0]), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                Console.WriteLine($"region: {Path.GetFileName(png[0])} {image.PixelWidth}x{image.PixelHeight} (expected 400x300)");
                if (image.PixelWidth != 400 || image.PixelHeight != 300) failures++;
            }
            else
            {
                Console.WriteLine($"region: {png.Length} png files");
                failures++;
            }
            bool editor = FindWindow(null, Path.GetFileName(png.FirstOrDefault() ?? "") + " - Capture") != IntPtr.Zero;
            Console.WriteLine($"editor open: {editor}");
            if (!editor) failures++;
            Keys(VkEscape);  // 편집 창 닫기
            Thread.Sleep(800);

            // 2. GIF, 3. 영상: Enter = 모니터 전체, 2초 뒤 같은 단축키로 멈춤
            foreach (var (name, keys, extension) in new[] { ("GIF", new[] { VkShift, VkSnapshot }, "*.gif"), ("video", new[] { VkControl, VkShift, VkSnapshot }, "*.mp4") })
            {
                Keys(keys);
                Thread.Sleep(1200);
                Keys(VkReturn);
                Thread.Sleep(2500);
                Keys(keys);
                Thread.Sleep(name == "GIF" ? 6000 : 3000);
                var files = Directory.GetFiles(saves, extension);
                long size = files.Length == 1 ? new FileInfo(files[0]).Length : 0;
                Console.WriteLine($"{name}: {files.Length} file(s), {size:N0} bytes");
                if (files.Length != 1 || size < 1000) failures++;
                Thread.Sleep(500);
            }
        }
        finally
        {
            if (!app.HasExited) app.Kill();
        }
        Console.WriteLine(failures == 0 ? "end to end: ok" : $"end to end: {failures} problem(s)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>단축키 창을 실제로 써 봄: GIF 칸 → Ctrl+Alt+G, 영상 칸 → Backspace(없음), 영역 칸 → Ctrl+Alt+G(겹침, 거절) → Esc, 그 뒤 Ctrl+Alt+G 가 GIF 로 오는지</summary>
    public static int HotkeyDialog(string folder)
    {
        Directory.CreateDirectory(folder);
        var settings = new Capture.Services.AppSettings { SaveFolder = folder };
        using var hotkeys = new Capture.Services.Hotkeys(settings);
        hotkeys.Register();
        CaptureMode? pressed = null;
        hotkeys.Pressed += mode => pressed = mode;
        var dialog = new Capture.SettingsWindow(hotkeys, new Capture.CaptureController(settings)) { WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen };
        dialog.Show();
        dialog.Activate();
        Program.Pump(TimeSpan.FromMilliseconds(600), () => false);
        var boxes = Capture.Services.Hotkeys.Modes.ToDictionary(m => m, m => dialog.Rows.Children.OfType<System.Windows.Controls.Grid>()
            .SelectMany(g => g.Children.OfType<System.Windows.Controls.Button>()).First(b => (CaptureMode)b.Tag == m));
        void Click(CaptureMode mode)
        {
            var box = boxes[mode];
            var point = box.PointToScreen(new System.Windows.Point(box.ActualWidth / 2, box.ActualHeight / 2));
            int size = Marshal.SizeOf<Input>();
            SendInput(1, [Move((int)point.X, (int)point.Y)], size);
            Program.Pump(TimeSpan.FromMilliseconds(150), () => false);
            SendInput(2, [new Input { Type = 0, Mouse = new MouseInput { Flags = 0x0002 } }, new Input { Type = 0, Mouse = new MouseInput { Flags = 0x0004 } }], size);
            Program.Pump(TimeSpan.FromMilliseconds(300), () => false);
        }
        void Press(params ushort[] keys)
        {
            Keys(keys);
            Program.Pump(TimeSpan.FromMilliseconds(400), () => false);
        }
        const ushort VkAlt = 0x12, VkG = 0x47, VkBack = 0x08;
        int failures = 0;
        void Expect(bool ok, string what)
        {
            Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
            if (!ok) failures++;
        }

        void Clear(CaptureMode mode)
        {
            var row = (System.Windows.Controls.Grid)boxes[mode].Parent;
            var button = row.Children.OfType<System.Windows.Controls.Button>().Last();
            var point = button.PointToScreen(new System.Windows.Point(button.ActualWidth / 2, button.ActualHeight / 2));
            int size = Marshal.SizeOf<Input>();
            SendInput(1, [Move((int)point.X, (int)point.Y)], size);
            Program.Pump(TimeSpan.FromMilliseconds(150), () => false);
            SendInput(2, [new Input { Type = 0, Mouse = new MouseInput { Flags = 0x0002 } }, new Input { Type = 0, Mouse = new MouseInput { Flags = 0x0004 } }], size);
            Program.Pump(TimeSpan.FromMilliseconds(300), () => false);
        }

        Click(CaptureMode.Gif);
        Press(VkControl, VkAlt, VkG);
        Expect(hotkeys.LabelFor(CaptureMode.Gif) == "Ctrl+Alt+G", "GIF set to " + hotkeys.LabelFor(CaptureMode.Gif));
        Expect(!hotkeys.Failed.Contains(CaptureMode.Gif), "Ctrl+Alt+G registered");
        Click(CaptureMode.Video);
        Press(VkBack);
        Expect(hotkeys.LabelFor(CaptureMode.Video) == "Backspace" && dialog.Message.IsVisible, "Backspace alone is allowed with a note: " + hotkeys.LabelFor(CaptureMode.Video));
        Clear(CaptureMode.Video);
        Expect(hotkeys.Get(CaptureMode.Video).IsNone, "× clears video: " + hotkeys.LabelFor(CaptureMode.Video));
        Click(CaptureMode.Scroll);
        Press(VkControl, VkAlt, VkSnapshot);
        Expect(hotkeys.LabelFor(CaptureMode.Scroll) == "Ctrl+Alt+PrtSc", "scroll set with PrtSc: " + hotkeys.LabelFor(CaptureMode.Scroll));
        Click(CaptureMode.Region);
        Press(VkControl, VkAlt, VkG);
        Expect(hotkeys.LabelFor(CaptureMode.Region) == "Ctrl+Alt+G" && hotkeys.Get(CaptureMode.Gif).IsNone, "GIF's key moved to region: " + dialog.Message.Text);
        Click(CaptureMode.Gif);
        Press(VkEscape);
        Expect(hotkeys.Get(CaptureMode.Gif).IsNone && !hotkeys.Failed.Any(), "Esc keeps it and all are registered again");
        string saved = File.ReadAllText(Path.Combine(Capture.Services.AppSettings.DataDirectory, "settings.json"));
        Expect(saved.Contains("\"Region\"") && saved.Contains("\"Video\""), "saved to settings.json");
        dialog.Close();
        Program.Pump(TimeSpan.FromMilliseconds(300), () => false);
        Press(VkControl, VkAlt, VkG);
        Program.Pump(TimeSpan.FromMilliseconds(500), () => pressed is not null);
        Expect(pressed == CaptureMode.Region, "Ctrl+Alt+G starts region capture: " + pressed);

        return failures == 0 ? 0 : 1;
    }

    /// <summary>사람이 하는 그대로: 큰 창 제목 표시줄의 설정(톱니)을 눌러 설정 창(ShowDialog)을 열고, 영역 칸을 눌러 Ctrl+Alt+K → Esc 로 닫기</summary>
    public static int FromMainWindow(string folder)
    {
        Directory.CreateDirectory(folder);
        var settings = new Capture.Services.AppSettings { SaveFolder = folder };
        using var hotkeys = new Capture.Services.Hotkeys(settings);
        hotkeys.Register();
        var main = new Capture.MainWindow(new Capture.CaptureController(settings), hotkeys) { WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen };
        main.Show();
        main.Activate();
        Program.Pump(TimeSpan.FromMilliseconds(800), () => false);
        var gear = main.SettingsButton;
        var gearPoint = gear.PointToScreen(new System.Windows.Point(gear.ActualWidth / 2, gear.ActualHeight / 2));
        string log = "";
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var driver = Task.Run(() =>
        {
            Click((int)gearPoint.X, (int)gearPoint.Y);
            Thread.Sleep(1000);
            log += "opened=" + GetForegroundTitle() + "; ";
            // 열린 설정 창의 영역 칸 위치 (UI 스레드에서)
            var boxPoint = dispatcher.Invoke(() =>
            {
                var window = System.Windows.Application.Current.Windows.OfType<Capture.SettingsWindow>().First();
                var box = window.Rows.Children.OfType<System.Windows.Controls.Grid>().First().Children.OfType<System.Windows.Controls.Button>().First();
                return box.PointToScreen(new System.Windows.Point(box.ActualWidth / 2, box.ActualHeight / 2));
            });
            Click((int)boxPoint.X, (int)boxPoint.Y);
            Thread.Sleep(500);
            Keys(VkControl, 0x12, 0x4B);  // Ctrl+Alt+K
            Thread.Sleep(800);
            Keys(VkEscape);
            Thread.Sleep(600);
            log += "after Esc=" + GetForegroundTitle() + "; ";
        });
        Program.Pump(TimeSpan.FromSeconds(15), () => driver.IsCompleted);
        Console.WriteLine(log);
        Console.WriteLine("region now: " + hotkeys.LabelFor(CaptureMode.Region) + " · main shows " + main.RegionKey.Text);
        main.ExitApp();
        return hotkeys.LabelFor(CaptureMode.Region) == "Ctrl+Alt+K" && main.RegionKey.Text == "Ctrl+Alt+K" ? 0 : 1;
    }

    private static void Click(int x, int y)
    {
        int size = Marshal.SizeOf<Input>();
        SendInput(1, [Move(x, y)], size);
        Thread.Sleep(200);
        SendInput(2, [new Input { Type = 0, Mouse = new MouseInput { Flags = 0x0002 } }, new Input { Type = 0, Mouse = new MouseInput { Flags = 0x0004 } }], size);
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int count);
    private static string GetForegroundTitle()
    {
        var text = new System.Text.StringBuilder(256);
        GetWindowText(GetForegroundWindow(), text, 256);
        return text.ToString();
    }

    /// <summary>처음 실행: 설정이 비어 있으면 큰 창과 설정 창(안내)이 열리고, 그냥 닫으면 단축키 없이 다시 묻지 않음</summary>
    public static int FirstRun(string exe, string folder)
    {
        string data = Path.Combine(folder, "data");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(data);
        int failures = 0;
        void Expect(bool ok, string what)
        {
            Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
            if (!ok) failures++;
        }
        for (int run = 1; run <= 2; run++)
        {
            var start = new ProcessStartInfo(exe, "--tray") { UseShellExecute = false };
            start.Environment["CAPTURE_DATA_DIR"] = data;
            using var app = Process.Start(start)!;
            try
            {
                Thread.Sleep(3000);
                bool settings = FindWindow(null, "설정 - Capture") != IntPtr.Zero;
                if (run == 1)
                {
                    Expect(settings && FindWindow(null, "Capture") != IntPtr.Zero, "first run opens the main window and 설정 (even with --tray)");
                    Keys(VkEscape);  // 정하지 않고 닫기
                    Thread.Sleep(800);
                    string saved = File.ReadAllText(Path.Combine(data, "settings.json"));
                    Expect(saved.Contains("\"HotkeysChosen\": true"), "closing 설정 remembers it was shown");
                    Expect(!saved.Contains("\"Key\": 44"), "no shortcut was set");
                }
                else Expect(!settings, "second run does not ask again");
            }
            finally
            {
                if (!app.HasExited) app.Kill();
                app.WaitForExit();
            }
        }
        return failures == 0 ? 0 : 1;
    }

    private static void Keys(params ushort[] keys)
    {
        var inputs = new List<Input>();
        foreach (var key in keys) inputs.Add(new Input { Type = 1, Key = new KeyInput { Vk = key } });
        foreach (var key in keys.Reverse()) inputs.Add(new Input { Type = 1, Key = new KeyInput { Vk = key, Flags = 2 } });
        SendInput((uint)inputs.Count, [.. inputs], Marshal.SizeOf<Input>());
    }

    private static Input Move(int x, int y)
    {
        int w = GetSystemMetrics(0) - 1, h = GetSystemMetrics(1) - 1;
        return new Input { Type = 0, Mouse = new MouseInput { X = x * 65535 / w, Y = y * 65535 / h, Flags = 0x8001 } };  // MOVE | ABSOLUTE
    }

    private static void Drag(int x1, int y1, int x2, int y2)
    {
        int size = Marshal.SizeOf<Input>();
        SendInput(1, [Move(x1, y1)], size);
        Thread.Sleep(150);
        SendInput(1, [new Input { Type = 0, Mouse = new MouseInput { Flags = 0x0002 } }], size);  // LEFTDOWN
        for (int i = 1; i <= 12; i++)
        {
            SendInput(1, [Move(x1 + (x2 - x1) * i / 12, y1 + (y2 - y1) * i / 12)], size);
            Thread.Sleep(25);
        }
        Thread.Sleep(150);
        SendInput(1, [new Input { Type = 0, Mouse = new MouseInput { Flags = 0x0004 } }], size);  // LEFTUP
    }
}
