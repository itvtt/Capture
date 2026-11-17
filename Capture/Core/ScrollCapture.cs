using System.Diagnostics;

namespace Capture.Core;

/// <summary>
/// 스크롤 캡처: 영역 가운데에 마우스를 두고 휠을 굴리며 화면을 읽어 이어 붙임.
/// 처음에는 한 칸씩, 한 칸에 몇 화소 움직이는지 알면 보이는 높이의 60% 씩 굴림.
/// 더 내려가지 않거나(끝) · 멈춤을 누르거나 · 마우스를 직접 움직이거나 · 높이가 한도를 넘으면 끝냄
/// </summary>
public static class ScrollCapture
{
    public const int MaxHeight = 20000;

    public static PixelFrame Run(PixelRect rect, CancellationToken cancel, IProgress<int>? progress = null)
    {
        using var grabber = new ScreenGrabber(rect);
        var original = Screens.Cursor();
        int cx = rect.X + rect.Width / 2, cy = rect.Y + rect.Height / 2;
        Mouse.MoveTo(cx, cy);
        try
        {
            Thread.Sleep(100);
            var first = Stable(grabber, cancel);
            var stitcher = new ScrollStitcher(first);
            progress?.Report(stitcher.Height);
            int notches = 1, still = 0;
            double perNotch = 0;
            while (!cancel.IsCancellationRequested && stitcher.Height < MaxHeight)
            {
                if (UserMoved(cx, cy)) break;
                Mouse.Wheel(notches);
                var next = Stable(grabber, cancel);
                var outcome = stitcher.Add(next, perNotch > 0 ? (int)(perNotch * notches) : 0);
                if (outcome == StitchOutcome.NoChange)
                {
                    if (++still >= 2) break;  // 끝까지 내려감
                    continue;
                }
                still = 0;
                if (outcome == StitchOutcome.Added)
                {
                    perNotch = (double)stitcher.LastShift / notches;
                    notches = Math.Clamp((int)(stitcher.BandHeight * 0.6 / Math.Max(1, perNotch)), 1, 15);
                }
                else notches = Math.Max(1, notches / 2);
                progress?.Report(stitcher.Height);
            }
            return stitcher.Build();
        }
        finally
        {
            Mouse.MoveTo(original.X, original.Y);
        }
    }

    /// <summary>화면이 멈출 때까지(부드러운 스크롤이 끝날 때까지) 읽음. 1.2초가 지나면 마지막 것</summary>
    private static PixelFrame Stable(ScreenGrabber grabber, CancellationToken cancel)
    {
        var last = grabber.Grab();
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 1200 && !cancel.IsCancellationRequested)
        {
            Thread.Sleep(60);
            var current = grabber.Grab();
            if (current.SameAs(last)) return current;
            last = current;
        }
        return last;
    }

    private static bool UserMoved(int x, int y)
    {
        var (cx, cy) = Screens.Cursor();
        return Math.Abs(cx - x) > 8 || Math.Abs(cy - y) > 8;
    }
}
