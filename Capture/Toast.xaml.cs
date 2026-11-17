using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Capture.Core;

namespace Capture;

/// <summary>오른쪽 아래 알림. 한 번에 하나 (새 알림이 오면 앞의 것은 닫힘)</summary>
public partial class Toast : Window
{
    private static Toast? _current;
    private readonly DispatcherTimer _timer;

    internal Toast(string title, string detail, ImageSource? thumbnail, Geometry? icon, IEnumerable<(string Label, Action Run)> actions, TimeSpan duration)
    {
        InitializeComponent();
        TitleText.Text = title;
        DetailText.Text = detail;
        DetailText.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (thumbnail is not null) Thumb.Source = thumbnail;
        else if (icon is not null)
        {
            ThumbIcon.Data = icon;
            ThumbIcon.Visibility = Visibility.Visible;
        }
        else ThumbFrame.Visibility = Visibility.Collapsed;
        bool first = true;
        foreach (var (label, run) in actions)
        {
            var button = new Button
            {
                Content = label,
                Style = (Style)FindResource(first ? "PrimaryPillButtonStyle" : "PillButtonStyle"),
                Height = 30,
                Margin = new Thickness(0, 0, 6, 0)
            };
            button.Click += (_, _) =>
            {
                Close();
                run();
            };
            Actions.Children.Add(button);
            first = false;
        }
        Actions.Visibility = Actions.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _timer = new DispatcherTimer { Interval = duration };
        _timer.Tick += (_, _) => Close();
        SourceInitialized += (_, _) =>
        {
            WindowHelpers.NoActivate(this);
            WindowHelpers.RoundCorners(this);
            Frame.BorderThickness = new Thickness(0);
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && FindButton(source) is null && Clicked is { } click)
            {
                Close();
                click();
            }
        };
    }

    /// <summary>알림 자체를 누르면 (버튼 말고)</summary>
    private Action? Clicked { get; set; }

    public static void Notify(string title, string detail = "", ImageSource? thumbnail = null, Geometry? icon = null,
        IEnumerable<(string Label, Action Run)>? actions = null, Action? clicked = null, double seconds = 6)
    {
        _current?.Close();
        var toast = new Toast(title, detail, thumbnail, icon, actions ?? [], TimeSpan.FromSeconds(seconds)) { Clicked = clicked };
        if (clicked is not null) toast.Cursor = Cursors.Hand;
        _current = toast;
        toast.Closed += (_, _) => { if (_current == toast) _current = null; };
        toast.Show();
        toast.PlaceBottomRight();
        toast._timer.Start();
    }

    public static void Error(string message) => Notify("문제가 생겼습니다", message, seconds: 9);

    private void PlaceBottomRight()
    {
        var cursor = Screens.Cursor();
        var work = Screens.FromPoint(cursor.X, cursor.Y).WorkArea;
        WindowHelpers.MoveTo(this, work.X + 8, work.Y + 8);  // 그 모니터 배율로 크기를 맞춘 뒤
        UpdateLayout();
        var (w, h) = WindowHelpers.PixelSize(this);
        WindowHelpers.MoveTo(this, work.Right - w - 16, work.Bottom - h - 16);
    }

    private static Button? FindButton(DependencyObject? element)
    {
        while (element is not null and not Window)
        {
            if (element is Button button) return button;
            element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e) => _timer.Stop();

    private void Window_MouseLeave(object sender, MouseEventArgs e) => _timer.Start();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
