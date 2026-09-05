// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Vixen.Build;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>
///     Doc 48's exit criterion 10 — "it references <c>Vixen.Editor.App</c> in no build, asserted by
///     <c>CheckArchitecture</c>" — with the gate's own rule run here, so that its answer exists
///     without running the gate.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The rule had never produced an answer.</b> It was written inside
///         <c>CheckArchitecture</c>'s <c>Executes</c>, and the batch that wrote it was forbidden to
///         run a Nuke target — so what shipped was a rule whose only observable behaviour was
///         hypothetical. This repository's standard is to ask what a gate prints on the day it does
///         not run; a rule that has never run has not answered that even once, and "it will be fine
///         when somebody runs it" is the sentence that precedes every gate that was quietly
///         decoration.
///     </para>
///     <para>
///         <b>So <c>build/PluginReferenceRule.cs</c> is compiled into this assembly and called
///         here.</b> Not re-implemented — linked, by the <c>&lt;Compile Include&gt;</c> in this
///         project — because two transcriptions of one idea is the failure § D1 spent a slice
///         avoiding. What the gate evaluates over its glob, these tests evaluate over the repository
///         tree this assembly was compiled from.
///     </para>
///     <para>
///         ⚠ <b>What the orchestrator should see when it does run <c>CheckArchitecture</c>:</b>
///         <c>Checked N projects, of which 8 are editor plugins; no violations.</c> — 402 projects
///         and 8 plugins on this tree. Eight, not nine: the gate's own count said nine because
///         <c>Vixen.Editor.App</c> references the plugin contract (it hosts plugins) and was being
///         classified as one. It could never have produced a violation — nothing reaches itself
///         across an acyclic reference graph — which is exactly why it went unseen. A rule's subject
///         set is the half of it nothing checks.
///     </para>
///     <para>
///         <b>Anchored at this file's compiled path.</b> A walk from a hard-coded root, or one that
///         climbed until it found a <c>.git</c>, would read <c>.claude/worktrees</c> — a whole
///         checkout per agent — and compare other people's copies of these project files with each
///         other. <see cref="Repository" /> is <c>[CallerFilePath]</c> less two directories, so the
///         tree read is the one this assembly was built from.
///     </para>
/// </remarks>
public class PluginReferenceRuleTests {
    /// <summary>Where this file was compiled from.</summary>
    static string Here([CallerFilePath] string path = "") => path;

