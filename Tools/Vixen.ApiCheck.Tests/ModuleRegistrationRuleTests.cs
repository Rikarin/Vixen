// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Build;
using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>
///     Doc 50 § D6's <c>ModuleRegistrationRule</c>, run here so that its answer exists without running
///     the gate — over this checkout and over fixtures that open each side door.
/// </summary>
/// <remarks>
///     ⚠ The list is today's findings, so the tree's answer is "no violations" by construction; each
///     door is shown found in a fixture, and a project with no module is shown not to be a subject,
///     before that green is believed.
/// </remarks>
public class ModuleRegistrationRuleTests {
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

    sealed class Fixture : IDisposable {
        readonly ScratchCheckout checkout = new("vixen-modulereg-");

        public string Root => checkout.Root;

        public Fixture Write(string relative, string text) {
            checkout.Write(relative, text);
            return this;
        }

        public void Dispose() => checkout.Dispose();
    }

    const string Module = "sealed class WaterModule : IEditorPlugin {\n    public void Activate(PluginContext context) { }\n}\n";

    [Fact]
    public void Only_a_project_declaring_a_module_is_a_subject() {
        using var fixture = new Fixture()
            .Write("Editor/Vixen.Editor.Water/Vixen.Editor.Water.csproj", "<Project />")
            .Write("Editor/Vixen.Editor.Water/WaterModule.cs", Module)
            .Write("Editor/Vixen.Editor.Ui/Vixen.Editor.Ui.csproj", "<Project />")
            .Write("Editor/Vixen.Editor.Ui/Shell.cs", "sealed class Shell { void Go() => Shell.RegisterPanel(null); }\n")
            .Write("Editor/Vixen.Editor.Plugin/Vixen.Editor.Plugin.csproj", "<Project />")
            .Write("Editor/Vixen.Editor.Plugin/PluginContext.cs", "interface IEditorPlugin { }\nsealed class Host : IEditorPlugin { }\n")
            .Write("Editor/Vixen.Editor.Water.Tests/Vixen.Editor.Water.Tests.csproj", "<Project />")
            .Write("Editor/Vixen.Editor.Water.Tests/Probe.cs", Module)
            .Write("Tools/Vixen.Templates/templates/vixen-plugin/Plugin.csproj", "<Project />")
            .Write("Tools/Vixen.Templates/templates/vixen-plugin/Plugin1.cs", "sealed class Plugin1(int x) : Base, IEditorPlugin { }\n");

        // A module is a subject wherever it lives, the template included; the contract and a test are
        // not, and neither is an assembly that only mentions the interface in a constraint.
        Assert.Equal(
            ["Editor/Vixen.Editor.Water", "Tools/Vixen.Templates/templates/vixen-plugin"],
            ModuleRegistrationRule.Subjects(fixture.Root)
        );
    }

    /// <summary>The issue's sabotage: a <c>Shell.RegisterPanel</c> added to <c>WaterModule</c>.</summary>
    [Fact]
    public void Every_side_door_is_found_and_comments_are_not() {
        var doors = ModuleRegistrationRule.Doors(
            FeatureNameRule.Lex(
                """
                Shell.RegisterPanel("water.panel", title, Build);
                shell.Modes.Add(water);
                Shell.Commands.Add(command);
                Shell.RegisterLayout("Water", title, layout);
                WaterTheme.Install(Shell.Document);
                context.Services.Add<IWaterColliders>(colliders);
                // Shell.RegisterPanel("commented out");
                context.AddPanel("fine", title, Build);
                """
            ).Code
        );

        Assert.Equal(
            ["Services.Add", "Shell.Commands.Add", "Shell.Modes.Add", "Shell.RegisterLayout", "Shell.RegisterPanel", "WaterTheme.Install"],
            doors.Keys
        );
        Assert.All(doors.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public void A_new_door_a_grown_count_a_slack_count_and_a_stale_row_are_each_a_violation() {
        IReadOnlyList<FeatureNameRule.Finding> found = [new("Editor/Vixen.Editor.Water/WaterModule.cs", "Shell.RegisterPanel", 2)];

        static Dictionary<string, (int, string)> List(int count) =>
            new(StringComparer.Ordinal) { ["Editor/Vixen.Editor.Water/WaterModule.cs Shell.RegisterPanel"] = (count, "") };

        Assert.Empty(ModuleRegistrationRule.Violations(found, List(2)));
        Assert.Single(ModuleRegistrationRule.Violations(found, new Dictionary<string, (int, string)>()));
        Assert.Contains("allows 1", Assert.Single(ModuleRegistrationRule.Violations(found, List(1))), StringComparison.Ordinal);
        Assert.Contains("still says 3", Assert.Single(ModuleRegistrationRule.Violations(found, List(3))), StringComparison.Ordinal);
        Assert.Contains("no longer", Assert.Single(ModuleRegistrationRule.Violations([], List(2))), StringComparison.Ordinal);
    }

    [Fact]
    public void A_tree_with_no_module_is_vacuity_and_not_a_clean_result() {
        using var fixture = new Fixture().Write("Editor/Vixen.Editor.Ui/Vixen.Editor.Ui.csproj", "<Project />");

        Assert.NotNull(ModuleRegistrationRule.Vacuity(ModuleRegistrationRule.Subjects(fixture.Root)));
    }

    /// <summary>This checkout, against the committed list, with the instrument checked first.</summary>
    [Fact]
    public void No_module_on_this_tree_goes_round_PluginContext_beyond_the_list() {
        var root = Repository();
        var subjects = ModuleRegistrationRule.Subjects(root);

        Assert.Null(ModuleRegistrationRule.Vacuity(subjects));

        // Named rather than counted: the module doc 50 § F7 found leaking most, and the template a
        // third party copies.
        Assert.Contains("Editor/Vixen.Editor.Terrain", subjects);
        Assert.Contains("Tools/Vixen.Templates/templates/vixen-plugin", subjects);
        Assert.DoesNotContain(ModuleRegistrationRule.Contract, subjects.Select(Path.GetFileName));

        var findings = ModuleRegistrationRule.Scan(root, subjects);

        Assert.Equal([], ModuleRegistrationRule.Violations(findings, ModuleRegistrationRule.Exempt(root)));
    }
}
