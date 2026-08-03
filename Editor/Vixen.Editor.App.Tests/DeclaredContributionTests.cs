// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Editor.Inspector;
using Vixen.Editor.Plugin.Tests;
using Vixen.Editor.SceneView;
using Vixen.Editor.Testing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 36 § D3's attributes, in a project script and in a plugin, identically.</summary>
/// <remarks>
///     <para>
///         <b>The claim is symmetry.</b> <c>[EditorMenu]</c>, <c>[CustomInspector]</c>,
///         <c>[CustomDrawer]</c>, <c>[EditorTool]</c>, <c>[CreateAssetMenu]</c>, <c>[Overlay]</c> and
///         <c>[DrawGizmo]</c> mean the same thing whichever tier declares them, because both go
///         through <c>PluginHost.Declared</c> and the one scanner the editor registers. Before that
///         they did not: the first worked only in a script and the rest nowhere.
///     </para>
///     <para>
///         ⚠ <b>Seven here and eight in Unity's set, because <c>[Importer]</c> is asserted where its
///         effects are.</b> An importer's registration is a claim about the asset database rather than
///         about the editor's chrome — <c>EditorScriptTests</c> drives it through a real import and a
///         <c>.meta</c> round trip, which is a stronger statement than "the record is in the
///         registry" and does not belong in a file about declaration.
///     </para>
///     <para>
///         ⚠ <b>Here rather than in <c>Vixen.Editor.Scripts.Tests</c>, and the move is the point.</b>
///         The attributes are read by <c>DeclaredContributions</c>, which lives in this assembly
///         because it names <c>CustomInspector</c>, <c>DrawerRegistry</c> and <c>SceneTool</c> — types
///         the plugin contract must not reference. A bare <c>PluginHost</c> has no scanners, so
///         testing the attributes against one would be testing a configuration nobody ships.
///     </para>
/// </remarks>
public class DeclaredContributionTests {
    /// <summary>One file declaring all seven, which is the whole surface in one place.</summary>
    const string Declarations = """
        using System;
        using System.Threading;
        using Vixen.Editor.Core;
        using Vixen.Editor.Inspector;
        using Vixen.Editor.Plugin;
        using Vixen.Editor.SceneView;
        using Vixen.Ui;
        using Vixen.Ui.Controls;

        public sealed class Widget {
            public float Size { get; set; } = 1f;
        }

        public static class Declared {
            [EditorMenu("Tools/Bake", Priority = 200)]
            public static void Bake() { }

            [EditorMenu("Tools/Audit", Priority = 100)]
            public static void Audit() { }

            [CustomInspector(typeof(Widget))]
            public static void DrawWidget(UiElement body, EditTarget target) {
                var label = body.Add<TextBlock>(null, null, Array.Empty<string>());

                label.AddClass("declared-inspector");
                label.Text = "Widget, drawn by a declaration";
            }

            /// <summary>
            ///     Counts its own calls, which is how the test tells "evaluated once when the
            ///     plugin loaded" from "evaluated per file". A starter document carrying an
            ///     identifier is the case that breaks under the first.
            /// </summary>
            public static int Made;

            [CreateAssetMenu("Dialogue Table", ".dialogue", DefaultName = "New Dialogue", Opens = false)]
            public static string NewDialogue() => "id: " + Interlocked.Increment(ref Made) + "\n";

            [Overlay("Declared Overlay", Corner = OverlayCorner.BottomLeft, Id = "declared.overlay")]
            public static void BuildOverlay(UiElement host, SceneViewport pane) {
                var label = host.Add<TextBlock>(null, null, Array.Empty<string>());

                label.AddClass("declared-overlay");
                label.Text = "Overlay, declared";
            }

            [DrawGizmo(typeof(Widget), SelectedOnly = true, Order = 5)]
            public static void DrawWidgetGizmo(
                GizmoDraw draw,
                object component,
                GizmoPlacement placement,
                bool selected
            ) {
                draw.Sphere(placement.Position, ((Widget) component).Size, new(1f, 0f, 0f, 1f));
            }
        }

        [CustomDrawer(typeof(decimal))]
        public sealed class MoneyDrawer : IPropertyDrawer {
            public UiElement Build(InspectorField field, UiElement parent) =>
                parent.Add<TextBox>(null, null, Array.Empty<string>());

            public void Show(InspectorField field, UiElement editor) { }
        }

        [EditorTool("Sculpt", typeof(Widget), Id = "declared.sculpt")]
        public sealed class SculptTool : IViewportInput {
            public bool Pointer(SceneViewport pane, PointerEvent args) => false;

            public bool Key(SceneViewport pane, KeyEvent args) => false;
        }

        /// <summary>
        ///     The entry point a *plugin* needs and a script does not. It registers nothing: every
        ///     one of the four contributions above comes from its attribute, which is the whole
        ///     claim. Included in both tiers' source so the two are literally the same file.
        /// </summary>
        public sealed class DeclaredPlugin : IEditorPlugin {
            public void Activate(PluginContext context) { }
        }
        """;

