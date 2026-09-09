// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Build;
using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>
///     The <c>CheckArchitecture</c> rule that refuses a generator no packable library carries into
///     its package, run here so that its answer exists without running the gate.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The defect this rule is for is invisible to every other instrument in the tree.</b>
///         An analyzer does not flow through a <c>ProjectReference</c>, so every in-tree consumer
///         names the generator itself and a green build proves nothing about the package. Two
///         libraries were found in that state by hand — <c>Vixen.Editor.Inspector</c> and then
///         <c>Vixen.Editor.NodeGraph</c> — which is what a rule is for
///         (<a href="https://github.com/Rikarin/Vixen/issues/1165">#1165</a>).
///     </para>
///     <para>
///         ⚠ <b>Both directions, and a subject set, because a rule with no positives is satisfied by
///         exactly the defect it is meant to catch.</b> The fixtures below make it fire and make it
///         go quiet; the repository pass asserts that the subject set is non-empty <em>and</em> that
///         something is actually packaged, so a walk that stopped understanding
///         <c>TargetsForTfmSpecificContentInPackage</c> reports "everything is an offender" rather
///         than a clean tree.
///     </para>
/// </remarks>
public class GeneratorPackagingRuleTests {
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

    static string Temporary() {
        var path = Path.Combine(Path.GetTempPath(), "vixen-packaging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Writes one project file with the given body and returns its path.</summary>
    static string Fixture(string root, string name, string body) {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);

        var project = Path.Combine(directory, name + ".csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\">\n" + body + "</Project>\n");
        return project;
    }

    /// <summary>The generator project itself: packable to nobody, by the profile's own rule.</summary>
    const string Plugin =
        "  <PropertyGroup>\n    <TargetFramework>netstandard2.1</TargetFramework>\n  </PropertyGroup>\n";

    /// <summary>A library that packs the generator, spelled as Vixen.Core.Reflection spells it.</summary>
    static string Packing(string generator) =>
        "  <PropertyGroup>\n"
        + "    <TargetsForTfmSpecificContentInPackage>\n"
        + "      $(TargetsForTfmSpecificContentInPackage);PackIt\n"
        + "    </TargetsForTfmSpecificContentInPackage>\n"
        + "  </PropertyGroup>\n"
        + "  <Target Name=\"PackIt\">\n"
        + $"    <MSBuild Projects=\"..\\{generator}\\{generator}.csproj\" Targets=\"GetTargetPath\">\n"
        + "      <Output TaskParameter=\"TargetOutputs\" ItemName=\"Assembly\" />\n"
        + "    </MSBuild>\n"
        + "    <ItemGroup>\n"
        + "      <TfmSpecificPackageFile Include=\"@(Assembly)\" PackagePath=\"analyzers/dotnet/cs\" />\n"
        + "    </ItemGroup>\n"
        + "  </Target>\n";

    [Fact]
    public void A_generator_no_library_packs_is_an_offender() {
        var root = Temporary();

        try {
            var generator = Fixture(root, "Vixen.Fixture.Generators", Plugin);
            var library = Fixture(root, "Vixen.Fixture", "  <ItemGroup />\n");

            Assert.Equal(["Vixen.Fixture.Generators"], GeneratorPackagingRule.Subjects([generator, library]));
            Assert.Empty(GeneratorPackagingRule.Packaged([generator, library]));
            Assert.Equal(["Vixen.Fixture.Generators"], GeneratorPackagingRule.Offenders([generator, library]));
        } finally {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void A_generator_a_packable_library_carries_is_clean() {
        var root = Temporary();

        try {
            var generator = Fixture(root, "Vixen.Fixture.Generators", Plugin);
            var library = Fixture(root, "Vixen.Fixture", Packing("Vixen.Fixture.Generators"));

            // ⚠ Still a subject — it is a generator in the tree — and not an offender, which is the
            // distinction that keeps the rule from being green because it read nothing.
            Assert.Equal(["Vixen.Fixture.Generators"], GeneratorPackagingRule.Subjects([generator, library]));
            Assert.Equal(["Vixen.Fixture.Generators"], GeneratorPackagingRule.Packaged([generator, library]));
            Assert.Empty(GeneratorPackagingRule.Offenders([generator, library]));
        } finally {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     ⚠ The exception #1165 warns about, and it is not a special case: <c>Vixen.Ui</c> packs
    ///     <c>Vixen.Ui.Markup.Generators</c> and there is no <c>Vixen.Ui.Markup</c> package doing it,
    ///     so a rule matching on the sibling directory reports a false positive for the one generator
    ///     that is already correct. What is matched is "some packable library packs this".
    /// </summary>
    [Fact]
    public void The_library_that_packs_it_need_not_be_its_namesake() {
        var root = Temporary();

        try {
            var generator = Fixture(root, "Vixen.Fixture.Markup.Generators", Plugin);
            var namesake = Fixture(root, "Vixen.Fixture.Markup", "  <ItemGroup />\n");
            var other = Fixture(root, "Vixen.Fixture", Packing("Vixen.Fixture.Markup.Generators"));

            Assert.Empty(GeneratorPackagingRule.Offenders([generator, namesake, other]));
        } finally {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     ⚠ A library that says <c>IsPackable=false</c> ships to nobody, so what it names in a pack
    ///     target reaches no consumer. Counting it would report a generator as shipped on the
    ///     strength of a target that never produces a package — the failure that reads exactly like a
    ///     clean tree.
    /// </summary>
    [Fact]
    public void An_unpackable_library_does_not_ship_what_it_names() {
        var root = Temporary();

        try {
            var generator = Fixture(root, "Vixen.Fixture.Generators", Plugin);

            var library = Fixture(
                root,
                "Vixen.Fixture",
                "  <PropertyGroup>\n    <IsPackable>false</IsPackable>\n  </PropertyGroup>\n"
                + Packing("Vixen.Fixture.Generators")
            );

            Assert.Empty(GeneratorPackagingRule.Packaged([generator, library]));
            Assert.Equal(["Vixen.Fixture.Generators"], GeneratorPackagingRule.Offenders([generator, library]));
        } finally {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     ⚠ The two halves have to agree or nothing is packed. A <c>&lt;Target&gt;</c> that no
    ///     <c>TargetsForTfmSpecificContentInPackage</c> names is never run by <c>dotnet pack</c>, and
    ///     produces no error and no file — so reading the target alone would call a broken package
    ///     shipped.
    /// </summary>
    [Fact]
    public void A_target_the_pack_property_does_not_name_ships_nothing() {
        var root = Temporary();

        try {
            var generator = Fixture(root, "Vixen.Fixture.Generators", Plugin);

            var library = Fixture(
                root,
                "Vixen.Fixture",
                "  <Target Name=\"PackIt\">\n"
                + "    <MSBuild Projects=\"..\\Vixen.Fixture.Generators\\Vixen.Fixture.Generators.csproj\" "
                + "Targets=\"GetTargetPath\" />\n"
                + "  </Target>\n"
            );

            Assert.Empty(GeneratorPackagingRule.Packaged([generator, library]));
        } finally {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     ⚠ And an ordinary <c>Analyzer</c> <c>ProjectReference</c> is not packaging, which is the
    ///     whole point of the rule: that is the line every in-tree consumer already has, and it
    ///     reaches nobody outside this repository.
    /// </summary>
    [Fact]
    public void An_analyzer_project_reference_is_not_packaging() {
        var root = Temporary();

        try {
            var generator = Fixture(root, "Vixen.Fixture.Generators", Plugin);

            var library = Fixture(
                root,
                "Vixen.Fixture",
                "  <ItemGroup>\n"
                + "    <ProjectReference Include=\"..\\Vixen.Fixture.Generators\\Vixen.Fixture.Generators.csproj\" "
                + "OutputItemType=\"Analyzer\" ReferenceOutputAssembly=\"false\" />\n"
                + "  </ItemGroup>\n"
            );

            Assert.Equal(["Vixen.Fixture.Generators"], GeneratorPackagingRule.Offenders([generator, library]));
        } finally {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     ⚠ An exempted generator that has become packed is a violation too, which is the half that
    ///     makes the list only ever shrink — and the half a rule written in one direction does not
    ///     have.
    /// </summary>
    [Fact]
    public void The_exemption_list_can_only_shrink() {
        var root = Temporary();

        try {
            var generator = Fixture(root, "Vixen.Fixture.Generators", Plugin);
            var library = Fixture(root, "Vixen.Fixture", "  <ItemGroup />\n");
            var packed = Fixture(root, "Vixen.Other.Generators", Plugin);
            var carrier = Fixture(root, "Vixen.Other", Packing("Vixen.Other.Generators"));

            string[] projects = [generator, library, packed, carrier];

            Directory.CreateDirectory(Path.Combine(root, "docs"));
            var list = Path.Combine(root, "docs", "GeneratorPackagingExempt.txt");

            File.WriteAllText(list, "# nothing yet\n");
            var unlisted = GeneratorPackagingRule.Violations(root, projects);
            Assert.Single(unlisted);
            Assert.Contains("Vixen.Fixture.Generators", unlisted[0], StringComparison.Ordinal);

            File.WriteAllText(list, "Vixen.Fixture.Generators because the fixture says so\n");
            Assert.Empty(GeneratorPackagingRule.Violations(root, projects));

            File.WriteAllText(list, "Vixen.Other.Generators a library packs it, so this line is stale\n");
            var stale = GeneratorPackagingRule.Violations(root, projects);
            Assert.Equal(2, stale.Count);
            Assert.Contains(stale, message => message.Contains("can only shrink", StringComparison.Ordinal));
        } finally {
            Directory.Delete(root, true);
        }
    }

    /// <summary>The tree this assembly was compiled from, against the committed exemption file.</summary>
    /// <remarks>
    ///     ⚠ <b>Set equality in both directions.</b> A generator that stops being packed fails here,
    ///     and so does a line in the exemption file whose generator is packed again — so the list can
    ///     only shrink and a stale line cannot hide the next library to break the same way.
    /// </remarks>
    [Fact]
    public void The_repository_agrees_with_the_exemption_file() {
        var root = Repository();
        var projects = PluginReferenceRule.ProjectFiles(root);

        Assert.NotEmpty(projects);

        // Both halves of the subject set. A walk that found no generators, and one that could no
        // longer read a pack target, both report a number and mean nothing — and the second is the
        // one that would report every generator in the tree as an offender.
        Assert.NotEmpty(GeneratorPackagingRule.Subjects(projects));
        Assert.NotEmpty(GeneratorPackagingRule.Packaged(projects));

        Assert.Empty(GeneratorPackagingRule.Violations(root, projects));
    }

    /// <summary>
    ///     The libraries that carry a generator today, named rather than counted.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The list a reader of #1165's table would want, and the one thing a count cannot
    ///     say.</b> Ten of the thirteen generators in the tree are packed by a library; the three
    ///     that are not are in <c>docs/GeneratorPackagingExempt.txt</c> with a reason each. A
    ///     generator added without either is a violation above; this case is what makes a generator
    ///     silently dropped from a pack target fail with the name of the generator rather than with a
    ///     number.
    /// </remarks>
    [Fact]
    public void Every_generator_is_packed_or_exempt_by_name() {
        var root = Repository();
        var projects = PluginReferenceRule.ProjectFiles(root);

        var accounted = GeneratorPackagingRule.Packaged(projects)
            .Concat(GeneratorPackagingRule.Exempt(root))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(GeneratorPackagingRule.Subjects(projects), accounted);
    }
}
