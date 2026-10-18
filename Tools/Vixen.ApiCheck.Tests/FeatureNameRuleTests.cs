// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Build;
using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>
///     Doc 50 § D6's <c>FeatureNameRule</c>, run here so that its answer exists without running the
///     gate — over this checkout, and over a fixture tree that makes each of its questions fire.
/// </summary>
/// <remarks>
///     ⚠ <b>The exemption list is today's findings, so the tree's answer is "no violations" by
///     construction</b> — which a rule that had stopped lexing strings, stopped deriving types or
///     stopped reading <c>.vxml</c> would also say. Each kind of name is therefore shown firing against
///     a fixture first, and shown <i>not</i> firing where the rule claims not to look (comments, a
///     runtime-owned extension, a nested type, a member of the same name).
/// </remarks>
public class FeatureNameRuleTests {
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

    /// <summary>A tree with one feature, one runtime project and one shell file.</summary>
    sealed class Fixture : IDisposable {
        readonly ScratchCheckout checkout = new("vixen-featurename-");

        public string Root => checkout.Root;

        public Fixture() {
            Write("Editor/Vixen.Editor.Terrain/Vixen.Editor.Terrain.csproj", "<Project />");
            Write(
                "Editor/Vixen.Editor.Terrain/TerrainMode.cs",
                """
                namespace Vixen.Editor.Terrain;

                public sealed class TerrainMode {
                    public const string PanelId = "terrain.panel";
                    public const string FoliagePanelId = "foliage.panel";
                    const string Extension = ".vxlayer";
                    const string Shipped = ".vxanim";

                    sealed class Note { }
                }

                public static class Inspecting { }
                """
            );
            Write("Editor/Vixen.Editor.Terrain/SplineView.vxml", "<View>\n</View>\n");
            Write("Editor/Vixen.Editor.Ui/Vixen.Editor.Ui.csproj", "<Project />");
            Write("Editor/Vixen.Editor.Plugin/Vixen.Editor.Plugin.csproj", "<Project />");
            Write("Core/Vixen.Animation/Clip.cs", "sealed class Clip { const string Extension = \".vxanim\"; }\n");
            Write("Editor/Vixen.Editor.App/Vixen.Editor.App.csproj", "<Project />");
            Write("Editor/Vixen.Editor.App/Shell.cs", "sealed class Shell { void Inspecting(string id) { } }\n");
        }

        public void Write(string relative, string text) => checkout.Write(relative, text);

        public void Dispose() => checkout.Dispose();
    }

    static FeatureNameRule.Vocabulary Vocabulary(Fixture fixture) => FeatureNameRule.Measure(fixture.Root);

    static SortedDictionary<string, int> Names(Fixture fixture, string source) =>
        FeatureNameRule.Names(FeatureNameRule.Lex(source), Vocabulary(fixture));

    [Fact]
    public void The_vocabulary_is_derived_from_the_feature_and_not_from_anything_beside_it() {
        using var fixture = new Fixture();
        var vocabulary = Vocabulary(fixture);

        Assert.Equal(["Vixen.Editor.Terrain"], vocabulary.Features);
        Assert.Equal(["Vixen.Editor.Terrain"], vocabulary.Namespaces);

        Assert.Contains("TerrainMode", vocabulary.Types);
        Assert.Contains("SplineView", vocabulary.Types);

        // Nested, or the name of a shell method: neither is the feature's to own.
        Assert.DoesNotContain("Note", vocabulary.Types);
        Assert.DoesNotContain("Inspecting", vocabulary.Types);

        // `.vxanim` is spelled by the runtime too, so the runtime owns it.
        Assert.Equal([".vxlayer"], vocabulary.Extensions);

        Assert.Equal(["foliage.panel", "terrain.panel"], vocabulary.PanelIds.Order(StringComparer.Ordinal));

        // The feature's own name, and the first segment of a dotted panel id it registers.
        Assert.Equal(["foliage", "terrain"], vocabulary.Prefixes.Order(StringComparer.Ordinal));
    }

