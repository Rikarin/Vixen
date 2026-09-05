// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.Git;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

partial class Build {
    [Parameter("Benchmark filter, e.g. '*Matrix*'. Defaults to everything.")]
    readonly string BenchmarkFilter = "*";

    [Parameter("Run benchmarks with a short job — far quicker, and noisier. For local iteration.")]
    readonly bool Short;

    [Parameter("Rewrite Benchmarks/baseline.json from the results just produced, instead of judging them")]
    readonly bool UpdateBaseline;

    [Parameter("Fail on a timing regression as well as an allocation one. Nightly, on hardware that is not shared")]
    readonly bool GateTiming;

    [Parameter("Run the benchmarks and print the comparison without failing on it")]
    readonly bool ReportOnly;

    AbsolutePath BenchmarkResultsDirectory => ArtifactsDirectory / "benchmarks";

    AbsolutePath BenchmarkBaselineFile => RootDirectory / "Benchmarks" / "baseline.json";

    /// <summary>
    ///     How much slower than the baseline a benchmark may be before it is a regression. Timing
    ///     only; an allocation may not grow at all.
    /// </summary>
    const double TimingTolerance = 1.10;

    Target Benchmark => definition => definition
        .Description("Runs the benchmark projects and judges them against the committed baseline")
        .Produces(BenchmarkResultsDirectory / "**")
        .Executes(() => {
                // Benchmarking a Debug build measures the debugger, and BenchmarkDotNet refuses to
                // do it. Forcing Release here beats letting somebody discover that after a
                // twenty-minute run.
                BenchmarkResultsDirectory.CreateOrCleanDirectory();

                var projects = RootDirectory.GlobFiles("Benchmarks/*/*.csproj");
                Assert.True(projects.Count > 0, "Found no benchmark projects.");

                foreach (var project in projects) {
                    Log.Information("Benchmarking {Project}", project.NameWithoutExtension);

                    var arguments = $"--filter {BenchmarkFilter} --artifacts \"{BenchmarkResultsDirectory}\" --exporters json"
                        + (Short ? " --job short" : string.Empty);

                    DotNetRun(settings => settings
                        .SetProjectFile(project)
                        .SetConfiguration(Configuration.Release)
                        .SetApplicationArguments(arguments)
                    );
                }

                Log.Information("Results written to {Directory}", BenchmarkResultsDirectory);

                JudgeBenchmarks();
            }
        );

    /// <summary>
    ///     The comparison half of <see cref="Benchmark" />, over whatever is already in
    ///     <c>artifacts/benchmarks</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Here for the reason <see cref="CheckAttribution" /> is</b>: a judgement reachable only
    ///     behind a twenty-minute run is a judgement nobody has watched fail, and one nobody has
    ///     watched fail is indistinguishable from one that cannot. Edit a number in
    ///     <c>Benchmarks/baseline.json</c> and run this; it should name the benchmark.
    /// </remarks>
    Target CheckBenchmarks => definition => definition
        .Description("Judges the benchmark results already in artifacts/benchmarks against the baseline")
        .Executes(JudgeBenchmarks);

