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
///         ⚠ <b>The paragraph above is the history and not the arrangement, and the difference cost
///         an issue.</b> <c>RunSettingsFilePath</c> made <c>dotnet test &lt;one project&gt;</c> read
///         the file for exactly as long as the runs went through VSTest. #560 moved <c>Test</c>,
///         <c>GoldenImages</c> and <c>AffectedTests</c> onto Microsoft.Testing.Platform, which reads
///         no settings file and downgrades the property to an MTP0001 warning — so what carries the
///         variable now is <c>Build.ExportLayerLibraryPath</c>, and <c>Coverage</c> is the one caller
///         the file is still kept for. Anything that reasons "it passes under the gate, so the gate
///         passed it the file" is reasoning from a mechanism that has been gone since #560.
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
    ///     assertion with it. VSTest applies <c>RunConfiguration/EnvironmentVariables</c> to the test
    ///     host on every platform, so this is not a macOS-only claim even though its subject is.
    /// </remarks>
    public static TheoryData<string, string> Declared {
        get {
            var path = Path.Combine(RepositoryRoot(), ".runsettings");
            var document = XDocument.Load(path);

            var variables = document
                .Descendants("EnvironmentVariables")
                .Elements()
                .Select(element => (Name: element.Name.LocalName, element.Value))
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
    ///     ⚠ A run's environment reaches this process by one of two routes and they are not the same
    ///     route: <c>.runsettings</c> through VSTest, which only <c>Coverage</c> still uses, and
    ///     <c>Build.ExportLayerLibraryPath</c> for <c>Test</c>, <c>GoldenImages</c> and
    ///     <c>AffectedTests</c>, which run under Microsoft.Testing.Platform and read no settings file
    ///     at all (#560). Both set this; a shell profile does not.
    /// </remarks>
    const string Marker = "VIXEN_TEST_SETTINGS_APPLIED";

    /// <summary>
    ///     Every variable the run's environment declares is at the front of what this test host has —
    ///     or no mechanism applied one, and the theory says so instead of failing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Skipped rather than failed when <see cref="Marker" /> is absent, and gated on the
    ///         marker rather than on the variable under test.</b> A bare
    ///         <c>dotnet test &lt;one project&gt;</c> — the command the working agreement recommends,
    ///         and the only one an agent in a worktree may run — applies no run environment at all,
    ///         so this theory was red on every such run for a reason nobody caused, wearing the
    ///         meaning "something you did broke the API-coverage suite" (#985). Gating on the marker
    ///         and not on the individual variable is what keeps it from going quiet in CI: a
    ///         mechanism that runs and forgets <c>DYLD_LIBRARY_PATH</c> still fails here.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A prefix and not an equality, and the equality it replaces was wrong rather than
    ///         strict.</b> <c>ExportLayerLibraryPath</c> <i>prepends</i> to an inherited value on
    ///         purpose — an export somebody made for another reason should survive a test run — so
    ///         the host's value under <c>./build.sh Test</c> is the declared one <em>plus whatever
    ///         the shell had</em>. Equality was green in CI only because a runner has no ambient
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
            + "ExportLayerLibraryPath` runs only under `./build.sh Test`, `GoldenImages` and "
            + "`AffectedTests`. ⚠ On macOS that means the Vulkan suites in this run are unvalidated."
        );

        var reached = Environment.GetEnvironmentVariable(name);

        Assert.NotNull(reached);

        Assert.StartsWith(
            value,
            reached,
            StringComparison.Ordinal
        );
    }

    /// <summary>
    ///     ⚠ And the property that makes that true is in the shared build file, not in one project.
    /// </summary>
    /// <remarks>
    ///     The theory above is the evidence and this is the diagnosis: without it, a run started some
    ///     other way — a <c>settings</c> switch on the command line, or an ambient export that
    ///     happens to match — would satisfy the theory and leave the tree's 178 test projects
    ///     unsettled. 178 projects cannot be relied on to remember, and the one that forgets is
    ///     invisible.
    /// </remarks>
    [Fact]
    public void DirectoryBuildPropsPointsEveryTestProjectAtIt() {
        var path = Path.Combine(RepositoryRoot(), "Directory.Build.props");

        var property = XDocument.Load(path)
            .Descendants("RunSettingsFilePath")
            .SingleOrDefault();

        Assert.True(
            property is not null,
            $"{path} declares no RunSettingsFilePath, so `dotnet test <one project>` runs with no "
            + "settings at all — .runsettings then reaches only the four call sites in build/ that "
            + "name it explicitly. That is #916."
        );

        Assert.Equal("$(MSBuildThisFileDirectory).runsettings", property!.Value.Trim());

        // The test profile and not the whole tree: the property is meaningless on a library, and a
        // sibling that also carries VSTestLogger is what identifies the group without re-parsing the
        // condition MSBuild spells it with.
        Assert.True(
            property.Parent?.Elements("VSTestLogger").Any() == true,
            "RunSettingsFilePath is not in the PropertyGroup that carries VSTestLogger, so it is no "
            + "longer scoped to test projects — check the condition it ended up under."
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
