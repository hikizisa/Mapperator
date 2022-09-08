﻿using Gma.DataStructures.StringSearch;
using Mapperator.Matching.Judges;
using Mapperator.Model;
using Mapping_Tools_Core.MathUtil;

namespace Mapperator.Matching.Matchers {
    public class TrieDataMatcher : IDataMatcher {
        private const int FirstSearchLength = 32;
        private static readonly double[] DistanceRanges = { 0, 3, 9 };  // Best values found by trial-and-error
        private const double PogBonus = 50;
        private const int MaxLookBack = 8;
        private const int MaxSearch = 100000;

        private readonly List<MapDataPoint[]> mapDataPoints = new();
        private readonly UkkonenTrie<ushort, int> rhythmTrie = new(1);
        private readonly IJudge judge;

        private WordPosition<int>? lastId;
        private int? lastLength;
        private int? lastLookBack;
        private int pogs;
        private double totalScore;
        private double totalMatchingCost;
        private double totalRelationScore;
        private int totalSearched;
        private (ReadOnlyMemory<ushort>, ReadOnlyMemory<ushort>)[]? patternRhythmString;

        public TrieDataMatcher() : this(new SuperJudge()) { }

        public TrieDataMatcher(IJudge judge) {
            this.judge = judge;
        }

        public void AddData(IEnumerable<MapDataPoint> data) {
            var dataList = data.ToArray();
            var index = mapDataPoints.Count;
            var rhythmString = ToRhythmString(dataList);
            mapDataPoints.Add(dataList);
            rhythmTrie.Add(rhythmString, index);
        }

        public static ReadOnlyMemory<ushort> ToRhythmString(ReadOnlySpan<MapDataPoint> data) {
            var rhythmString = new ushort[data.Length];
            var i = 0;

            foreach (var mapDataPoint in data) {
                var ho = ToRhythmToken(mapDataPoint);
                rhythmString[i] = ho;
                i++;
            }

            return rhythmString.AsMemory();
        }

        public static ushort ToRhythmToken(MapDataPoint mapDataPoint) {
            const int gapResolution = 6;
            const int gapRange = 9;
            var dist = (int) MathHelper.Clamp(mapDataPoint.Spacing / 4, 0, 255);
            var gap = MathHelper.Clamp((int) Math.Log2(mapDataPoint.BeatsSince) + gapResolution, 0, gapRange - 1);
            var typeByte = mapDataPoint.DataType switch {
                DataType.Hit => gap,
                DataType.Spin => gapRange + gap,
                DataType.Release => mapDataPoint.Repeats switch {
                    0 => gapRange * 2 + gap,
                    1 => gapRange * 3 + gap,
                    _ => gapRange * 4 + gap
                },
                _ => gap
            };
            // (type   ,dist    )
            // (1111111100000000)
            return (ushort)((typeByte << 8) | dist);
        }

        public IEnumerable<MapDataPoint> FindSimilarData(ReadOnlyMemory<MapDataPoint> pattern, Func<MapDataPoint, bool> isValidFunc) {
            Console.WriteLine("Searching for matches");
            // We want to replace the previous parts of the pattern with the matches we found so the next matches have a better chance
            // of continuing the previous pattern
            patternRhythmString = ToDistanceRanges(ToRhythmString(pattern.Span).Span, DistanceRanges);
            var newPattern = pattern.ToArray();
            lastId = null;
            pogs = 0;
            totalScore = 0;
            totalMatchingCost = 0;
            totalRelationScore = 0;
            for (var i = 0; i < pattern.Length; i++) {
                var match = FindBestMatch(newPattern, i, isValidFunc);
                newPattern[i] = match;
                yield return match;
            }

            patternRhythmString = null;
            Console.WriteLine($"Pograte = {(float)pogs / pattern.Length}");
            Console.WriteLine($"Score = {totalScore / pattern.Length}");
            Console.WriteLine($"Avg matching cost = {totalMatchingCost / pattern.Length}");
            Console.WriteLine($"Avg relation score = {totalRelationScore / pattern.Length}");
            Console.WriteLine($"Avg searched = {totalSearched / pattern.Length}");
        }

