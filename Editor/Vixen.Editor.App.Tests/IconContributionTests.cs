// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.Loader;
using Vixen.Editor.SceneView;
using Vixen.Editor.Testing;
using Vixen.Editor.Ui;
using Vixen.Engine.Transforms;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 36 § D6: a type declares its icon, and every surface serves the same one.</summary>
/// <remarks>
///     ⚠ <b>Through the shell, because what is being tested is that four panels agree.</b> Asserting
///     that <c>EditorArt.Of</c> finds a registration would be asserting a dictionary lookup — the
///     claim is that the outliner, the two Project views and the inspector's header all reach it, and
///     the only place that is true or false is a running editor.
/// </remarks>
public class IconContributionTests {
    [Fact]
    public void An_outliner_row_is_drawn_by_what_the_entity_carries() {
        using var editor = EditorSession.Start();

        var lit = editor.Scene.Add("Lamp", LocalTransform.Identity);
        var bare = editor.Scene.Add("Empty", LocalTransform.Identity);

        editor.Scene.World.Add(lit, Lights.Default(LightKind.Point));
        editor.Settle();

        var rows = Rows(editor.Hierarchy).ToList();
        var lamp = rows.First(node => node.Text == "Lamp");
        var empty = rows.First(node => node.Text == "Empty");

        Assert.NotNull(lamp.Art);
        Assert.NotSame(lamp.Art, empty.Art);

        // And it is the registered picture rather than some other fallback, which is the difference
        // between "the registry is read" and "the two rows happen to differ".
        Assert.Same(EditorArt.Of(editor.Extensions.All<TypeIcon>(), typeof(Light)), lamp.Art);

        Assert.NotEqual(default, bare);
    }

    /// <summary>
    ///     ⚠ The outliner listens to the structure and the rename; a component arriving moved neither,
    ///     so a light added to an existing entity used to leave the row drawing the plain dot until
    ///     something unrelated rebuilt the tree.
    /// </summary>
    [Fact]
    public void Adding_a_component_redraws_the_row_it_was_added_to() {
        using var editor = EditorSession.Start();

        var entity = editor.Scene.Add("Lamp", LocalTransform.Identity);
        editor.Settle();

        var before = Rows(editor.Hierarchy).First(node => node.Text == "Lamp").Art;

        editor.Scene.World.Add(entity, Lights.Default(LightKind.Point));
        editor.Scene.Recomposed(entity);
        editor.Settle();

        var after = Rows(editor.Hierarchy).First(node => node.Text == "Lamp").Art;

        Assert.NotSame(before, after);
    }

    [Fact]
    public void A_component_foldout_carries_the_components_icon() {
        using var editor = EditorSession.Start();

        var entity = editor.Scene.Add("Lamp", LocalTransform.Identity);

        editor.Scene.World.Add(entity, Lights.Default(LightKind.Point));
        editor.Scene.Selection.Set([entity]);
        editor.Settle();

        var icons = Descendants(editor.Inspector)
            .OfType<Icon>()
            .Where(icon => icon.HasClass("component-icon"))
            .ToList();

        var light = EditorArt.Of(editor.Extensions.All<TypeIcon>(), typeof(Light));

        Assert.Contains(icons, icon => ReferenceEquals(icon.Art, light));
    }

    /// <summary>
    ///     ⚠ <b>The claim doc 36 exists to earn, in one assertion.</b> Terrain is a module: its
    ///     assembly cannot see <c>Vixen.Editor.App</c> at all, and the Project panel cannot see
    ///     <c>Vixen.Editor.Terrain</c>. The picture a <c>.vxlayer</c> shows therefore travels through
    ///     the registry or it does not travel.
    /// </summary>
    [Fact]
    public void A_module_declares_the_pictures_for_the_file_kinds_it_introduced() {
        using var editor = EditorSession.Start();

        var art = EditorArt.Of(editor.Extensions.All<AssetIcon>(), importer: null, "Rock.vxlayer");

        Assert.NotNull(art);

        // Multicoloured, which is D6's exit criterion and not merely "has an icon": three bands in
        // three literal colours, because a paint layer is one of a stack.
        Assert.True(art.Paths.Count >= 3, $"a terrain layer should be several paths, and was {art.Paths.Count}");
        Assert.All(art.Paths, path => Assert.Equal(IconPaintKind.Literal, path.Fill.Kind));

        var colours = art.Paths.Select(path => path.Fill.Color).Distinct().Count();
        Assert.True(colours >= 3, $"its paths should be different colours, and there were {colours}");
    }

