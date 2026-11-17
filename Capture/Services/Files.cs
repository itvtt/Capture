using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Capture.Services;

/// <summary>클립보드 (다른 프로그램이 잡고 있으면 잠깐 기다렸다 다시)</summary>
public static class Copier
{
    /// <summary>그림: 일반 비트맵 + PNG (붙여 넣는 프로그램이 고르게)</summary>
    public static bool Image(BitmapSource image)
    {
        var data = new DataObject();
        data.SetImage(image);
        var png = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(png);
        png.Position = 0;
        data.SetData("PNG", png, false);
        return Retry(() => Clipboard.SetDataObject(data, true));
    }

    public static bool Text(string text) => Retry(() => Clipboard.SetText(text));

    /// <summary>파일 (탐색기 · 메신저에 붙여 넣으면 파일이 들어감)</summary>
    public static bool File(string path) => Retry(() => Clipboard.SetFileDropList(new StringCollection { path }));

    private static bool Retry(Action set)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                set();
                return true;
            }
            catch (COMException) when (attempt < 8)
            {
                Thread.Sleep(50);
            }
            catch (COMException)
            {
                Toast.Error("클립보드를 다른 프로그램이 쓰고 있어 복사하지 못했습니다.");
                return false;
            }
        }
    }
}

/// <summary>그림 파일 저장 · 미리 보기 · 폴더 · 휴지통</summary>
public static class ImageFiles
{
    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".gif", ".mp4"];

    /// <summary>확장자에 맞게 저장 (.jpg 는 JPEG, 나머지는 PNG). 임시 파일에 쓴 뒤 바꿔서 중간에 끊겨도 원본이 깨지지 않게</summary>
    public static void Save(BitmapSource image, string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        BitmapEncoder encoder = extension is ".jpg" or ".jpeg" ? new JpegBitmapEncoder { QualityLevel = 92 } : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".saving";
        using (var stream = File.Create(temp)) encoder.Save(stream);
        File.Move(temp, path, overwrite: true);
    }

    public static BitmapSource Load(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>작은 미리 보기 (GIF 는 첫 그림). 영상이거나 읽지 못하면 null</summary>
    public static BitmapSource? Thumbnail(string path, int width)
    {
        if (IsVideo(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = width;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception error) when (error is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException or COMException)
        {
            return null;
        }
    }

    public static bool IsVideo(string path) => Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase);
    public static bool IsGif(string path) => Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase);

    public static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose(); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or FileNotFoundException) { Toast.Error(error.Message); }
    }

    public static void ShowInFolder(string path)
    {
        if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();
        else OpenFolder(Path.GetDirectoryName(path)!);
    }

    public static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true })?.Dispose();
    }

    /// <summary>휴지통으로 (되살릴 수 있게)</summary>
    public static void Recycle(string path) =>
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

    public static string SizeText(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B"
    };
}
