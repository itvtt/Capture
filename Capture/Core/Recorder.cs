using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace Capture.Core;

/// <summary>
/// 녹화: 한 스레드가 정해진 간격으로 화면(+마우스 포인터)을 읽고, 다른 스레드가 파일에 씀.
/// 쓰기가 밀리면 그림을 건너뜀 (시각은 실제대로라 재생 속도는 맞음). Stop 하면 남은 그림을 다 쓰고 파일을 닫음
/// </summary>
public sealed class Recorder
{
    private readonly PixelRect _rect;
    private readonly int _fps;
    private readonly Func<IFrameWriter> _createWriter;
    private readonly BlockingCollection<(byte[] Pixels, long Time)> _queue = new(boundedCapacity: 6);
    private readonly ConcurrentBag<byte[]> _pool = [];
    private readonly Stopwatch _clock = new();
    private volatile bool _stop;
    private Thread? _captureThread, _encodeThread;
    private Exception? _error;
    private long _endTime;

    public Recorder(PixelRect rect, int framesPerSecond, Func<IFrameWriter> createWriter)
    {
        _rect = rect;
        _fps = framesPerSecond;
        _createWriter = createWriter;
    }

    public TimeSpan Elapsed => _clock.Elapsed;
    public int Captured { get; private set; }
    public int Dropped { get; private set; }

    public void Start()
    {
        // 파일과 인코더를 먼저 만들어 실패하면 바로 알림
        var ready = new ManualResetEventSlim();
        IFrameWriter? writer = null;
        _encodeThread = new Thread(() =>
        {
            try { writer = _createWriter(); }
            catch (Exception error) { _error = error; }
            ready.Set();
            if (writer is null) return;
            try
            {
                foreach (var (pixels, time) in _queue.GetConsumingEnumerable())
                {
                    if (_error is null) writer.Write(pixels, time);
                    _pool.Add(pixels);
                }
                if (_error is null) writer.Finish(_endTime);
            }
            catch (Exception error) { _error ??= error; foreach (var _ in _queue.GetConsumingEnumerable()) { } }
            finally { writer.Dispose(); }
        }) { IsBackground = true, Name = "Capture encoder" };
        _encodeThread.Start();
        ready.Wait();
        if (_error is { } failed)
        {
            _encodeThread.Join();
            throw new InvalidOperationException(failed.Message, failed);
        }

        _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "Capture frames", Priority = ThreadPriority.AboveNormal };
        _clock.Start();
        _captureThread.Start();
    }

    private void CaptureLoop()
    {
        try
        {
            using var grabber = new ScreenGrabber(_rect);
            long interval = Stopwatch.Frequency / _fps, next = 0;
            int size = _rect.Width * _rect.Height * 4;
            while (!_stop)
            {
                long now = _clock.ElapsedTicks;
                if (now < next)
                {
                    int wait = (int)((next - now) * 1000 / Stopwatch.Frequency);
                    if (wait > 1) Thread.Sleep(wait - 1);
                    else Thread.Yield();
                    continue;
                }
                next = Math.Max(next + interval, now - interval);  // 늦어지면 따라잡되 몰아서 찍지 않음
                if (!_pool.TryTake(out var buffer)) buffer = new byte[size];
                grabber.Grab(buffer, cursor: true, layered: false);
                long time = _clock.Elapsed.Ticks;
                if (_queue.TryAdd((buffer, time))) Captured++;
                else
                {
                    Dropped++;
                    _pool.Add(buffer);
                }
            }
        }
        catch (Exception error) { _error ??= error; }
        finally
        {
            _endTime = _clock.Elapsed.Ticks;
            _queue.CompleteAdding();
        }
    }

    /// <summary>녹화 중 오류 (화면을 읽지 못함 · 인코더 오류)</summary>
    public Exception? Error => _error;

    /// <summary>멈추고 파일을 끝까지 씀. 오류가 있었으면 던짐</summary>
    public void Stop()
    {
        _stop = true;
        _captureThread?.Join();
        _encodeThread?.Join();
        _clock.Stop();
        if (_error is { } error) throw new IOException(error.Message, error);
    }
}
