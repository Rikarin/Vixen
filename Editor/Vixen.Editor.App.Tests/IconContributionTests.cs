// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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