    /// <summary>The repository tree this assembly was compiled from.</summary>
    static string Repository() =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Here())!, "..", ".."));

    /// <summary>The directories <c>CheckArchitecture</c> globs, in its order.</summary>
    static readonly string[] Layers = [
        "Core",
        "Gameplay",
        "Platform",
        "Editor",
        "Raven",
        "Tools",
        "Live",
        "Samples"
    ];

    /// <summary>The project files the gate reads, found the way the gate finds them.</summary>
    /// <remarks>
    ///     ⚠ <b>The three exclusions are the gate's own and each one matters here too.</b>
    ///     <c>bin/</c> and <c>obj/</c> hold copies of project files that would double every edge;
    ///     <c>Tools/Vixen.Templates/templates/</c> holds project files that are not this repository's
    ///     — they are what <c>dotnet new</c> writes into somebody else's directory. A test that read
    ///     a different set from the gate would be answering a different question and saying it was
    ///     the same one.
    /// </remarks>
    static List<string> Projects() {
        var root = Repository();

        return Layers
            .Select(layer => Path.Combine(root, layer))
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.GetFiles(directory, "*.csproj", SearchOption.AllDirectories))
            .Select(path => path.Replace('\\', '/'))
            .Where(path => !path.Contains("/bin/", StringComparison.Ordinal))
            .Where(path => !path.Contains("/obj/", StringComparison.Ordinal))
            .Where(path => !path.Contains("/Vixen.Templates/templates/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    ///     ⚠ No plugin in this repository reaches the editor application, and the walk that says so
    ///     found the repository.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is the criterion's answer, and everything before the last assertion is the
    ///         instrument.</b> A walk that found no project files, an <c>Editor/</c> that was not
    ///         there, a contract that had been renamed — each of those produces "no violations" and
    ///         means nothing, so each is refused by name before the finding is read.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>Vixen.Editor.Host</c> is the load-bearing half.</b> It reaches the application
    ///         and is not a plugin, so a clean result is a measurement of a walk that can see such an
    ///         edge rather than a property of a walk that sees none.
    ///     </para>
    /// </remarks>
    [Fact]
    public void No_plugin_in_this_repository_reaches_the_editor_application() {
        var projects = Projects();

        Assert.True(
            projects.Count > 100,
            $"Only {projects.Count} project files were found under {Repository()}. This roll call is anchored "
            + "at this file's compiled path and reads the tree it was built from; a run whose sources are not "
            + "on the machine reads nothing and would otherwise report no violations."
        );

        var edges = PluginReferenceRule.Edges(projects);

        Assert.Null(PluginReferenceRule.Vacuity(edges));

        var plugins = PluginReferenceRule.Plugins(edges);

        // The subject set, named rather than counted: this plugin has to be in it, or the rule is
        // clean about somebody else's assemblies.
        Assert.Contains("Vixen.Editor.Texturing", plugins);

        // ⚠ And the application is not a plugin of itself. It references the contract because it
        // hosts plugins, and the plain "names the contract" test counted it as the ninth.
        Assert.DoesNotContain(PluginReferenceRule.Application, plugins);

        // The other half of the instrument: an edge into the application is something this walk can
        // find. Vixen.Editor.Host has one and is not a plugin.
        Assert.True(PluginReferenceRule.Reaches(edges, "Vixen.Editor.Host", PluginReferenceRule.Application));

        Assert.Equal([], PluginReferenceRule.Violations(edges));
    }

    /// <summary>The four project files a fixture needs, written into a temporary directory.</summary>
    /// <param name="root">Where to write them.</param>
    /// <param name="throughMiddle">Whether the intermediate links the application.</param>
    /// <returns>The paths, as the rule would be handed them.</returns>
    static List<string> Fixture(string root, bool throughMiddle) {
        List<string> paths = [];

        void Write(string name, params string[] references) {
            var directory = Path.Combine(root, name);

            Directory.CreateDirectory(directory);

            var lines = references.Select(reference =>
                $"    <ProjectReference Include=\"..\\{reference}\\{reference}.csproj\" />"
            );

            var path = Path.Combine(directory, name + ".csproj");

            File.WriteAllText(
                path,
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n"
                + string.Join('\n', lines)
                + "\n  </ItemGroup>\n</Project>\n"
            );

            paths.Add(path);
        }

        Write(PluginReferenceRule.Contract);
        Write(PluginReferenceRule.Application, PluginReferenceRule.Contract);
        Write("Middle", throughMiddle ? [PluginReferenceRule.Application] : []);
        Write("Fixture.Plugin", PluginReferenceRule.Contract, "Middle");
        Write("Fixture.Host", PluginReferenceRule.Application);

        return paths;
    }

    /// <summary>
    ///     ⚠ A plugin that reaches the application through one intermediate is a violation, and the
    ///     same fixture without that one edge is not.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The sabotage, and it is the only thing that makes the clean result above worth
    ///         reading.</b> A rule that cannot fire and a rule over a tree that does not violate it
    ///         print the same thing. The two halves are one fixture differing in one
    ///         <c>ProjectReference</c>, so the difference in the answer is that reference's.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Transitive, which is the half a reflection-based test structurally cannot
    ///         make.</b> <c>Fixture.Plugin</c> names the contract and <c>Middle</c>; only
    ///         <c>Middle</c> names the application. A plugin that shipped this way would carry the
    ///         whole application in its folder, and <c>Assembly.GetReferencedAssemblies</c> on the
    ///         plugin would list <c>Middle</c> and be satisfied.
    ///     </para>
    ///     <para>
    ///         <b>And the message names the plugin</b>, because a violation that does not say which
    ///         project is a gate somebody has to reproduce before they can act on it.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_plugin_that_reaches_the_application_through_an_intermediate_is_caught(bool throughMiddle) {
        var root = Path.Combine(Path.GetTempPath(), "vixen-plugin-rule-" + Guid.NewGuid().ToString("n"));

        try {
            var edges = PluginReferenceRule.Edges(Fixture(root, throughMiddle));

            // The fixture is a fixture the rule takes seriously: one plugin, and something reaching
            // the application either way, so neither answer below is vacuity.
            Assert.Null(PluginReferenceRule.Vacuity(edges));
            Assert.Equal(["Fixture.Plugin"], PluginReferenceRule.Plugins(edges));
            Assert.True(PluginReferenceRule.Reaches(edges, "Fixture.Host", PluginReferenceRule.Application));

            var violations = PluginReferenceRule.Violations(edges);

            if (throughMiddle) {
                Assert.Single(violations);
                Assert.Contains("Fixture.Plugin", violations[0], StringComparison.Ordinal);
                Assert.Contains(PluginReferenceRule.Application, violations[0], StringComparison.Ordinal);
            } else {
                Assert.Equal([], violations);
            }
        } finally {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     ⚠ The rule says so when it is checking nothing, in both of the two ways it can be.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>A rule whose subject set is empty passes silently.</b> Rename the contract and no
    ///         project is a plugin, so every plugin is clean; read a tree whose project files carry
    ///         no references and nothing reaches the application, so no chain can be found. Both are
    ///         "no violations" and both are worthless, and until this test existed the two guards
    ///         against them were themselves unexercised — the shape of a gate whose instrument nobody
    ///         has ever seen fire.
    ///     </para>
    ///     <para>
    ///         The messages are asserted rather than merely the nullness, because what the gate
    ///         prints on that day is the whole value of the guard.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_rule_refuses_to_be_vacuous() {
        Dictionary<string, HashSet<string>> nothing = new(StringComparer.Ordinal) {
            ["Something"] = new(StringComparer.Ordinal) { "Something.Else" }
        };

        Assert.Contains(PluginReferenceRule.Contract, PluginReferenceRule.Vacuity(nothing) ?? "", StringComparison.Ordinal);

        Dictionary<string, HashSet<string>> unreachable = new(StringComparer.Ordinal) {
            ["A.Plugin"] = new(StringComparer.Ordinal) { PluginReferenceRule.Contract },
            [PluginReferenceRule.Contract] = new(StringComparer.Ordinal)
        };

        Assert.Single(PluginReferenceRule.Plugins(unreachable));

        Assert.Contains(
            PluginReferenceRule.Application,
            PluginReferenceRule.Vacuity(unreachable) ?? "",
            StringComparison.Ordinal
        );

        // And a graph with both halves is not vacuous, so the two above are findings rather than a
        // guard that says yes to everything.
        unreachable["A.Host"] = new(StringComparer.Ordinal) { PluginReferenceRule.Application };

        Assert.Null(PluginReferenceRule.Vacuity(unreachable));
    }

    /// <summary>
    ///     ⚠ A cycle in the reference graph is walked rather than hung on.
    /// </summary>
    /// <remarks>
    ///     MSBuild refuses a cycle, but this rule reads project files rather than a restore, so it
    ///     cannot assume it will never see one — and a half-edited pair of project files is exactly
    ///     when somebody runs the gate. Without the visited set this is an infinite loop inside a
    ///     build, which reads as a hung machine and not as a rule with a bug.
    /// </remarks>
    [Fact]
    public void A_cycle_does_not_hang_the_walk() {
        Dictionary<string, HashSet<string>> edges = new(StringComparer.Ordinal) {
            ["A"] = new(StringComparer.Ordinal) { "B" },
            ["B"] = new(StringComparer.Ordinal) { "A" }
        };

        Assert.False(PluginReferenceRule.Reaches(edges, "A", PluginReferenceRule.Application));
        Assert.True(PluginReferenceRule.Reaches(edges, "A", "B"));
    }
}
