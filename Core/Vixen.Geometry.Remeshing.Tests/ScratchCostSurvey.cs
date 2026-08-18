// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using CsCheck;
using Xunit;


namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>SCRATCH — task #158. Not for merge.</summary>
public class ScratchCostSurvey(ITestOutputHelper output) {
    [Fact]
    public void Replay_the_named_case() {
        // The recipe RunawayGuard.Cap's remarks record at 54.4 s.
        MeshRecipe named = new(ShapeKind.Box, 7, 1, [], 3, 0f, 0.001f);

        foreach (var shrinkwrap in new[] { false, true }) {
            foreach (var rounds in new[] { 0, 1, 2, 3 }) {
                foreach (var fill in new[] { false, true }) {
                    var mesh = BrokenMeshSpace.Build(named);
                    var settings = new ConditioningSettings {
                        PreRemeshIterations = rounds,
                        FillHoles = fill,
                        Shrinkwrap = shrinkwrap
                    };

                    var clock = Stopwatch.StartNew();

                    MeshConditioner.Condition(mesh, settings, out var report);

                    output.WriteLine(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"shrinkwrap {shrinkwrap,-5} rounds {rounds} fill {fill,-5} : "
                            + $"{clock.Elapsed.TotalMilliseconds,9:N1} ms  in {mesh.FaceCount} faces  "
                            + $"out {report.Triangles} tris  wrapped {report.Shrinkwrapped}"
                        )
                    );
                }
            }
        }
    }

    /// <summary>The quantity the comment is actually about: the slowest case of one run, over many runs.</summary>
    [Fact]
    public void The_slowest_case_of_a_run_over_many_runs() {
        List<double> maxima = [];

        for (var replicate = 0; replicate < 12; replicate++) {
            var slowest = 0d;
            var slowestWhat = "";
            var total = 0d;

            Gen.Select(
                    BrokenMeshSpace.Sized,
                    Gen.Int[0, 3],
                    Gen.Bool,
                    Gen.Bool,
                    (recipe, rounds, fill, shrinkwrap) => (recipe, rounds, fill, shrinkwrap)
                )
                .Sample(
                    entry => {
                        var (recipe, rounds, fill, shrinkwrap) = entry;
                        var mesh = BrokenMeshSpace.Build(recipe);

                        var settings = new ConditioningSettings {
                            PreRemeshIterations = rounds,
                            FillHoles = fill,
                            Shrinkwrap = shrinkwrap
                        };

                        var clock = Stopwatch.StartNew();

                        MeshConditioner.Condition(mesh, settings, out _);

                        var taken = clock.Elapsed.TotalMilliseconds;

                        total += taken;

                        if (taken > slowest) {
                            slowest = taken;
                            slowestWhat = string.Create(
                                CultureInfo.InvariantCulture,
                                $"{recipe} rounds {rounds} fill {fill} shrinkwrap {shrinkwrap}"
                            );
                        }
                    },

                    // The sweep's own count: 200 draws is one run of the property on a build.
                    iter: 200,
                    threads: 1,
                    seed: string.Create(CultureInfo.InvariantCulture, $"{replicate}{replicate}{replicate}{replicate}{replicate}{replicate}{replicate}{replicate}{replicate}{replicate}{replicate}{replicate}0")
                );

            maxima.Add(slowest);

            output.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"RUN {replicate,2}  slowest {slowest,8:N1} ms  sweep total {total / 1000d,6:N1} s  {slowestWhat}"
                )
            );
        }

        maxima.Sort();

        output.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"RUNS n {maxima.Count}  min {maxima[0]:N0} ms  median {maxima[maxima.Count / 2]:N0} ms  "
                + $"max {maxima[^1]:N0} ms  under 4 s: {maxima.Count(one => one < 4000)}"
            )
        );
    }

    /// <summary>Only the shape that costs: no pre-remesh, shrinkwrap on. What separates dear from cheap.</summary>
    [Fact]
    public void Survey_the_expensive_corner() {
        var rows = new List<(double Ms, int In, int Out, bool Wrapped, string What)>();

        BrokenMeshSpace.Sized.Sample(
            recipe => {
                var mesh = BrokenMeshSpace.Build(recipe);
                var settings = new ConditioningSettings {
                    PreRemeshIterations = 0,
                    FillHoles = false,
                    Shrinkwrap = true
                };

                var clock = Stopwatch.StartNew();

                MeshConditioner.Condition(mesh, settings, out var report);

                rows.Add(
                    (clock.Elapsed.TotalMilliseconds, mesh.FaceCount, report.Triangles, report.Shrinkwrapped,
                        recipe.ToString())
                );
            },
            iter: 300,
            threads: 1,
            seed: "0000000000000"
        );

        var wrapped = rows.Where(row => row.Wrapped).ToList();
        var not = rows.Where(row => !row.Wrapped).ToList();

        output.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"CORNER n {rows.Count}  extracted {wrapped.Count} at mean {Mean(wrapped)} ms, "
                + $"mean {(wrapped.Count > 0 ? wrapped.Average(row => row.Out) : 0):N0} tris out; "
                + $"nothing extracted {not.Count} at mean {Mean(not)} ms"
            )
        );

        foreach (var bucket in rows.GroupBy(row => row.Out / 10000).OrderBy(group => group.Key)) {
            output.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"CORNER out {bucket.Key * 10,3}0k–{(bucket.Key + 1) * 10,3}0k tris: n {bucket.Count(),4}  "
                    + $"mean {bucket.Average(row => row.Ms),9:N1} ms  max {bucket.Max(row => row.Ms),9:N1} ms"
                )
            );
        }

        return;

        static string Mean(List<(double Ms, int In, int Out, bool Wrapped, string What)> rows) =>
            string.Create(CultureInfo.InvariantCulture, $"{(rows.Count > 0 ? rows.Average(row => row.Ms) : 0):N1}");
    }

    /// <summary>The dearest draws the pinned seeds found, replayed three times each.</summary>
    [Fact]
    public void Replay_the_worst() {
        (MeshRecipe Recipe, bool Fill)[] worst = [
            (new(ShapeKind.Box, 5, 4, [MeshDefect.ZeroArea], 6, 0.13925982f, 0.01f), true),
            (new(ShapeKind.Box, 8, 3, [MeshDefect.ZeroArea], 6, 0.026629418f, 10f), true),
            (new(ShapeKind.Box, 7, 4, [MeshDefect.TinyComponent], 4, 0.031620383f, 0.1f), false),
            (new(ShapeKind.Box, 4, 5, [], 3, 0.06911123f, 0.1f), true)
        ];

        foreach (var (recipe, fill) in worst) {
            for (var attempt = 0; attempt < 3; attempt++) {
                var mesh = BrokenMeshSpace.Build(recipe);
                var settings = new ConditioningSettings {
                    PreRemeshIterations = 0,
                    FillHoles = fill,
                    Shrinkwrap = true
                };

                var cpu = Process.GetCurrentProcess().TotalProcessorTime;
                var clock = Stopwatch.StartNew();

                MeshConditioner.Condition(mesh, settings, out var report);

                var burnt = Process.GetCurrentProcess().TotalProcessorTime - cpu;

                output.WriteLine(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"WORST {clock.Elapsed.TotalMilliseconds,10:N1} ms wall  "
                        + $"{burnt.TotalMilliseconds,10:N1} ms cpu  in {mesh.FaceCount,4} faces  "
                        + $"out {report.Triangles,6} tris  {recipe}"
                    )
                );
            }
        }
    }

    [Theory]
    [InlineData("0000000000000")]
    [InlineData("1111111111111")]
    [InlineData("2222222222222")]
    public void Survey_the_sweep(string seed) {
        var rows = new List<(double Ms, string What)>();

        Gen.Select(
                BrokenMeshSpace.Sized,
                Gen.Int[0, 3],
                Gen.Bool,
                Gen.Bool,
                (recipe, rounds, fill, shrinkwrap) => (recipe, rounds, fill, shrinkwrap)
            )
            .Sample(
                entry => {
                    var (recipe, rounds, fill, shrinkwrap) = entry;
                    var mesh = BrokenMeshSpace.Build(recipe);

                    var settings = new ConditioningSettings {
                        PreRemeshIterations = rounds,
                        FillHoles = fill,
                        Shrinkwrap = shrinkwrap
                    };

                    var clock = Stopwatch.StartNew();

                    MeshConditioner.Condition(mesh, settings, out _);

                    rows.Add(
                        (clock.Elapsed.TotalMilliseconds,
                            string.Create(
                                CultureInfo.InvariantCulture,
                                $"{recipe} rounds {rounds} fill {fill} shrinkwrap {shrinkwrap} faces {mesh.FaceCount}"
                            ))
                    );
                },
                iter: 1000,
                threads: 1,
                seed: seed
            );

        rows.Sort((one, two) => two.Ms.CompareTo(one.Ms));

        var total = rows.Sum(row => row.Ms);
        var zeroRounds = rows.Count(row => row.What.Contains("rounds 0", StringComparison.Ordinal));
        var wrapped = rows.Count(row => row.What.Contains("shrinkwrap True", StringComparison.Ordinal));

        output.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"SEED {seed}  n {rows.Count}  total {total / 1000d:N1} s  mean {total / rows.Count:N1} ms  "
                + $"median {rows[rows.Count / 2].Ms:N1} ms  p90 {rows[rows.Count / 10].Ms:N1} ms  "
                + $"p99 {rows[rows.Count / 100].Ms:N1} ms  max {rows[0].Ms:N1} ms"
            )
        );

        output.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"SEED {seed}  over 1 s: {rows.Count(row => row.Ms > 1000)}  over 2 s: "
                + $"{rows.Count(row => row.Ms > 2000)}  over 4 s: {rows.Count(row => row.Ms > 4000)}  "
                + $"over 10 s: {rows.Count(row => row.Ms > 10000)}  "
                + $"(rounds 0: {zeroRounds}, shrinkwrap: {wrapped})"
            )
        );

        var top = rows.Take(50).ToList();

        output.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"SEED {seed}  of the 50 dearest: rounds 0 in "
                + $"{top.Count(row => row.What.Contains("rounds 0", StringComparison.Ordinal))}, shrinkwrap in "
                + $"{top.Count(row => row.What.Contains("shrinkwrap True", StringComparison.Ordinal))}"
            )
        );

        foreach (var row in rows.Take(5)) {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"SEED {seed} {row.Ms,9:N1} ms  {row.What}"));
        }
    }
}
