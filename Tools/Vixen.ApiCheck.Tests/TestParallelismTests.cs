// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;
using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>
///     ⚠ The two committed xunit runner configurations, held to the one rule that decides what they
///     actually mean: a multiplier-style <c>maxParallelThreads</c> <em>truncates</em>.
/// </summary>
/// <remarks>
///     <para>
///         <c>Directory.Build.props</c> links one of these beside every test assembly, and nothing
///         else in the tree reads either file — so an error in one of them is invisible until
///         somebody times a run and wonders. The rule is
///         <c>(int)(multiplier * Environment.ProcessorCount)</c>, a decimal-to-int cast in
///         <c>ConfigReader_Json</c> (xunit.v3.runner.common 3.2.2), which rounds toward zero and
///         never up. So <c>0.5x</c> is five threads on the ten-core machine it was measured on, two
///         on a four-core runner, and <b>one</b> on a three-core one — which is not a halved pool but
///         collection parallelism switched off. <c>macos-14</c> is a three-core M1.
///     </para>
///     <para>
///         ⚠ And zero is worse than one: <c>MaxParallelThreadsOrDefault</c> treats <c>0</c> as unset
///         and answers with the processor count, so a cap that truncates to nothing reports as no cap
///         at all rather than as an error.
///     </para>
///     <para>
///         Here rather than in a suite of its own because the subject is a committed build file and
///         this is the assembly that already walks the repository to ask what the build reads —
///         <c>ApiCoverageTests</c> is the same shape one gate over. The last test is the one that
///         matters most: it reads the copy <em>beside this assembly</em>, which is the only evidence
///         that the link in <c>Directory.Build.props</c> reached anything. An unlinked config file is
///         worse than none, because it changes nothing and reports success by looking present.
///     </para>
/// </remarks>
public sealed class TestParallelismTests {
    /// <summary>
    ///     The core counts of the runners <c>ci.yml</c>'s <c>test</c> job actually uses, per GitHub's
    ///     published specification: <c>ubuntu-latest</c> and <c>windows-latest</c> are four,
    ///     <c>macos-14</c> is three.
    /// </summary>
    public static TheoryData<int> RunnerCoreCounts => new(3, 4);

    /// <summary>
    ///     The CI configuration must leave at least two collections running at once on every runner
    ///     the matrix names. This is the assertion <c>0.5x</c> fails on <c>macos-14</c>, and failing
    ///     it is the whole reason there are two files.
    /// </summary>
    [Theory]
    [MemberData(nameof(RunnerCoreCounts))]
    public void TheCiPoolSurvivesTheSmallestRunner(int cores) {
        var configured = MaxParallelThreads("xunit.runner.ci.json");
        var threads = Effective(configured, cores);

        Assert.True(
            threads >= 2,
            $"xunit.runner.ci.json says maxParallelThreads {configured}, which is {threads} thread(s) "
            + $"on a {cores}-core runner. A multiplier truncates, so anything below 4 / multiplier "
            + "cores collapses the pool; raise the value or write a fixed count."
        );
    }

    /// <summary>
    ///     ⚠ The local configuration is deliberately the smaller of the two and is not held to the
    ///     same floor — bounding a developer's machine is what it is for. What it may not do is
    ///     truncate to zero, because zero reads as "unset" and gives the whole processor count back.
    /// </summary>
    [Fact]
    public void TheLocalPoolNeverTruncatesToNoCapAtAll() {
        var configured = MaxParallelThreads("xunit.runner.json");

        // Two upward: a single-core machine truncating to zero is harmless, because the count it
        // falls back to is one.
        for (var cores = 2; cores <= 128; cores++) {
            Assert.True(
                Effective(configured, cores) >= 1,
                $"xunit.runner.json says maxParallelThreads {configured}, which truncates to zero on "
                + $"{cores} cores — and zero is read as no configuration, so the cap disappears."
            );
        }
    }

    /// <summary>
    ///     ⚠ The link, which is the only part of this that can fail silently. xunit reads
    ///     <c>xunit.runner.json</c> from the directory the test assembly is in and nowhere else, so a
    ///     root file that is not copied beside the assembly is read by nobody.
    /// </summary>
    [Fact]
    public void TheConfigurationBesideThisAssemblyIsTheOneThisEnvironmentChose() {
        var copied = Path.Combine(AppContext.BaseDirectory, "xunit.runner.json");

        Assert.True(
            File.Exists(copied),
            $"No xunit.runner.json beside {AppContext.BaseDirectory}. Directory.Build.props links one "
            + "into every test project's output; without it xunit reads no configuration at all and "
            + "runs its collections on a pool the size of the processor count."
        );

        var expected = OnGitHubActions ? "xunit.runner.ci.json" : "xunit.runner.json";

        Assert.Equal(
            Normalized(File.ReadAllText(Path.Combine(RepositoryRoot(), expected))),
            Normalized(File.ReadAllText(copied))
        );
    }

    static bool OnGitHubActions =>
        string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);

    static string Normalized(string json) => json.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    static string MaxParallelThreads(string file) {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), file)));

        Assert.True(
            document.RootElement.TryGetProperty("maxParallelThreads", out var value),
            $"{file} declares no maxParallelThreads, so it caps nothing and the file is doing no work."
        );

        return value.ToString();
    }

    /// <summary>
    ///     xunit's own arithmetic, restated: a bare integer is the count, and a multiplier is scaled
    ///     by the processor count and cast to <c>int</c>.
    /// </summary>
    /// <remarks>
    ///     This is a re-implementation and re-implementations drift, which is why it is four lines
    ///     over one documented cast rather than a copy of the reader. The alternative — measuring the
    ///     pool from a run — is what the TRX interval arithmetic in <c>build/Build.cs</c> does, and it
    ///     cannot be done before the value is chosen.
    /// </remarks>
    static int Effective(string configured, int cores) {
        if (int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fixedCount)) {
            return fixedCount;
        }

        Assert.EndsWith("x", configured, StringComparison.OrdinalIgnoreCase);

        var multiplier = decimal.Parse(
            configured[..^1],
            NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture
        );

        return (int)(multiplier * cores);
    }

    static string RepositoryRoot() {
        var directory = AppContext.BaseDirectory;

        while (directory is not null) {
            if (File.Exists(Path.Combine(directory, "Vixen.slnx"))) {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("No Vixen.slnx above the test assembly, so no repository root.");
    }
}
