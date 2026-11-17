using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Capture.Core;

/// <summary>
/// MP4 (H.264) 녹화: Windows 에 들어 있는 Media Foundation 싱크 라이터로 바로 씀 (NuGet · ffmpeg 없음).
/// 그림(RGB32, 위에서 아래로)을 받는 대로 인코더에 넘기므로 메모리에 쌓이지 않음. 너비 · 높이는 짝수여야 함.
/// COM 인터페이스는 필요한 메서드만 vtable 번호로 부름 (mfobjects.h · mfreadwrite.h 순서)
/// </summary>
public sealed unsafe class Mp4Writer : IFrameWriter
{
    private const uint MfVersion = 0x00020070;  // MF_SDK_VERSION << 16 | MF_API_VERSION
    private IntPtr _writer;
    private readonly uint _stream;
    private readonly int _width, _height;
    private readonly long _frameDuration;
    private long _lastTime = -1;
    private bool _started, _finished;

    // IMFAttributes (IUnknown 3개 다음): SetUINT32 21, SetUINT64 22, SetGUID 24
    private const int SetUInt32Slot = 21, SetUInt64Slot = 22, SetGuidSlot = 24;
    // IMFSample (IMFAttributes 30개 다음): SetSampleTime 36, SetSampleDuration 38, AddBuffer 42
    private const int SetSampleTimeSlot = 36, SetSampleDurationSlot = 38, AddBufferSlot = 42;
    // IMFMediaBuffer: Lock 3, Unlock 4, SetCurrentLength 6
    // IMFSinkWriter: AddStream 3, SetInputMediaType 4, BeginWriting 5, WriteSample 6, Finalize 11

    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid SubType = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid FormatH264 = new("34363248-0000-0010-8000-00aa00389b71");
    private static readonly Guid FormatRgb32 = new("00000016-0000-0010-8000-00aa00389b71");
    private static readonly Guid AverageBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid PixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid HardwareTransforms = new("a634a91c-822b-41b9-a494-4de4643612b0");

    [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] private static extern int MFShutdown();
    [DllImport("mfplat.dll")] private static extern int MFCreateAttributes(out IntPtr attributes, uint size);
    [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out IntPtr type);
    [DllImport("mfplat.dll")] private static extern int MFCreateMemoryBuffer(uint length, out IntPtr buffer);
    [DllImport("mfplat.dll")] private static extern int MFCreateSample(out IntPtr sample);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    private static extern int MFCreateSinkWriterFromURL(string url, IntPtr byteStream, IntPtr attributes, out IntPtr writer);

