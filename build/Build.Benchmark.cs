// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

partial class Build {
    [Parameter("Benchmark filter, e.g. '*Matrix*'. Defaults to everything.")]
    readonly string BenchmarkFilter = "*";

    [Parameter("Run benchmarks with a short job — far quicker, and noisier. For local iteration.")]
    readonly bool Short;

    AbsolutePath BenchmarkResultsDirectory => ArtifactsDirectory / "benchmarks";

    Target Benchmark => definition => definition
        .Description("Runs the benchmark projects and exports their results")
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

                // Comparing against a committed baseline is what makes this a gate rather than a
                // report, and it needs a baseline taken on hardware that will run it again. Per
                // doc 12 that is the nightly job, not a shared PR runner — so the comparison lands
                // with the nightly workflow rather than being faked here against a machine whose
                // numbers nobody can reproduce.
                Log.Warning("No baseline comparison yet — see docs/plan/12 § Nuke `Benchmark`.");
            }
        );
}