    /// <summary>Something with a decimal on it, so the declared drawer has a member to resolve for.</summary>
    sealed class Probe {
        public decimal Amount;
    }

    static void Write(EditorSession editor, string source) {
        var folder = Path.Combine(editor.ProjectRoot, "Assets", "Editor");

        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Declared.cs"), source);
    }

    /// <summary>Asserts the seven effects, whichever tier put them there.</summary>
    static void AssertAllSeven(EditorSession editor, IEditorRegistry registry) {
        // ── One: two menu items, in priority order rather than in the order the types came out. ──
        Assert.True(editor.CanRun("scripts.tools.bake"));
        Assert.True(editor.CanRun("scripts.tools.audit"));

        // ⚠ Their order relative to each other, not the whole menu's. The editor already ships a
        // Tools menu with its own lines in it, and a declaration lands among them — asserting the
        // whole list would be asserting what the editor happens to put there today.
        var tools = editor.Shell.Menus.Menus.Single(group => group.Title.Source == "Tools");
        var lines = tools.Entries.OfType<Vixen.Editor.Ui.MenuCommand>().Select(entry => entry.CommandId).ToList();

        Assert.True(
            lines.IndexOf("scripts.tools.audit") < lines.IndexOf("scripts.tools.bake"),
            "Priority 100 should come before priority 200; the source declares Bake first, so this "
            + "cannot pass by accident. Lines: " + string.Join(", ", lines)
        );

        // And it is on the bar somebody looks at, not only in the model.
        editor.Ui.Contains("Tools").ShouldExist();

        // ── Two: the custom inspector, for a type with no generated descriptor. ─────────────────
        var inspector = Assert.Single(registry.All<CustomInspector>(), entry => entry.Target.Name == "Widget");
        var widget = Activator.CreateInstance(inspector.Target)!;

        editor.Open("inspector");
        editor.Inspector.Inspect(widget);
        editor.Settle();

        editor.Ui.Contains("Widget, drawn by a declaration").ShouldExist();

        // ── Three: the drawer, resolved for a member the declaration has never seen. ────────────
        var member = new InspectorMember<Probe, decimal>("Amount", static probe => ref probe.Amount);

        Assert.Equal("MoneyDrawer", DrawerRegistry.Default.Resolve(member)?.GetType().Name);

        // ── Four: the scene tool, with the type it is for. ──────────────────────────────────────
        var tool = Assert.Single(registry.All<SceneTool>(), entry => entry.Id == "declared.sculpt");

        Assert.Equal("Sculpt", tool.Title);
        Assert.Equal("Widget", tool.Target?.Name);

        // ── Five: the Create ▸ line, asserted by the file it writes rather than by the record. ───
        var kind = Assert.Single(registry.All<NewAssetKind>(), entry => entry.Id == "assets.create.dialogue-table");

        Assert.Equal(".dialogue", kind.Extension);
        Assert.Equal("New Dialogue", kind.DefaultName);
        Assert.True(editor.CanRun(kind.Id));

        editor.Run(kind.Id);
        editor.Run(kind.Id);
        editor.Settle();

        var written = Directory.GetFiles(Path.Combine(editor.ProjectRoot, "Assets"), "*.dialogue")
            .Select(File.ReadAllText)
            .Order()
            .ToList();

        // ⚠ Two files with *different* contents, which is the whole reason `NewAssetKind.Build` is a
        // delegate. Evaluated once at load, both would say `id: 1` — and a starter document carrying
        // an identifier would collide with every other asset made from the same line, which is not a
        // failure anybody would go looking for in the Create menu.
        Assert.Equal(["id: 1\n", "id: 2\n"], written);

        // ── Six: the overlay, asserted on the pane rather than in the registry. ─────────────────
        var overlay = Assert.Single(registry.All<SceneOverlay>(), entry => entry.Id == "declared.overlay");

        Assert.Equal("Declared Overlay", overlay.Title);
        Assert.Equal(OverlayCorner.BottomLeft, overlay.Corner);

        // ⚠ The registry entry alone would pass with `ViewportChrome` never reading it — which is
        // this document's own "an attribute that looks like a mechanism", one layer along. Opening
        // the scene arranges the panes, and arranging them is what hosts the overlays.
        editor.Open("scene");
        editor.Frames(2);

        editor.Ui.Contains("Overlay, declared").ShouldExist();
        editor.Ui.Contains("Declared Overlay").ShouldExist();

        // ── Seven: the gizmo, with its target and its flags. ────────────────────────────────────
        var gizmo = Assert.Single(registry.All<ComponentGizmo>(), entry => entry.Target.Name == "Widget");

        Assert.True(gizmo.SelectedOnly);
        Assert.Equal(5, gizmo.Order);

        // ⚠ Driven directly rather than through a scene, because what this file is about is whether
        // the *declaration* arrived. That the pass finds the entities and honours the show flag is
        // `ComponentGizmoTests`, which does it against a real world.
        var vertices = new List<Vixen.Rendering.LineVertex>();

        gizmo.Draw(new(vertices), Activator.CreateInstance(gizmo.Target)!, default, selected: true);

        Assert.Equal(GizmoDraw.Segments * 3 * 2, vertices.Count);
    }

