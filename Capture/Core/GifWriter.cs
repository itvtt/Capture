using System.IO;
using System.Runtime.InteropServices;

namespace Capture.Core;

/// <summary>녹화 파일 쓰기 (GIF · MP4 공통). 그림은 BGRA, 시각은 녹화 시작부터 100ns 단위</summary>
public interface IFrameWriter : IDisposable
{
    void Write(byte[] bgra, long time);
    void Finish(long endTime);
}

/// <summary>
/// 움직이는 GIF (반복 재생). 그림마다 바뀐 부분만 넣고(나머지는 투명) 색은 그 부분에서 고름:
/// 256색 이하면 그대로, 넘으면 미디언 컷. 바뀐 곳이 없는 그림은 앞 그림을 더 오래 보여 줌
/// </summary>
public sealed class GifWriter : IFrameWriter
{
    private readonly Stream _output;
    private readonly bool _ownsOutput;
    private readonly int _width, _height;
    private byte[]? _previous;
    private PendingFrame? _pending;
    private long _start = -1;
    private long _writtenCentiseconds;
    private bool _finished;

    public GifWriter(string path, int width, int height) : this(File.Create(path), width, height, ownsOutput: true) { }

    public GifWriter(Stream output, int width, int height, bool ownsOutput = false)
    {
        if (width < 1 || height < 1 || width > 65535 || height > 65535) throw new ArgumentException("GIF 크기");
        _output = output;
        _ownsOutput = ownsOutput;
        _width = width;
        _height = height;
        var header = new BinaryWriter(_output);
        header.Write("GIF89a"u8);
        header.Write((ushort)width);
        header.Write((ushort)height);
        header.Write((byte)0x70);  // 전역 색표 없음, 색 깊이 8비트
        header.Write((byte)0);
        header.Write((byte)0);
        // NETSCAPE2.0: 계속 반복
        header.Write([0x21, 0xFF, 0x0B]);
        header.Write("NETSCAPE2.0"u8);
        header.Write([0x03, 0x01, 0x00, 0x00, 0x00]);
        header.Flush();
    }

    /// <summary>쓴 그림 수 (바뀐 곳이 없어 합친 그림은 빼고)</summary>
    public int FrameCount { get; private set; }

    public void Write(byte[] bgra, long time)
    {
        if (_start < 0) _start = time;
        PendingFrame frame;
        if (_previous is null)
        {
            frame = Encode(bgra, 0, 0, _width, _height, null);
            _previous = new byte[_width * _height * 4];
        }
        else
        {
            var box = ChangedBox(_previous, bgra);
            if (box.IsEmpty) return;
            frame = Encode(bgra, box.X, box.Y, box.Width, box.Height, _previous);
        }
        frame.Time = time;
        Flush(time);
        _pending = frame;
        Buffer.BlockCopy(bgra, 0, _previous, 0, _width * _height * 4);
    }

    public void Finish(long endTime)
    {
        if (_finished) return;
        _finished = true;
        if (_pending is null)
        {
            // 그림이 하나도 없으면 검은 화면 하나
            Write(new byte[_width * _height * 4], endTime);
        }
        Flush(Math.Max(endTime, (_pending?.Time ?? endTime) + 100_000));
        _output.WriteByte(0x3B);
        _output.Flush();
    }

    /// <summary>기다리던 그림을 씀 (보여 줄 시간 = 다음 그림까지). 1/100초 단위 누적으로 반올림해 밀리지 않게, 최소 2 (브라우저는 1 이하를 느리게 보여 줌)</summary>
    private void Flush(long nextTime)
    {
        if (_pending is not { } frame) return;
        long end = (nextTime - _start + 50_000) / 100_000;
        int delay = (int)Math.Clamp(end - _writtenCentiseconds, 2, 65535);
        _writtenCentiseconds += delay;
        var writer = new BinaryWriter(_output);
        // 그래픽 제어: 다음 그림 전에 지우지 않음(1), 투명 색
        writer.Write([0x21, 0xF9, 0x04]);
        writer.Write((byte)((1 << 2) | (frame.Transparent >= 0 ? 1 : 0)));
        writer.Write((ushort)delay);
        writer.Write((byte)Math.Max(frame.Transparent, 0));
        writer.Write((byte)0);
        // 그림 설명 + 이 그림의 색표
        writer.Write((byte)0x2C);
        writer.Write((ushort)frame.Left);
        writer.Write((ushort)frame.Top);
        writer.Write((ushort)frame.Width);
        writer.Write((ushort)frame.Height);
        writer.Write((byte)(0x80 | (frame.TableBits - 1)));
        writer.Write(frame.Palette);
        writer.Write((byte)frame.MinCodeSize);
        writer.Flush();
        WriteBlocks(_output, frame.Data);
        _pending = null;
        FrameCount++;
    }

