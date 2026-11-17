using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Capture.Core;
using CaptureMode = Capture.Core.CaptureMode;
using Path = System.Windows.Shapes.Path;
using Capture.Services;

namespace Capture;

/// <summary>
/// 캡처 편집: 사각형 · 화살표 · 펜 · 형광펜 · 글자 · 모자이크 · 자르기, 되돌리기. 복사 · File Shared 공유.
/// 캡처는 이미 저장되어 있고, 고친 내용은 닫을 때(또는 Ctrl+S · 공유할 때) 같은 파일에 저장
/// </summary>
public partial class EditorWindow : Window
{
    private enum Tool { Rect, Arrow, Pen, Marker, Text, Mosaic, Crop }

    private static readonly (Color Color, string Name)[] Palette =
    [
        (Color.FromRgb(0xFF, 0x3B, 0x30), "빨강"), (Color.FromRgb(0xFF, 0x9F, 0x0A), "주황"), (Color.FromRgb(0xFF, 0xD6, 0x0A), "노랑"),
        (Color.FromRgb(0x30, 0xD1, 0x58), "초록"), (Color.FromRgb(0x0A, 0x84, 0xFF), "파랑"), (Colors.Black, "검정"), (Colors.White, "흰색"),
    ];
    // 다음 편집 창도 같은 색 · 굵기로
    private static Color _lastColor = Palette[0].Color;
    private static int _lastSize = 1;