    /// <summary>
    ///     Holds the benchmark results against <c>Benchmarks/baseline.json</c>: an allocation may not
    ///     grow at all, and a mean may not grow by more than <see cref="TimingTolerance" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The two halves are gated differently because only one of them is a property of the
    ///         code.</b> An allocation count is machine-independent, so it can fail a pull request on a
    ///         shared runner. A mean is not: this repository has attributed a ±14 % swing to machine
    ///         state alone, and wall-clock budgets calibrated on an idle machine are its single largest
    ///         flake source. So a timing regression is reported everywhere and fatal only under
    ///         <c>--gate-timing</c>, which is the nightly job on hardware that is not shared.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it is refused outright when the baseline was taken on a different machine.</b>
    ///         Comparing an Apple M1 Max mean with a shared Linux runner's is not a weak signal, it is
    ///         no signal, and a gate that produces one anyway is worse than no gate — the green is read
    ///         as evidence. The allocation half still applies, because it is the same number
    ///         everywhere.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A benchmark in the baseline that is missing from the results fails.</b> That is
    ///         this target's answer to the question the repository keeps asking of its instruments: on
    ///         the day a filter matches nothing, a project fails to launch, or a benchmark is quietly
    ///         renamed, the comparison would otherwise iterate an empty intersection and report
    ///         success. So would an absent baseline, which is why that fails too rather than warning —
    ///         <c>--report-only</c> is how you ask for numbers without a verdict, and
    ///         <c>--update-baseline</c> is how the file is written.
    ///     </para>
    /// </remarks>
    void JudgeBenchmarks() {
        var reports = BenchmarkResultsDirectory.GlobFiles("**/*-report-full*.json");

        if (UpdateBaseline) {
            Assert.True(
                reports.Count > 0,
                $"No BenchmarkDotNet reports under {BenchmarkResultsDirectory}, so there is nothing to "
                + "write a baseline from. Run `nuke Benchmark --update-baseline`, which produces them "
                + "first."
            );

            WriteBenchmarkBaseline(reports);

            return;
        }

        if (!BenchmarkBaselineFile.FileExists()) {
            Assert.Fail(
                $"There is no {BenchmarkBaselineFile.Name} to judge against, so this target would "
                + "otherwise run the whole suite and report success having judged nothing — which is "
                + "the failure mode it exists to prevent. Write one with `nuke Benchmark "
                + "--update-baseline`, on hardware that will run it again and with the provenance "
                + "block it stamps left intact; or pass --report-only for numbers without a verdict."
            );
        }

        Assert.True(
            reports.Count > 0,
            $"No BenchmarkDotNet reports under {BenchmarkResultsDirectory}. A comparison over nothing "
            + "is the shape of a gate that did not run, so it fails rather than passing."
        );

        var baseline = JsonNode.Parse(BenchmarkBaselineFile.ReadAllText())!.AsObject();
        var recorded = ReadBenchmarkNumbers(reports);
        var expected = baseline["benchmarks"]!.AsObject();

        var host = baseline["host"]!.AsObject();
        var thisProcessor = reports.Select(ProcessorOf).FirstOrDefault(name => name is not null);
        var baselineProcessor = host["processor"]?.GetValue<string>();
        var sameMachine = baselineProcessor is not null && baselineProcessor == thisProcessor;

        var allocations = new List<string>();
        var timings = new List<string>();
        var absent = new List<string>();

        foreach (var (name, node) in expected.Select(pair => (pair.Key, pair.Value!.AsObject()))) {
            if (!recorded.TryGetValue(name, out var actual)) {
                absent.Add(name);

                continue;
            }

            var wasBytes = node["bytesPerOperation"]!.GetValue<double>();
            var wasMean = node["meanNs"]!.GetValue<double>();

            if (actual.BytesPerOperation > wasBytes) {
                allocations.Add(
                    $"{name}: {Round(wasBytes)} B/op → {Round(actual.BytesPerOperation)} B/op"
                );
            }

            if (actual.MeanNs > wasMean * TimingTolerance) {
                timings.Add(
                    $"{name}: {Round(wasMean)} ns → {Round(actual.MeanNs)} ns "
                    + $"(+{Round((actual.MeanNs / wasMean - 1) * 100)} %)"
                );
            }
        }

        foreach (var added in recorded.Keys.Where(name => !expected.ContainsKey(name)).Order(StringComparer.Ordinal)) {
            Log.Information("{Benchmark} is not in the baseline yet — nothing judged it", added);
        }

        var fatal = new List<string>();

        if (absent.Count > 0) {
            fatal.Add(
                $"{absent.Count} benchmark(s) are in the baseline and were not run — a renamed, "
                + "deleted or unlaunched benchmark is judged by nobody, which is what this catches:"
                + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", absent.Order(StringComparer.Ordinal))
            );
        }

        if (allocations.Count > 0) {
            fatal.Add(
                "Allocation grew, and an allocation count is the same number on every machine:"
                + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", allocations)
            );
        }

        if (timings.Count > 0) {
            var report = $"Slower than the baseline by more than {Round((TimingTolerance - 1) * 100)} %:"
                + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", timings);

            if (GateTiming && sameMachine) {
                fatal.Add(report);
            } else if (!sameMachine) {
                Log.Warning(
                    "{Report}{NewLine}Not failed on: the baseline was taken on '{Baseline}' and this "
                    + "is '{Current}'. A mean measured on one machine says nothing about another, and "
                    + "a verdict drawn from it would be read as evidence.",
                    report,
                    Environment.NewLine,
                    baselineProcessor ?? "an unrecorded processor",
                    thisProcessor ?? "an unrecorded processor"
                );
            } else {
                Log.Warning(
                    "{Report}{NewLine}Not failed on: --gate-timing was not passed. Timing gates the "
                    + "nightly run, not a pull request on a shared runner.",
                    report,
                    Environment.NewLine
                );
            }
        }

        if (fatal.Count > 0 && !ReportOnly) {
            Assert.Fail(
                $"The benchmarks disagree with {BenchmarkBaselineFile.Name}, recorded "
                + $"{baseline["recorded"]?.GetValue<string>() ?? "at an unrecorded time"} on "
                + $"{baselineProcessor ?? "an unrecorded processor"} at commit "
                + $"{baseline["commit"]?.GetValue<string>() ?? "unrecorded"}:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, fatal)
            );
        }

        if (fatal.Count > 0) {
            Log.Warning("--report-only: {Count} finding(s) above would have failed this target", fatal.Count);

            return;
        }

        Log.Information(
            "{Count} benchmarks judged against {File}: no allocation growth{Timing}",
            expected.Count,
            BenchmarkBaselineFile.Name,
            GateTiming && sameMachine ? " and no timing regression" : ", timing reported only"
        );
    }