    private PixelRect ChangedBox(byte[] before, byte[] after)
    {
        var a = MemoryMarshal.Cast<byte, uint>(before.AsSpan(0, _width * _height * 4));
        var b = MemoryMarshal.Cast<byte, uint>(after.AsSpan(0, _width * _height * 4));
        int top = -1, bottom = -1;
        for (int y = 0; y < _height && top < 0; y++)
            if (RowDiffers(a, b, y)) top = y;
        if (top < 0) return default;
        for (int y = _height - 1; y >= top && bottom < 0; y--)
            if (RowDiffers(a, b, y)) bottom = y;
        int left = _width, right = -1;
        for (int y = top; y <= bottom; y++)
        {
            int row = y * _width;
            for (int x = 0; x < left; x++)
                if (((a[row + x] ^ b[row + x]) & 0x00FFFFFF) != 0) { left = x; break; }
            for (int x = _width - 1; x > right; x--)
                if (((a[row + x] ^ b[row + x]) & 0x00FFFFFF) != 0) { right = x; break; }
        }
        return new PixelRect(left, top, right - left + 1, bottom - top + 1);
    }

    private bool RowDiffers(ReadOnlySpan<uint> a, ReadOnlySpan<uint> b, int y)
    {
        int row = y * _width;
        for (int x = 0; x < _width; x++)
            if (((a[row + x] ^ b[row + x]) & 0x00FFFFFF) != 0) return true;
        return false;
    }

    private sealed class PendingFrame
    {
        public int Left, Top, Width, Height, TableBits, MinCodeSize, Transparent = -1;
        public byte[] Palette = [];
        public byte[] Data = [];
        public long Time;
    }

    /// <summary>사각형 부분을 색 번호로 바꿔 LZW 로 압축. previous 가 있으면 그대로인 화소는 투명</summary>
    private PendingFrame Encode(byte[] bgra, int left, int top, int width, int height, byte[]? previous)
    {
        var pixels = MemoryMarshal.Cast<byte, uint>(bgra.AsSpan(0, _width * _height * 4));
        var before = previous is null ? default : MemoryMarshal.Cast<byte, uint>(previous.AsSpan(0, _width * _height * 4));
        var colors = new int[width * height];  // -1 = 투명
        bool anyTransparent = false;
        for (int y = 0; y < height; y++)
        {
            int source = (top + y) * _width + left, target = y * width;
            for (int x = 0; x < width; x++)
            {
                uint pixel = pixels[source + x] & 0x00FFFFFF;
                if (previous is not null && (before[source + x] & 0x00FFFFFF) == pixel)
                {
                    colors[target + x] = -1;
                    anyTransparent = true;
                }
                else colors[target + x] = (int)pixel;
            }
        }

        int maxColors = anyTransparent ? 255 : 256;
        var exact = new Dictionary<int, int>();
        foreach (int color in colors)
        {
            if (color < 0 || exact.ContainsKey(color)) continue;
            if (exact.Count == maxColors) { exact = null; break; }
            exact[color] = exact.Count;
        }

        List<int> paletteColors;
        var indices = new byte[colors.Length];
        if (exact is not null)
        {
            paletteColors = [.. exact.OrderBy(pair => pair.Value).Select(pair => pair.Key)];
            for (int i = 0; i < colors.Length; i++)
                indices[i] = colors[i] < 0 ? (byte)0 : (byte)exact[colors[i]];
        }
        else
        {
            var lookup = MedianCut.Build(colors, maxColors, out paletteColors);
            for (int i = 0; i < colors.Length; i++)
                indices[i] = colors[i] < 0 ? (byte)0 : lookup[Key15(colors[i])];
        }

        int transparent = -1;
        if (anyTransparent)
        {
            transparent = paletteColors.Count;
            paletteColors.Add(0);
            for (int i = 0; i < colors.Length; i++)
                if (colors[i] < 0) indices[i] = (byte)transparent;
        }

        int bits = 1;
        while ((1 << bits) < paletteColors.Count) bits++;
        var palette = new byte[3 << bits];
        for (int i = 0; i < paletteColors.Count; i++)
        {
            int color = paletteColors[i];
            palette[i * 3] = (byte)(color >> 16);      // R
            palette[i * 3 + 1] = (byte)(color >> 8);   // G
            palette[i * 3 + 2] = (byte)color;          // B
        }
        int minCodeSize = Math.Max(2, bits);
        return new PendingFrame
        {
            Left = left, Top = top, Width = width, Height = height,
            TableBits = bits, MinCodeSize = minCodeSize, Transparent = transparent,
            Palette = palette, Data = Lzw(indices, minCodeSize)
        };
    }

    internal static int Key15(int color) => ((color >> 19) & 0x1F) << 10 | ((color >> 11) & 0x1F) << 5 | ((color >> 3) & 0x1F);

