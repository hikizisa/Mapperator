﻿using Gma.DataStructures.StringSearch;
using Mapperator.Matching.Judges;
using Mapperator.Model;
using Mapping_Tools_Core.MathUtil;

namespace Mapperator.Matching.Matchers {
    public class TrieDataMatcher : IDataMatcher {
        private const int FirstSearchLength = 32;
        private const double PogBonus = 25;
        private const int MaxLookBack = 4;
        private const int MaxSearch = 400;

        private readonly List<MapDataPoint[]> mapDataPoints = new();
        private readonly UkkonenTrie<byte, int> rhythmTrie = new(1);
        private readonly IJudge judge;

        private WordPosition<int>? lastId;
        private int? lastLength;
        private int pogs;
        private ReadOnlyMemory<byte>? patternRhythmString;

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

        public static ReadOnlyMemory<byte> ToRhythmString(ReadOnlySpan<MapDataPoint> data) {
            var rhythmString = new byte[data.Length];
            var i = 0;

            foreach (var mapDataPoint in data) {
                var ho = ToRhythmToken(mapDataPoint);
                rhythmString[i] = ho;
                i++;
            }

            return rhythmString.AsMemory();
        }

        public static byte ToRhythmToken(MapDataPoint mapDataPoint) {
            const int gapResolution = 6;
            const int gapRange = 9;
            var gap = (byte) MathHelper.Clamp((int) Math.Log2(mapDataPoint.BeatsSince) + gapResolution, 0, gapRange - 1);
            return mapDataPoint.DataType switch {
                DataType.Hit => gap,
                DataType.Spin => (byte)(gapRange + gap),
                DataType.Release => mapDataPoint.Repeats switch {
                    0 => (byte)(gapRange * 2 + gap),
                    1 => (byte)(gapRange * 3 + gap),
                    _ => (byte)(gapRange * 4 + gap)
                },
                _ => gap
            };
        }

        public IEnumerable<MapDataPoint> FindSimilarData(ReadOnlyMemory<MapDataPoint> pattern, Func<MapDataPoint, bool> isValidFunc) {
            Console.WriteLine("Searching for matches");
            // We want to replace the previous parts of the pattern with the matches we found so the next matches have a better chance
            // of continuing the previous pattern
            patternRhythmString = ToRhythmString(pattern.Span);
            var newPattern = pattern.ToArray();
            lastId = null;
            pogs = 0;
            for (var i = 0; i < pattern.Length; i++) {
                var match = FindBestMatch(newPattern, i, isValidFunc);
                newPattern[i] = match;
                yield return match;
            }

            patternRhythmString = null;
            Console.WriteLine($"Pograte = {(float)pogs / pattern.Length}");
        }

        public MapDataPoint FindBestMatch(ReadOnlySpan<MapDataPoint> pattern, int i, Func<MapDataPoint, bool> isValidFunc) {
            var localPatternRhythmString = patternRhythmString ?? ToRhythmString(pattern);
            var searchLength = Math.Min(FirstSearchLength, localPatternRhythmString.Length);
            var numSearched = 0;

            var bestScore = double.NegativeInfinity;
            var best = new WordPosition<int>(0, 0);
            var bestLength = 0;

            // First try the pog option
            if (lastId.HasValue && lastLength.HasValue && lastLength.Value > 2) {
                var pogLength = lastLength.Value % 2 == 0 ? lastLength.Value - 2 : lastLength.Value - 1;
                var lookBack = GetLookBack(i, pogLength, localPatternRhythmString.Length);
                var pogPos = new WordPosition<int>(lastId.Value.CharPosition + 1, lastId.Value.Value);

                if (!IsValidSeries(pogPos, pogLength - lookBack, isValidFunc)) {
                    goto PogTried;
                }

                // Rate the quality of the match
                bestScore = RateMatchQuality(pogPos, pattern, i, pogLength, lookBack) + PogBonus;
                best = pogPos;
                bestLength = pogLength;
            }
            PogTried:

            while (searchLength > 0 && bestScore < 0.5 * BestPossibleScore(i, searchLength, localPatternRhythmString.Length)) {
                var lookBack = GetLookBack(i, searchLength, localPatternRhythmString.Length);
                var query = localPatternRhythmString.Span.Slice(i - lookBack, searchLength);

                var result = rhythmTrie
                    .RetrieveSubstrings(query);

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

                    // Rate the quality of the match
                    var score = RateMatchQuality(middlePos, pattern, i, searchLength, lookBack);

                    if (!(score > bestScore)) continue;

                    bestScore = score;
                    best = middlePos;
                    bestLength = searchLength;
                }
                searchLength--;

                if (numSearched >= MaxSearch) {
                    break;
                }
            }

            if (lastId.HasValue && best.Value == lastId.Value.Value &&
                best.CharPosition == lastId.Value.CharPosition + 1) {
                pogs++;
            }

            lastId = best;
            lastLength = bestLength;
            Console.WriteLine($"match {i}, id = {lastId}, score = {bestScore}");

            return GetMapDataPoint(best);
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

        private int GetMatchLength(WordPosition<int> wordPosition, ReadOnlySpan<byte> pattern) {
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