    /// <summary>The issue's sabotage: the shell names <c>Vixen.Editor.Terrain.TerrainMode</c>.</summary>
    [Fact]
    public void A_shell_file_naming_a_feature_type_and_namespace_is_found() {
        using var fixture = new Fixture();

        var names = Names(fixture, "sealed class Shell { object mode = new Vixen.Editor.Terrain.TerrainMode(); TerrainMode other; }");

        Assert.Equal(1, names["Vixen.Editor.Terrain"]);
        Assert.Equal(1, names["TerrainMode"]);
    }

    [Fact]
    public void An_extension_a_panel_id_and_a_command_id_are_found_in_string_literals() {
        using var fixture = new Fixture();

        var names = Names(
            fixture,
            """"
            var kinds = new[] { ".vxlayer", ".vxanim" };
            var panel = @"terrain.panel";
            var command = $"foliage.paint";
            var raw = """terrain.sculpt""";
            var shell = "assets.create-scene";
            """"
        );

        Assert.Equal([".vxlayer", "foliage.paint", "terrain.panel", "terrain.sculpt"], names.Keys);
    }

    [Fact]
    public void Comments_and_a_display_string_are_not_names() {
        using var fixture = new Fixture();

        var names = Names(
            fixture,
            """
            // The shell no longer names TerrainMode, and Vixen.Editor.Terrain is gone.
            /* ".vxlayer" */
            /// <see cref="TerrainMode" />
            var title = "TerrainMode";
            var apostrophe = 'x';
            """
        );

        Assert.Empty(names);
    }

    [Fact]
    public void A_call_to_a_shell_method_with_a_feature_types_name_is_not_a_type() {
        using var fixture = new Fixture();

        fixture.Write("Editor/Vixen.Editor.Terrain/Inspecting.cs", "namespace Vixen.Editor.Terrain;\npublic sealed class Selecting { }\n");

        var names = Names(fixture, "Selecting(\"x\"); var made = new Selecting();");

        Assert.Equal(1, names["Selecting"]);
    }

    [Fact]
    public void Markup_is_read_and_its_comments_are_not() {
        using var fixture = new Fixture();

        fixture.Write(
            "Editor/Vixen.Editor.App/Panel.vxml",
            """
            <!-- Once named TerrainMode, and ".vxlayer". Don't. -->
            @using Vixen.Editor.Terrain
            <Label Text="Don't panic" />
            <code>
                TerrainMode? mode;
            </code>
            """
        );

        var findings = FeatureNameRule.Scan(fixture.Root, Vocabulary(fixture));

        Assert.Equal(
            ["Editor/Vixen.Editor.App/Panel.vxml TerrainMode", "Editor/Vixen.Editor.App/Panel.vxml Vixen.Editor.Terrain"],
            findings.Select(finding => finding.Key)
        );
    }

    [Fact]
    public void The_composition_roots_module_list_is_skipped_by_name_and_only_by_name() {
        using var fixture = new Fixture();
        const string modules = "using Vixen.Editor.Terrain;\nstatic class EditorModules { static object[] Standard() => [new TerrainMode()]; }\n";

        fixture.Write("Editor/Vixen.Editor.Host/Vixen.Editor.Host.csproj", "<Project />");
        fixture.Write("Editor/Vixen.Editor.Host/EditorModules.cs", modules);
        fixture.Write("Editor/Vixen.Editor.Host/EditorHost.cs", modules);

        var findings = FeatureNameRule.Scan(fixture.Root, Vocabulary(fixture));

        Assert.All(findings, finding => Assert.Equal("Editor/Vixen.Editor.Host/EditorHost.cs", finding.File));
        Assert.Equal(2, findings.Count);
    }

    static readonly IReadOnlyList<FeatureNameRule.Finding> Found = [
        new("Editor/Vixen.Editor.App/Shell.cs", "TerrainMode", 2),
        new("Editor/Vixen.Editor.App/Shell.cs", ".vxlayer", 1)
    ];

    static Dictionary<string, (int Count, string Reason)> List(params (string Key, int Count)[] rows) =>
        rows.ToDictionary(row => row.Key, row => (row.Count, ""), StringComparer.Ordinal);

    [Fact]
    public void Findings_that_match_the_list_are_clean() {
        Assert.Empty(
            FeatureNameRule.Violations(Found, List(("Editor/Vixen.Editor.App/Shell.cs TerrainMode", 2), ("Editor/Vixen.Editor.App/Shell.cs .vxlayer", 1)))
        );
    }

