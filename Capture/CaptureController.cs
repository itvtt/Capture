using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Capture.Core;
using Capture.Services;

namespace Capture;

/// <summary>
/// 캡처 흐름: 고르기 → (영역) 저장 · 복사 · 편집 창 / (스크롤) 이어 붙여 같은 흐름 / (GIF · 영상) 녹화 → 알림.
/// 녹화 · 스크롤 캡처 중에 단축키를 다시 누르면 멈춤
/// </summary>
public sealed class CaptureController(AppSettings settings)
{
    private static readonly Color RecordColor = Color.FromRgb(0xFF, 0x45, 0x3A);
    private static readonly Color ScrollColor = Color.FromRgb(0x3B, 0x82, 0xF6);
    private bool _busy;
    private Action? _stop;

    public AppSettings Settings { get; } = settings;

    /// <summary>새 파일을 저장했거나 지움 (최근 목록을 다시 읽게)</summary>
    public event Action? FilesChanged;

    public void NotifySaved() => FilesChanged?.Invoke();

    /// <summary>단축키 · 트레이 · 큰 창에서. 녹화 중이면 멈춤</summary>
    public async void Start(CaptureMode mode)
    {
        if (_stop is { } stop)
        {
            stop();
            return;
        }
        if (_busy) return;
        _busy = true;
        try
        {
            await RunAsync(mode);
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            Toast.Error(error.Message);
        }
        finally
        {
            _busy = false;
            _stop = null;
        }
    }

    private async Task RunAsync(CaptureMode mode)
    {
        await HideMainWindowAsync();
        var selection = await SelectionSession.RunAsync(mode);
        if (selection is null) return;
        switch (selection.Mode)
        {
            case CaptureMode.Region:
                await SaveImageAsync(selection.Image!.ToBitmap());
                break;
            case CaptureMode.Scroll:
                await Task.Delay(150);  // 고르기 창이 사라진 화면으로
                if (await ScrollAsync(selection.Rect) is { } tall) await SaveImageAsync(tall.ToBitmap());
                break;
            default:
                await Task.Delay(150);
                await RecordAsync(selection.Rect, gif: selection.Mode == CaptureMode.Gif);
                break;
        }
    }

    /// <summary>큰 창에서 눌렀으면 그 창이 찍히지 않게 먼저 숨김</summary>
    private static async Task HideMainWindowAsync()
    {
        if (Application.Current.MainWindow is { IsVisible: true } main && main.WindowState != WindowState.Minimized)
        {
            main.Hide();
            await Task.Delay(180);
        }
    }

    // ───────── 그림 ─────────

    private async Task SaveImageAsync(BitmapSource image)
    {
        string path = Settings.NewFilePath("png");
        await Task.Run(() => ImageFiles.Save(image, path));
        Copier.Image(image);
        FilesChanged?.Invoke();
        OpenEditor(path, image);
    }

    public void OpenEditor(string path, BitmapSource? image = null)
    {
        try
        {
            var editor = new EditorWindow(this, path, image ?? ImageFiles.Load(path));
            editor.Show();
            editor.Activate();
        }
        catch (Exception error) when (error is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            Toast.Error("그림을 열지 못했습니다: " + error.Message);
        }
    }

    // ───────── 스크롤 캡처 ─────────

    private async Task<PixelFrame?> ScrollAsync(PixelRect rect)
    {
        var frame = new RegionFrame(rect, ScrollColor);
        var bar = new RecordBar("스크롤 캡처", canCancel: true, ScrollColor) { StopLabel = "끝" };
        using var cancel = new CancellationTokenSource();
        bool canceled = false;
        bar.StopRequested += () => cancel.Cancel();
        bar.CancelRequested += () =>
        {
            canceled = true;
            cancel.Cancel();
        };
        _stop = () => cancel.Cancel();
        frame.ShowAround();
        bar.ShowNear(rect);
        bar.ShowText("0 px");
        var progress = new Progress<int>(height => bar.ShowText($"{height:N0} px"));
        try
        {
            var result = await Task.Run(() => ScrollCapture.Run(rect, cancel.Token, progress));
            return canceled ? null : result;
        }
        finally
        {
            _stop = null;
            frame.Close();
            bar.Close();
        }
    }

    // ───────── 녹화 ─────────

