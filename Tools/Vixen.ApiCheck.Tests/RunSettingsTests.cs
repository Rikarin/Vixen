// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Xml.Linq;
using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>
///     ⚠ The root <c>.runsettings</c>, held to the only thing that decides whether it is doing any
///     work: whether the variables it declares are in <em>this</em> process.
/// </summary>
/// <remarks>
///     <para>
///         Nothing discovers that file. <c>dotnet test</c> does not look for a <c>.runsettings</c>
///         beside the solution, and until <c>Directory.Build.props</c> gained a
///         <c>RunSettingsFilePath</c> it reached exactly four call sites, all inside <c>build/</c> —
///         so <c>dotnet test &lt;one project&gt;</c>, the command <c>CLAUDE.md</c> recommends and the
///         working agreement prefers, ran without it
///         (<a href="https://github.com/Rikarin/Vixen/issues/916">#916</a>). What that costs is not
///         cosmetic: the file sets <c>DYLD_LIBRARY_PATH</c> so the Homebrew Khronos validation layer
///         can load, and every Vulkan test in the tree otherwise runs unvalidated while
///         <c>VulkanInstanceTests.ValidationIsOnWhereTheLayerIsInstalled</c> goes red saying nothing
///         is wrong with the code.
///     </para>
///     <para>
///         ⚠ <b>And a developer's own shell hides it.</b> <c>DYLD_LIBRARY_PATH</c> exported from a
///         profile makes that suite pass whatever the settings say, so "are the validation layers
///         on?" answered yes in one terminal and no in an IDE, a <c>launchctl</c> session and a
///         runner — the exact drift <c>.runsettings</c>'s own comment says it exists to remove.
///         Which is why the assertion below is an <em>equality</em> against the file's declared value
///         rather than a "contains": an ambient export is a different string, and a check that
///         accepted it would be green in precisely the case it exists to catch.
///     </para>
///     <para>
///         ⚠ <b>The paragraphs above are the history and not the arrangement.</b> No test runner
///         reads the file any more: #560 moved the runs onto Microsoft.Testing.Platform, which reads
///         no settings file, and the move to the platform's v2 (xunit.v3 4) took the last VSTest
///         caller, <c>Coverage</c>, with it — and with it the <c>RunSettingsFilePath</c>
///         <c>Directory.Build.props</c> carried, which pointed nothing at anything. The file is now
///         the declaration of the environment a run needs, and <c>Build.ExportLayerLibraryPath</c>
///         applies it to every build target that starts test executables. This test is what holds
///         the two to each other.
///     </para>
///     <para>
///         Here for the reason <see cref="TestParallelismTests" /> is: this is the assembly that
///         already asks what the build actually reads, and this is the same failure one file over —
///         a committed settings file that changes nothing and reports success by looking present.
///     </para>
/// </remarks>
public sealed class RunSettingsTests {
    /// <summary>The environment variables the committed <c>.runsettings</c> declares.</summary>
    /// <remarks>
    ///     Read out of the file rather than written down twice, so editing the file moves the
    ///     assertion with it.
    /// </remarks>
    public static TheoryData<string, string> Declared {
        get {
            var path = Path.Combine(RepositoryRoot(), ".runsettings");
            var document = XDocument.Load(path);

            var variables = document
                .Descendants("EnvironmentVariables")
                .Elements()
                .Select(static element => (Name: element.Name.LocalName, element.Value))
                .ToList();

            Assert.True(
                variables.Count > 0,
                $"{path} declares no environment variables under RunConfiguration, so passing it to a "
                + "run changes nothing and the theory below has no cases."
            );

            var data = new TheoryData<string, string>();

            foreach (var (name, value) in variables) {
                data.Add(name, value);
            }

            return data;
        }
    }

    /// <summary>The marker both mechanisms set, and nothing else does.</summary>
    /// <remarks>
    ///     <c>Build.ExportLayerLibraryPath</c> sets it for every build target that starts test
    ///     executables; a shell profile does not.
    /// </remarks>
    const string Marker = "VIXEN_TEST_SETTINGS_APPLIED";

    /// <summary>
    ///     Every variable the run's environment declares is at the front of what this test host has —
    ///     or no mechanism applied one, and the theory says so instead of failing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Skipped rather than failed when <see cref="Marker" /> is absent, and gated on the
    ///             marker rather than on the variable under test.
    ///         </b>
    ///         A bare
    ///         <c>dotnet test &lt;one project&gt;</c> — the command the working agreement recommends,
    ///         and the only one an agent in a worktree may run — applies no run environment at all,
    ///         so this theory was red on every such run for a reason nobody caused, wearing the
    ///         meaning "something you did broke the API-coverage suite" (#985). Gating on the marker
    ///         and not on the individual variable is what keeps it from going quiet in CI: a
    ///         mechanism that runs and forgets <c>DYLD_LIBRARY_PATH</c> still fails here.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             A prefix and not an equality, and the equality it replaces was wrong rather than
    ///             strict.
    ///         </b>
    ///         <c>ExportLayerLibraryPath</c> <i>prepends</i> to an inherited value on
    ///         purpose — an export somebody made for another reason should survive a test run — so
    ///         the host's value under <c>./build.sh Test</c> is the declared one
    ///         <em>
    ///             plus whatever
    ///             the shell had
    ///         </em>
    ///         . Equality was green in CI only because a runner has no ambient
    ///         <c>DYLD_LIBRARY_PATH</c>, and red under the gate itself for any developer who does.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What the old equality was defending is defended better by the marker.</b> The
    ///         worry was that <c>DYLD_LIBRARY_PATH</c> exported from a shell profile would make this
    ///         green while the settings did nothing. Nothing exports <see cref="Marker" /> by
    ///         accident, so an ambient value now reaches a <em>skip</em> rather than a pass.
    ///     </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Declared))]
    public void TheRunSettingsReachedThisProcess(string name, string value) {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable(Marker) is null,
            $"Nothing applied a run environment to this process ({Marker} is unset), so there is "
            + "nothing here to be right or wrong. That is what a bare `dotnet test <one project>` "
            + "does: Microsoft.Testing.Platform reads no .runsettings, and `Build."
            + "ExportLayerLibraryPath` runs only under the build's test targets. ⚠ On macOS that "
            + "means the Vulkan suites in this run are unvalidated."
        );

        var reached = Environment.GetEnvironmentVariable(name);

        Assert.NotNull(reached);

        Assert.StartsWith(
            value,
            reached,
            StringComparison.Ordinal
        );
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
