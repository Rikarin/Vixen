// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.CodeAnalysis;
using Xunit;

namespace Vixen.Editor.Plugin.Generator.Tests;

/// <summary>Doc 50 § D5: what the generator writes for an in-tree module, and what it refuses.</summary>
/// <remarks>
///     ⚠ <b>Whether the generated registration does the same thing as the scan is not asserted
///     here</b> — that needs the scan, which lives in <c>Vixen.Editor.App</c>, and is
///     <c>DeclaredParityTests</c> there. What is here is the generator alone: that it writes code that
///     compiles, writes nothing when there is nothing to write, and turns every way a declaration can
///     be wrong into a compile error with its own id.
/// </remarks>
public class DeclaredRegistrationTests {
    const string Usings = """
        using System;
        using System.Collections.Generic;
        using Vixen.Editor.Core;
        using Vixen.Editor.Inspector;
        using Vixen.Editor.Plugin;
        using Vixen.Editor.SceneView;
        using Vixen.Editor.Ui;
        using Vixen.Ui;

        namespace Fixture;

        """;

    [Fact]
    public void The_fixture_with_every_attribute_generates_a_register_that_compiles() {
        var result = GeneratorHarness.Run(DeclaredFixture.Source);

        Assert.Empty(result.Generator);
        Assert.Empty(result.Errors);

        var source = Assert.Single(result.Sources);

        Assert.Contains("namespace Fixture;", source, StringComparison.Ordinal);
        Assert.Contains("internal static partial class Declared", source, StringComparison.Ordinal);
        Assert.Contains("public static void Register(global::Vixen.Editor.Plugin.PluginContext context)", source, StringComparison.Ordinal);

        // ⚠ Every kind is in it, by the record or the call it ends in. A generator that silently
        // dropped one kind would still compile.
        foreach (var expected in new[] {
                     "menus.Submenu(", "menus.Command(", "menus.Line(", "menus.Lines(", "menus.Apply();",
                     "Inspector.CustomInspector(", "Inspector.DrawerRegistry>", "SceneView.SceneTool(",
                     "Core.NewAssetKind(", "SceneView.SceneOverlay(", "SceneView.ComponentGizmo(", "Ui.TypeIcon("
                 }) {
            Assert.Contains(expected, source, StringComparison.Ordinal);
        }
    }

