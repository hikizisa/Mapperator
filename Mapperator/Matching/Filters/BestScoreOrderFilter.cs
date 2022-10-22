﻿using Mapperator.Model;

namespace Mapperator.Matching.Filters;

/// <summary>
/// Looks ahead by BufferSize and yields the highest score match in the buffer.
/// This will eventually yield all items.
/// </summary>
public class BestScoreOrderFilter : IMatchFilter {
    private readonly IJudge judge;
    private readonly ReadOnlyMemory<MapDataPoint> pattern;

    private readonly PriorityQueue<Match, double> queue;

    public int BufferSize { get; }

    public int PatternIndex { get; set; }

    public IMinLengthProvider? MinLengthProvider { get; init; }

    public BestScoreOrderFilter(IJudge judge, ReadOnlyMemory<MapDataPoint> pattern, int bufferSize) {
        this.judge = judge;
        this.pattern = pattern;
        BufferSize = bufferSize;
        queue = new PriorityQueue<Match, double>(BufferSize);
    }

    public IEnumerable<Match> FilterMatches(IEnumerable<Match> matches) {
        queue.Clear();
        var bestScore = double.NegativeInfinity;

        foreach (var match in matches) {
            var score = RateMatchQuality(match);

            if (queue.Count >= BufferSize) {
                yield return queue.EnqueueDequeue(match, -score);
            } else {
                queue.Enqueue(match, -score);
            }

            if (!(score > bestScore) || MinLengthProvider is null) continue;

            bestScore = score;
            MinLengthProvider.MinLength = Math.Max(MinLengthProvider.MinLength, judge.MinLengthForScore(bestScore));
        }

        while (queue.Count > 0) {
            yield return queue.Dequeue();
        }
    }

    private double RateMatchQuality(Match match) {
        var mult = match.MinMult == 0 && double.IsPositiveInfinity(match.MaxMult) ? 1 : Math.Sqrt(match.MinMult * match.MaxMult);
        return judge.Judge(match.Sequence.Span, pattern.Span.Slice(PatternIndex, match.Length), 0, mult);
    }
}