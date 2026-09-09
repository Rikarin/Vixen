// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>
///     The <c>CheckAot</c> rule that refuses a runtime assembly the probe neither roots nor writes
///     down, run here so that its answer exists without an ILC publish.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The gate roots 29 of 95 runtime assemblies and its README claimed it rooted them
///         all</b> (<a href="https://github.com/Rikarin/Vixen/issues/506">#506</a>). The README is
///         corrected; what this adds is that the correction fails when it stops being true, because
///         a paragraph cannot notice a project somebody adds next month.
///     </para>
///     <para>
///         ⚠ <b>Running it here rather than only inside the gate is the point.</b> <c>CheckAot</c>
///         is an ILC publish measured in minutes, so a rule that only answers inside it is a rule
///         nobody has watched produce an answer — the state <c>PluginReferenceRule</c> shipped in
///         and the reason every rule in <c>build/</c> is a pure function now.
///     </para>
/// </remarks>
public class AotRootingRuleTests {
    static string Repository() {
        var directory = AppContext.BaseDirectory;

        while (directory is not null) {
            if (File.Exists(Path.Combine(directory, "Vixen.slnx"))) {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("No Vixen.slnx above the test assembly, so no repository root.");
    }

    static string Probe(string root) =>
        Path.Combine(root, "Tools", "Vixen.AotProbe", "Vixen.AotProbe.csproj");

    static string Temporary() {
        var path = Path.Combine(Path.GetTempPath(), "vixen-aot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Writes a project file under one of the two runtime layers.</summary>
    static void Project(string root, string layer, string name, string framework) {
        var directory = Path.Combine(root, layer, name);
        Directory.CreateDirectory(directory);

        File.WriteAllText(
            Path.Combine(directory, name + ".csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n"
            + $"    <TargetFramework>{framework}</TargetFramework>\n"
            + "  </PropertyGroup>\n</Project>\n"
        );
    }

    /// <summary>Writes a probe rooting exactly those assemblies, and returns its path.</summary>
    static string ProbeFixture(string root, params string[] rooted) {
        var directory = Path.Combine(root, "Tools", "Vixen.AotProbe");
        Directory.CreateDirectory(directory);

        var items = string.Concat(rooted.Select(name => $"    <TrimmerRootAssembly Include=\"{name}\" />\n"));
        var path = Path.Combine(directory, "Vixen.AotProbe.csproj");

        File.WriteAllText(
            path,
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n" + items + "  </ItemGroup>\n</Project>\n"
        );

        return path;
    }

    /// <summary>
    ///     ⚠ The subject set, which is the half of this rule that could go quiet. A walk that stopped
    ///     reading target frameworks would report nothing unrooted and mean "I read nothing".
    /// </summary>
    [Fact]
    public void The_runtime_set_is_every_net10_non_test_project_under_the_two_layers() {
        var root = Temporary();

        try {
            Project(root, "Core", "Vixen.Thing", "net10.0");
            Project(root, "Core", "Vixen.Thing.Tests", "net10.0");
            Project(root, "Core", "Vixen.Thing.Generators", "netstandard2.1");
            Project(root, "Platform", "Vixen.Platform.Thing", "net10.0");
            Project(root, "Platform", "Vixen.Platform.iOS", "net10.0-ios");
            Project(root, "Editor", "Vixen.Editor.Thing", "net10.0");

            Assert.Equal(
                ["Vixen.Platform.Thing", "Vixen.Thing"],
                AotRootingRule.RuntimeAssemblies(root)
            );
        } finally {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     ⚠ A root the probe declares inside a comment is not a root MSBuild has, and this rule
    ///     reads the file through <c>AotProbeProjectFile</c> for exactly that reason — the blindness
    ///     that made a substring check call an assembly covered when nothing covered it.
    /// </summary>
    [Fact]
    public void A_commented_out_root_leaves_the_assembly_unrooted() {
        var root = Temporary();

        try {
            Project(root, "Core", "Vixen.Thing", "net10.0");
            var probe = ProbeFixture(root, "Vixen.Thing");

            Assert.Empty(AotRootingRule.Unrooted(root, probe));

            File.WriteAllText(
                probe,
                File.ReadAllText(probe)
                    .Replace(
                        "<TrimmerRootAssembly Include=\"Vixen.Thing\" />",
                        "<!-- <TrimmerRootAssembly Include=\"Vixen.Thing\" /> -->",
                        StringComparison.Ordinal
                    )
            );

            Assert.Equal(["Vixen.Thing"], AotRootingRule.Unrooted(root, probe));
        } finally {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     ⚠ Both directions. A ledger line for an assembly that is rooted now fails too, which is
    ///     what makes the list only ever shrink — without it the scoreboard stops counting the
    ///     moment somebody does the work.
    /// </summary>
    [Fact]
    public void The_ledger_can_only_shrink() {
        var root = Temporary();

        try {
            Project(root, "Core", "Vixen.Rooted", "net10.0");
            Project(root, "Core", "Vixen.Unrooted", "net10.0");
            var probe = ProbeFixture(root, "Vixen.Rooted");

            var ledger = Path.Combine(root, "Tools", "Vixen.AotProbe", "NotRooted.txt");

            File.WriteAllText(ledger, "# nothing yet\n");
            var unlisted = AotRootingRule.Violations(root, probe);
            Assert.Single(unlisted);
            Assert.Contains("Vixen.Unrooted", unlisted[0], StringComparison.Ordinal);

            File.WriteAllText(ledger, "Vixen.Unrooted untried\n");
            Assert.Empty(AotRootingRule.Violations(root, probe));

            File.WriteAllText(ledger, "Vixen.Rooted this line is stale\n");
            var stale = AotRootingRule.Violations(root, probe);
            Assert.Equal(2, stale.Count);
            Assert.Contains(stale, message => message.Contains("can only shrink", StringComparison.Ordinal));
        } finally {
            Directory.Delete(root, true);
        }
    }

    /// <summary>The tree this assembly was compiled from, against the committed ledger.</summary>
    /// <remarks>
    ///     ⚠ <b>The count is asserted as well as the agreement</b>, because a rule whose subject set
    ///     silently emptied would report a clean tree. 95 runtime assemblies and 29 rooted was #506's
    ///     measurement on 2026-09-03 and is still the measurement; the assertions are floors rather
    ///     than equalities, so adding an assembly is not a failure here — it is a failure in the
    ///     ledger check above, where it belongs and where the message names it.
    /// </remarks>
    [Fact]
    public void The_repository_agrees_with_the_ledger() {
        var root = Repository();
        var probe = Probe(root);

        Assert.True(File.Exists(probe), "no AOT probe project, so this rule is checking nothing.");

        var runtime = AotRootingRule.RuntimeAssemblies(root);
        var rooted = AotProbeProjectFile.RootedAssemblies(probe);

        Assert.True(runtime.Count >= 90, $"only {runtime.Count} runtime assemblies read out of the tree.");
        Assert.True(rooted.Count >= 25, $"only {rooted.Count} roots read out of the probe.");

        Assert.Empty(AotRootingRule.Violations(root, probe));
    }

    /// <summary>
    ///     What the gate covers, stated as a number rather than as a paragraph.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the sentence #506 was filed about</b>: the probe's README said "every
    ///     <c>Core/</c> assembly" and the answer was 29 of 95. It is not a budget and not a ratchet —
    ///     the ledger above is the ratchet — but a reader of a green <c>CheckAot</c> deserves to see
    ///     the fraction it was green about, and a test that prints it is the cheapest place for that
    ///     to live.
    /// </remarks>
    [Fact]
    public void The_covered_fraction_is_every_runtime_assembly_minus_the_ledger() {
        var root = Repository();
        var probe = Probe(root);

        var runtime = AotRootingRule.RuntimeAssemblies(root).Count;
        var unrooted = AotRootingRule.Unrooted(root, probe).Count;

        Assert.Equal(runtime - unrooted, AotProbeProjectFile.RootedAssemblies(probe).Count);
        Assert.Equal(unrooted, AotRootingRule.Ledger(root).Count);
    }
}