        public MapDataPoint FindBestMatch(ReadOnlySpan<MapDataPoint> pattern, int i, Func<MapDataPoint, bool> isValidFunc) {
            var localPatternRhythmString = patternRhythmString ?? ToDistanceRanges(ToRhythmString(pattern).Span, DistanceRanges);
            var searchLength = Math.Min(FirstSearchLength, pattern.Length);
            var numSearched = 0;

            var bestScore = double.NegativeInfinity;
            var best = new WordPosition<int>(0, 0);
            var bestLength = 0;
            var bestLookBack = 0;

            // First try the pog option
            if (lastId.HasValue && lastLength.HasValue && lastLookBack.HasValue && lastLength - lastLookBack > 1) {
                var pogLength = lastLength.Value;
                var lookBack = lastLookBack.Value + 1;
                var pogPos = new WordPosition<int>(lastId.Value.CharPosition + 1, lastId.Value.Value);

                if (!IsValidSeries(pogPos, pogLength - lookBack, isValidFunc)) {
                    goto PogTried;
                }

                // Rate the quality of the match
                bestScore = RateMatchQuality(pogPos, pattern, i, pogLength, lookBack) + PogBonus;
                best = pogPos;
                bestLength = pogLength;
                bestLookBack = lookBack;
            }
            PogTried:

            while (searchLength > 0 && bestScore < 0.5 * BestPossibleScore(i, searchLength, pattern.Length)) {
                var lookBack = GetLookBack(i, searchLength, pattern.Length);

                for (var j = 0; j < localPatternRhythmString.Length; j++) {
                    var min = localPatternRhythmString[j].Item1.Slice(i - lookBack, searchLength);
                    var max = localPatternRhythmString[j].Item2.Slice(i - lookBack, searchLength);
                    var foundAnything = false;

                    var result = rhythmTrie
                        .RetrieveSubstringsRange(min, max);

                    // Find the best match
                    foreach (var wordPosition in result) {
                        if (numSearched++ >= MaxSearch) {
                            break;
                        }

                        // Get the position of the middle data point
                        var middlePos = new WordPosition<int>(wordPosition.CharPosition + lookBack, wordPosition.Value);

                        if (!IsValidSeries(middlePos, searchLength - lookBack, isValidFunc)) {
                            continue;
                        }

                        foundAnything = true;

                        // Rate the quality of the match
                        var score = RateMatchQuality(middlePos, pattern, i, searchLength, lookBack);

                        if (!(score > bestScore)) continue;

                        bestScore = score;
                        best = middlePos;
                        bestLength = searchLength;
                        bestLookBack = lookBack;
                    }

                    if (foundAnything)
                        break;
                }

                searchLength--;

                if (numSearched >= MaxSearch) {
                    break;
                }
            }

            totalScore += double.IsNegativeInfinity(bestScore) ? 0 : bestScore;
            var matchingCost = judge.MatchingCost(pattern[i], GetMapDataPoint(best));
            var relationScore = judge.RelationScore(pattern, mapDataPoints[best.Value].AsSpan(), i, best.CharPosition, 50);
            totalMatchingCost += matchingCost;
            totalRelationScore += relationScore;
            totalSearched += numSearched;
            if (lastId.HasValue && best.Value == lastId.Value.Value &&
                best.CharPosition == lastId.Value.CharPosition + 1) {
                pogs++;
            }

            lastId = best;
            lastLength = bestLength;
            lastLookBack = bestLookBack;
            Console.WriteLine($"match {i}, id = {lastId}, num searched = {numSearched}, length = {bestLength}, score = {bestScore}, matching cost = {matchingCost}, relation = {relationScore}");

            return GetMapDataPoint(best);
        }

