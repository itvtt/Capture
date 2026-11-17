using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Capture.Core;

/// <summary>화면의 사각형 (실제 화소, 가상 화면 좌표)</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(int x, int y) => x >= X && y >= Y && x < Right && y < Bottom;

    public PixelRect Intersect(PixelRect other)
    {
        int left = Math.Max(X, other.X), top = Math.Max(Y, other.Y);
        int right = Math.Min(Right, other.Right), bottom = Math.Min(Bottom, other.Bottom);
        return right > left && bottom > top ? new PixelRect(left, top, right - left, bottom - top) : default;
    }

    public PixelRect Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };

    /// <summary>누른 점에서 놓은 점까지 (400 화소 끌면 너비 400)</summary>
    public static PixelRect FromCorners(int x1, int y1, int x2, int y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));

    public override string ToString() => $"{Width} × {Height}";
}

/// <summary>화면에서 읽은 그림: BGRA, 위에서 아래로, 한 줄 = 너비 × 4 바이트 (알파는 쓰지 않음)</summary>
public sealed class PixelFrame
{
    public PixelFrame(int width, int height, byte[]? pixels = null)
    {
        Width = width;
        Height = height;
        Pixels = pixels ?? new byte[width * height * 4];
        if (Pixels.Length < width * height * 4) throw new ArgumentException("pixels too short");
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    public int Stride => Width * 4;

    public Span<uint> Row(int y) => MemoryMarshal.Cast<byte, uint>(Pixels.AsSpan(y * Stride, Stride));

    public PixelFrame Crop(PixelRect rect)
    {
        var result = new PixelFrame(rect.Width, rect.Height);
        for (int y = 0; y < rect.Height; y++)
            Buffer.BlockCopy(Pixels, ((rect.Y + y) * Width + rect.X) * 4, result.Pixels, y * result.Stride, result.Stride);
        return result;
    }

    /// <summary>화면에 그리거나 저장할 WPF 그림 (96 DPI: 화소 1개 = 1 단위)</summary>
    public BitmapSource ToBitmap()
    {
        var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgr32, null, Pixels, Stride);
        bitmap.Freeze();
        return bitmap;
    }

    public static PixelFrame FromBitmap(BitmapSource source)
    {
        var converted = source.Format == PixelFormats.Bgr32 || source.Format == PixelFormats.Bgra32 || source.Format == PixelFormats.Pbgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var frame = new PixelFrame(converted.PixelWidth, converted.PixelHeight);
        converted.CopyPixels(frame.Pixels, frame.Stride, 0);
        return frame;
    }

    /// <summary>색이 모두 같은지 (알파 무시)</summary>
    public bool SameAs(PixelFrame other)
    {
        if (other.Width != Width || other.Height != Height) return false;
        var a = MemoryMarshal.Cast<byte, uint>(Pixels.AsSpan(0, Width * Height * 4));
        var b = MemoryMarshal.Cast<byte, uint>(other.Pixels.AsSpan(0, Width * Height * 4));
        for (int i = 0; i < a.Length; i++)
            if (((a[i] ^ b[i]) & 0x00FFFFFF) != 0) return false;
        return true;
    }
}