    /// <summary>GIF 의 LZW (가변 길이 부호, 4095 에서 처음부터)</summary>
    private static byte[] Lzw(byte[] indices, int minCodeSize)
    {
        int clear = 1 << minCodeSize, end = clear + 1;
        int codeSize = minCodeSize + 1, maxCode = end;
        var table = new Dictionary<int, int>(4096);
        var output = new MemoryStream(indices.Length / 2 + 16);
        uint buffer = 0;
        int bitCount = 0;

        void Emit(int code, int size)
        {
            buffer |= (uint)code << bitCount;
            bitCount += size;
            while (bitCount >= 8)
            {
                output.WriteByte((byte)buffer);
                buffer >>= 8;
                bitCount -= 8;
            }
        }

        Emit(clear, codeSize);
        int current = -1;
        foreach (byte value in indices)
        {
            if (current < 0)
            {
                current = value;
                continue;
            }
            int key = (current << 8) | value;
            if (table.TryGetValue(key, out int code))
            {
                current = code;
                continue;
            }
            Emit(current, codeSize);
            table[key] = ++maxCode;
            if (maxCode >= (1 << codeSize)) codeSize++;
            if (maxCode == 4095)
            {
                Emit(clear, codeSize);
                table.Clear();
                codeSize = minCodeSize + 1;
                maxCode = end;
            }
            current = value;
        }
        if (current >= 0) Emit(current, codeSize);
        Emit(clear, codeSize);
        Emit(end, minCodeSize + 1);
        if (bitCount > 0) output.WriteByte((byte)buffer);
        return output.ToArray();
    }

    private static void WriteBlocks(Stream output, byte[] data)
    {
        for (int offset = 0; offset < data.Length; offset += 255)
        {
            int count = Math.Min(255, data.Length - offset);
            output.WriteByte((byte)count);
            output.Write(data, offset, count);
        }
        output.WriteByte(0);
    }

    public void Dispose()
    {
        if (_ownsOutput) _output.Dispose();
    }
}

/// <summary>색 줄이기 (미디언 컷): RGB 를 5비트씩(32768칸)으로 세고, 가장 많이 쓰인 상자를 가장 긴 축의 중간에서 나눔</summary>
internal static class MedianCut
{
    /// <summary>15비트 색 → 팔레트 번호 표. palette 는 0xRRGGBB</summary>
    public static byte[] Build(int[] colors, int maxColors, out List<int> palette)
    {
        var histogram = new int[32768];
        foreach (int color in colors)
            if (color >= 0) histogram[GifWriter.Key15(color)]++;
        var used = new List<int>();
        for (int key = 0; key < histogram.Length; key++)
            if (histogram[key] > 0) used.Add(key);
        var items = used.ToArray();

        var boxes = new List<(int Start, int Length)> { (0, items.Length) };
        var sortKeys = new int[items.Length];
        while (boxes.Count < maxColors)
        {
            int pick = -1;
            long pickCount = -1;
            int pickAxis = 0;
            for (int b = 0; b < boxes.Count; b++)
            {
                var (start, length) = boxes[b];
                if (length < 2) continue;
                var (axis, range) = LongestAxis(items, start, length);
                if (range == 0) continue;
                long count = 0;
                for (int i = start; i < start + length; i++) count += histogram[items[i]];
                if (count > pickCount)
                {
                    pick = b;
                    pickCount = count;
                    pickAxis = axis;
                }
            }
            if (pick < 0) break;
            var (s, n) = boxes[pick];
            for (int i = s; i < s + n; i++) sortKeys[i] = Channel(items[i], pickAxis);
            Array.Sort(sortKeys, items, s, n);
            long half = pickCount / 2, running = 0;
            int split = s + 1;
            for (int i = s; i < s + n - 1; i++)
            {
                running += histogram[items[i]];
                split = i + 1;
                if (running >= half) break;
            }
            boxes[pick] = (s, split - s);
            boxes.Add((split, s + n - split));
        }

        var lookup = new byte[32768];
        palette = new List<int>(boxes.Count);
        for (int b = 0; b < boxes.Count; b++)
        {
            var (start, length) = boxes[b];
            long r = 0, g = 0, bl = 0, total = 0;
            for (int i = start; i < start + length; i++)
            {
                int key = items[i], weight = histogram[key];
                r += ((key >> 10) & 0x1F) * weight;
                g += ((key >> 5) & 0x1F) * weight;
                bl += (key & 0x1F) * weight;
                total += weight;
                lookup[key] = (byte)b;
            }
            if (total == 0) total = 1;
            int R = Expand(r, total), G = Expand(g, total), B = Expand(bl, total);
            palette.Add((R << 16) | (G << 8) | B);
        }
        return lookup;
    }

    private static int Expand(long sum, long total) => (int)Math.Min(255, (sum * 255 + total * 31 / 2) / (total * 31));

    private static int Channel(int key, int axis) => axis switch { 0 => (key >> 10) & 0x1F, 1 => (key >> 5) & 0x1F, _ => key & 0x1F };

    private static (int Axis, int Range) LongestAxis(int[] items, int start, int length)
    {
        int bestAxis = 0, bestRange = -1;
        for (int axis = 0; axis < 3; axis++)
        {
            int min = 31, max = 0;
            for (int i = start; i < start + length; i++)
            {
                int value = Channel(items[i], axis);
                if (value < min) min = value;
                if (value > max) max = value;
            }
            // 사람 눈은 초록에 민감: 초록 범위를 조금 크게 봄
            int range = (max - min) * (axis == 1 ? 5 : 4);
            if (range > bestRange)
            {
                bestRange = range;
                bestAxis = axis;
            }
        }
        return (bestAxis, bestRange);
    }
}