        private (ReadOnlyMemory<ushort>, ReadOnlyMemory<ushort>)[] ToDistanceRanges(ReadOnlySpan<ushort> query, double[] widths) {
            var ranges = new (ReadOnlyMemory<ushort>, ReadOnlyMemory<ushort>)[widths.Length];

            for (var i = 0; i < widths.Length; i++) {
                ranges[i] = ToDistanceRange(query, widths[i]);
            }

            return ranges;
        }

        private (ReadOnlyMemory<ushort>, ReadOnlyMemory<ushort>) ToDistanceRange(ReadOnlySpan<ushort> query, double width) {
            var min = new ushort[query.Length];
            var max = new ushort[query.Length];
            for (var i = 0; i < query.Length; i++) {
                var token = query[i];
                var rhythmPart = token >> 8;
                var distancePart = token & 255;
                var minDistance = MathHelper.Clamp((int)(distancePart - width), 0, 255);
                var maxDistance = MathHelper.Clamp((int)(distancePart + width), 0, 255);
                min[i] = (ushort)((rhythmPart << 8) | minDistance);
                max[i] = (ushort)((rhythmPart << 8) | maxDistance);
            }

            return (min.AsMemory(), max.AsMemory());
        }

        private int GetLookBack(int i, int length, int totalLength) {
            return MathHelper.Clamp(Math.Min(length / 2, MaxLookBack), i + length - totalLength, i);
        }

        private double BestPossibleScore(int i, int length, int totalLength) {
            return judge.BestPossibleScore(length, GetLookBack(i, length, totalLength));
        }

        private double RateMatchQuality(WordPosition<int> pos, ReadOnlySpan<MapDataPoint> pattern, int index, int length, int lookBack) {
            return judge.Judge(mapDataPoints[pos.Value].AsSpan().Slice(pos.CharPosition - lookBack, length),
                pattern.Slice(index - lookBack, length), lookBack);
        }

        private bool IsValidSeries(WordPosition<int> wordPosition, int count, Func<MapDataPoint, bool> isValidFunc) {
            double cumulativeAngle = 0;
            var pos = Vector2.Zero;
            double beatsSince = 0;
            for (var i = 0; i < count; i++) {
                var dataPoint = GetMapDataPoint(wordPosition, i);

                cumulativeAngle += dataPoint.Angle;
                var dir = Vector2.Rotate(Vector2.UnitX, cumulativeAngle);
                pos += dataPoint.Spacing * dir;
                beatsSince += dataPoint.BeatsSince;

                // Test a new datapoint with the cumulated angle and distance and gap
                dataPoint.BeatsSince = beatsSince;
                dataPoint.Spacing = pos.Length;
                dataPoint.Angle = pos.Theta;

                if (!isValidFunc(dataPoint)) {
                    return false;
                }
            }

            return true;
        }

        private MapDataPoint GetMapDataPoint(WordPosition<int> wordPosition, int offset = 0) {
            return mapDataPoints[wordPosition.Value][wordPosition.CharPosition + offset];
        }

        private bool WordPositionInRange(WordPosition<int> wordPosition, int offset = 0) {
            return wordPosition.Value < mapDataPoints.Count && wordPosition.Value >= 0 &&
                wordPosition.CharPosition + offset < mapDataPoints[wordPosition.Value].Length &&
                wordPosition.CharPosition + offset >= 0;
        }

        private int GetMatchLength(WordPosition<int> wordPosition, ReadOnlySpan<ushort> pattern) {
            var length = 0;
            while (wordPosition.CharPosition + length < mapDataPoints[wordPosition.Value].Length &&
                   length < pattern.Length &&
                ToRhythmToken(GetMapDataPoint(wordPosition, length)) ==
                   pattern[length]) {
                length++;
            }

            return length;
        }
    }
}