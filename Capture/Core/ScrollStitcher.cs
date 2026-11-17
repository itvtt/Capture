using System.IO;

namespace Capture.Core;

public enum StitchOutcome { Added, NoChange, NoOverlap }

/// <summary>
/// 스크롤 캡처의 이어 붙이기. 같은 영역을 스크롤하며 읽은 그림을 차례로 넣으면 긴 그림 하나를 만듦.
/// 줄마다 8화소 칸의 밝기 합을 내어 앞 그림의 아래쪽과 새 그림의 위쪽이 겹치는 만큼(밀린 거리)을 찾고, 새로 보인 줄만 붙임.
/// 글자 가장자리가 그릴 때마다 조금씩 다른 것(1~3 차이)은 같은 줄로 봄.
/// 위아래에 고정된 띠(머리글 · 바닥글)는 처음 두 그림에서 찾아 한 번만 넣음. 오른쪽 끝(스크롤 막대)은 비교하지 않음
/// </summary>
public sealed class ScrollStitcher
{
    private const int Block = 8;
    /// <summary>칸(8화소)의 평균 밝기가 이만큼까지 달라도 같은 줄</summary>
    private const int Tolerance = 6;
    private readonly int _width, _height, _compareWidth, _blocks;
    private readonly MemoryStream _rows = new();
    private PixelFrame _last;
    private Row[] _lastRows;
    private int _top = -1, _bottom = -1;

    private readonly record struct Row(int[] Sums, bool Plain);

    public ScrollStitcher(PixelFrame first)
    {
        _width = first.Width;
        _height = first.Height;
        _compareWidth = _width >= 200 ? _width - Math.Min(32, _width / 10) : _width;
        _blocks = (_compareWidth + Block - 1) / Block;
        _last = first;
        _lastRows = Describe(first);
    }

    /// <summary>지금까지 만든 그림의 높이 (바닥글 포함)</summary>
    public int Height => _top < 0 ? _height : (int)(_rows.Length / (_width * 4)) + _bottom;

    /// <summary>마지막으로 찾은 밀린 거리 (화소). 처음에는 0</summary>
    public int LastShift { get; private set; }

    /// <summary>위아래 고정 띠를 뺀, 스크롤되는 부분의 높이</summary>
    public int BandHeight => _top < 0 ? _height : _height - _top - _bottom;

    public StitchOutcome Add(PixelFrame next, int expectedShift = 0)
    {
        if (next.Width != _width || next.Height != _height) throw new ArgumentException("크기가 다른 그림");
        var rows = Describe(next);

        if (_top < 0)
        {
            int top = 0;
            while (top < _height && Same(rows[top], _lastRows[top])) top++;
            if (top == _height) return StitchOutcome.NoChange;
            int bottom = 0;
            while (bottom < _height - top && Same(rows[_height - 1 - bottom], _lastRows[_height - 1 - bottom])) bottom++;
            // 고정 띠가 너무 크면 (빈 여백이 우연히 같은 것) 스크롤되는 부분이 남도록 줄임
            int limit = _height / 3;
            _top = Math.Min(top, limit);
            _bottom = Math.Min(bottom, limit);
            // 첫 그림에서 바닥글 위까지
            _rows.Write(_last.Pixels, 0, (_height - _bottom) * _width * 4);
        }

        int bandTop = _top, bandEnd = _height - _bottom, band = bandEnd - bandTop;
        bool same = true;
        for (int y = bandTop; y < bandEnd && same; y++) same = Same(rows[y], _lastRows[y]);
        if (same || band < 8)
        {
            _last = next;
            _lastRows = rows;
            return StitchOutcome.NoChange;
        }

        int shift = FindShift(_lastRows, rows, bandTop, band, expectedShift);
        StitchOutcome outcome;
        if (shift > 0)
        {
            AppendRows(next, bandEnd - shift, shift);
            LastShift = shift;
            outcome = StitchOutcome.Added;
        }
        else
        {
            // 겹치는 곳이 없음 (너무 많이 밀림): 이음매가 생겨도 새 부분을 통째로 붙임
            AppendRows(next, bandTop, band);
            LastShift = band;
            outcome = StitchOutcome.NoOverlap;
        }
        _last = next;
        _lastRows = rows;
        return outcome;
    }

    /// <summary>
    /// 앞 그림의 [top+s, end) 줄과 새 그림의 [top, end-s) 줄이 같은 s. 다른 줄(깜박이는 글자 커서 등)은 3% 까지 봐 줌.
    /// 같은 줄 중 한 색이 아닌 줄이 가장 많은 s, 같으면 예상 거리에 가까운(모르면 작은) s
    /// </summary>
    private int FindShift(Row[] before, Row[] after, int top, int band, int expected)
    {
        int minOverlap = Math.Max(8, band / 10);
        int best = 0, bestScore = -1, bestDistance = int.MaxValue;
        for (int s = 1; s <= band - minOverlap; s++)
        {
            int overlap = band - s, allowed = overlap * 3 / 100 + 1, mismatches = 0, score = 0;
            for (int i = 0; i < overlap; i++)
            {
                var a = before[top + s + i];
                if (!Same(a, after[top + i]))
                {
                    if (++mismatches > allowed) break;
                }
                else if (!a.Plain) score++;
            }
            if (mismatches > allowed) continue;
            int distance = expected > 0 ? Math.Abs(s - expected) : s;
            if (score > bestScore || (score == bestScore && distance < bestDistance))
            {
                best = s;
                bestScore = score;
                bestDistance = distance;
            }
        }
        // 모두 한 색인 줄뿐이면 (빈 화면) 어디가 맞는지 알 수 없음
        return bestScore >= 2 ? best : 0;
    }

    private bool Same(Row a, Row b)
    {
        var x = a.Sums;
        var y = b.Sums;
        for (int i = 0; i < _blocks; i++)
        {
            int limit = Tolerance * Math.Min(Block, _compareWidth - i * Block);
            if (Math.Abs(x[i] - y[i]) > limit) return false;
        }
        return true;
    }

    private void AppendRows(PixelFrame frame, int from, int count) =>
        _rows.Write(frame.Pixels, from * _width * 4, count * _width * 4);

    /// <summary>만든 그림: 머리글 + 이어 붙인 부분 + 마지막 그림의 바닥글</summary>
    public PixelFrame Build()
    {
        if (_top < 0) return _last;
        int rows = (int)(_rows.Length / (_width * 4));
        var result = new PixelFrame(_width, rows + _bottom);
        _rows.Position = 0;
        _rows.ReadExactly(result.Pixels, 0, rows * _width * 4);
        Buffer.BlockCopy(_last.Pixels, (_height - _bottom) * _width * 4, result.Pixels, rows * _width * 4, _bottom * _width * 4);
        return result;
    }

    /// <summary>줄마다 8화소 칸의 밝기 합 (밝기 = (R·2 + G·5 + B) / 8), 그리고 한 색인 줄인지</summary>
    private Row[] Describe(PixelFrame frame)
    {
        var rows = new Row[_height];
        for (int y = 0; y < _height; y++)
        {
            var pixels = frame.Row(y);
            var sums = new int[_blocks];
            uint first = pixels[0] & 0x00FFFFFF;
            bool plain = true;
            for (int x = 0; x < _compareWidth; x++)
            {
                uint p = pixels[x];
                plain &= (p & 0x00FFFFFF) == first;
                sums[x / Block] += (int)(((p >> 16) & 0xFF) * 2 + ((p >> 8) & 0xFF) * 5 + (p & 0xFF)) >> 3;
            }
            rows[y] = new Row(sums, plain);
        }
        return rows;
    }
}
