namespace Capture.Core;

/// <summary>캡처 방식: 영역(창 · 전체 화면 포함) · 스크롤 · GIF · 영상</summary>
public enum CaptureMode { Region, Scroll, Gif, Video }

/// <summary>고른 결과: 화면의 사각형, 영역 캡처면 그때 멈춰 둔 그림에서 잘라 낸 것</summary>
public sealed record Selection(CaptureMode Mode, PixelRect Rect, PixelFrame? Image);
