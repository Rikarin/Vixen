// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Editor.Inspector;
using Vixen.Editor.Plugin.Tests;
using Vixen.Editor.Testing;
using Vixen.Editor.Ui;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 36 § P2's acceptance criterion: the front door, walked through from outside.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the test the whole document is scaffolding for.</b> F2's finding was that
///         every built-in feature is a project reference, so the plugin API has never had to be
///         sufficient and nothing ever proved the front door works. The plugin here is compiled at
///         run time from source this file holds, dropped into a folder, and loaded by an ordinary
///         editor start-up — it can see exactly what the editor publishes and nothing else.
///     </para>
///     <para>
///         Four contributions, because four is what P2 promised: a Create ▸ entry, a custom
///         inspector, a property drawer and a scene-view tool. Each is asserted by its <i>effect</i>
///         — a file on disk, an element in the panel, the drawer the registry resolves, the camera
///         the tool moved — rather than by the registration having been accepted.
///     </para>
/// </remarks>
public class OutOfTreePluginTests {
    /// <summary>What a plugin author writes. Nothing here is generated and nothing is in-tree.</summary>
    /// <remarks>
    ///     ⚠ <b>No <c>[Inspector]</c> attributes and no descriptor generator.</b> A plugin compiled
    ///     outside the solution has no analyzer, so <c>Widget</c> is a type the inspector registry has
    ///     never heard of — which is why the custom inspector below has to work without a descriptor,
    ///     and why the panel asks for one before it asks for the generated rows.
    /// </remarks>
    const string Source = """
        using System;
        using Vixen.Editor.Core;
        using Vixen.Editor.Inspector;
        using Vixen.Editor.Plugin;
        using Vixen.Editor.SceneView;
        using Vixen.Ui;
        using Vixen.Ui.Controls;

        namespace Sample;

        public sealed class Widget {
            public float Size { get; set; } = 1f;
        }

        /// <summary>Draws every decimal as a plain box, so the test can tell it apart from the built-in.</summary>
        public sealed class SampleDrawer : IPropertyDrawer {
            public UiElement Build(InspectorField field, UiElement parent) =>
                parent.Add<TextBox>(null, null, Array.Empty<string>());

            public void Show(InspectorField field, UiElement editor) { }
        }

        /// <summary>Moves the camera's pivot somewhere unmistakable when the pane is pressed.</summary>
        public sealed class SampleTool : IViewportInput {
            public bool Pointer(SceneViewport pane, PointerEvent args) {
                if (args.Action != PointerAction.Pressed) {
                    return false;
                }

                pane.Camera.Pivot = new Vixen.Core.Mathematics.Vector3(7f, 7f, 7f);
                return true;
            }

            public bool Key(SceneViewport pane, KeyEvent args) => false;
        }

        public sealed class SamplePlugin : IEditorPlugin {
            public void Activate(PluginContext context) {
                var registry = context.Services.Require<IEditorRegistry>();

                context.Owns(
                    registry.Add(
                        new NewAssetKind("sample.create-widget", "Widget", ".widget", "New Widget", "size: 1", false)
                    )
                );

                context.Owns(registry.Add(new CustomInspector(typeof(Widget), Draw)));
                context.Owns(registry.Add(new SceneTool("sample.paint", "Paint", new SampleTool())));

                var drawer = new SampleDrawer();

                context.With<DrawerRegistry>(
                    drawers => drawers.ForType(typeof(decimal), drawer),
                    drawers => drawers.Remove(drawer)
                );
            }

            static void Draw(UiElement body, EditTarget target) {
                var label = body.Add<TextBlock>(null, null, Array.Empty<string>());

                label.AddClass("sample-inspector");
                label.Text = "Widget, drawn by the plugin";
            }
        }
        """;

    /// <summary>Something with a decimal on it, so the plugin's drawer has a member to be resolved for.</summary>
    sealed class Probe {
        public decimal Amount;
    }

    [Fact]
    public void A_plugin_built_outside_the_solution_contributes_four_things_and_all_four_work() {
        var data = Path.Combine(
            Path.GetTempPath(),
            "vixen-plugin-acceptance",
            Guid.NewGuid().ToString("N")
        );

        // Written before the editor starts, because plugins are loaded during start-up — a plugin's
        // commands have to exist before the keymap is read and its panels before the layout is
        // applied. See `EditorApplication.StartPlugins`.
        using var folder = new PluginFolder(Path.Combine(data, "Plugins"));

        folder.Write("sample", Source);

        // ⚠ This session's own registry rather than the process-wide one, and the plugin's
        // contributions go into it because the editor publishes *its* registry to plugins. Without
        // it, a `SceneTool` and a Create ▸ entry from this test would be visible to every other
        // session in the run — the suite is parallel, and one of them asserts what Create ▸ holds.
        var registry = new EditorRegistry();

        using var editor = EditorSession.Start(new() { DataDirectory = data, Extensions = registry });

        try {
            // ── One: the Create ▸ entry. F3 was a literal tuple array in the application. ──────────
            Assert.NotNull(editor.Shell.Commands["sample.create-widget"]);
            Assert.Contains("sample.create-widget", CreateLines(editor));

            editor.Run("sample.create-widget");

            Assert.NotEmpty(Directory.GetFiles(Path.Combine(editor.ProjectRoot, "Assets"), "*.widget"));

            // ── Two: the custom inspector, for a type the descriptor generator never saw. F5. ─────
            var contributed = Assert.Single(registry.All<CustomInspector>());
            var widget = Activator.CreateInstance(contributed.Target)!;

            editor.Open("inspector");
            editor.Inspector.Inspect(widget);
            editor.Settle();

            // No generated rows — there is no descriptor for this type and there could not be.
            Assert.Empty(editor.Inspector.Rows);
            editor.Ui.Contains("Widget, drawn by the plugin").ShouldExist();

            // ── Three: the drawer, resolved for a member the plugin has never seen. F4. ───────────
            var member = new InspectorMember<Probe, decimal>("Amount", static probe => ref probe.Amount);
            var resolved = DrawerRegistry.Default.Resolve(member);

            Assert.NotNull(resolved);
            Assert.StartsWith("Sample.", resolved.GetType().FullName, StringComparison.Ordinal);

            // ── Four: the scene-view tool, which gets the pane's input before the mode does. F6. ──
            editor.Open("scene");
            editor.Settle();

            var pane = editor.Viewport ?? throw new InvalidOperationException("The scene panel opened no pane.");
            var tool = pane.FindTool("sample.paint");

            Assert.NotNull(tool);

            pane.ActiveTool = tool;
            editor.Click(pane.Control);

            Assert.Equal(7f, pane.Camera.Pivot.X, 3);
        } finally {
            // Disposing unloads the plugin, which is what withdraws its contributions — the scopes
            // `PluginContext.Owns` took. The registry goes with this test either way.
            editor.Dispose();

            try {
                Directory.Delete(data, recursive: true);
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                // A temp directory that would not go is not a failed test.
            }
        }
    }

    /// <summary>The ids the Create submenu offers, wherever the bar puts them.</summary>
    static IEnumerable<string> CreateLines(EditorSession editor) =>
        editor.Shell.Menus.Menus
            .SelectMany(static menu => menu.Entries)
            .OfType<MenuSubmenu>()
            .SelectMany(static submenu => submenu.Group.Entries)
            .SelectMany(
                static entry => entry switch {
                    MenuCommand command => [command.CommandId],
                    MenuDynamic dynamic => dynamic.CommandIds(),
                    _ => []
                }
            );
}