    /// <summary>#1284: a title is the declaration it names, resolved at compile time, not a literal.</summary>
    [Fact]
    public void A_title_resolves_to_the_declaration_class_member_it_names() {
        var source = Assert.Single(GeneratorHarness.Run(DeclaredFixture.Source).Sources);

        Assert.Contains("global::Fixture.FixtureStrings.CommandBake", source, StringComparison.Ordinal);
        Assert.Contains("global::Fixture.FixtureStrings.MenuThings", source, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>The question <c>docs/GeneratorPackagingExempt.txt</c> tells everybody to ask</b>: what
    ///     does it emit into a compilation that declares none of its attributes? Nothing — so an
    ///     assembly that references the generator and declares nothing gets no <c>Declared</c> type,
    ///     and a module calling one is a compile error rather than a registration that does nothing.
    /// </summary>
    [Fact]
    public void An_assembly_that_declares_nothing_gets_nothing() {
        var result = GeneratorHarness.Run(Usings + "public static class Nothing { public static void Here() { } }");

        Assert.Empty(result.Generator);
        Assert.Empty(result.Sources);
    }

    [Theory]
    [InlineData("""[EditorMenu("Tools/Go")] public static void Go(int how) { }""", "VXP0001", "takes arguments")]
    [InlineData("""[EditorMenu("Tools/Go")] public static int Go() => 1;""", "VXP0001", "returns Int32")]
    [InlineData("""[EditorMenu("Tools/Go")] static void Go() { }""", "VXP0001", "not public and static")]
    [InlineData("""[EditorMenu("Tools")] public const int Go = 1;""", "VXP0001", "is not a string")]
    [InlineData("""[EditorMenu("Tools")] public static void Go() { }""", "VXP0004", "'Tools' is not a path to a menu line")]
    [InlineData("""[EditorMenu("Tools", Id = "x")] public const string Go = "x";""", "VXP0005", "names an Id or a Title")]
    [InlineData("""[EditorMenu("Tools/Go", Title = "Nowhere")] public static void Go() { }""", "VXP0003", "declares a public static StringId called Nowhere")]
    [InlineData("""[EditorMenu("Tools/Go", Strings = typeof(EditorStrings))] public static void Go() { }""", "VXP0003", "no Title to find in it")]
    [InlineData("""[CustomInspector(typeof(string))] public static void Draw(UiElement body) { }""", "VXP0001", "static void (UiElement body, EditTarget target)")]
    [InlineData("""[CreateAssetMenu("Thing", "thing")] public static string Make() => "";""", "VXP0001", "'thing' is not an extension")]
    [InlineData("""[Overlay("Over")] public static void Build(UiElement host) { }""", "VXP0001", "static void (UiElement host, IViewportPane pane)")]
    [InlineData("""[Overlay("Over")] public static void Build(UiElement host, SceneViewport pane) { }""", "VXP0001", "static void (UiElement host, IViewportPane pane)")]
    [InlineData("""[DrawGizmo(typeof(string))] public static void Draw(GizmoDraw draw) { }""", "VXP0001", "GizmoPlacement placement")]
    [InlineData("""[CreateAssetMenu("Thing", ".thing", Title = "Nowhere")] public static string Make() => "";""", "VXP0003", "declares a public static StringId called Nowhere")]
    [InlineData("""[CreateAssetMenu("Thing", ".thing", Strings = typeof(EditorStrings))] public static string Make() => "";""", "VXP0003", "no Title to find in it")]
    [InlineData("""[Overlay("Over", Title = "Nowhere")] public static void Build(UiElement host, IViewportPane pane) { }""", "VXP0003", "declares a public static StringId called Nowhere")]
    [InlineData("""[Overlay("Over", Strings = typeof(EditorStrings))] public static void Build(UiElement host, IViewportPane pane) { }""", "VXP0003", "no Title to find in it")]
    public void A_wrong_declaration_is_a_compile_error_with_its_own_id(string member, string id, string says) {
        var result = GeneratorHarness.Run(Usings + "public static class Holder {\n    " + member + "\n}\n");

        var diagnostic = Assert.Single(result.Generator);

        Assert.Equal(id, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains(says, diagnostic.GetMessage(), StringComparison.Ordinal);

        // ⚠ And it points at the declaration, not at nothing. A diagnostic with no location is one
        // the IDE puts at the top of the error list with no file to open.
        Assert.Equal("Fixture.cs", diagnostic.Location.GetLineSpan().Path);

        // Nothing is written for an assembly with a refused declaration: half a registration would
        // only add errors about the half.
        Assert.Empty(result.Sources);
    }

    [Fact]
    public void A_title_two_declaration_classes_have_is_refused_rather_than_guessed() {
        var result = GeneratorHarness.Run(
            Usings + """
                public static class OneStrings { public static StringId Go { get; } = new("one.go", "Go"); }
                public static class TwoStrings { public static StringId Go { get; } = new("two.go", "Go"); }
                public static class Holder {
                    [EditorMenu("Tools/Go", Title = "Go")] public static void Go() { }
                }
                """
        );

        var diagnostic = Assert.Single(result.Generator);

        Assert.Equal("VXP0003", diagnostic.Id);
        Assert.Contains("OneStrings and TwoStrings both declare", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void Strings_names_the_class_when_the_title_is_another_assemblys() {
        var result = GeneratorHarness.Run(
            Usings + """
                public static class Holder {
                    [EditorMenu("Scene/Geometry", Title = nameof(EditorStrings.MenuGeometry), Strings = typeof(EditorStrings))]
                    public static void Go() { }
                }
                """
        );

        Assert.Empty(result.Generator);
        Assert.Empty(result.Errors);
        Assert.Contains("global::Vixen.Editor.Ui.EditorStrings.MenuGeometry", Assert.Single(result.Sources), StringComparison.Ordinal);
    }

    /// <summary>The scan would skip it; the generated class could not name it. Refused either way.</summary>
    [Fact]
    public void A_declaration_in_a_type_the_generated_class_cannot_name_is_refused() {
        var result = GeneratorHarness.Run(
            Usings + """
                public static class Outer {
                    static class Hidden {
                        [EditorMenu("Tools/Go")] public static void Go() { }
                    }
                }
                """
        );

        Assert.Equal("VXP0002", Assert.Single(result.Generator).Id);
    }

    [Fact]
    public void A_drawer_that_is_not_one_is_refused() {
        var result = GeneratorHarness.Run(Usings + "[CustomDrawer(typeof(decimal))] public sealed class NotADrawer { }");

        var diagnostic = Assert.Single(result.Generator);

        Assert.Equal("VXP0001", diagnostic.Id);
        Assert.Contains("is not a concrete IPropertyDrawer", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_submenu_path_with_no_submenu_in_it_is_refused() {
        var result = GeneratorHarness.Run(Usings + """[EditorSubmenu("Scene")] public static class Holder { }""");

        Assert.Equal("VXP0004", Assert.Single(result.Generator).Id);
    }

    /// <summary>A partial type with an attribute is registered once, not once per declaration.</summary>
    [Fact]
    public void A_partial_type_registers_its_submenus_once() {
        var result = GeneratorHarness.Run(
            Usings + """
                [EditorSubmenu("Tools/Things")]
                public static partial class Holder { }

                public static partial class Holder {
                    [EditorMenu("Tools/Things")] public const string Go = "go";
                }
                """
        );

        var source = Assert.Single(result.Sources);

        Assert.Equal(1, source.Split("menus.Submenu(").Length - 1);
    }

    /// <summary>
    ///     ⚠ <b>An attribute on a later part of a partial type still registers</b> — and each of two
    ///     parts' attributes once. The pipeline visits only the parts that carry an attribute, so a
    ///     generator that read the type from its first part alone would see nothing here.
    /// </summary>
    [Fact]
    public void Attributes_on_any_part_of_a_partial_type_register_once_each() {
        var result = GeneratorHarness.Run(
            Usings + """
                public static partial class Holder {
                    [EditorMenu("Tools/Things")] public const string Go = "go";
                }

                [EditorSubmenu("Tools/Things")]
                public static partial class Holder { }

                [EditorSubmenu("Tools/Others")]
                public static partial class Holder { }
                """
        );

        var source = Assert.Single(result.Sources);

        Assert.Equal(2, source.Split("menus.Submenu(").Length - 1);
        Assert.Contains("\"Tools/Things\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Tools/Others\"", source, StringComparison.Ordinal);
    }

    /// <summary>The namespace is the assembly's, made into an identifier where the name is not one.</summary>
    [Fact]
    public void The_namespace_is_the_assembly_name_made_legal() {
        var result = GeneratorHarness.Run(DeclaredFixture.Source, "my-plugin.2nd.class");

        Assert.Empty(result.Errors);
        Assert.Contains("namespace my_plugin._2nd.@class;", Assert.Single(result.Sources), StringComparison.Ordinal);
    }
}
