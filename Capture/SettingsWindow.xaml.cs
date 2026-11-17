using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Capture.Services;
using CaptureMode = Capture.Core.CaptureMode;

namespace Capture;

/// <summary>
/// 설정: 단축키 · 저장(자동 저장 · 폴더) · Windows 시작할 때 실행. 바꾸면 바로 적용 · 저장 (저장 단추 없음).
/// 단축키는 처음에 하나도 없고 자동 저장도 꺼져 있음. 처음 실행하면 이 창이 안내와 함께 열려 사용자가 정함 (처음 설정)
/// 단축키: 모드마다 칸 하나. 칸을 누르면 키를 기다리고(그동안 이 앱의 단축키는 모두 놓음), 누른 조합으로 바꿔 바로 등록.
/// 어떤 키든 됨 (글자 키처럼 글을 쓸 때 누르는 키는 알려 주기만). 다른 모드가 쓰던 키면 이쪽으로 옮김.
/// 다른 프로그램이 쓰는 조합이면 알려 주고 그대로 둠
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly Brush Waiting = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
    private static readonly Brush Normal = new SolidColorBrush(Color.FromRgb(0x3A, 0x3B, 0x42));
    private readonly Hotkeys _hotkeys;
    private readonly CaptureController _controller;
    private readonly Dictionary<CaptureMode, Button> _boxes = [];
    private CaptureMode? _waiting;

    public SettingsWindow(Hotkeys hotkeys, CaptureController controller, bool firstRun = false)
    {
        _hotkeys = hotkeys;
        _controller = controller;
        InitializeComponent();
        foreach (var mode in Hotkeys.Modes)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = Hotkeys.Name(mode), FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
            var box = new Button { Style = (Style)FindResource("KeyBoxStyle"), Tag = mode, ToolTip = "눌러서 바꾸기" };
            box.Click += (_, _) => StartWaiting(mode);
            Grid.SetColumn(box, 1);
            row.Children.Add(box);
            // 단축키 없애기
            var clear = new Button
            {
                Style = (Style)FindResource("IconButtonStyle"),
                Margin = new Thickness(6, 0, 0, 0),
                ToolTip = "단축키 없애기",
                Tag = mode,
                Content = new System.Windows.Shapes.Path
                {
                    Width = 10, Height = 10, Data = Geometry.Parse("M 0.5,0.5 L 9.5,9.5 M 9.5,0.5 L 0.5,9.5"),
                    Stroke = new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xA8)), StrokeThickness = 1.4
                }
            };
            clear.Click += (_, _) =>
            {
                StopWaiting();
                _hotkeys.Set(mode, default);
                Message.Visibility = Visibility.Collapsed;
                ShowAll();
            };
            Grid.SetColumn(clear, 2);
            row.Children.Add(clear);
            Rows.Children.Add(row);
            _boxes[mode] = box;
        }
        ShowAll();
        AutoSaveBox.IsChecked = _controller.Settings.AutoSave;
        ShowSave();
        StartupBox.IsChecked = StartWithWindows.IsEnabled;
        Intro.Visibility = firstRun ? Visibility.Visible : Visibility.Collapsed;
        if (firstRun)
        {
            Title = "처음 설정 - Capture";
            Heading.Text = "처음 설정";
            DoneButton.Content = "완료";
        }
    }

    // ───────── 단축키 ─────────

    private void ShowAll()
    {
        var failed = _hotkeys.Failed;
        foreach (var (mode, box) in _boxes)
        {
            bool waiting = _waiting == mode;
            box.BorderBrush = waiting ? Waiting : Normal;
            box.Content = waiting ? "원하는 키를 누르세요…" : _hotkeys.LabelFor(mode) + (failed.Contains(mode) ? "  · 사용 중" : "");
            box.Foreground = waiting ? (Brush)FindResource("MutedTextBrush") : failed.Contains(mode) ? (Brush)FindResource("WarningBrush") : (Brush)FindResource("TextBrush");
        }
    }

    private void StartWaiting(CaptureMode mode)
    {
        if (_waiting is null) _hotkeys.Suspend();
        _waiting = mode;
        Message.Visibility = Visibility.Collapsed;
        ShowAll();
        Keyboard.Focus(_boxes[mode]);
    }

    private void StopWaiting()
    {
        if (_waiting is null) return;
        _waiting = null;
        _hotkeys.Resume();
        ShowAll();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_waiting is not { } mode)
        {
            if (e.Key == Key.Escape) Close();
            return;
        }
        e.Handled = true;
        var key = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, _ => e.Key };
        if (key == Key.Snapshot) return;  // PrtSc 는 놓을 때만 옴 (PreviewKeyUp)
        Take(mode, key);
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (_waiting is not { } mode) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key != Key.Snapshot) return;
        e.Handled = true;
        Take(mode, key);
    }

    private void Take(CaptureMode mode, Key key)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
            return;  // 수정 키만 누름: 나머지 키를 기다림
        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None && key == Key.Escape)
        {
            StopWaiting();
            return;
        }
        if (HotkeyBinding.FromKey(key, modifiers, out string? warning) is not { } binding) return;
        // 다른 모드가 쓰던 키면 이쪽으로 옮기고 그 모드는 단축키 없음
        string? moved = null;
        foreach (var other in Hotkeys.Modes.Where(other => other != mode && _hotkeys.Get(other) == binding).ToList())
        {
            _hotkeys.Set(other, default);
            moved = $"{Hotkeys.Name(other)} 단축키였던 {binding} 를 옮겼습니다. {Hotkeys.Name(other)} 는 이제 단축키가 없습니다.";
        }
        var previous = _hotkeys.Get(mode);
        _hotkeys.Set(mode, binding);
        StopWaiting();
        Message.Visibility = Visibility.Collapsed;
        if (moved is not null || warning is not null) Say(string.Join(" ", new[] { moved, warning }.Where(text => text is not null)));
        if (_hotkeys.Failed.Contains(mode))
        {
            // 다른 프로그램이 쓰는 조합: 앞의 것으로 되돌림
            _hotkeys.Set(mode, previous);
            ShowAll();
            Say(binding.Modifiers == 0 && binding.Key == Core.NativeMethods.VK_SNAPSHOT
                ? "PrtSc 를 Windows 캡처 도구가 쓰고 있습니다. Windows 설정 → 접근성 → 키보드에서 \"Print Screen 키로 화면 캡처 열기\"를 끄세요."
                : $"{binding} 는 다른 프로그램이 쓰고 있어 쓸 수 없습니다.");
        }
    }

    private void Say(string text)
    {
        Message.Text = text;
        Message.Visibility = Visibility.Visible;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _waiting = null;
        _hotkeys.ClearAll();
        _hotkeys.Resume();
        Message.Visibility = Visibility.Collapsed;
        ShowAll();
    }

    // ───────── 저장 · 일반 ─────────

    private void ShowSave()
    {
        bool on = _controller.Settings.AutoSave;
        FolderRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ManualHint.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        FolderText.Text = _controller.Settings.SaveFolder;
        FolderText.ToolTip = _controller.Settings.SaveFolder;
    }

    // 자동 저장을 켜면 폴더부터 고름 (고르지 않으면 다시 꺼짐)
    private void AutoSaveBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        StopWaiting();
        bool on = AutoSaveBox.IsChecked == true;
        if (on && !_controller.Settings.HasFolder && !ChooseFolder())
        {
            AutoSaveBox.IsChecked = false;
            return;
        }
        _controller.Settings.AutoSave = on;
        _controller.Settings.Save();
        ShowSave();
        _controller.NotifySaved();
    }

    private void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        StopWaiting();
        if (ChooseFolder()) ShowSave();
    }

    /// <summary>저장할 폴더를 고름. 고르면 바로 저장</summary>
    private bool ChooseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "캡처를 저장할 폴더", InitialDirectory = _controller.Settings.DialogFolder };
        if (dialog.ShowDialog(this) != true) return false;
        _controller.Settings.SaveFolder = dialog.FolderName;
        _controller.Settings.Save();
        return true;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_controller.Settings.HasFolder) ImageFiles.OpenFolder(_controller.Settings.SaveFolder);
    }

    private void StartupBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        try { StartWithWindows.Set(StartupBox.IsChecked == true); }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException) { Toast.Error(error.Message); }
    }

    // ───────── 창 ─────────

    // 다른 창으로 가면 키 기다리기를 그만둠 (단축키가 계속 꺼져 있지 않게)
    private void Window_Deactivated(object? sender, EventArgs e) => StopWaiting();

    private void Window_Closed(object? sender, EventArgs e)
    {
        StopWaiting();
        // 한 번 보았으면 다음부터 처음 설정 없이 (정하지 않은 단축키는 없음, 자동 저장은 고른 대로)
        if (!_controller.Settings.SetupDone)
        {
            _controller.Settings.SetupDone = true;
            _controller.Settings.Save();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