    [Fact]
    public void A_new_name_is_a_violation() {
        var violation = Assert.Single(FeatureNameRule.Violations(Found, List(("Editor/Vixen.Editor.App/Shell.cs TerrainMode", 2))));

        Assert.Contains(".vxlayer", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_count_above_its_row_is_a_violation_and_so_is_one_below() {
        var grew = Assert.Single(
            FeatureNameRule.Violations(Found, List(("Editor/Vixen.Editor.App/Shell.cs TerrainMode", 1), ("Editor/Vixen.Editor.App/Shell.cs .vxlayer", 1)))
        );

        Assert.Contains("allows 1", grew, StringComparison.Ordinal);

        var slack = Assert.Single(
            FeatureNameRule.Violations(Found, List(("Editor/Vixen.Editor.App/Shell.cs TerrainMode", 5), ("Editor/Vixen.Editor.App/Shell.cs .vxlayer", 1)))
        );

        Assert.Contains("still says 5", slack, StringComparison.Ordinal);
    }

    /// <summary>The issue's second sabotage: a row whose name the shell no longer holds.</summary>
    [Fact]
    public void A_row_the_shell_no_longer_names_is_a_violation() {
        var violation = Assert.Single(
            FeatureNameRule.Violations(
                Found,
                List(
                    ("Editor/Vixen.Editor.App/Shell.cs TerrainMode", 2),
                    ("Editor/Vixen.Editor.App/Shell.cs .vxlayer", 1),
                    ("Editor/Vixen.Editor.App/Gone.cs WaterMode", 1)
                )
            )
        );

        Assert.Contains("Gone.cs WaterMode", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void The_rendered_list_reads_back_and_keeps_the_reasons_of_rows_that_survive() {
        var previous = new Dictionary<string, (int, string)>(StringComparer.Ordinal) {
            ["Editor/Vixen.Editor.App/Shell.cs TerrainMode"] = (3, "the module list's caption, until P3"),
            ["Editor/Vixen.Editor.App/Gone.cs WaterMode"] = (1, "dropped")
        };

        var rows = FeatureNameRule.Parse(FeatureNameRule.Render(Found, previous));

        Assert.Equal((2, "the module list's caption, until P3"), rows["Editor/Vixen.Editor.App/Shell.cs TerrainMode"]);
        Assert.Equal((1, ""), rows["Editor/Vixen.Editor.App/Shell.cs .vxlayer"]);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void A_tree_with_no_feature_is_vacuity_and_not_a_clean_result() {
        var root = Path.Combine(Path.GetTempPath(), "vixen-featurename-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try {
            var vocabulary = FeatureNameRule.Measure(root);

            Assert.NotNull(FeatureNameRule.Vacuity(vocabulary));
            Assert.Empty(FeatureNameRule.Scan(root, vocabulary));
        } finally {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>This checkout, against the committed list, with the instrument checked first.</summary>
    [Fact]
    public void The_shell_on_this_tree_names_no_feature_beyond_the_list() {
        var root = Repository();
        var vocabulary = FeatureNameRule.Measure(root);

        Assert.Null(FeatureNameRule.Vacuity(vocabulary));

        // The subject set, named rather than counted: things the features own today on any reading.
        Assert.Contains("Vixen.Editor.Terrain", vocabulary.Features);
        Assert.Contains("TerrainMode", vocabulary.Types);
        Assert.Contains(".vxlayer", vocabulary.Extensions);
        Assert.Contains("terrain.panel", vocabulary.PanelIds);
        Assert.DoesNotContain(".vxanim", vocabulary.Extensions);

        // ⚠ The named false positive is real: read without its exemption, the module list is nothing
        // but feature names. If this ever goes empty, `ByDesign` is excusing a file that no longer
        // needs it.
        var modules = File.ReadAllText(Path.Combine(root, FeatureNameRule.ByDesign[0].Replace('/', Path.DirectorySeparatorChar)));
        Assert.NotEmpty(FeatureNameRule.Names(FeatureNameRule.Lex(modules), vocabulary));

        var findings = FeatureNameRule.Scan(root, vocabulary);

        Assert.Equal([], FeatureNameRule.Violations(findings, FeatureNameRule.Exempt(root)));
    }
}
