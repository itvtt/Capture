using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Capture.Core;

namespace Capture;

/// <summary>녹화 · 스크롤 캡처 중 막대 (영역 옆). 캡처에 찍히지 않고, 눌러도 녹화 중인 창의 포커스를 뺏지 않음</summary>
public partial class RecordBar : Window
{
    public RecordBar(string state, bool canCancel, Color dot)
    {
        InitializeComponent();
        StateText.Text = state;
        Dot.Fill = new SolidColorBrush(dot);
        CancelButton.Visibility = canCancel ? Visibility.Visible : Visibility.Collapsed;
        SourceInitialized += (_, _) =>
        {
            WindowHelpers.NoActivate(this);
            WindowHelpers.RoundCorners(this);
            if (WindowHelpers.ExcludeFromCapture(this)) { }
            Frame.BorderThickness = new Thickness(0);
        };
    }

    public event Action? StopRequested;
    public event Action? CancelRequested;

    public string StopLabel { set => StopText.Text = value; }

    public void ShowTime(TimeSpan elapsed)
    {
        TimeText.Text = elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"mm\:ss");
        // 0.5초마다 점이 깜박임
        Dot.Opacity = elapsed.Milliseconds < 500 ? 1 : 0.35;
    }

    public void ShowText(string text) => TimeText.Text = text;

    /// <summary>영역 옆에 띄움</summary>
    public void ShowNear(PixelRect region)
    {
        Show();
        WindowHelpers.PlaceNear(this, region);
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopRequested?.Invoke();

    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelRequested?.Invoke();
}

/// <summary>녹화 영역 테두리: 영역 바로 바깥에 얇은 선. 마우스가 지나가고 캡처에 찍히지 않음</summary>
public sealed class RegionFrame : Window
{
    private readonly PixelRect _region;

    public RegionFrame(PixelRect region, Color color)
    {
        _region = region;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -10000;
        Top = -10000;
        Width = 10;
        Height = 10;
        Content = new Border { BorderBrush = new SolidColorBrush(color), BorderThickness = new Thickness(2) };
        SourceInitialized += (_, _) =>
        {
            WindowHelpers.NoActivate(this, clickThrough: true);
            WindowHelpers.ExcludeFromCapture(this);
        };
    }

    public void ShowAround()
    {
        Show();
        const int margin = 3;
        WindowHelpers.PlaceAt(this, new PixelRect(_region.X - margin, _region.Y - margin, _region.Width + margin * 2, _region.Height + margin * 2));
    }
}