    private readonly CaptureController _controller;
    private readonly string _path;
    private BitmapSource _base;
    private PixelFrame? _basePixels;
    private Tool _tool = Tool.Rect, _toolBeforeCrop = Tool.Rect;
    private Color _color = _lastColor;
    private int _size = _lastSize;
    private readonly Stack<IEdit> _undo = new(), _redo = new();
    private bool _dirty, _deleted, _drawing;
    private Point _from;
    private FrameworkElement? _active;
    private Polyline? _stroke;
    private TextBox? _typing;
    private double _zoom = 1;
    private bool _fitted = true;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public EditorWindow(CaptureController controller, string path, BitmapSource image)
    {
        _controller = controller;
        _path = path;
        _base = image;
        int size = _lastSize;  // 굵기 단추의 처음 값(보통)이 InitializeComponent 중에 덮어쓰지 않게
        InitializeComponent();
        FileText.Text = System.IO.Path.GetFileName(path);
        Title = FileText.Text + " - Capture";
        for (int i = 0; i < Palette.Length; i++)
        {
            var (color, name) = Palette[i];
            var swatch = new RadioButton
            {
                Style = (Style)FindResource("SwatchStyle"),
                GroupName = "Color",
                Background = new SolidColorBrush(color),
                ToolTip = name,
                Tag = color,
                IsChecked = color == _color
            };
            swatch.Checked += (_, _) => _color = _lastColor = (Color)swatch.Tag;
            Swatches.Children.Add(swatch);
        }
        (size == 0 ? ThinSize : size == 2 ? ThickSize : MediumSize).IsChecked = true;
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            ShowSize();
        };
        SetBase(image);
        SizeToImage();
        Loaded += (_, _) => Fit();
    }

    // ───────── 판 · 확대 ─────────

    private double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>선 굵기 등은 화면 배율에 맞춰 (화소 단위)</summary>
    private double Unit => Math.Max(1, DpiScale);
    private double StrokeWidth => new[] { 2.0, 4, 7 }[_size] * Unit;
    private double MarkerWidth => new[] { 12.0, 20, 32 }[_size] * Unit;
    private double TextSize => new[] { 16.0, 24, 36 }[_size] * Unit;
    private int MosaicBlock => (int)Math.Round(new[] { 6.0, 10, 16 }[_size] * Unit);

    private void SetBase(BitmapSource image)
    {
        _base = image;
        _basePixels = null;
        Base.Source = image;
        Sheet.Width = image.PixelWidth;
        Sheet.Height = image.PixelHeight;
        Ink.Width = image.PixelWidth;
        Ink.Height = image.PixelHeight;
        ShowSize();
        if (_fitted && IsLoaded) Fit();
    }

    /// <summary>처음 창 크기: 그림이 실제 크기로 들어가게, 화면의 85% 까지</summary>
    private void SizeToImage()
    {
        var work = SystemParameters.WorkArea;
        double scale = Math.Max(1, VisualTreeHelper.GetDpi(Application.Current.MainWindow ?? this).DpiScaleX);
        Width = Math.Clamp(_base.PixelWidth / scale + 48 + 24, MinWidth, work.Width * 0.85);
        Height = Math.Clamp(_base.PixelHeight / scale + 44 + 52 + 28 + 48 + 24, MinHeight, work.Height * 0.85);
    }

    private void Fit()
    {
        double natural = 1 / DpiScale;
        double width = Math.Max(40, Viewer.ActualWidth - 48 - 14), height = Math.Max(40, Viewer.ActualHeight - 48 - 14);
        double w = _base.PixelWidth, h = _base.PixelHeight;
        // 아주 긴 그림(스크롤 캡처)은 너비에 맞추고 아래로 스크롤
        double fit = h / w > 2.5 ? width / w : Math.Min(width / w, height / h);
        SetZoom(Math.Min(natural, fit), fitted: true);
    }

    private void SetZoom(double zoom, bool fitted)
    {
        _zoom = Math.Clamp(zoom, 0.02, 16);
        _fitted = fitted;
        Zoomer.LayoutTransform = new ScaleTransform(_zoom, _zoom);
        RenderOptions.SetBitmapScalingMode(Base, _zoom * DpiScale >= 0.999 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        ZoomText.Text = $"{Math.Round(_zoom * DpiScale * 100)}%";
    }

    private void Viewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_fitted && IsLoaded) Fit();
    }

    private void Viewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true;
        SetZoom(_zoom * Math.Pow(1.15, e.Delta / 120.0), fitted: false);
    }

    private void ZoomText_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        double natural = 1 / DpiScale;
        if (Math.Abs(_zoom - natural) < 0.001) Fit();
        else SetZoom(natural, fitted: false);
    }

    private void ShowSize()
    {
        StatusText.Text = $"{_base.PixelWidth:N0} × {_base.PixelHeight:N0}";
    }

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    // ───────── 도구 ─────────

    private void Tool_Checked(object sender, RoutedEventArgs e)
    {
        CommitText();
        var tool = sender == ArrowTool ? Tool.Arrow : sender == PenTool ? Tool.Pen : sender == MarkerTool ? Tool.Marker
            : sender == TextTool ? Tool.Text : sender == MosaicTool ? Tool.Mosaic : sender == CropTool ? Tool.Crop : Tool.Rect;
        if (tool == Tool.Crop && _tool != Tool.Crop) _toolBeforeCrop = _tool;
        _tool = tool;
        if (Ink is not null) Ink.Cursor = tool == Tool.Text ? Cursors.IBeam : Cursors.Cross;
    }

    private void SelectTool(Tool tool) => (tool switch
    {
        Tool.Arrow => ArrowTool, Tool.Pen => PenTool, Tool.Marker => MarkerTool, Tool.Text => TextTool,
        Tool.Mosaic => MosaicTool, Tool.Crop => CropTool, _ => RectTool
    }).IsChecked = true;

    private void Size_Checked(object sender, RoutedEventArgs e) =>
        _size = _lastSize = sender == ThinSize ? 0 : sender == ThickSize ? 2 : 1;

    private Point SheetPoint(MouseEventArgs e)
    {
        var p = e.GetPosition(Sheet);
        return new Point(Math.Clamp(p.X, 0, Sheet.Width), Math.Clamp(p.Y, 0, Sheet.Height));
    }

    private void Ink_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_typing is not null)
        {
            CommitText();
            e.Handled = true;
            return;
        }
        _from = SheetPoint(e);
        var brush = new SolidColorBrush(_color);
        switch (_tool)
        {
            case Tool.Text:
                BeginText(_from);
                e.Handled = true;
                return;
            case Tool.Rect:
                _active = new Rectangle { Stroke = brush, StrokeThickness = StrokeWidth, IsHitTestVisible = false };
                Canvas.SetLeft(_active, _from.X);
                Canvas.SetTop(_active, _from.Y);
                Ink.Children.Add(_active);
                break;
            case Tool.Arrow:
                _active = new Path { Fill = brush, IsHitTestVisible = false };
                Ink.Children.Add(_active);
                break;
            case Tool.Pen:
            case Tool.Marker:
                bool marker = _tool == Tool.Marker;
                _stroke = new Polyline
                {
                    Stroke = brush,
                    StrokeThickness = marker ? MarkerWidth : StrokeWidth,
                    Opacity = marker ? 0.4 : 1,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = marker ? PenLineCap.Square : PenLineCap.Round,
                    StrokeEndLineCap = marker ? PenLineCap.Square : PenLineCap.Round,
                    IsHitTestVisible = false
                };
                _stroke.Points.Add(_from);
                _active = _stroke;
                Ink.Children.Add(_stroke);
                break;
            default:
                ShowMarquee(new Rect(_from, _from));
                break;
        }
        _drawing = true;
        Ink.CaptureMouse();
        e.Handled = true;
    }

    private void Ink_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_drawing) return;
        var p = SheetPoint(e);
        if (Keyboard.Modifiers == ModifierKeys.Shift && _tool is Tool.Rect or Tool.Mosaic or Tool.Crop)
        {
            // Shift: 정사각형
            double side = Math.Max(Math.Abs(p.X - _from.X), Math.Abs(p.Y - _from.Y));
            p = new Point(_from.X + Math.Sign(p.X - _from.X) * side, _from.Y + Math.Sign(p.Y - _from.Y) * side);
        }
        var rect = new Rect(_from, p);
        switch (_tool)
        {
            case Tool.Rect when _active is not null:
                Canvas.SetLeft(_active, rect.X);
                Canvas.SetTop(_active, rect.Y);
                _active.Width = rect.Width;
                _active.Height = rect.Height;
                break;
            case Tool.Arrow when _active is Path path:
                path.Data = ArrowGeometry(_from, p, StrokeWidth);
                break;
            case Tool.Pen or Tool.Marker when _stroke is not null:
                var last = _stroke.Points[^1];
                if (Math.Abs(last.X - p.X) + Math.Abs(last.Y - p.Y) >= 1) _stroke.Points.Add(p);
                break;
            case Tool.Mosaic or Tool.Crop:
                ShowMarquee(rect);
                break;
        }
    }

    private void Ink_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_drawing) return;
        _drawing = false;
        Ink.ReleaseMouseCapture();
        var p = SheetPoint(e);
        var rect = new Rect(_from, p);
        bool tiny = rect.Width < 3 && rect.Height < 3;
        switch (_tool)
        {
            case Tool.Rect or Tool.Arrow when _active is not null:
                if (tiny || (_tool == Tool.Rect && (_active.Width < 2 || _active.Height < 2))) Ink.Children.Remove(_active);
                else Push(new AddEdit(Ink, _active));
                break;
            case Tool.Pen or Tool.Marker when _stroke is not null:
                if (_stroke.Points.Count == 1) _stroke.Points.Add(new Point(_from.X + 0.5, _from.Y));  // 점 하나
                Push(new AddEdit(Ink, _stroke));
                break;
            case Tool.Mosaic or Tool.Crop:
                Marquee.Visibility = Visibility.Collapsed;
                var area = ToPixels(Marquee.Tag is Rect shown ? shown : rect);
                if (area.Width >= 4 && area.Height >= 4)
                {
                    if (_tool == Tool.Mosaic) ApplyMosaic(area);
                    else ApplyCrop(area);
                }
                break;
        }
        _active = null;
        _stroke = null;
    }

    private void ShowMarquee(Rect rect)
    {
        Marquee.Tag = rect;
        Marquee.Margin = new Thickness(rect.X, rect.Y, 0, 0);
        Marquee.Width = Math.Max(0, rect.Width);
        Marquee.Height = Math.Max(0, rect.Height);
        Marquee.StrokeThickness = 1 / Math.Max(0.05, _zoom * DpiScale);
        Marquee.Visibility = Visibility.Visible;
    }

    private PixelRect ToPixels(Rect rect)
    {
        int left = (int)Math.Floor(rect.X), top = (int)Math.Floor(rect.Y);
        int right = (int)Math.Ceiling(rect.Right), bottom = (int)Math.Ceiling(rect.Bottom);
        return new PixelRect(left, top, right - left, bottom - top).Intersect(new PixelRect(0, 0, _base.PixelWidth, _base.PixelHeight));
    }

    /// <summary>화살표: 끝으로 갈수록 굵어지는 몸통 + 삼각형 머리 (채움)</summary>
    internal static Geometry ArrowGeometry(Point from, Point to, double width)
    {
        var direction = to - from;
        double length = direction.Length;
        if (length < 1) return Geometry.Empty;
        direction.Normalize();
        var normal = new Vector(-direction.Y, direction.X);
        double head = Math.Min(length, width * 3 + 10), half = width * 1.7 + 5;
        var neck = to - direction * head * 0.85;
        var headBase = to - direction * head;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(from + normal * width * 0.3, true, true);
            g.PolyLineTo([neck + normal * width / 2, neck - normal * width / 2, from - normal * width * 0.3], true, true);
            g.BeginFigure(headBase + normal * half, true, true);
            g.PolyLineTo([to, headBase - normal * half], true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>화면 확인용 (Capture.Checks): 사각형 · 화살표 · 형광펜 · 글자 · 모자이크를 그려 둠</summary>
    internal void PreviewMarks()
    {
        var red = new SolidColorBrush(Palette[0].Color);
        var box = new Rectangle { Stroke = red, StrokeThickness = StrokeWidth, Width = 220, Height = 90 };
        Canvas.SetLeft(box, 30);
        Canvas.SetTop(box, 30);
        Ink.Children.Add(box);
        Push(new AddEdit(Ink, box));
        var arrow = new Path { Fill = red, Data = ArrowGeometry(new Point(420, 260), new Point(270, 110), StrokeWidth) };
        Ink.Children.Add(arrow);
        Push(new AddEdit(Ink, arrow));
        var marker = new Polyline { Stroke = new SolidColorBrush(Palette[2].Color), StrokeThickness = MarkerWidth, Opacity = 0.4, Points = { new Point(40, 170), new Point(250, 170) } };
        Ink.Children.Add(marker);
        Push(new AddEdit(Ink, marker));
        BeginText(new Point(300, 300));
        _typing!.Text = "여기를 보세요";
        CommitText();
        ApplyMosaic(new PixelRect(40, 210, 160, 60));
    }

    // ───────── 글자 ─────────

    private void BeginText(Point at)
    {
        var brush = new SolidColorBrush(_color);
        var box = new TextBox
        {
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xC0, 0x3B, 0x82, 0xF6)),
            BorderThickness = new Thickness(1),
            Foreground = brush,
            CaretBrush = brush,
            FontFamily = (FontFamily)FindResource("UiFont"),
            FontSize = TextSize,
            FontWeight = FontWeights.SemiBold,
            AcceptsReturn = true,
            MinWidth = TextSize,
            Padding = new Thickness(2, 0, 2, 0)
        };
        Canvas.SetLeft(box, at.X);
        Canvas.SetTop(box, Math.Max(0, at.Y - TextSize * 0.75));
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape || (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift))
            {
                e.Handled = true;
                CommitText();
            }
        };
        box.LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(CommitText);
        Ink.Children.Add(box);
        _typing = box;
        box.Loaded += (_, _) => box.Focus();
    }

    /// <summary>쓰던 글자를 그림에 붙임 (비었으면 지움). 고칠 수 없는 글자로 바꾸어 그대로 그림</summary>
    private void CommitText()
    {
        if (_typing is not { } box) return;
        _typing = null;
        if (string.IsNullOrWhiteSpace(box.Text))
        {
            Ink.Children.Remove(box);
            return;
        }
        box.IsReadOnly = true;
        box.BorderBrush = Brushes.Transparent;
        box.CaretBrush = Brushes.Transparent;
        box.IsHitTestVisible = false;
        box.Focusable = false;
        Push(new AddEdit(Ink, box));
        Keyboard.Focus(this);
    }

    // ───────── 모자이크 · 자르기 ─────────

    private void ApplyMosaic(PixelRect area)
    {
        _basePixels ??= PixelFrame.FromBitmap(_base);
        int block = Math.Max(2, MosaicBlock);
        var tile = new PixelFrame(area.Width, area.Height);
        var source = _basePixels;
        for (int by = 0; by < area.Height; by += block)
        for (int bx = 0; bx < area.Width; bx += block)
        {
            int bw = Math.Min(block, area.Width - bx), bh = Math.Min(block, area.Height - by);
            long b = 0, g = 0, r = 0;
            for (int y = 0; y < bh; y++)
            {
                int offset = ((area.Y + by + y) * source.Width + area.X + bx) * 4;
                for (int x = 0; x < bw; x++, offset += 4)
                {
                    b += source.Pixels[offset];
                    g += source.Pixels[offset + 1];
                    r += source.Pixels[offset + 2];
                }
            }
            int count = bw * bh;
            byte B = (byte)(b / count), G = (byte)(g / count), R = (byte)(r / count);
            for (int y = 0; y < bh; y++)
            {
                int offset = ((by + y) * area.Width + bx) * 4;
                for (int x = 0; x < bw; x++, offset += 4)
                {
                    tile.Pixels[offset] = B;
                    tile.Pixels[offset + 1] = G;
                    tile.Pixels[offset + 2] = R;
                    tile.Pixels[offset + 3] = 255;
                }
            }
        }
        var image = new Image { Source = tile.ToBitmap(), Width = area.Width, Height = area.Height, Stretch = Stretch.Fill, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        Canvas.SetLeft(image, area.X);
        Canvas.SetTop(image, area.Y);
        Ink.Children.Add(image);
        Push(new AddEdit(Ink, image));
    }

    private void ApplyCrop(PixelRect area)
    {
        var flat = PixelFrame.FromBitmap(Flatten()).Crop(area).ToBitmap();
        var edit = new CropEdit(this, _base, [.. Ink.Children.Cast<UIElement>()], flat);
        edit.Redo();
        Push(edit);
        SelectTool(_toolBeforeCrop);
    }

    /// <summary>그림 + 그린 것을 화소 그대로 한 장으로</summary>
    internal BitmapSource Flatten()
    {
        CommitText();
        if (Ink.Children.Count == 0) return _base;
        int w = _base.PixelWidth, h = _base.PixelHeight;
        Marquee.Visibility = Visibility.Collapsed;
        Sheet.UpdateLayout();
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(_base, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(Ink)
            {
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, w, h),
                ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, w, h),
                Stretch = Stretch.Fill
            }, null, new Rect(0, 0, w, h));
        }
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    // ───────── 되돌리기 ─────────

    private interface IEdit
    {
        void Undo();
        void Redo();
    }

    private sealed class AddEdit(Canvas canvas, UIElement element) : IEdit
    {
        public void Undo() => canvas.Children.Remove(element);
        public void Redo() { if (!canvas.Children.Contains(element)) canvas.Children.Add(element); }
    }

    private sealed class CropEdit(EditorWindow editor, BitmapSource before, UIElement[] children, BitmapSource after) : IEdit
    {
        public void Undo()
        {
            editor.Ink.Children.Clear();
            editor.SetBase(before);
            foreach (var child in children) editor.Ink.Children.Add(child);
        }

        public void Redo()
        {
            editor.Ink.Children.Clear();
            editor.SetBase(after);
        }
    }

    private void Push(IEdit edit)
    {
        _undo.Push(edit);
        _redo.Clear();
        MarkDirty(true);
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        CommitText();
        if (_undo.Count == 0) return;
        var edit = _undo.Pop();
        edit.Undo();
        _redo.Push(edit);
        MarkDirty(true);
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;
        var edit = _redo.Pop();
        edit.Redo();
        _undo.Push(edit);
        MarkDirty(true);
    }

    private void MarkDirty(bool dirty)
    {
        _dirty = dirty;
        DirtyText.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        UndoButton.IsEnabled = _undo.Count > 0;
        RedoButton.IsEnabled = _redo.Count > 0;
    }

    // ───────── 저장 · 복사 · 공유 ─────────

    /// <summary>고친 내용을 캡처 파일에 저장 (고친 게 없으면 그대로)</summary>
    private bool Save()
    {
        CommitText();
        if (!_dirty) return true;
        try
        {
            ImageFiles.Save(Flatten(), _path);
            MarkDirty(false);
            _controller.NotifySaved();
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Toast.Error("저장하지 못했습니다: " + error.Message);
            return false;
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => Copy();

    private void Copy()
    {
        if (Copier.Image(Flatten())) ShowStatus("복사했습니다");
    }

    private async void Share_Click(object sender, RoutedEventArgs e)
    {
        if (!Save()) return;
        ShareButton.IsEnabled = false;
        ShareText.Text = "올리는 중…";
        try { await _controller.ShareAsync(_path); }
        finally
        {
            ShareButton.IsEnabled = true;
            ShareText.Text = "공유";
        }
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = MoreButton.ContextMenu;
        menu.PlacementTarget = MoreButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void SaveAs_Click(object sender, RoutedEventArgs e) => SaveAs();

    private void SaveAs()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = System.IO.Path.GetFileNameWithoutExtension(_path),
            InitialDirectory = System.IO.Path.GetDirectoryName(_path),
            Filter = "PNG 그림 (*.png)|*.png|JPEG 그림 (*.jpg)|*.jpg",
            DefaultExt = ".png"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            ImageFiles.Save(Flatten(), dialog.FileName);
            ShowStatus("저장했습니다: " + System.IO.Path.GetFileName(dialog.FileName));
            _controller.NotifySaved();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Toast.Error("저장하지 못했습니다: " + error.Message);
        }
    }

    private void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        Save();
        ImageFiles.ShowInFolder(_path);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        _deleted = true;
        Close();
        _controller.Delete(_path);
    }

    // ───────── 창 ─────────

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_typing is not null) return;  // 글자를 쓰는 중에는 글자 칸이 받음
        var modifiers = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool handled = true;
        if (modifiers == ModifierKeys.Control)
        {
            switch (key)
            {
                case Key.C: Copy(); break;
                case Key.S: if (Save()) ShowStatus("저장했습니다"); break;
                case Key.Z: Undo(); break;
                case Key.Y: Redo(); break;
                case Key.D0: SetZoom(1 / DpiScale, fitted: false); break;
                default: handled = false; break;
            }
        }
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            switch (key)
            {
                case Key.S: SaveAs(); break;
                case Key.Z: Redo(); break;
                default: handled = false; break;
            }
        }
        else if (modifiers == ModifierKeys.None)
        {
            switch (key)
            {
                case Key.Escape: Close(); break;
                case Key.R: SelectTool(Tool.Rect); break;
                case Key.A: SelectTool(Tool.Arrow); break;
                case Key.P: SelectTool(Tool.Pen); break;
                case Key.H: SelectTool(Tool.Marker); break;
                case Key.T: SelectTool(Tool.Text); break;
                case Key.M: SelectTool(Tool.Mosaic); break;
                case Key.C: SelectTool(Tool.Crop); break;
                default: handled = false; break;
            }
        }
        else handled = false;
        e.Handled = handled;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_deleted) return;
        // 고친 내용은 저장하고 닫음 (저장하지 못하면 닫지 않음: 다른 이름으로 저장할 수 있게)
        if (!Save()) e.Cancel = true;
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
        // 테두리 없는 창을 최대화하면 화면 밖으로 넘치는 만큼 안쪽으로
        BorderThickness = maximized ? new Thickness(7) : new Thickness(0);
    }
}
