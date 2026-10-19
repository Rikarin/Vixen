// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Build;
using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>
///     Doc 50 § D6's <c>ApplicationSizeRule</c>, run here so that its answer exists without running the
///     gate — over this checkout, and over fixtures that make each of its halves fire.
/// </summary>
/// <remarks>
///     ⚠ <b>The table is today's sizes, so the tree's answer is "no violations" by construction.</b> A
///     rule that had stopped measuring would say the same, which is why every direction is shown red
///     against a fixture before the tree's green is believed: a file over its row, a row left above a
///     shrunk file, a new partial file, a row for a file that is gone, and the total.
/// </remarks>
public class ApplicationSizeRuleTests {
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

    /// <summary>A temporary tree with an application directory holding the given parts.</summary>
    sealed class Fixture : IDisposable {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "vixen-appsize-" + Guid.NewGuid().ToString("N"));

        public string Directory => Path.Combine(Root, ApplicationSizeRule.Directory.Replace('/', Path.DirectorySeparatorChar));

        public Fixture Part(string name, int lines, bool partial = true) {
            System.IO.Directory.CreateDirectory(Directory);

            var body = new List<string> { partial ? "sealed partial class EditorApplication {" : "sealed class Other {" };

            while (body.Count < lines - 1) {
                body.Add("    // filler");
            }

            body.Add("}");
            File.WriteAllText(Path.Combine(Directory, name), string.Join('\n', body) + "\n");
            return this;
        }