    private async Task RecordAsync(PixelRect rect, bool gif)
    {
        if (!gif) rect = rect with { Width = rect.Width & ~1, Height = rect.Height & ~1 };  // H.264 는 짝수 크기
        if (rect.Width < 16 || rect.Height < 16)
        {
            Toast.Error("녹화할 영역이 너무 작습니다.");
            return;
        }
        string path = Settings.NewFilePath(gif ? "gif" : "mp4");
        var recorder = new Recorder(rect, gif ? 15 : 30, () => gif
            ? new GifWriter(path, rect.Width, rect.Height)
            : new Mp4Writer(path, rect.Width, rect.Height, 30));
        var frame = new RegionFrame(rect, RecordColor);
        var bar = new RecordBar(gif ? "GIF" : "영상", canCancel: true, RecordColor);
        var done = new TaskCompletionSource<bool>();
        bar.StopRequested += () => done.TrySetResult(true);
        bar.CancelRequested += () => done.TrySetResult(false);
        frame.ShowAround();
        bar.ShowNear(rect);
        try
        {
            recorder.Start();
        }
        catch (InvalidOperationException error)
        {
            frame.Close();
            bar.Close();
            TryDelete(path);
            Toast.Error("녹화를 시작하지 못했습니다: " + error.Message);
            return;
        }
        _stop = () => done.TrySetResult(true);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            bar.ShowTime(recorder.Elapsed);
            if (recorder.Error is not null) done.TrySetResult(true);
        };
        timer.Start();

        bool save = await done.Task;
        _stop = null;
        timer.Stop();
        bar.ShowText(save ? "저장 중…" : "취소 중…");
        bar.IsEnabled = false;
        Exception? failure = null;
        try
        {
            await Task.Run(recorder.Stop);
        }
        catch (IOException error)
        {
            failure = error;
        }
        finally
        {
            frame.Close();
            bar.Close();
        }
        if (!save)
        {
            TryDelete(path);
            return;
        }
        if (failure is not null)
        {
            TryDelete(path);
            Toast.Error("녹화하지 못했습니다: " + failure.Message);
            return;
        }
        FilesChanged?.Invoke();
        ShowRecorded(path, recorder.Elapsed);
    }

    private void ShowRecorded(string path, TimeSpan length)
    {
        bool gif = ImageFiles.IsGif(path);
        long size = new FileInfo(path).Length;
        string detail = $"{Path.GetFileName(path)} · {ImageFiles.SizeText(size)} · {length:m\\:ss}";
        Toast.Notify(gif ? "GIF 를 저장했습니다" : "영상을 저장했습니다", detail,
            thumbnail: gif ? ImageFiles.Thumbnail(path, 128) : null,
            icon: gif ? null : (Geometry)Application.Current.FindResource("VideoIcon"),
            actions:
            [
                ("공유", () => _ = ShareAsync(path)),
                ("복사", () => CopyFile(path)),
                ("폴더", () => ImageFiles.ShowInFolder(path)),
            ],
            clicked: () => ImageFiles.Open(path), seconds: 10);
    }

    // ───────── 공유 · 복사 · 지우기 ─────────

    /// <summary>File Shared 에 올리고 링크를 클립보드에 (알림으로 알려 줌)</summary>
    public async Task ShareAsync(string path)
    {
        try
        {
            string link = await FileShared.ShareAsync(path);
            Copier.Text(link);
            Toast.Notify("공유 링크를 복사했습니다", link, icon: (Geometry)Application.Current.FindResource("ShareIcon"),
                actions: [("열기", () => OpenLink(link))]);
        }
        catch (Exception error) when (error is FileSharedException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            Toast.Error(error.Message);
        }
    }

    public void CopyFile(string path)
    {
        if (Copier.File(path)) Toast.Notify("파일을 복사했습니다", "메신저나 탐색기에 붙여 넣으세요.", seconds: 4);
    }

    public void CopyImage(string path)
    {
        try
        {
            if (Copier.Image(ImageFiles.Load(path))) Toast.Notify("그림을 복사했습니다", Path.GetFileName(path), seconds: 4);
        }
        catch (Exception error) when (error is IOException or NotSupportedException)
        {
            Toast.Error(error.Message);
        }
    }

    public void Delete(string path)
    {
        try
        {
            ImageFiles.Recycle(path);
            FilesChanged?.Invoke();
            Toast.Notify("휴지통으로 옮겼습니다", Path.GetFileName(path), seconds: 4);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Toast.Error("지우지 못했습니다: " + error.Message);
        }
    }

    private static void OpenLink(string link)
    {
        try { Process.Start(new ProcessStartInfo(link) { UseShellExecute = true })?.Dispose(); }
        catch (Win32Exception) { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
