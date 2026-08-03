// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Editor.Inspector;
using Vixen.Editor.SceneView;
using Vixen.Editor.Plugin.Tests;
using Vixen.Editor.Testing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 36 § D3's four attributes, in a project script and in a plugin, identically.</summary>
/// <remarks>
///     <para>
///         <b>The claim is symmetry.</b> <c>[EditorMenu]</c>, <c>[CustomInspector]</c>,
///         <c>[CustomDrawer]</c> and <c>[EditorTool]</c> mean the same thing whichever tier declares
///         them, because both go through <c>PluginHost.Declared</c> and the one scanner the editor
///         registers. Before that they did not: the first worked only in a script and the other three
///         nowhere.
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
    /// <summary>One file declaring all four, which is the whole surface in one place.</summary>
    const string Declarations = """
        using System;
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

    /// <summary>Asserts the four effects, whichever tier put them there.</summary>
    static void AssertAllFour(EditorSession editor, IEditorRegistry registry) {
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
    }

    [Fact]
    public void All_four_work_in_a_project_script() {
        var data = Path.Combine(Path.GetTempPath(), "vixen-declared-script-" + Guid.NewGuid().ToString("N"));
        var registry = new EditorRegistry();

        try {
            using var editor = EditorSession.Start(new() { DataDirectory = data, Extensions = registry });

            Write(editor, Declarations);

            editor.Run("scripts.rebuild");
            editor.Settle();

            AssertAllFour(editor, registry);
        } finally {
            if (Directory.Exists(data)) {
                Directory.Delete(data, recursive: true);
            }
        }
    }

    /// <summary>
    ///     ⚠ <b>The same file, compiled and dropped in as a plugin instead.</b> A plugin's
    ///     <c>Activate</c> registers nothing here — everything comes from the four attributes, which
    ///     is the half of the symmetry that did not exist at all before.
    /// </summary>
    [Fact]
    public void All_four_work_in_a_plugin() {
        var data = Path.Combine(Path.GetTempPath(), "vixen-declared-plugin-" + Guid.NewGuid().ToString("N"));
        var registry = new EditorRegistry();

        using var folder = new PluginFolder(Path.Combine(data, "Plugins"));

        folder.Write("declared", Declarations);

        try {
            using var editor = EditorSession.Start(new() { DataDirectory = data, Extensions = registry });

            AssertAllFour(editor, registry);
        } finally {
            if (Directory.Exists(data)) {
                Directory.Delete(data, recursive: true);
            }
        }
    }
}
