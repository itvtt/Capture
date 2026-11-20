using System.Windows;
using Capture.Core;
using Capture.Services;
using Forms = System.Windows.Forms;

namespace Capture;

/// <summary>
/// Capture.exe          큰 창을 열고 트레이에 상주 (단축키는 설정에서 정한 것만, 처음 실행하면 설정이 열림)
/// Capture.exe --tray   트레이로만 시작 (Windows 시작할 때)
/// 하나만 실행: 이미 실행 중이면 그 큰 창을 앞으로
/// </summary>
public partial class App : Application
{
    private const string ShowSignalName = @"Local\Capture-show";
    private Mutex? _single;
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _showWait;
    private Forms.NotifyIcon? _tray;
    private Hotkeys? _hotkeys;
    private CaptureController? _controller;
    private MainWindow? _main;

    /// <summary>Windows 종료 · 런처의 업데이트(Restart Manager)로 끝나는 중: 창을 숨기지 않고 그냥 닫음</summary>
    public static bool Ending { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _single = new Mutex(true, @"Local\Capture-single", out bool first);
        if (!first)
        {
            try
            {
                using var show = EventWaitHandle.OpenExisting(ShowSignalName);
                show.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
            Shutdown();
            return;
        }

        SessionEnding += (_, _) => Ending = true;
        var settings = AppSettings.Load();
        // 처음 한 번: Windows 시작할 때 실행 (PrtSc 가 늘 되게). 큰 창 아래에서 끌 수 있음.
        // 설치된 사본(%LOCALAPPDATA%\Programs\Capture)일 때만: bin 에서 실행한 개발 빌드는 등록하지 않음
        bool installed = ILuncher.AppKit.AppUpdater.IsInstalledCopy("Capture");
        if (!settings.StartupConfigured && installed)
        {
            settings.StartupConfigured = true;
            settings.Save();
            try { StartWithWindows.Set(true); }
            catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        else if (installed) StartWithWindows.Refresh();

        _controller = new CaptureController(settings);
        _hotkeys = new Hotkeys(settings);
        _hotkeys.Register();
        _hotkeys.Pressed += mode => _controller.Start(mode);
        _main = new MainWindow(_controller, _hotkeys);
        MainWindow = _main;
        CreateTray();

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => Dispatcher.BeginInvoke(() => _main.ShowFront()), null, -1, executeOnlyOnce: false);

        AppSettings.CleanTemp();
        if (!settings.SetupDone)
        {
            // 처음 실행(처음 설정): 큰 창과 설정을 열어 단축키 · 저장 방식을 정하게 함 (정하지 않으면 단축키 없음 · 자동 저장 안 함)
            _main.Show();
            Dispatcher.BeginInvoke(() => _main.OpenSettings(firstRun: true));
        }
        else if (!e.Args.Any(arg => string.Equals(arg, "--tray", StringComparison.OrdinalIgnoreCase))) _main.Show();

        // 사용 시간 · 캡처 건수 집계 (AppKit\AppUsage.cs → API 서버, ImPerformance 성과 계산용)
        ILuncher.AppKit.AppUsage.Start();
        // i-luncher AppKit: 설치된 사본이면 시작할 때 GitHub 릴리스를 확인해 스스로 업데이트 (AppKit\AppUpdater.cs)
        ILuncher.AppKit.AppUpdater.StartBackground(ready => Dispatcher.BeginInvoke(() =>
        {
            if (MessageBox.Show($"새 버전 {ready.Update.Version}을(를) 받았습니다. (지금 {ready.CurrentVersion})\n\n지금 다시 시작할까요? [아니요]를 누르면 종료할 때 적용됩니다.",
                    "Capture 업데이트", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                ready.RestartNow();
                Quit();
            }
        }));
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        var items = new Dictionary<CaptureMode, Forms.ToolStripMenuItem>();
        foreach (var mode in Hotkeys.Modes)
        {
            string name = mode switch { CaptureMode.Scroll => "스크롤 캡처", CaptureMode.Gif => "GIF 녹화", CaptureMode.Video => "영상 녹화", _ => "영역 캡처" };
            items[mode] = new Forms.ToolStripMenuItem(name, null, (_, _) => _controller!.Start(mode));
            menu.Items.Add(items[mode]);
        }
        // 메뉴의 단축키 글자는 바꿀 때마다
        void ShowKeys()
        {
            foreach (var (mode, item) in items) item.ShortcutKeyDisplayString = _hotkeys!.Get(mode).IsNone ? "" : _hotkeys.LabelFor(mode);
            if (_tray is not null) _tray.Text = _hotkeys!.Get(CaptureMode.Region).IsNone ? "Capture" : $"Capture ({_hotkeys.LabelFor(CaptureMode.Region)})";
        }
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("설정…", null, (_, _) => _main!.OpenSettings());
        menu.Items.Add("저장 폴더 열기", null, (_, _) =>
        {
            if (_controller!.Settings.HasFolder) ImageFiles.OpenFolder(_controller.Settings.SaveFolder);
            else Toast.Notify("저장 폴더를 아직 고르지 않았습니다", "설정에서 자동 저장을 켜고 폴더를 고르세요.", seconds: 5);
        });
        menu.Items.Add("창 열기", null, (_, _) => _main!.ShowFront());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => Quit());
        _tray = new Forms.NotifyIcon
        {
            Text = "Capture",
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
            Visible = true
        };
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) _main!.ShowFront(); };
        ShowKeys();
        _hotkeys!.Changed += ShowKeys;
    }

    private static System.Drawing.Icon LoadIcon()
    {
        var resource = GetResourceStream(new Uri("pack://application:,,,/Assets/Capture.ico"));
        if (resource is null) return System.Drawing.SystemIcons.Application;
        using var stream = resource.Stream;
        return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    private void Quit()
    {
        _main?.ExitApp();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null);
        _showSignal?.Dispose();
        _hotkeys?.Dispose();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _single?.Dispose();
        base.OnExit(e);
    }
}