    /// <summary>
    ///     Writes <c>Benchmarks/baseline.json</c> from the results just produced.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The provenance block is not decoration.</b> An unattributed number is
    ///     indistinguishable from a guess within a month, and this file is asked to be the gate — so it
    ///     records the machine, the runtime, the BenchmarkDotNet version, the commit and the moment,
    ///     all of them read out of BenchmarkDotNet's own <c>HostEnvironmentInfo</c> rather than typed.
    ///     The comparison reads the processor back out and refuses to judge timing across a mismatch.
    /// </remarks>
    void WriteBenchmarkBaseline(IReadOnlyCollection<AbsolutePath> reports) {
        var first = JsonNode.Parse(reports.First().ReadAllText())!.AsObject();
        var environment = first["HostEnvironmentInfo"]!.AsObject();

        var benchmarks = new JsonObject();

        foreach (var (name, numbers) in ReadBenchmarkNumbers(reports).OrderBy(pair => pair.Key, StringComparer.Ordinal)) {
            benchmarks[name] = new JsonObject {
                ["meanNs"] = numbers.MeanNs,
                ["bytesPerOperation"] = numbers.BytesPerOperation
            };
        }

        var baseline = new JsonObject {
            ["recorded"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["commit"] = GitTasks.GitCurrentCommit(),
            ["host"] = new JsonObject {
                ["processor"] = environment["ProcessorName"]?.GetValue<string>(),
                ["logicalCoreCount"] = environment["LogicalCoreCount"]?.GetValue<int>(),
                ["operatingSystem"] = environment["OsVersion"]?.GetValue<string>(),
                ["architecture"] = environment["Architecture"]?.GetValue<string>(),
                ["runtime"] = environment["RuntimeVersion"]?.GetValue<string>(),
                ["benchmarkDotNet"] = environment["BenchmarkDotNetVersion"]?.GetValue<string>()
            },
            ["benchmarks"] = benchmarks
        };

        BenchmarkBaselineFile.WriteAllText(baseline.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        Log.Warning(
            "Wrote {File} from {Count} benchmarks measured on '{Processor}'. Read it before "
            + "committing: a baseline that rewrites itself is a gate that always passes, and one taken "
            + "on a machine that will not run it again gates nothing.",
            BenchmarkBaselineFile,
            benchmarks.Count,
            environment["ProcessorName"]?.GetValue<string>() ?? "an unrecorded processor"
        );
    }

    /// <summary>
    ///     The two numbers this gate judges, per benchmark, out of BenchmarkDotNet's own JSON export.
    /// </summary>
    /// <remarks>
    ///     <c>FullName</c> is the key rather than <c>DisplayInfo</c>: the latter carries the job's
    ///     parameters, so a <c>--job short</c> run and a full one would not share a single key.
    ///     ⚠ <c>Memory</c> is absent unless the benchmark class carries <c>[MemoryDiagnoser]</c>, and a
    ///     missing node is read as zero — which is the safe direction, because zero can only be
    ///     regressed against.
    /// </remarks>
    static Dictionary<string, (double MeanNs, double BytesPerOperation)> ReadBenchmarkNumbers(
        IReadOnlyCollection<AbsolutePath> reports
    ) {
        var numbers = new Dictionary<string, (double, double)>(StringComparer.Ordinal);

        foreach (var report in reports) {
            foreach (var benchmark in JsonNode.Parse(report.ReadAllText())!["Benchmarks"]!.AsArray()) {
                var node = benchmark!.AsObject();
                var name = node["FullName"]!.GetValue<string>();

                numbers[name] = (
                    node["Statistics"]?["Mean"]?.GetValue<double>() ?? 0,
                    node["Memory"]?["BytesAllocatedPerOperation"]?.GetValue<double>() ?? 0
                );
            }
        }

        return numbers;
    }

    static string? ProcessorOf(AbsolutePath report)
        => JsonNode.Parse(report.ReadAllText())?["HostEnvironmentInfo"]?["ProcessorName"]?.GetValue<string>();

    static string Round(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
