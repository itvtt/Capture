using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Capture.Core;
using CaptureMode = Capture.Core.CaptureMode;
using Capture.Services;
using static Capture.Core.NativeMethods;

namespace Capture;

/// <summary>
/// 큰 창: 캡처 방식 네 칸, 최근 캡처, 저장 위치 · 시작할 때 실행. 닫으면 트레이로 (단축키는 계속)
/// </summary>
public partial class MainWindow : Window
{
    private const int RecentCount = 40;
    private readonly CaptureController _controller;
    private readonly Hotkeys _hotkeys;
    private bool _exiting;

    public MainWindow(CaptureController controller, Hotkeys hotkeys)
    {
        _controller = controller;
        _hotkeys = hotkeys;
        InitializeComponent();
        VersionText.Text = "v" + Version;
        ShowHotkeyState();
        _hotkeys.Changed += ShowHotkeyState;
        _controller.FilesChanged += () => Dispatcher.BeginInvoke(LoadRecent);
        LoadRecent();
    }

    public static string Version
    {
        get
        {
            string text = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";
            return text.Split('+')[0];
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 캡처할 때 바로 사라지게 (닫힘 애니메이션이 찍히지 않게)
        int disabled = 1;
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DWMWA_TRANSITIONS_FORCEDISABLED, ref disabled, sizeof(int));
    }

    /// <summary>트레이 · 다시 실행에서 앞으로</summary>
    public void ShowFront()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    public void ExitApp()
    {
        _exiting = true;
        Close();
    }

    // ───────── 캡처 ─────────

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag && Enum.TryParse<CaptureMode>(tag, out var mode)) _controller.Start(mode);
    }

    // ───────── 최근 ─────────

    private void LoadRecent()
    {
        string folder = _controller.Settings.SaveFolder;
        List<RecentItem> items = [];
        try
        {
            if (Directory.Exists(folder))
                items = [.. new DirectoryInfo(folder).EnumerateFiles()
                    .Where(file => ImageFiles.Extensions.Contains(file.Extension.ToLowerInvariant()))
                    .OrderByDescending(file => file.LastWriteTime)
                    .Take(RecentCount)
                    .Select(file => new RecentItem(file.FullName, file.LastWriteTime))];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        RecentList.ItemsSource = items;
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // 미리 보기는 뒤에서 읽어 채움
        _ = Task.Run(() =>
        {
            foreach (var item in items)
            {
                var thumbnail = ImageFiles.Thumbnail(item.Path, 280);
                if (thumbnail is not null) Dispatcher.BeginInvoke(() => item.Thumbnail = thumbnail);
            }
        });
    }

    private static RecentItem? ItemOf(object sender) => (sender as FrameworkElement)?.Tag as RecentItem;

    private void Recent_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) Open(item);
    }

    private void RecentOpen_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) Open(item);
    }

    /// <summary>그림은 편집 창, GIF · 영상은 기본 프로그램으로</summary>
    private void Open(RecentItem item)
    {
        if (!File.Exists(item.Path))
        {
            LoadRecent();
            return;
        }
        if (item.IsImage) _controller.OpenEditor(item.Path);
        else ImageFiles.Open(item.Path);
    }

    private void RecentCopy_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        if (item.IsImage) _controller.CopyImage(item.Path);
        else _controller.CopyFile(item.Path);
    }

    private async void RecentShare_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) await _controller.ShareAsync(item.Path);
    }

    private void RecentFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ImageFiles.ShowInFolder(item.Path);
    }

    private void RecentDelete_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) _controller.Delete(item.Path);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => ImageFiles.OpenFolder(_controller.Settings.SaveFolder);

    // ───────── 설정 ─────────

    /// <summary>칸 아래 단축키 글자, 그리고 등록하지 못한 단축키 안내 (PrtSc 는 Windows 11 캡처 도구가 먼저 쓰는 경우가 많음)</summary>
    private void ShowHotkeyState()
    {
        RegionKey.Text = _hotkeys.LabelFor(CaptureMode.Region);
        ScrollKey.Text = _hotkeys.LabelFor(CaptureMode.Scroll);
        GifKey.Text = _hotkeys.LabelFor(CaptureMode.Gif);
        VideoKey.Text = _hotkeys.LabelFor(CaptureMode.Video);
        var failed = _hotkeys.Failed;
        if (failed.Count == 0)
        {
            HotkeyWarning.Visibility = Visibility.Collapsed;
            return;
        }
        bool printScreen = failed.Any(mode => _hotkeys.Get(mode) == new HotkeyBinding(0, VK_SNAPSHOT));
        HotkeyWarningText.Text = printScreen
            ? "PrtSc 를 Windows 캡처 도구가 쓰고 있습니다. Windows 설정 → 접근성 → 키보드에서 \"Print Screen 키로 화면 캡처 열기\"를 끄거나, 설정에서 단축키를 바꾸세요."
            : string.Join(", ", failed.Select(mode => _hotkeys.LabelFor(mode))) + " 를 다른 프로그램이 쓰고 있습니다. 설정에서 단축키를 바꾸세요.";
        WindowsSettingsButton.Visibility = printScreen ? Visibility.Visible : Visibility.Collapsed;
        HotkeyWarning.Visibility = Visibility.Visible;
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>설정 창: 단축키 · 저장 위치 · 시작할 때 실행 (트레이 메뉴에서도)</summary>
    public void OpenSettings()
    {
        if (!IsVisible) ShowFront();
        new SettingsWindow(_hotkeys, _controller) { Owner = this }.ShowDialog();
    }

    private void KeyboardSettings_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:easeofaccess-keyboard") { UseShellExecute = true })?.Dispose(); }
        catch (Win32Exception) { }
    }

    // 설정을 바꾸고 돌아오면 단축키를 다시 등록해 봄
    private void Window_Activated(object? sender, EventArgs e)
    {
        if (_hotkeys.Failed.Count == 0) return;
        _hotkeys.Register();
        ShowHotkeyState();
    }

    // ───────── 창 ─────────

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        e.Cancel = true;
        Hide();
        if (!_controller.Settings.TrayHintShown)
        {
            _controller.Settings.TrayHintShown = true;
            _controller.Settings.Save();
            Toast.Notify("트레이에서 계속 실행됩니다", "PrtSc 로 언제든 캡처하세요. 끝내려면 트레이 아이콘 → 종료.", seconds: 7);
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreGlyph.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
        BorderThickness = maximized ? new Thickness(7) : new Thickness(0);
    }
}

/// <summary>최근 목록의 한 칸</summary>
public sealed class RecentItem(string path, DateTime time) : INotifyPropertyChanged
{
    private ImageSource? _thumbnail;

    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    public bool IsImage => !ImageFiles.IsGif(Path) && !ImageFiles.IsVideo(Path);
    public string? Badge => ImageFiles.IsGif(Path) ? "GIF" : ImageFiles.IsVideo(Path) ? "영상" : null;
    public Visibility BadgeVisibility => Badge is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility VideoIconVisibility => ImageFiles.IsVideo(Path) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>오늘 14:30 · 어제 09:12 · 11월 3일 18:00</summary>
    public string When
    {
        get
        {
            var today = DateTime.Today;
            string day = time.Date == today ? "오늘" : time.Date == today.AddDays(-1) ? "어제"
                : time.Year == today.Year ? $"{time.Month}월 {time.Day}일" : $"{time.Year}. {time.Month}. {time.Day}.";
            return $"{day} {time:HH:mm}";
        }
    }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
