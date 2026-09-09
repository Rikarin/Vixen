// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Blockout;
using Vixen.Geometry;
using Vixen.Ui;
using Vixen.Ui.Composition;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Editor.Blockout.Tests;

/// <summary>
///     docs/plan/42 § D13's UV panel, asserted through the rectangles it put on screen rather than
///     through the model it was handed.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every assertion here reads elements, and that is the point of the file.</b>
///         <c>BlockoutUvPanel</c> had a full suite before this view existed and every one of those
///         tests still passes with nothing drawn at all — which is what
///         <a href="https://github.com/Rikarin/Vixen/issues/414">#414</a> was. A model test cannot
///         distinguish "the islands were described" from "the islands are visible", and the second is
///         the whole of what was owed.
///     </para>
///     <para>
///         ⚠ <b>The placement assertions are a closed-form oracle rather than an eyeballing.</b> An
///         island covering <c>[0.25, 0.5]²</c> of the atlas must land at exactly a quarter of the
///         square's width from the left and a half of its height from the top — the second number
///         being the v flip, which is the half of this that looks right when it is wrong.
///     </para>
/// </remarks>
public sealed class BlockoutUvViewTests : IDisposable {
    readonly UiTest test = UiTest.Create();

    public BlockoutUvViewTests() {
        ControlTheme.Install(test.Document);
        AdvancedTheme.Install(test.Document);
        BlockoutTheme.Install(test.Document);
    }

    public void Dispose() => test.Dispose();

    /// <summary>A pack puts one rectangle in the atlas per island, which is § D13's "islands".</summary>
    /// <remarks>
    ///     ⚠ <b>The count is asserted on both sides.</b> A loop that asserts inside itself passes
    ///     vacuously over an empty collection, and "the view drew nothing" is precisely the state this
    ///     file exists to fail on — so the island count has to be non-zero before the equality means
    ///     anything.
    /// </remarks>
    [Fact]
    public void Every_island_becomes_a_rectangle_in_the_atlas() {
        var (view, panel) = Panel(MeshShapes.Create(ShapeKind.Box));

        Assert.True(panel.Pack());
        test.Frames(2);

        Assert.NotEmpty(panel.Views);
        Assert.Equal(panel.Views.Count, Islands(view).Length);
    }

    /// <summary>Nothing is drawn before a verb has run, so an empty atlas is honest rather than broken.</summary>
    [Fact]
    public void An_atlas_with_nothing_flattened_holds_no_rectangles() {
        var (view, _) = Panel(MeshShapes.Create(ShapeKind.Box));

        Assert.Empty(Islands(view));
        Assert.Contains("Chart", Text(view.Atlas.Parent!));
    }

