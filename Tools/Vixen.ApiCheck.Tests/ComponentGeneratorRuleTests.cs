// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Build;
using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>
///     The <c>CheckArchitecture</c> rule that refuses a <c>[Component]</c> <c>[DataContract]</c> type
///     in a project that can see the scene registry and does not name the generator that declares it,
///     run here so that its answer exists without running the gate.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is not the rule <c>DataContractGeneratorRuleTests</c> covers, and the difference
///         is the whole issue.</b> That one refuses a project naming <em>neither</em>
///         <c>Vixen.Core</c> generator. A project that names both of those and not
///         <c>Vixen.Engine.Generators</c> reads clean there and is still missing
///         <c>SceneComponentRegistry.Declare&lt;T&gt;()</c> — which is exactly the state
///         <c>Vixen.Ai.Nodes</c> and <c>Vixen.Ai.Perception</c> were in (#1056), and the third time
///         the shape had to be found by hand.
///     </para>
///     <para>
///         ⚠ <b>Both directions, and a subject set, because a text rule with no positives is
///         satisfied by exactly the defect it is meant to catch.</b> The fixtures below make it fire
///         and make it go quiet, and the repository pass asserts the subject set is non-empty — a
///         walk that found no pair, or one that could no longer tell which projects reach
///         <c>Vixen.Engine</c>, would report "no violations" and mean "I read nothing".
///     </para>
/// </remarks>
public class ComponentGeneratorRuleTests {
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
        var path = Path.Combine(Path.GetTempPath(), "vixen-component-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A reference to the engine, which is what puts the registry in view.</summary>
    const string Engine = "    <ProjectReference Include=\"..\\..\\Core\\Vixen.Engine\\Vixen.Engine.csproj\" />\n";

    /// <summary>And the generator, as every in-tree consumer names it.</summary>
    const string Generators =
        "    <ProjectReference Include=\"..\\..\\Core\\Vixen.Engine.Generators\\Vixen.Engine.Generators.csproj\" "
        + "OutputItemType=\"Analyzer\" ReferenceOutputAssembly=\"false\" />\n";

    const string Pair = "[Component]\n[DataContract(\"Thing\")]\npublic struct Thing {\n}\n";

    [Fact]
    public void A_project_with_the_pair_that_skips_the_generator_is_an_offender() {
        var root = Temporary();

        try {
            var project = Fixture(Path.Combine(root, "Vixen.Fixture.Bare"), Engine, Pair);

            Assert.Equal(["Vixen.Fixture.Bare"], ComponentGeneratorRule.Subjects([project]));
            Assert.Equal(["Vixen.Fixture.Bare"], ComponentGeneratorRule.Offenders([project]));
        } finally {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(Engine + Generators)]
    [InlineData(Engine + "    <PackageReference Include=\"Vixen.Engine\" />\n")]
    public void Naming_the_generator_or_the_package_makes_it_clean(string references) {
        var root = Temporary();

        try {
            var project = Fixture(Path.Combine(root, "Vixen.Fixture.Named"), references, Pair);

            // ⚠ Still a subject — it declares the pair and sees the registry — and not an offender,
            // which is the distinction that keeps the rule from being green because it read nothing.
            Assert.Equal(["Vixen.Fixture.Named"], ComponentGeneratorRule.Subjects([project]));
            Assert.Empty(ComponentGeneratorRule.Offenders([project]));
        } finally {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     ⚠ <c>[Component]</c> alone is a bridge handle — <c>PhysicsBody</c> is one — and the
    ///     generator emits nothing for it by design. A rule keyed on that attribute alone would
    ///     demand an analyzer reference that could not produce a line of code, and the first person
    ///     to read its output would stop believing it.
    /// </summary>
    [Theory]
    [InlineData("[Component]\npublic struct Handle {\n}\n")]
    [InlineData("[DataContract(\"Value\")]\npublic struct Value {\n}\n")]
    public void Either_attribute_alone_is_not_the_pair(string source) {
        var root = Temporary();

        try {
            var project = Fixture(Path.Combine(root, "Vixen.Fixture.Half"), Engine, source);

            Assert.Empty(ComponentGeneratorRule.Subjects([project]));
            Assert.Empty(ComponentGeneratorRule.Offenders([project]));
        } finally {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     ⚠ <c>Vixen.Net</c> declares components and does not reference <c>Vixen.Engine</c>
    ///     deliberately, and <c>ComponentRegistrationGenerator</c> emits nothing where the registry
    ///     type is absent. So a project out of the engine's sight is not an offender: the reference
    ///     the rule would demand buys it nothing at all.
    /// </summary>
    [Fact]
    public void A_project_that_cannot_see_the_registry_is_not_a_subject() {
        var root = Temporary();

        try {
            var project = Fixture(
                Path.Combine(root, "Vixen.Fixture.Detached"),
                "    <ProjectReference Include=\"..\\..\\Core\\Vixen.Ecs\\Vixen.Ecs.csproj\" />\n",
                Pair
            );

            Assert.Empty(ComponentGeneratorRule.Subjects([project]));
            Assert.Empty(ComponentGeneratorRule.Offenders([project]));
        } finally {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     ⚠ Reachability is transitive and the engine assembly is its own case. <c>Vixen.Engine</c>
    ///     does not reference <c>Vixen.Engine</c>, so a walk alone reads the one project that
    ///     certainly holds the registry as unable to see it — a rule quietly skipping its own centre.
    /// </summary>
    [Fact]
    public void Reachability_is_transitive_and_the_engine_itself_counts() {
        Dictionary<string, HashSet<string>> edges = new(StringComparer.Ordinal) {
            ["Vixen.Engine"] = new(StringComparer.Ordinal) { "Vixen.Ecs" },
            ["Vixen.Rendering"] = new(StringComparer.Ordinal) { "Vixen.Engine" },
            ["Vixen.Rendering.Water"] = new(StringComparer.Ordinal) { "Vixen.Rendering" },
            ["Vixen.Net"] = new(StringComparer.Ordinal) { "Vixen.Ecs" }
        };

        Assert.True(ComponentGeneratorRule.SeesTheRegistry(edges, "Vixen.Engine"));
        Assert.True(ComponentGeneratorRule.SeesTheRegistry(edges, "Vixen.Rendering.Water"));
        Assert.False(ComponentGeneratorRule.SeesTheRegistry(edges, "Vixen.Net"));
    }

    /// <summary>
    ///     ⚠ The order the two attributes are written in is the author's, and a rule that read one
    ///     arrangement would report a clean tree about the other. A comma list is the same two
    ///     attributes and nothing makes the compiler prefer either form.
    /// </summary>
    [Theory]
    [InlineData("[Component]\n[DataContract(\"T\")]\nstruct T;\n")]
    [InlineData("[DataContract(\"T\")]\n[Component]\nstruct T;\n")]
    [InlineData("[Component, DataContract(\"T\")]\nstruct T;\n")]
    [InlineData("[Component]\n// why it is also a contract\n[DataContract(\"T\")]\nstruct T;\n")]
    [InlineData("[Component]\n[Vixen.Core.DataContract(\"T\")]\nstruct T;\n")]
    [InlineData("[Component]\n[DataContract(\n    \"T\"\n)]\nstruct T;\n")]
    public void Every_arrangement_of_the_pair_on_one_type_is_read(string source) {
        var file = Path.Combine(Temporary(), "Subject.cs");
        File.WriteAllText(file, source);

        try {
            Assert.Equal([1], ComponentGeneratorRule.Applications(file));
        } finally {
            Directory.Delete(Path.GetDirectoryName(file)!, true);
        }
    }

    /// <summary>
    ///     ⚠ Two attributes in a file is not two attributes on a type, and the difference is a false
    ///     positive that would put a correct project on the exemption list. The declaration between
    ///     them ends the run.
    /// </summary>
    [Theory]
    [InlineData("[Component]\nstruct A;\n\n[DataContract(\"B\")]\nstruct B;\n")]
    [InlineData("/// <remarks>A <c>[Component]</c> that is also a <c>[DataContract]</c>.</remarks>\nstruct A;\n")]
    [InlineData("// [Component]\n// [DataContract(\"A\")]\nstruct A;\n")]
    [InlineData("[Component]\n[Fixture(1, DataContract)]\nstruct A;\n")]
    [InlineData("[Component]\n[Fixture(\"[DataContract]\")]\nstruct A;\n")]
    public void Two_attributes_in_one_file_are_not_two_attributes_on_one_type(string source) {
        var file = Path.Combine(Temporary(), "Subject.cs");
        File.WriteAllText(file, source);

        try {
            Assert.Empty(ComponentGeneratorRule.Applications(file));
        } finally {
            Directory.Delete(Path.GetDirectoryName(file)!, true);
        }
    }

    /// <summary>
    ///     ⚠ A <c>.vxml</c> <c>&lt;code&gt;</c> block is production C#, and a sweep that reads only
    ///     <c>*.cs</c> reports a gap that is not there — silently, because a clean grep looks like
    ///     evidence. The same fixture <c>DataContractGeneratorRuleTests</c> carries, because the
    ///     source walk is literally the same function.
    /// </summary>
    [Fact]
    public void A_markup_files_code_block_counts_as_a_source() {
        var root = Temporary();

        try {
            var directory = Path.Combine(root, "Vixen.Fixture.Markup");
            var project = Fixture(directory, Engine, "public sealed partial class View {\n}\n");
            File.WriteAllText(Path.Combine(directory, "View.vxml"), "<code>\n" + Pair + "</code>\n");

            Assert.Equal(["Vixen.Fixture.Markup"], ComponentGeneratorRule.Offenders([project]));
        } finally {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     ⚠ An exempted project that has become clean is a violation too, which is the half that
    ///     makes the list only ever shrink — and the half a rule written in one direction does not
    ///     have. Without it a line that has done its job sits there hiding the next project to break
    ///     the same way, which is the exact shape <c>docs/WhitespaceExempt.txt</c> has a rule about.
    /// </summary>
    [Fact]
    public void The_exemption_list_can_only_shrink() {
        var root = Temporary();

        try {
            var offender = Fixture(Path.Combine(root, "Vixen.Fixture.Bare"), Engine, Pair);
            var clean = Fixture(Path.Combine(root, "Vixen.Fixture.Named"), Engine + Generators, Pair);

            Directory.CreateDirectory(Path.Combine(root, "docs"));
            var list = Path.Combine(root, "docs", "ComponentGeneratorExempt.txt");

            // Neither listed: the offender is reported and the clean project is not.
            File.WriteAllText(list, "# nothing yet\n");
            var unlisted = ComponentGeneratorRule.Violations(root, [offender, clean]);
            Assert.Single(unlisted);
            Assert.Contains("Vixen.Fixture.Bare", unlisted[0], StringComparison.Ordinal);

            // Listed with a reason: silent.
            File.WriteAllText(list, "Vixen.Fixture.Bare because the fixture says so\n");
            Assert.Empty(ComponentGeneratorRule.Violations(root, [offender, clean]));

            // And a line naming a project that is not an offender is itself the violation.
            File.WriteAllText(list, "Vixen.Fixture.Named it names the generator, so this line is stale\n");
            var stale = ComponentGeneratorRule.Violations(root, [offender, clean]);
            Assert.Equal(2, stale.Count);
            Assert.Contains(stale, message => message.Contains("can only shrink", StringComparison.Ordinal));
        } finally {
            Directory.Delete(root, true);
        }
    }

    /// <summary>The tree this assembly was compiled from, against the committed exemption file.</summary>
    /// <remarks>
    ///     ⚠ <b>Set equality in both directions, which is what makes the list only ever shrink.</b> An
    ///     exempted project that has become clean fails here, so a line that has done its job cannot
    ///     sit there hiding the next project to break the same way.
    /// </remarks>
    [Fact]
    public void The_repository_agrees_with_the_exemption_file() {
        var root = Repository();
        var projects = PluginReferenceRule.ProjectFiles(root);

        Assert.NotEmpty(projects);

        // The subject set, and both halves of it: a walk that found no pair, and a walk that could no
        // longer tell which projects reach Vixen.Engine, both report no violations and mean nothing.
        Assert.NotEmpty(ComponentGeneratorRule.Subjects(projects));

        Assert.Empty(ComponentGeneratorRule.Violations(root, projects));
    }
}