    [Fact]
    public void All_seven_work_in_a_project_script() {
        var data = Path.Combine(Path.GetTempPath(), "vixen-declared-script-" + Guid.NewGuid().ToString("N"));
        var registry = new EditorRegistry();

        try {
            using var editor = EditorSession.Start(new() { DataDirectory = data, Extensions = registry });

            Write(editor, Declarations);

            editor.Run("scripts.rebuild");
            editor.Settle();

            AssertAllSeven(editor, registry);
        } finally {
            if (Directory.Exists(data)) {
                Directory.Delete(data, recursive: true);
            }
        }
    }

    /// <summary>
    ///     ⚠ <b>The same file, compiled and dropped in as a plugin instead.</b> A plugin's
    ///     <c>Activate</c> registers nothing here — everything comes from the seven attributes, which
    ///     is the half of the symmetry that did not exist at all before.
    /// </summary>
    [Fact]
    public void All_seven_work_in_a_plugin() {
        var data = Path.Combine(Path.GetTempPath(), "vixen-declared-plugin-" + Guid.NewGuid().ToString("N"));
        var registry = new EditorRegistry();

        using var folder = new PluginFolder(Path.Combine(data, "Plugins"));

        folder.Write("declared", Declarations);

        try {
            using var editor = EditorSession.Start(new() { DataDirectory = data, Extensions = registry });

            AssertAllSeven(editor, registry);
        } finally {
            if (Directory.Exists(data)) {
                Directory.Delete(data, recursive: true);
            }
        }
    }
}