    /// <summary>
    ///     ⚠ <b>Every component the editor ships, not three of them.</b> <c>Light</c>, <c>Camera</c>
    ///     and <c>PrimitiveShape</c> had a registration and the other thirty drew the generic chip —
    ///     an inspector where two rows in nine are identifiable is one where the icon column is
    ///     decoration. This is the assertion that keeps it true: a component added to a subsystem
    ///     without a line in <c>MaterialIcons.Components</c> fails here rather than quietly arriving
    ///     unlabelled.
    /// </summary>
    [Fact]
    public void Every_builtin_component_has_a_picture_of_its_own() {
        using var editor = EditorSession.Start();

        var icons = editor.Extensions.All<TypeIcon>();

        // ⚠ Not this assembly's own components, and the exclusion is load-bearing rather than tidy.
        // `SceneComponentRegistry` is process-wide and a registration is never withdrawn — the test
        // two files over registers a component to prove the bridge list is a live view of it, and
        // that type is then visible to every test that runs afterwards in the same process. What is
        // being asserted here is what the editor *ships*.
        //
        // ⚠ And a plugin's components are not in this assembly, which is the half that was missing.
        // `OutOfTreePluginTests` compiles `Sample.Runtime` at run time and loads it through a
        // `PluginLoadContext`, whose module initializer declares a component into the same
        // process-wide registry — so the filter above let it through and this failed with
        // `Sample.Runtime.WidgetCount`. It was green only while xunit ran the two classes at once
        // and this one usually won: `Every_builtin_component_has_a_picture_of_its_own` takes a
        // second and the plugin test spends its first ten compiling with Roslyn. Disabling this
        // assembly's parallelism for #365 fixed the order and made the pollution certain, which is
        // the better failure — but the defect is the filter, and the load context is what says
        // "shipped" exactly: everything the editor was built with is in the default one, and
        // everything a plugin brought is deliberately not.
        var bare = ComponentsView.Default(null, editor.Extensions)
            .Where(bridge => bridge.Kind == AuthoringKind.Component)
            .Where(bridge => bridge.ComponentType.Assembly != typeof(IconContributionTests).Assembly)
            .Where(
                bridge => AssemblyLoadContext.GetLoadContext(bridge.ComponentType.Assembly)
                    == AssemblyLoadContext.Default
            )
            .Where(bridge => EditorArt.Of(icons, bridge.ComponentType) is null)
            .Select(bridge => bridge.ComponentType.FullName)
            .ToList();

        Assert.True(bare.Count == 0, "these ship with no icon: " + string.Join(", ", bare));
    }

    /// <summary>The art is real geometry rather than an empty shell.</summary>
    /// <remarks>
    ///     ⚠ <b>The icons are SVG path data now — see <c>MaterialIcons</c> — so "has an icon" and "has
    ///     an icon that draws something" are two different claims.</b> A typo in a <c>d</c> string
    ///     would give an <c>IconArt</c> with a path of no segments in it, which is an icon slot that is
    ///     reserved, laid out, and empty.
    /// </remarks>
    [Fact]
    public void And_every_one_of_them_has_geometry_in_it() {
        using var editor = EditorSession.Start();

        foreach (var icon in editor.Extensions.All<TypeIcon>()) {
            Assert.NotEmpty(icon.Art.Paths);
            Assert.All(icon.Art.Paths, path => Assert.NotEmpty(path.Geometry.Segments));
        }
    }

    /// <summary>A folder and a file are pictures rather than tinted verbs.</summary>
    [Fact]
    public void A_folder_is_layered_so_that_it_reads_as_one() {
        // Three tones stacked: the back and tab, the front panel, and the band of light along its
        // top. `IconPaint` has no gradient — see `MaterialIcons.Folder` — and this is what stands in
        // for one.
        Assert.Equal(3, StandardIcons.Folder.Paths.Count);
        Assert.All(StandardIcons.Folder.Paths, path => Assert.Equal(IconPaintKind.Literal, path.Fill.Kind));

        var tones = StandardIcons.Folder.Paths.Select(path => path.Fill.Color).Distinct().Count();

        Assert.Equal(3, tones);
    }

    static IEnumerable<TreeNode> Rows(TreeView tree) {
        return Walk(tree.Root.Children);

        static IEnumerable<TreeNode> Walk(IReadOnlyList<TreeNode> nodes) {
            foreach (var node in nodes) {
                yield return node;

                foreach (var child in Walk(node.Children)) {
                    yield return child;
                }
            }
        }
    }

    static IEnumerable<Vixen.Ui.UiElement> Descendants(Vixen.Ui.UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }
}