    /// <summary>
    ///     An island lands where its UV coordinates say, with v measured from the bottom and the
    ///     rectangle drawn from the top.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the assertion that fails when the flip is dropped, and nothing else does.</b>
    ///     A packed atlas is roughly symmetric, so a vertically mirrored one has the right number of
    ///     rectangles of the right sizes in plausible places — every count-shaped test stays green.
    /// </remarks>
    [Fact]
    public void An_island_is_placed_from_the_top_with_v_flipped() {
        var tile = BlockoutUvView.Tile(new(0, new Vector2(0.25f, 0.25f), new Vector2(0.5f, 0.5f), 1f, 0));
        var quarter = BlockoutUvView.AtlasSize * 0.25f;

        // left = u_min, top = 1 - v_max: the same quarter here only because the island is centred,
        // which is deliberate — the next case is the one that separates them.
        Assert.Contains($"left: {quarter}px", tile.Geometry, StringComparison.Ordinal);
        Assert.Contains($"top: {quarter * 2f}px", tile.Geometry, StringComparison.Ordinal);
        Assert.Contains($"width: {quarter}px", tile.Geometry, StringComparison.Ordinal);
        Assert.Contains($"height: {quarter}px", tile.Geometry, StringComparison.Ordinal);

        // An island hard against the top of UV space draws at the top of the square, not the bottom.
        var high = BlockoutUvView.Tile(new(0, new Vector2(0f, 0.75f), new Vector2(1f, 1f), 1f, 0));

        Assert.Contains("top: 0px", high.Geometry, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A flipped island is marked as flipped and never as merely hot, which is the whole reason
    ///     <see cref="UvIslandView.IsBad" /> is a disjunction.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Both directions, because only the pair says the two channels are separate.</b> A
    ///     drawing that gave every bad island the same class would pass the first assertion and fail
    ///     the second; one that only ever used the ramp would pass the second and fail the first.
    /// </remarks>
    [Fact]
    public void A_flipped_island_is_graded_apart_from_the_stretch_ramp() {
        var flipped = BlockoutUvView.Tile(new(0, Vector2.Zero, Vector2.One, 1f, 1));
        var stretched = BlockoutUvView.Tile(new(1, Vector2.Zero, Vector2.One, 100f, 0));
        var flat = BlockoutUvView.Tile(new(2, Vector2.Zero, Vector2.One, 1f, 0));

        // Undistorted and inside out: the ramp would call this the best island in the atlas.
        Assert.Equal("uv-flipped", flipped.Grade);
        Assert.NotNull(flipped.Caption);

        // The worst the ramp can say is still not what a flipped island says.
        Assert.Equal("uv-heat-" + (BlockoutUvView.HeatSteps - 1), stretched.Grade);
        Assert.NotEqual(flipped.Grade, stretched.Grade);

        Assert.Equal("uv-heat-0", flat.Grade);
        Assert.Null(flat.Caption);
    }

    /// <summary>The grade reaches the element, so the class the stylesheet colours is the one on screen.</summary>
    [Fact]
    public void The_grade_lands_on_the_rectangle_as_a_class() {
        var (view, panel) = Panel(MeshShapes.Create(ShapeKind.Box));

        Assert.True(panel.Pack());
        test.Frames(2);

        var islands = Islands(view);

        Assert.NotEmpty(islands);

        foreach (var island in islands) {
            var flipped = island.HasClass("uv-flipped");
            var ramp = 0;

            for (var step = 0; step < BlockoutUvView.HeatSteps; step++) {
                if (island.HasClass("uv-heat-" + step)) {
                    ramp++;
                }
            }

            // Exactly one grade, and never both channels at once.
            Assert.Equal(flipped ? 0 : 1, ramp);
        }
    }

    /// <summary>
    ///     A second run replaces the rectangles, which is the reactivity a panel that rendered once
    ///     would not have.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The model raises <c>Changed</c> and the view redraws off it.</b> Nothing calls the
    ///     view when a verb runs from a menu or the CLI-shaped path, so a view that only refreshed
    ///     inside its own button handlers would show the first unwrap for ever.
    /// </remarks>
    [Fact]
    public void A_second_unwrap_replaces_the_rectangles() {
        var (view, panel) = Panel(MeshShapes.Create(ShapeKind.Box));

        Assert.True(panel.Pack());
        test.Frames(2);

        var before = Islands(view).Length;

        Assert.True(before > 0);

        panel.Mesh = MeshShapes.Create(ShapeKind.Cylinder);
        test.Frames(2);

        // Setting the mesh clears the derived state, so the atlas empties — through the event and
        // not through a call the test made on the view.
        Assert.Empty(Islands(view));

        Assert.True(panel.Pack());
        test.Frames(2);

        Assert.NotEmpty(Islands(view));
    }

    /// <summary>A refusal is a sentence on screen rather than an exception or an empty panel.</summary>
    [Fact]
    public void A_refusal_reaches_the_message_list() {
        var view = Build();

        view.Model = new BlockoutUvPanel();
        view.RunChart();
        test.Frames(2);

        Assert.NotEmpty(Tagged(view, "uv-message"));
        Assert.Contains("no mesh", Text(view.Messages), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Unmounting takes the subscription with it, so a closed tab stops being written to.</summary>
    /// <remarks>
    ///     ⚠ <b>The model is the module's and outlives every panel factory run.</b> A view that stayed
    ///     subscribed would write a signal on an element the document has already removed, once per
    ///     closed tab, for the rest of the session.
    /// </remarks>
    [Fact]
    public void A_removed_view_stops_following_its_model() {
        var view = Build();
        var panel = new BlockoutUvPanel { Mesh = MeshShapes.Create(ShapeKind.Box) };

        view.Model = panel;
        Assert.True(panel.Pack());
        test.Frames(2);

        Assert.NotEmpty(Islands(view));

        view.Remove();
        test.Frames(2);

        // No exception, and the drawing is not touched again.
        panel.Mesh = MeshShapes.Create(ShapeKind.Cylinder);
        Assert.True(panel.Pack());
        test.Frames(2);
    }

    // ============================================================ Harness

    BlockoutUvView Build() {
        var built = test.Document.Root.Add<BlockoutUvView>();

        test.Frames(2);

        return built;
    }

    (BlockoutUvView View, BlockoutUvPanel Panel) Panel(EditMesh mesh) {
        var view = Build();
        var panel = new BlockoutUvPanel { Mesh = mesh, Packing = new() { Resolution = 512, Margin = 2 } };

        view.Model = panel;
        test.Frames(2);

        return (view, panel);
    }

    static UiElement[] Islands(BlockoutUvView view) => Tagged(view, "uv-island");

    static UiElement[] Tagged(UiElement root, string tag) =>
        [.. Descendants(root).Where(element => string.Equals(element.Tag, tag, StringComparison.Ordinal))];

    static IEnumerable<UiElement> Descendants(UiElement root) {
        foreach (var child in root.Children) {
            yield return child;

            foreach (var deeper in Descendants(child)) {
                yield return deeper;
            }
        }
    }

    /// <summary>Everything an element and its descendants say, joined.</summary>
    static string Text(UiElement root) =>
        string.Join(
            ' ',
            Descendants(root)
                .Prepend(root)
                .Select(element => element.Text)
                .Where(text => !string.IsNullOrEmpty(text))
        );
}
