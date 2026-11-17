using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Capture.Services;
using CaptureMode = Capture.Core.CaptureMode;

namespace Capture;

/// <summary>
/// 단축키 바꾸기: 모드마다 칸 하나. 칸을 누르면 키를 기다리고(그동안 이 앱의 단축키는 모두 놓음), 누른 조합으로 바꿔 바로 등록.
/// 다른 모드와 같거나 다른 프로그램이 쓰는 조합이면 알려 주고 그대로 둠
/// </summary>
public partial class HotkeyWindow : Window
{
    private static readonly Brush Waiting = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
    private static readonly Brush Normal = new SolidColorBrush(Color.FromRgb(0x3A, 0x3B, 0x42));
    private readonly Hotkeys _hotkeys;
    private readonly Dictionary<CaptureMode, Button> _boxes = [];
    private CaptureMode? _waiting;

    public HotkeyWindow(Hotkeys hotkeys)
    {
        _hotkeys = hotkeys;
        InitializeComponent();
        foreach (var mode in Hotkeys.Modes)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new TextBlock { Text = Hotkeys.Name(mode), FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
            var box = new Button { Style = (Style)FindResource("KeyBoxStyle"), Tag = mode };
            box.Click += (_, _) => StartWaiting(mode);
            Grid.SetColumn(box, 1);
            row.Children.Add(box);
            Rows.Children.Add(row);
            _boxes[mode] = box;
        }
        ShowAll();
    }

    private void ShowAll()
    {
        var failed = _hotkeys.Failed;
        foreach (var (mode, box) in _boxes)
        {
            bool waiting = _waiting == mode;
            box.BorderBrush = waiting ? Waiting : Normal;
            box.Content = waiting ? "새 키를 누르세요…" : _hotkeys.LabelFor(mode) + (failed.Contains(mode) ? "  · 다른 프로그램이 쓰는 중" : "");
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
        if (modifiers == ModifierKeys.None && key is Key.Back or Key.Delete)
        {
            _hotkeys.Set(mode, default);
            StopWaiting();
            return;
        }
        if (HotkeyBinding.FromKey(key, modifiers, out string? problem) is not { } binding)
        {
            Say(problem!);
            return;
        }
        if (Hotkeys.Modes.Where(other => other != mode && _hotkeys.Get(other) == binding).Cast<CaptureMode?>().FirstOrDefault() is { } taken)
        {
            Say($"{binding} 는 {Hotkeys.Name(taken)} 단축키입니다. 다른 키를 누르세요.");
            return;
        }
        var previous = _hotkeys.Get(mode);
        _hotkeys.Set(mode, binding);
        StopWaiting();
        if (_hotkeys.Failed.Contains(mode))
        {
            // 다른 프로그램이 쓰는 조합: 앞의 것으로 되돌림
            _hotkeys.Set(mode, previous);
            ShowAll();
            Say(binding.Modifiers == 0 && binding.Key == Core.NativeMethods.VK_SNAPSHOT
                ? "PrtSc 를 Windows 캡처 도구가 쓰고 있습니다. 설정 → 접근성 → 키보드에서 \"Print Screen 키로 화면 캡처 열기\"를 끄세요."
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
        _hotkeys.ResetDefaults();
        _hotkeys.Resume();
        Message.Visibility = Visibility.Collapsed;
        ShowAll();
        if (_hotkeys.Failed.Count > 0) Say(string.Join(", ", _hotkeys.Failed.Select(Hotkeys.Name)) + " 단축키를 다른 프로그램이 쓰고 있습니다.");
    }

    // 다른 창으로 가면 키 기다리기를 그만둠 (단축키가 계속 꺼져 있지 않게)
    private void Window_Deactivated(object? sender, EventArgs e) => StopWaiting();

    private void Window_Closed(object? sender, EventArgs e) => StopWaiting();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