    public Mp4Writer(string path, int width, int height, int framesPerSecond)
    {
        if (width % 2 != 0 || height % 2 != 0 || width < 16 || height < 16)
            throw new ArgumentException("영상 크기는 16 이상 짝수여야 합니다.");
        _width = width;
        _height = height;
        _frameDuration = 10_000_000L / framesPerSecond;
        Check(MFStartup(MfVersion, 0), "Media Foundation");
        _started = true;
        try
        {
            // 그래픽 카드의 인코더가 있으면 그것으로, 안 되면 Windows 기본 인코더로
            try { _stream = Open(path, framesPerSecond, hardware: true); }
            catch (Win32Exception)
            {
                Release(ref _writer);
                _stream = Open(path, framesPerSecond, hardware: false);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private uint Open(string path, int fps, bool hardware)
    {
        IntPtr attributes = IntPtr.Zero, output = IntPtr.Zero, input = IntPtr.Zero;
        try
        {
            Check(MFCreateAttributes(out attributes, 1), "attributes");
            SetUInt32(attributes, HardwareTransforms, hardware ? 1u : 0u);
            Check(MFCreateSinkWriterFromURL(path, IntPtr.Zero, attributes, out _writer), "sink writer");

            Check(MFCreateMediaType(out output), "output type");
            SetGuid(output, MajorType, MediaTypeVideo);
            SetGuid(output, SubType, FormatH264);
            // 화면 녹화는 글자가 많아 화소당 0.15 비트 정도 (1920×1080 30fps ≈ 9 Mbps)
            SetUInt32(output, AverageBitrate, (uint)Math.Clamp((long)_width * _height * fps * 15 / 100, 500_000, 40_000_000));
            SetUInt32(output, InterlaceMode, 2);  // 순차 주사
            SetUInt64(output, FrameSize, ((ulong)_width << 32) | (uint)_height);
            SetUInt64(output, FrameRate, ((ulong)fps << 32) | 1);
            SetUInt64(output, PixelAspectRatio, (1UL << 32) | 1);
            uint stream;
            Check(CallPtrPtr(_writer, 3, output, (IntPtr)(&stream)), "add stream");

            Check(MFCreateMediaType(out input), "input type");
            SetGuid(input, MajorType, MediaTypeVideo);
            SetGuid(input, SubType, FormatRgb32);
            SetUInt32(input, InterlaceMode, 2);
            SetUInt64(input, FrameSize, ((ulong)_width << 32) | (uint)_height);
            SetUInt64(input, FrameRate, ((ulong)fps << 32) | 1);
            SetUInt64(input, PixelAspectRatio, (1UL << 32) | 1);
            SetUInt32(input, DefaultStride, (uint)(_width * 4));  // 양수: 위에서 아래로
            Check(CallSetInput(_writer, stream, input), "input type");
            Check(Call0(_writer, 5), "begin");
            return stream;
        }
        finally
        {
            Release(ref attributes);
            Release(ref output);
            Release(ref input);
        }
    }

    public void Write(byte[] bgra, long time)
    {
        if (_finished || _writer == IntPtr.Zero) return;
        if (time <= _lastTime) time = _lastTime + 1;
        int length = _width * _height * 4;
        IntPtr buffer = IntPtr.Zero, sample = IntPtr.Zero;
        try
        {
            Check(MFCreateMemoryBuffer((uint)length, out buffer), "buffer");
            byte* data;
            uint max, current;
            Check(CallLock(buffer, &data, &max, &current), "lock");
            Marshal.Copy(bgra, 0, (IntPtr)data, length);
            Check(Call0(buffer, 4), "unlock");
            Check(CallUInt(buffer, 6, (uint)length), "length");
            Check(MFCreateSample(out sample), "sample");
            Check(CallPtr(sample, AddBufferSlot, buffer), "add buffer");
            Check(CallLong(sample, SetSampleTimeSlot, time), "time");
            Check(CallLong(sample, SetSampleDurationSlot, _frameDuration), "duration");
            Check(CallUIntPtr(_writer, 6, _stream, sample), "write");
            _lastTime = time;
        }
        finally
        {
            Release(ref sample);
            Release(ref buffer);
        }
    }

    public void Finish(long endTime)
    {
        if (_finished) return;
        _finished = true;
        if (_writer == IntPtr.Zero) return;
        if (_lastTime < 0) Write(new byte[_width * _height * 4], 0);  // 그림이 없으면 검은 화면 하나
        Check(Call0(_writer, 11), "finalize");
    }

    public void Dispose()
    {
        Release(ref _writer);
        if (_started)
        {
            _started = false;
            MFShutdown();
        }
    }

    // ───────── vtable 호출 ─────────

    private static IntPtr Slot(IntPtr instance, int index) => (*(IntPtr**)instance)[index];

    private static void SetUInt32(IntPtr attributes, Guid key, uint value)
    {
        Guid k = key;
        Check(((delegate* unmanaged<IntPtr, Guid*, uint, int>)Slot(attributes, SetUInt32Slot))(attributes, &k, value), "set");
    }

    private static void SetUInt64(IntPtr attributes, Guid key, ulong value)
    {
        Guid k = key;
        Check(((delegate* unmanaged<IntPtr, Guid*, ulong, int>)Slot(attributes, SetUInt64Slot))(attributes, &k, value), "set");
    }

    private static void SetGuid(IntPtr attributes, Guid key, Guid value)
    {
        Guid k = key, v = value;
        Check(((delegate* unmanaged<IntPtr, Guid*, Guid*, int>)Slot(attributes, SetGuidSlot))(attributes, &k, &v), "set");
    }

    private static int Call0(IntPtr instance, int slot) =>
        ((delegate* unmanaged<IntPtr, int>)Slot(instance, slot))(instance);

    private static int CallPtrPtr(IntPtr instance, int slot, IntPtr a, IntPtr b) =>
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)Slot(instance, slot))(instance, a, b);

    private static int CallSetInput(IntPtr writer, uint stream, IntPtr type) =>
        ((delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, int>)Slot(writer, 4))(writer, stream, type, IntPtr.Zero);

    private static int CallLock(IntPtr buffer, byte** data, uint* max, uint* current) =>
        ((delegate* unmanaged<IntPtr, byte**, uint*, uint*, int>)Slot(buffer, 3))(buffer, data, max, current);

    private static int CallUInt(IntPtr instance, int slot, uint value) =>
        ((delegate* unmanaged<IntPtr, uint, int>)Slot(instance, slot))(instance, value);

    private static int CallLong(IntPtr instance, int slot, long value) =>
        ((delegate* unmanaged<IntPtr, long, int>)Slot(instance, slot))(instance, value);

    private static int CallPtr(IntPtr instance, int slot, IntPtr value) =>
        ((delegate* unmanaged<IntPtr, IntPtr, int>)Slot(instance, slot))(instance, value);

    private static int CallUIntPtr(IntPtr instance, int slot, uint a, IntPtr b) =>
        ((delegate* unmanaged<IntPtr, uint, IntPtr, int>)Slot(instance, slot))(instance, a, b);

    private static void Release(ref IntPtr instance)
    {
        if (instance == IntPtr.Zero) return;
        ((delegate* unmanaged<IntPtr, uint>)Slot(instance, 2))(instance);
        instance = IntPtr.Zero;
    }

    private static void Check(int hr, string step)
    {
        if (hr < 0) throw new Win32Exception(hr, $"영상 인코더 오류 ({step}, 0x{hr:X8})");
    }
}
