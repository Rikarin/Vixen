// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Vixen.Build;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>
///     Doc 36 § P3's third exit criterion — "<c>CheckArchitecture</c> gains a rule that fails the
///     build if a feature assembly is referenced again" — with the gate's own rule run here, so that
///     its answer exists without running the gate.
/// </summary>
/// <remarks>
///     <para>
///         <b>The criterion had never been written at all.</b> Blockout and Terrain were decoupled at
///         real cost, and nothing failed the build when either was referenced again — which for F2,
///         the finding doc 36 says matters most, is the difference between a fix and a tidy-up.
///     </para>
///     <para>
///         ⚠ <b>One half now, and there is no second half to sabotage</b> (#1302). The rule had a
///         shrink-only list of what the application still named — five feature assemblies once — and
///         its tests proved the list could only shrink. The asset editors were its last name (#1297),
///         and the list was deleted rather than emptied, because an empty shrink-only list asserts
///         nothing. The sabotage that reaches what is left is the one
///         <see cref="A_feature_assembly_referenced_again_is_a_violation" /> runs on a fixture and that
///         also holds on the real tree: put <c>Vixen.Editor.Terrain</c> back in
///         <c>Vixen.Editor.App.csproj</c> and <see cref="The_editor_application_references_no_feature_assembly" />
///         goes red.
///     </para>
///     <para>
///         ⚠ <b>Beside <c>PluginReferenceRuleTests</c> for its reason, not for tidiness.</b> A rule
///         written inside a Nuke target can only answer by running a target that compiles the
///         solution in Release, so it ships without anybody having seen it produce an answer — which
///         is the state <c>PluginReferenceRule</c> was in, and the state this repository's own
///         standard says to fix before the rule is believed. The rule file is compiled into this
///         assembly by a <c>&lt;Compile Include&gt;</c>: a second caller, not a second transcription.
///     </para>
///     <para>
///         <b>Anchored at this file's compiled path</b>, on <c>PluginReferenceRuleTests</c>' terms: a
///         walk from a hard-coded root, or one that climbed until it found a <c>.git</c>, would read
///         <c>.claude/worktrees</c> — a whole checkout per agent — and answer about somebody else's
///         copy of the project file.
///     </para>
/// </remarks>
public class ApplicationReferenceRuleTests {
    /// <summary>Where this file was compiled from.</summary>
    static string Here([CallerFilePath] string path = "") => path;

