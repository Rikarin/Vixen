// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Editor.Inspector;
using Vixen.Editor.Plugin.Tests;
using Vixen.Editor.Testing;
using Vixen.Editor.Ui;
using Vixen.Engine.Scenes;
using Vixen.Ui;
using Vixen.Ui.Controls;
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
[Collection(SharedDrawerRegistry.Name)]
public class OutOfTreePluginTests {
    /// <summary>What a plugin author writes. Nothing here is generated and nothing is in-tree.</summary>
    /// <remarks>
    ///     ⚠ <b>No <c>[Inspector]</c> attributes and no descriptor generator.</b> A plugin compiled
    ///     outside the solution has no analyzer, so <c>Widget</c> is a type the inspector registry has
    ///     never heard of — which is why the custom inspector below has to work without a descriptor,
    ///     and why the panel asks for one before it asks for the generated rows.
    /// </remarks>
    /// <summary>A library beside the plugin, holding a component and declaring it the ordinary way.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A second assembly and not the plugin's own, because the plugin's own proves
    ///         nothing.</b> The loader instantiates <c>SamplePlugin</c>, so that module's initializer
    ///         has already run by the time <c>Activate</c> is called and its components would be
    ///         registered whether anybody declared them or not. <c>AuthoringAssembly</c>'s own remark
    ///         names the case it is for — "a plugin whose components lived in a runtime assembly of
    ///         its own" — and this is that assembly.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The registrations are hand-written because a run-time compilation has no
    ///         generators.</b> What <c>[Component]</c> and <c>[DataContract]</c> would have emitted is
    ///         a serializer and a <c>[ModuleInitializer]</c> that declares the component; a plugin with
    ///         a build gets both written for it, and the only thing this fixture can do is write them
    ///         out. The <i>timing</i> is the same either way, and the timing is what is under test.
    ///     </para>
    /// </remarks>
    const string Runtime = """
        using System.Runtime.CompilerServices;
        using Vixen.Core.Serialization;
        using Vixen.Engine.Scenes;

        namespace Sample.Runtime;

        public struct WidgetCount {
            public int Value;
        }

        sealed class WidgetCountSerializer : DataSerializer<WidgetCount> {
            public override void Serialize(ref SerializationWriter writer, in WidgetCount value) =>
                writer.WriteInt32(value.Value);

            public override void Deserialize(ref SerializationReader reader, ref WidgetCount value) =>
                value.Value = reader.ReadInt32();
        }

        public static class Declarations {
            [ModuleInitializer]
            internal static void Run() {
                SerializerRegistry.Register("SampleWidgetCount", new WidgetCountSerializer());
                SceneComponentRegistry.Declare<WidgetCount>();
            }
        }
        """;

    const string Source = """
        using System;
        using Vixen.Editor.Core;
        using Vixen.Editor.Inspector;
        using Vixen.Editor.Plugin;
        using Vixen.Editor.SceneView;
        using Vixen.Editor.Ui;
        using Vixen.Ui;
        using Vixen.Ui.Controls;

        namespace Sample;

        // Doc 36 § D6's attribute, spelled the way doc 36 spells it. The data is Material's
        // "widgets" glyph, trimmed — what matters is that it is a `d` string and nothing else.
        [EditorIcon("M4 4h7v7H4zM13 4h7v7h-7zM13 13h7v7h-7zM4 13h7v7H4z", Tint = "#7cc4ff")]
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

                // Doc 36 § D5's fourth row. The components are in a library beside this assembly,
                // and nothing in the editor will ever call into it — declaring it is the whole of
                // what makes them appear.
                context.Owns(registry.Add(new AuthoringAssembly(typeof(Sample.Runtime.WidgetCount))));

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

    /// <summary>
    ///     ⚠ <b>Doc 36 § F8, and it is an assertion about an extension <i>point</i> rather than about
    ///     an extension.</b> The finding said there was no registry for a plugin to add an importer
    ///     to; there is one now, and what a plugin needs is for the editor to publish it. The
    ///     mechanism itself — folded into every registry built afterwards, withdrawn on unload — is
    ///     asserted in <c>Vixen.Editor.Assets.Tests.ImporterContributionTests</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Not exercised by the plugin above, and the reason is worth writing down.</b> That
    ///     plugin is compiled at run time by this test, with no source generators — and an importer is
    ///     named by its settings type's <c>[DataContract]</c> alias, which a generator writes. A
    ///     packaged plugin has a build and therefore has one; a run-time compilation does not, which
    ///     is the same limit a project's <c>Editor/</c> scripts hit.
    /// </remarks>
    [Fact]
    public void The_editor_publishes_somewhere_for_a_plugin_to_add_an_importer() {
        var data = Path.Combine(Path.GetTempPath(), "vixen-plugin-importers", Guid.NewGuid().ToString("N"));

        try {
            using var editor = EditorSession.Start(new() { DataDirectory = data });

            Assert.True(editor.Plugins.Services.Contains<Vixen.Editor.Assets.ImporterContributions>());
        } finally {
            if (Directory.Exists(data)) {
                Directory.Delete(data, recursive: true);
            }
        }
    }

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

        // The plugin's own library first, because the plugin is compiled against it.
        var runtime = folder.WriteLibrary("sample", "SampleRuntime", Runtime);

        folder.Write("sample", Source, manifest: null, runtime);

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
            // ⚠ The plugin's, not the only one. The terrain module contributes a markup inspector for
            // its brush settings — doc 36 § P4 — so what this asserts is that the plugin's arrived
            // beside the built-ins rather than that it is alone, which was only ever true by accident.
            var contributed = Assert.Single(
                registry.All<CustomInspector>(),
                inspector => inspector.Target.Name == "Widget"
            );
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

            // ── Five: the icon, which doc 36 § D6 wrote out and nothing implemented until there ──
            // was an SVG path parser to implement it with. The plugin declared a `d` string and a
            // tint on a type; what the editor has is art the outliner, the inspector header and the
            // Project panel all read through `EditorArt`.
            var icon = Assert.Single(registry.All<TypeIcon>(), entry => entry.Target.Name == "Widget");
            var drawn = Assert.Single(icon.Art.Paths);

            // Four squares, so four contours: a move, three lines and a close, four times over.
            Assert.Equal(4, drawn.Geometry.Segments.Count(segment => segment.Verb == PathVerb.Move));
            Assert.Equal(24f, icon.Art.ViewBox.Width);

            // And the tint the attribute named rather than the inherited colour, which is what a
            // literal is for.
            Assert.Equal(IconPaintKind.Literal, drawn.Fill.Kind);
            Assert.Equal(0.486f, drawn.Fill.Color.R, 2);

            // ── Six: the component, out of a runtime assembly nothing in the editor calls into. ──
            // Doc 36 § D5's fourth row, and the one contribution whose *effect* is a module
            // initializer having been run. `AuthoringAssembly` is the declaration; the plugin makes
            // it from `Activate`, which is after the editor read the registry — so this is an
            // assertion about when the declaration is acted on and not that it was accepted.
            var declared = Assert.Single(
                editor.Extensions.All<AuthoringAssembly>(),
                entry => entry.Marker.Name == "WidgetCount"
            );

            Assert.True(
                SceneComponentRegistry.TryGet("SampleWidgetCount", out _),
                "the plugin's component never registered, so its assembly's declarations never ran"
            );

            // And therefore in the offer, which re-reads the registry on every enumeration — the
            // menu was never the thing that was late.
            Assert.Contains(
                ComponentsView.Default(),
                bridge => bridge.ComponentType == declared.Marker
            );
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
