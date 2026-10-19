// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Plugin.Generator.Tests;

/// <summary>One assembly's worth of every declaration attribute, for the generator and the scan alike.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>One source, linked into <c>Vixen.Editor.App.Tests</c> as well.</b> The parity test
///         there compiles this through the generator and reads it with the scan; the tests here hold
///         the generator alone to it. Two copies of the fixture would be two fixtures, and the parity
///         would be between them rather than between the readers.
///     </para>
///     <para>
///         Everything a reader could disagree about is in it at least once: a verb with a localised
///         title and an explicit id, a verb whose id is derived, a line for a command registered
///         elsewhere, a computed run of lines, a declared submenu with a title, a placement and a
///         priority, an undeclared submenu under a declared one, a separator, the seven contribution
///         kinds with a non-default named argument each, and an icon.
///     </para>
/// </remarks>
public static class DeclaredFixture {
    /// <summary>The C#.</summary>
    public const string Source = """
        using System;
        using System.Collections.Generic;
        using Vixen.Editor.Core;
        using Vixen.Editor.Inspector;
        using Vixen.Editor.Plugin;
        using Vixen.Editor.SceneView;
        using Vixen.Editor.Ui;
        using Vixen.Ui;
        using Vixen.Ui.Controls;

        namespace Fixture;

        public static class FixtureStrings {
            public static StringId MenuThings { get; } = new("fixture.menu.things", "Things");

            public static StringId CommandBake { get; } = new("fixture.command.bake", "Bake It");
        }

        [EditorIcon("M0 0 L10 0 L10 10 Z", Tint = "#ff0000", ViewBox = 10f, Order = 2)]
        public sealed class Widget {
            public float Size { get; set; } = 1f;
        }

        [EditorSubmenu("Tools/Things", Title = nameof(FixtureStrings.MenuThings), After = "editor.menu.nothing", Priority = 1)]
        public static class Verbs {
            public static int Baked;

            public static int Audited;

            [EditorMenu("Tools/Things/Bake", Title = nameof(FixtureStrings.CommandBake), Id = "fixture.bake", Priority = 20)]
            public static void Bake() => Baked++;

            [EditorMenu("Tools/Things/Audit", Priority = 10)]
            public static void Audit() => Audited++;

            [EditorMenu("Tools/Things/Deeper/Sweep", Separator = true)]
            public static void Sweep() { }

            [EditorMenu("Tools/Things", Priority = 30, Separator = true)]
            public const string Existing = "fixture.existing";

            [EditorMenu("Tools/Things/Deeper", Priority = -5)]
            public static IEnumerable<string> Family() => ["fixture.one", "fixture.two"];

            [CustomInspector(typeof(Widget), Order = 3)]
            public static void DrawWidget(UiElement body, EditTarget target) { }

            [CreateAssetMenu("Dialogue Table", ".dialogue", DefaultName = "New Dialogue", Opens = false)]
            public static string NewDialogue() => "id: 1\n";

            [Overlay("Declared Overlay", Corner = OverlayCorner.BottomLeft)]
            public static void BuildOverlay(UiElement host, IViewportPane pane) { }

            [DrawGizmo(typeof(Widget), SelectedOnly = true, Order = 5)]
            public static void DrawWidgetGizmo(GizmoDraw draw, object component, GizmoPlacement placement, bool selected) { }
        }

        [CustomDrawer(typeof(decimal))]
        public sealed class MoneyDrawer : IPropertyDrawer {
            public UiElement Build(InspectorField field, UiElement parent) => parent;

            public void Show(InspectorField field, UiElement editor) { }
        }

        [EditorTool("Sculpt", typeof(Widget), Order = 4)]
        public sealed class SculptTool : IViewportInput {
            public bool Pointer(IViewportPane pane, PointerEvent args) => false;

            public bool Key(IViewportPane pane, KeyEvent args) => false;
        }
        """;
}