    /// <summary>The repository tree this assembly was compiled from.</summary>
    static string Repository() =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Here())!, "..", ".."));

    /// <summary>The project files the gate reads, read by calling what the gate calls.</summary>
    static List<string> Projects() => PluginReferenceRule.ProjectFiles(Repository());

    /// <summary>The criterion's answer about this tree, with the instrument checked first.</summary>
    /// <remarks>
    ///     Everything before the last assertion is the instrument. A walk that found no project files,
    ///     an application that had been renamed, a reference list read out of the wrong file — each
    ///     produces "no violations" and means nothing, so each is refused by name.
    /// </remarks>
    [Fact]
    public void The_editor_application_references_no_feature_assembly() {
        var root = Repository();
        var projects = Projects();

        Assert.True(
            projects.Count > 100,
            $"Only {projects.Count} project files were found under {root}. This rule is anchored at this "
            + "file's compiled path and reads the tree it was built from; a run whose sources are not on the "
            + "machine reads nothing and would otherwise report no violations."
        );

        Assert.Null(ApplicationReferenceRule.Vacuity(root, projects));

        var referenced = ApplicationReferenceRule.EditorReferences(
            root,
            projects.Single(path =>
                Path.GetFileNameWithoutExtension(path) == ApplicationReferenceRule.Application
            )
        );

        // ⚠ The subject set, named rather than counted. `Vixen.Editor.Ui` is the shell — an
        // application that did not reference it would not be an application — so its absence means
        // the walk is reading something other than the file it thinks it is.
        Assert.Contains("Vixen.Editor.Ui", referenced);

        // And the other half of the instrument: a feature reference is something this walk can find.
        // ⚠ The real tree has none left to point at since #1297 took the asset editors off — this
        // named the profiler until #1298, the node graph until #1299 and the asset editors until then
        // — so the walk is shown one on a project made for it, through the same `EditorReferences`
        // the assertions above read the tree with. A walk that could not see a feature reference
        // would make the tree's empty violation list mean nothing.
        using (var fixture = new Fixture()) {
            fixture.Application([.. ApplicationReferenceRule.Allowed, "Vixen.Editor.AssetEditors"]);

            Assert.Contains(
                "Vixen.Editor.AssetEditors",
                ApplicationReferenceRule.EditorReferences(fixture.Root, fixture.Projects[0])
            );
        }

        // And the tree's own list no longer carries it — the reference #1297 removed.
        Assert.DoesNotContain("Vixen.Editor.AssetEditors", referenced);

        // ⚠ The generator is not in the list, and that is the one exclusion the rule makes.
        // `Vixen.Editor.Inspector.Generator` is an `OutputItemType="Analyzer"` reference under
        // `Editor/`, so a rule that counted project references by path alone would fail the build on
        // the arrangement every project in this repository uses to reach a source generator.
        Assert.DoesNotContain("Vixen.Editor.Inspector.Generator", referenced);

        Assert.Equal([], ApplicationReferenceRule.Violations(root, projects));
    }

    /// <summary>A feature assembly referenced again fails, which is the whole criterion.</summary>
    [Fact]
    public void A_feature_assembly_referenced_again_is_a_violation() {
        using var fixture = new Fixture();

        // Everything the application is entitled to today, plus one that moved out and came back.
        fixture.Application([
            .. ApplicationReferenceRule.Allowed,
            "Vixen.Editor.Blockout"
        ]);

        var violation = Assert.Single(ApplicationReferenceRule.Violations(fixture.Root, fixture.Projects));

        Assert.Contains("Vixen.Editor.Blockout", violation, StringComparison.Ordinal);
    }

    /// <summary>An analyzer reference is not a feature reference.</summary>
    [Fact]
    public void An_analyzer_reference_under_Editor_is_not_counted() {
        using var fixture = new Fixture();

        fixture.Application([.. ApplicationReferenceRule.Allowed]);
        fixture.AddAnalyzer("Vixen.Editor.Inspector.Generator");

        Assert.DoesNotContain(
            "Vixen.Editor.Inspector.Generator",
            ApplicationReferenceRule.EditorReferences(fixture.Root, fixture.Projects[0])
        );

        Assert.Equal([], ApplicationReferenceRule.Violations(fixture.Root, fixture.Projects));
    }

    /// <summary>A renamed application reports that it is checking nothing rather than that it is clean.</summary>
    [Fact]
    public void An_application_that_cannot_be_found_is_vacuity_and_not_a_clean_result() {
        using var fixture = new Fixture();

        Assert.NotNull(ApplicationReferenceRule.Vacuity(fixture.Root, fixture.Projects));
        Assert.Equal([], ApplicationReferenceRule.Violations(fixture.Root, fixture.Projects));
    }

    /// <summary>An application project written into a temporary <c>Editor/</c>.</summary>
    sealed class Fixture : IDisposable {
        readonly List<string> projects = [];

        public string Root { get; } = Path.Combine(
            Path.GetTempPath(),
            $"vixen-app-reference-{Environment.ProcessId}-{Guid.NewGuid():N}"
        );

        public IReadOnlyList<string> Projects => projects;

        public void Application(params string[] references) {
            var directory = Path.Combine(Root, "Editor", ApplicationReferenceRule.Application);

            Directory.CreateDirectory(directory);

            var lines = references.Select(reference =>
                $"""    <ProjectReference Include="..\{reference}\{reference}.csproj" />"""
            );

            var path = Path.Combine(directory, ApplicationReferenceRule.Application + ".csproj");

            File.WriteAllText(path, $"<Project>\n  <ItemGroup>\n{string.Join("\n", lines)}\n  </ItemGroup>\n</Project>\n");
            projects.Add(path);
        }

        public void AddAnalyzer(string name) {
            var path = projects[0];
            var text = File.ReadAllText(path);

            File.WriteAllText(
                path,
                text.Replace(
                    "  </ItemGroup>",
                    $"""    <ProjectReference Include="..\{name}\{name}.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />"""
                    + "\n  </ItemGroup>",
                    StringComparison.Ordinal
                )
            );
        }

        public void Dispose() {
            if (Directory.Exists(Root)) {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
