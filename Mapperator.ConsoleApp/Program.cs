﻿using CommandLine;
using Mapping_Tools_Core;
using Mapping_Tools_Core.BeatmapHelper;
using Mapping_Tools_Core.BeatmapHelper.IO.Editor;
using Mapping_Tools_Core.MathUtil;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Mapperator.ConsoleApp.Resources;
using Mapperator.Construction;
using Mapperator.Matching;
using Mapperator.Matching.Matchers;
using Mapping_Tools_Core.BeatmapHelper.Enums;
using OsuParsers.Database.Objects;
using OsuParsers.Enums;
using OsuParsers.Enums.Database;

namespace Mapperator.ConsoleApp {
    public static class Program {
        [Verb("count", HelpText = "Count the amount of beatmaps available matching the specified filter.")]
        private class CountOptions {
            [Option('c', "collection", HelpText = "Name of osu! collection to be extracted.")]
            public string? CollectionName { get; set; }

            [Option('i', "minId", HelpText = "Filter the minimum beatmap set ID.")]
            public int? MinId { get; set; }

            [Option('s', "status", HelpText = "Filter the ranked status.")]
            public RankedStatus? RankedStatus { get; set; }

            [Option('m', "mode", HelpText = "Filter the game mode.")]
            public Ruleset? Ruleset { get; set; }

            [Option('r', "starRating", HelpText = "Filter the star rating.")]
            public double? MinStarRating { get; set; }
        }

        [Verb("extract", HelpText = "Extract beatmap data from an osu! collection.")]
        private class ExtractOptions {
            [Option('c', "collection", HelpText = "Name of osu! collection to be extracted.")]
            public string? CollectionName { get; set; }

            [Option('i', "minId", HelpText = "Filter the minimum beatmap set ID.")]
            public int? MinId { get; set; }

            [Option('s', "status", HelpText = "Filter the ranked status.")]
            public RankedStatus? RankedStatus { get; set; }

            [Option('m', "mode", HelpText = "Filter the game mode.")]
            public Ruleset? Ruleset { get; set; }

            [Option('r', "starRating", HelpText = "Filter the star rating.")]
            public double? MinStarRating { get; set; }

            [Option('o', "output", Required = true, HelpText = "Filename of the output.")]
            public string? OutputName { get; set; }
        }

        [Verb("build", HelpText = "Build a data structure using extracted beatmap data.")]
        private class BuildOptions {
            [Option('d', "data", Required = true, HelpText = "Input extracted beatmap data for the graph.")]
            public string? DataPath { get; set; }

            [Option('h', "structOutput", Required = true, HelpText = "Filename for the generated data structure.")]
            public string? OutputStructName { get; set; }

            [Option('m', "matcher", Default = MatcherType.Trie, HelpText = "The type of data matcher to use.")]
            public MatcherType MatcherType { get; set; }
        }

        [Verb("convert", HelpText = "Reconstruct a beatmap using extracted beatmap data.")]
        private class ConvertOptions {
            [Option('d', "data", Required = true, HelpText = "Input extracted beatmap data for the conversion.")]
            public string? DataPath { get; set; }

            [Option('i', "input", Required = true, HelpText = "Input beatmap to be converted.")]
            public string? InputBeatmapPath { get; set; }

            [Option('o', "output", Required = true, HelpText = "Filename of the output.")]
            public string? OutputName { get; set; }

            [Option('g', "structInput", HelpText = "Serialized data structure file to speed up matching.")]
            public string? InputStructName { get; set; }

            [Option('h', "structOutput", HelpText = "Filename for the generated data structure.")]
            public string? OutputStructName { get; set; }

            [Option('m', "matcher", Default = MatcherType.Trie, HelpText = "The type of data matcher to use.")]
            public MatcherType MatcherType { get; set; }
        }

        [Verb("search", HelpText = "Search your entire Songs folder for a specific pattern.")]
        class SearchOptions {
            [Option('p', "pattern", Required = true, HelpText = "Prints all messages to standard output.")]
            public string? Pattern { get; set; }

            [Option('c', "collection", HelpText = "Name of osu! collection to be searched.")]
            public string? CollectionName { get; set; }
        }

