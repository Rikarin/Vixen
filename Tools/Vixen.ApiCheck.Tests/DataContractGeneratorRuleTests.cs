// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Build;
using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>
///     The <c>CheckArchitecture</c> rule that refuses a <c>[DataContract]</c> in a project naming
///     neither registration generator, run here so that its answer exists without running the gate.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The defect is invisible in every other instrument this repository has.</b> The
///         attribute compiles — <c>DataContractAttribute</c> is in <c>Vixen.Core</c> and arrives
///         transitively — the assembly runs, and the type is simply absent from <c>TypeRegistry</c>
///         and <c>SerializerRegistry</c> under the name it states, because the generators are
///         analyzers and an analyzer does not flow through a <c>ProjectReference</c>.
///         <c>Vixen.Editor.Terrain</c> carried eight of them and a runtime roll call in that one
///         assembly's own suite is what found them (#989); nothing in the tree answered "which other
///         projects are in that state" (#1005).
///     </para>
///     <para>
///         ⚠ <b>Both directions, because a text rule with no positives is satisfied by exactly the
///         defect it is meant to catch.</b> The fixtures below make it fire and make it go quiet, and
///         the repository pass asserts the rule's <em>subject set</em> is non-empty — a walk that
///         found no application anywhere would report "no violations" and mean "I read nothing".
///     </para>
/// </remarks>
public class DataContractGeneratorRuleTests {
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

    /// <summary>Writes a one-project tree and returns its project file.</summary>
    static string Fixture(string directory, string references, string source) {
        Directory.CreateDirectory(directory);

        var project = Path.Combine(directory, Path.GetFileName(directory) + ".csproj");

        File.WriteAllText(
            project,
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n" + references + "  </ItemGroup>\n</Project>\n"
        );

        File.WriteAllText(Path.Combine(directory, "Subject.cs"), source);
        return project;
    }

    static string Temporary() {
        var path = Path.Combine(Path.GetTempPath(), "vixen-datacontract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    const string Applied = "[DataContract(\"Thing\")]\npublic sealed class Thing {\n}\n";

    /// <summary>
    ///     ⚠ The fixture names <c>Vixen.Core</c> as a <c>ProjectReference</c>, which is where the
    ///     attribute comes from and is exactly what does not carry the generators.
    /// </summary>
    /// <remarks>
    ///     Accepting that form read three real projects as clean while writing this rule, and the
    ///     exemption file's reverse direction is what said so — a list that only refuses additions
    ///     would have agreed with the bug.
    /// </remarks>
    [Fact]
    public void A_project_that_names_neither_generator_is_an_offender() {
        var root = Temporary();

        try {
            var project = Fixture(
                Path.Combine(root, "Vixen.Fixture.Bare"),
                "    <ProjectReference Include=\"..\\..\\Core\\Vixen.Core\\Vixen.Core.csproj\" />\n",
                Applied
            );

            Assert.Equal(["Vixen.Fixture.Bare"], DataContractGeneratorRule.Offenders([project]));
            Assert.Equal(1, DataContractGeneratorRule.Subjects([project]));
        } finally {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(
        "    <ProjectReference Include=\"..\\" + DataContractGeneratorRule.SerializationGenerator
        + "\\" + DataContractGeneratorRule.SerializationGenerator + ".csproj\" OutputItemType=\"Analyzer\" />\n"
    )]
    [InlineData(
        "    <ProjectReference Include=\"..\\" + DataContractGeneratorRule.ReflectionGenerator
        + "\\" + DataContractGeneratorRule.ReflectionGenerator + ".csproj\" OutputItemType=\"Analyzer\" />\n"
    )]
    [InlineData("    <PackageReference Include=\"" + DataContractGeneratorRule.Package + "\" />\n")]
    public void Either_generator_or_the_package_makes_it_clean(string references) {
        var root = Temporary();

        try {
            var project = Fixture(Path.Combine(root, "Vixen.Fixture.Named"), references, Applied);

            // ⚠ Still a subject — it applies the attribute — and not a violation, which is the
            // distinction that keeps the rule from being green because it read nothing.
            Assert.Equal(1, DataContractGeneratorRule.Subjects([project]));
            Assert.Empty(DataContractGeneratorRule.Offenders([project]));
        } finally {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     ⚠ Seventy-odd files in this repository <em>mention</em> the attribute in prose. A rule that
    ///     counted those would report eighty offenders, and the first person to read the list would
    ///     stop believing it.
    /// </summary>
    [Fact]
    public void A_mention_in_a_comment_is_not_an_application() {
        var root = Temporary();

        try {
            var project = Fixture(
                Path.Combine(root, "Vixen.Fixture.Mentions"),
                string.Empty,
                "/// <remarks>A type marked <c>[DataContract]</c> is registered by the generator.</remarks>\n"
                + "// [DataContract] here would do nothing.\n"
                + "public sealed class Thing {\n}\n"
            );

            Assert.Equal(0, DataContractGeneratorRule.Subjects([project]));
            Assert.Empty(DataContractGeneratorRule.Offenders([project]));
        } finally {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     ⚠ A <c>.vxml</c> <c>&lt;code&gt;</c> block is production C#, and a sweep that reads only
    ///     <c>*.cs</c> reports a gap that is not there — silently, because a clean grep looks like
    ///     evidence. Two issues were filed that way in one batch and both were closed invalid.
    /// </summary>
    [Fact]
    public void A_markup_files_code_block_counts_as_a_source() {
        var root = Temporary();

        try {
            var directory = Path.Combine(root, "Vixen.Fixture.Markup");
            var project = Fixture(directory, string.Empty, "public sealed partial class View {\n}\n");
            File.WriteAllText(Path.Combine(directory, "View.vxml"), "<code>\n" + Applied + "</code>\n");

            Assert.Equal(["Vixen.Fixture.Markup"], DataContractGeneratorRule.Offenders([project]));
        } finally {
            Directory.Delete(root, true);
        }
    }

    /// <summary>The tree this assembly was compiled from, against the committed exemption file.</summary>
    /// <remarks>
    ///     ⚠ <b>Set equality in both directions, which is what makes the list only ever shrink.</b> An
    ///     exempted project that has become clean fails here, so a line that has done its job cannot
    ///     sit there hiding the next project to break the same way — the property
    ///     <c>docs/WhitespaceExempt.txt</c> and <c>docs/DocCommentExempt.txt</c> have.
    /// </remarks>
    [Fact]
    public void The_repository_agrees_with_the_exemption_file() {
        var root = Repository();
        var projects = PluginReferenceRule.ProjectFiles(root);

        Assert.NotEmpty(projects);

        // The subject set. A rule whose walk found nothing would report no violations and mean
        // nothing at all, which is the failure this repository has a name for.
        Assert.True(
            DataContractGeneratorRule.Subjects(projects) > 0,
            "No project in the tree applies [DataContract], so this rule is checking nothing — the walk "
            + "or the detection is wrong, not the repository."
        );

        Assert.Empty(DataContractGeneratorRule.Violations(root, projects));
    }
}