        public void Dispose() {
            if (System.IO.Directory.Exists(Root)) {
                System.IO.Directory.Delete(Root, recursive: true);
            }
        }
    }

    static Dictionary<string, int> Table(params (string Row, int Ceiling)[] rows) =>
        rows.ToDictionary(row => row.Row, row => row.Ceiling, StringComparer.Ordinal);

    [Fact]
    public void Lines_are_counted_as_wc_counts_them() {
        Assert.Equal(0, ApplicationSizeRule.Lines(""));
        Assert.Equal(1, ApplicationSizeRule.Lines("a\n"));
        Assert.Equal(2, ApplicationSizeRule.Lines("a\nb"));
        Assert.Equal(3, ApplicationSizeRule.Lines("a\n\nb\n"));
    }

    [Fact]
    public void Only_files_that_declare_part_of_the_class_are_measured() {
        using var fixture = new Fixture().Part("EditorApplication.cs", 100).Part("Helper.cs", 50, partial: false);

        var parts = ApplicationSizeRule.Measure(fixture.Root);

        Assert.Equal(["EditorApplication.cs"], parts.Keys);
        Assert.Equal(100, parts["EditorApplication.cs"]);
    }

    [Fact]
    public void A_table_at_todays_sizes_is_clean() {
        using var fixture = new Fixture().Part("EditorApplication.cs", 100).Part("SceneMenus.cs", 40);

        var parts = ApplicationSizeRule.Measure(fixture.Root);

        Assert.Empty(ApplicationSizeRule.Violations(parts, Table(("EditorApplication.cs", 100), ("SceneMenus.cs", 40), (ApplicationSizeRule.Total, 140))));
    }

    /// <summary>The issue's first sabotage: fifty lines appended to <c>SceneMenus.cs</c>.</summary>
    [Fact]
    public void A_file_that_grew_past_its_row_is_a_violation_and_so_is_the_total() {
        using var fixture = new Fixture().Part("EditorApplication.cs", 100).Part("SceneMenus.cs", 90);

        var violations = ApplicationSizeRule.Violations(
            ApplicationSizeRule.Measure(fixture.Root),
            Table(("EditorApplication.cs", 100), ("SceneMenus.cs", 40), (ApplicationSizeRule.Total, 140))
        );

        Assert.Equal(2, violations.Count);
        Assert.Contains(violations, violation => violation.Contains("SceneMenus.cs is 90 lines", StringComparison.Ordinal));
        Assert.Contains(violations, violation => violation.Contains("all parts is 190", StringComparison.Ordinal));
    }

    /// <summary>The shrink-only half: a row raised two hundred above its file, or left there after an extraction.</summary>
    [Fact]
    public void A_row_more_than_the_slack_above_its_file_is_a_violation() {
        using var fixture = new Fixture().Part("EditorApplication.cs", 100);
        var parts = ApplicationSizeRule.Measure(fixture.Root);

        Assert.Empty(ApplicationSizeRule.Violations(parts, Table(("EditorApplication.cs", 100 + ApplicationSizeRule.Slack), (ApplicationSizeRule.Total, 100))));

        var violation = Assert.Single(
            ApplicationSizeRule.Violations(parts, Table(("EditorApplication.cs", 300), (ApplicationSizeRule.Total, 100)))
        );

        Assert.Contains("still 300", violation, StringComparison.Ordinal);
    }

    /// <summary>The evasion: moving a verb into a new partial file moves nothing.</summary>
    [Fact]
    public void A_new_partial_file_with_no_row_is_a_violation() {
        using var fixture = new Fixture().Part("EditorApplication.cs", 100).Part("EditorPlay.cs", 30);

        var violations = ApplicationSizeRule.Violations(
            ApplicationSizeRule.Measure(fixture.Root),
            Table(("EditorApplication.cs", 100), (ApplicationSizeRule.Total, 130))
        );

        Assert.Contains("EditorPlay.cs", Assert.Single(violations), StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_for_a_file_that_no_longer_holds_part_of_the_class_is_a_violation() {
        using var fixture = new Fixture().Part("EditorApplication.cs", 100).Part("EditorParity.cs", 30, partial: false);

        var violation = Assert.Single(
            ApplicationSizeRule.Violations(
                ApplicationSizeRule.Measure(fixture.Root),
                Table(("EditorApplication.cs", 100), ("EditorParity.cs", 3233), (ApplicationSizeRule.Total, 100))
            )
        );

        Assert.Contains("EditorParity.cs", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void An_application_that_cannot_be_found_is_vacuity_and_not_a_clean_result() {
        using var fixture = new Fixture();

        var parts = ApplicationSizeRule.Measure(fixture.Root);

        Assert.NotNull(ApplicationSizeRule.Vacuity(parts, Table(("EditorApplication.cs", 100))));
        Assert.Empty(ApplicationSizeRule.Violations(parts, Table((ApplicationSizeRule.Total, 0))));
    }

    [Fact]
    public void The_rendered_table_reads_back_as_the_sizes_it_was_written_from() {
        using var fixture = new Fixture().Part("EditorApplication.cs", 100).Part("SceneMenus.cs", 40);
        var parts = ApplicationSizeRule.Measure(fixture.Root);

        var table = ApplicationSizeRule.Parse(ApplicationSizeRule.Render(parts));

        Assert.Equal(100, table["EditorApplication.cs"]);
        Assert.Equal(140, table[ApplicationSizeRule.Total]);
        Assert.Empty(ApplicationSizeRule.Violations(parts, table));
    }

    /// <summary>
    ///     ⚠ A named file outside the class — <c>ProjectBrowser.cs</c>, doc 50 § P4's (#1304) — is measured,
    ///     held to its own row, and kept out of the class's total.
    /// </summary>
    /// <remarks>
    ///     Both halves: the browser growing past its row is a violation naming it, and its lines are not the
    ///     application's, so a total at the partial files' sum stays clean.
    /// </remarks>
    [Fact]
    public void A_named_file_outside_the_class_has_its_own_ceiling_and_is_not_in_the_total() {
        var named = Assert.Single(ApplicationSizeRule.Named);

        using var fixture = new Fixture().Part("EditorApplication.cs", 100).Part(named, 60, partial: false);

        var parts = ApplicationSizeRule.Measure(fixture.Root);

        Assert.Equal(60, parts[named]);
        Assert.Empty(ApplicationSizeRule.Violations(parts, Table(("EditorApplication.cs", 100), (named, 60), (ApplicationSizeRule.Total, 100))));

        var grown = ApplicationSizeRule.Violations(parts, Table(("EditorApplication.cs", 100), (named, 40), (ApplicationSizeRule.Total, 100)));

        Assert.Contains(grown, violation => violation.Contains(named + " is 60 lines", StringComparison.Ordinal));
        Assert.Single(grown);

        var table = ApplicationSizeRule.Parse(ApplicationSizeRule.Render(parts));

        Assert.Equal(60, table[named]);
        Assert.Equal(100, table[ApplicationSizeRule.Total]);
    }

    /// <summary>This checkout, against the committed table, with the instrument checked first.</summary>
    [Fact]
    public void The_application_on_this_tree_is_within_every_ceiling_and_no_ceiling_is_slack() {
        var root = Repository();
        var parts = ApplicationSizeRule.Measure(root);
        var ceilings = ApplicationSizeRule.Read(root);

        Assert.Null(ApplicationSizeRule.Vacuity(parts, ceilings));

        // The subject set, named rather than counted: the file the class is named after. A walk that
        // did not find it is reading some other directory.
        Assert.Contains("EditorApplication.cs", parts.Keys);
        Assert.All(ApplicationSizeRule.Named, named => Assert.Contains(named, parts.Keys));

        Assert.Equal([], ApplicationSizeRule.Violations(parts, ceilings));
    }
}