        private static int Main(string[] args) {
            ConfigManager.LoadConfig();

            return Parser.Default.ParseArguments<CountOptions, ExtractOptions, BuildOptions, ConvertOptions, SearchOptions>(args)
              .MapResult(
                  (CountOptions opts) => DoDataCount(opts),
                (ExtractOptions opts) => DoDataExtraction(opts),
                (BuildOptions opts) => DoBuildGraph(opts),
                (ConvertOptions opts) => DoMapConvert(opts),
                (SearchOptions opts) => DoPatternSearch(opts),
                _ => 1);
        }

        private static int DoPatternSearch(SearchOptions opts) {
            var matches = 0;
            var i = 0;
            foreach (var path in string.IsNullOrEmpty(opts.CollectionName) ? Directory.EnumerateFiles(ConfigManager.Config.SongsPath, "*.osu",
                new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, ReturnSpecialDirectories = false }) :
                DbManager.GetCollection(opts.CollectionName).Select(o => Path.Combine(ConfigManager.Config.SongsPath, o.FolderName, o.FileName))) {
                PatternSearchMap(path, opts.Pattern, i++, ref matches);
            }
            return 0;
        }

        private static void PatternSearchMap(string path, string? pattern, int i, ref int matches) {
            if (pattern is null) throw new ArgumentNullException(nameof(pattern));

            if (i % 1000 == 0) {
                Console.Write('.');
            }
            //Console.WriteLine(path);

            var startBracketIndex = pattern.IndexOf("(", StringComparison.Ordinal);
            var endBracketIndex = pattern.IndexOf(")", StringComparison.Ordinal);
            var t = InputParsers.ParseOsuTimestamp(pattern).TotalMilliseconds;
            var l = 0;
            if (startBracketIndex != -1) {
                if (endBracketIndex == -1) {
                    endBracketIndex = pattern.Length - 1;
                }

                // Get the part of the code between the brackets
                var comboNumbersString = pattern.Substring(startBracketIndex + 1, endBracketIndex - startBracketIndex - 1);

                l = comboNumbersString.Split(',').Length;
            }

            try {
                var beatmap = new BeatmapEditor(path).ReadFile();
                var en = beatmap.QueryTimeCode(pattern);
                var hos = en.ToArray();
                if (hos.Length != l || !Precision.AlmostEquals(hos[0].StartTime, t)) return;
                matches++;
                Console.WriteLine(Strings.Program_PatternSearchMap_Found_match__0__in_beatmap___1_, matches, path);
            } catch (Exception e) {
                Console.WriteLine(Strings.Program_PatternSearchMap_Can_t_parse_this_map__ + path);
                Console.WriteLine(e);
            }
        }

        private static IDataMatcher GetDataMatcher(MatcherType matcherType) {
            return matcherType switch {
                MatcherType.Simple => new SimpleDataMatcher(),
                MatcherType.Trie => new TrieDataMatcher(),
                _ => new TrieDataMatcher()
            };
        }

        private static int DoBuildGraph(BuildOptions opts) {
            if (opts.OutputStructName is null) throw new ArgumentNullException(nameof(opts));
            if (opts.DataPath is null) throw new ArgumentNullException(nameof(opts));

            var trainData = DataSerializer.DeserializeBeatmapData(File.ReadLines(Path.ChangeExtension(opts.DataPath, ".txt")));
            var matcher = GetDataMatcher(opts.MatcherType);

            if (matcher is not ISerializable sMatcher) {
                Console.WriteLine(Strings.Program_DoBuildGraph_The__0__matcher_is_not_compatible_with_building_, opts.MatcherType);
                return 0;
            }

            foreach (var str in trainData) {
                matcher.AddData(str);
            }

            using Stream file = File.Create(Path.ChangeExtension(opts.OutputStructName, sMatcher.DefaultExtension));
            sMatcher.Save(file);
            return 0;
        }

        private static int DoMapConvert(ConvertOptions opts) {
            if (opts.DataPath is null) throw new ArgumentNullException(nameof(opts));

            // Start time measurement
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            Console.WriteLine(Strings.Program_DoMapConvert_Extracting_data___);
            var trainData = DataSerializer.DeserializeBeatmapData(File.ReadLines(Path.ChangeExtension(opts.DataPath, ".txt")));
            var map = new BeatmapEditor(Path.ChangeExtension(opts.InputBeatmapPath, ".osu")).ReadFile();
            var input = new DataExtractor().ExtractBeatmapData(map).ToArray();

            var matcher = GetDataMatcher(opts.MatcherType);

            // Add the data to the matcher or load the data
            Console.WriteLine(Strings.Program_DoMapConvert_Adding_data___);
            if (matcher is ISerializable sMatcher && 
                !string.IsNullOrEmpty(opts.InputStructName) && 
                File.Exists(Path.ChangeExtension(opts.InputStructName, sMatcher.DefaultExtension))) {
                using Stream file = File.OpenRead(Path.ChangeExtension(opts.InputStructName, sMatcher.DefaultExtension));
                sMatcher.Load(trainData, file);
            } else {
                Stopwatch buildStopwatch = new Stopwatch();
                buildStopwatch.Start();

                foreach (var str in trainData) {
                    matcher.AddData(str);
                    Console.Write('.');
                }

                buildStopwatch.Stop();
                Console.WriteLine(Strings.Program_DoMapConvert_Elapsed_Time_is, buildStopwatch.ElapsedMilliseconds.ToString());

                if (matcher is ISerializable sMatcher2 && !string.IsNullOrEmpty(opts.OutputStructName)) {
                    using Stream file = File.Create(Path.ChangeExtension(opts.OutputStructName, sMatcher2.DefaultExtension));
                    sMatcher2.Save(file);
                }
            }

            // Construct new beatmap
            Console.WriteLine(Strings.Program_DoMapConvert_Constructing_beatmap___);
            map.Metadata.Version = "Converted";
            map.HitObjects.Clear();
            map.Editor.Bookmarks.Clear();
            var constructor = new BeatmapConstructor();
            constructor.PopulateBeatmap(map, input, matcher);

            new BeatmapEditor(Path.ChangeExtension(opts.OutputName, ".osu")).WriteFile(map);

            // Print elapsed time
            stopwatch.Stop();
            Console.WriteLine(Strings.Program_DoMapConvert_Elapsed_Time_is, stopwatch.ElapsedMilliseconds.ToString());

            return 0;
        }

        private static int DoDataCount(CountOptions opts) {
            Console.WriteLine((opts.CollectionName is null ? DbManager.GetAll() : DbManager.GetCollection(opts.CollectionName))
                .Count(o => (!opts.MinId.HasValue || o.BeatmapSetId >= opts.MinId)
                            && (!opts.RankedStatus.HasValue || o.RankedStatus == opts.RankedStatus)
                            && (!opts.Ruleset.HasValue || o.Ruleset == opts.Ruleset)
                            && (!opts.MinStarRating.HasValue || GetDefaultStarRating(o) >= opts.MinStarRating)));
            return 0;
        }

        private static int DoDataExtraction(ExtractOptions opts) {
            if (opts.OutputName is null) throw new ArgumentNullException(nameof(opts));

            bool[] mirrors = { false, true };
            var extractor = new DataExtractor();
            File.WriteAllLines(Path.ChangeExtension(opts.OutputName, ".txt"),
                DataSerializer.SerializeBeatmapData((opts.CollectionName is null ? DbManager.GetAll() : DbManager.GetCollection(opts.CollectionName))
                .Where(o => (!opts.MinId.HasValue || o.BeatmapSetId >= opts.MinId)
                            && (!opts.RankedStatus.HasValue || o.RankedStatus == opts.RankedStatus)
                            && (!opts.Ruleset.HasValue || o.Ruleset == opts.Ruleset)
                            && (!opts.MinStarRating.HasValue || GetDefaultStarRating(o) >= opts.MinStarRating))
                .Select(o => Path.Combine(ConfigManager.Config.SongsPath, o.FolderName.Trim(), o.FileName.Trim()))
                .Where(o => {
                    if (File.Exists(o)) {
                        Console.Write('.');
                        return true;
                    }
                    Console.WriteLine(Strings.CouldNotFindFile, o);
                    return false;
                })
                .Select(o => {
                    try {
                        return new BeatmapEditor(o).ReadFile();
                    }
                    catch (Exception e) {
                        Console.WriteLine(Strings.ErrorReadingFile, o, e);
                        return null;
                    }
                }).Where(o => o is not null)
                .SelectMany(b => mirrors.Select(m => extractor.ExtractBeatmapData(b, m)))
                ));
            return 0;
        }

        private static double GetDefaultStarRating(DbBeatmap beatmap) {
            return beatmap.Ruleset switch {
                Ruleset.Taiko => beatmap.TaikoStarRating[Mods.None],
                Ruleset.Mania => beatmap.ManiaStarRating[Mods.None],
                Ruleset.Fruits => beatmap.CatchStarRating[Mods.None],
                _ => beatmap.StandardStarRating[Mods.None]
            };
        }
    }
}
