// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Texturing.Painting;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>
///     A drag on the model: the projector driving <c>PaintSession</c>, mirrors and all.
/// </summary>
/// <remarks>
///     <para>
///         <b>⚠ The question this file exists to answer is not whether the arithmetic is right — that
///         is <c>PaintProjectionTests</c>' — but whether the 3D path goes <em>through</em> the chain
///         that already exists or around it.</b> Doc 48 § D13's warning is that a stroke crossing a
///         UV island edge leaves a hairline that only appears after mipping, and the dilation that
///         prevents it lives in <c>PaintStroke</c>. A projection that composed its own stamps would
///         look identical in the pane and be wrong three mips down, which is precisely the defect
///         nobody sees while painting.
///     </para>
///     <para>
///         ⚠ <b>So the seam case here is not a duplicate of <c>PaintSeamTests</c>.</b> That file
///         proves the dilation works when a stroke is handed texel positions; this one proves that
///         positions arriving from a ray reach it, which is a claim about the wiring and not about
///         the gutter.
///     </para>
/// </remarks>
public class PaintProjectorTests {
    const uint Opaque = 0xFF0000FFu;

    /// <summary>The atlas these cases paint into.</summary>
    const int Size = 64;

    /// <summary>A camera set so that one render pixel is exactly one texel.</summary>
    /// <remarks>
    ///     ⚠ <b>Chosen so the conversion is the identity, which is what makes every radius below
    ///     readable.</b> Eight units of view over 512 pixels is 1/64 of a unit per pixel, and this
    ///     fixture's layout is 64 texels per unit — so a brush of <i>n</i> screen pixels is a brush
    ///     of <i>n</i> texels. The conversion itself is <c>PaintFootprintTests</c>' subject; here it
    ///     is deliberately made to disappear so that a failure is about the drag.
    /// </remarks>
    static PaintEye Eye() => PaintEye.Orthographic(new(0f, 0f, 10f), new(0f, 0f, -1f), 8f, 512);

    /// <summary>A session over a blank atlas with a hard brush of a radius.</summary>
    /// <param name="radius">The brush radius, in texels.</param>
    /// <param name="image">The layer that was painted into.</param>
    /// <param name="coverage">Which texels an island covers.</param>
    /// <param name="gutter">How far a stamp is dilated past an island edge.</param>
    /// <returns>The session.</returns>
    static PaintSession Session(float radius, out PaintImage image, PaintCoverage? coverage = null, int gutter = 4) {
        image = new(Size, Size);

        return PaintSession.Begin(
            new(image, coverage ?? PaintCoverage.Everywhere(Size, Size), PaintStackImages.Empty(Size, Size), gutter),
            PaintStrokeTests.Hard(radius),
            Opaque
        );
    }

    /// <summary>A drag across the model is one undo entry, mirrors or not.</summary>
    /// <remarks>
    ///     ⚠ <b>Doc 48 § M9's second exit criterion, asked of the 3D path.</b> The single entry is
    ///     <c>PaintSession</c>'s and is asserted there too; what is new here is that symmetry — which
    ///     is two strokes — is still one, because it is the one place a mirrored path could
    ///     plausibly have been given its own command.
    /// </remarks>
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public void A_projected_drag_is_one_undo_entry_however_many_mirrors_it_has(bool mirrored, int paths) {
        var projection = PaintProjectionTests.Plane();
        PaintProjector projector = new(projection, Size, Size) {
            // The plane x = ½, which maps a hit at u to one at 1 − u — mirrored in the atlas as well
            // as on the model, because this layout is the identity.
            Symmetry = mirrored ? PaintSymmetry.Across(new(1f, 0f, 0f), new(0.5f, 0f, 0f)) : null
        };

        Assert.True(projector.Begin(Eye(), PaintProjectionTests.Down(0.2f, 0.5f), 8f, out var radius));
        Assert.True(radius > 0f);
        Assert.Equal(paths, projector.Paths);

        var session = Session(radius, out _);

        for (var step = 0; step <= 10; step++) {
            session.MoveAll(projector.Resolve(PaintProjectionTests.Down(0.2f + (step * 0.02f), 0.5f)));
        }

        Assert.Equal(paths, session.Strokes);
        Assert.False(session.IsEmpty);

        var command = session.End("Paint");

        Assert.NotNull(command);
    }

    /// <summary>⚠ Clearing symmetry mid-drag does not freeze the mirror, and moving it does not aim it.</summary>
    /// <remarks>
    ///     <b>The property is latched at pointer-down, and the first version only said so.</b>
    ///     <c>Begin</c> stored the mirrored texel and <c>Advance</c> re-read <c>Symmetry</c>, so a
    ///     plane cleared halfway through a drag left the mirror stuck at the position it started at
    ///     — a stroke with one live path and one dead one, painting a stamp that never moves — and a
    ///     plane <em>moved</em> mid-drag redirected the mirrored ray on the next event. Both are
    ///     silent, and both contradict <c>PaintSession.MoveAll</c>, whose path count is fixed for
    ///     the drag.
    /// </remarks>
    [Fact]
    public void A_symmetry_plane_changed_during_a_drag_is_ignored_until_the_next_one() {
        var projection = PaintProjectionTests.Plane();

        PaintProjector projector = new(projection, Size, Size) {
            Symmetry = new(new(1f, 0f, 0f), 0.5f)
        };

        Assert.True(projector.Begin(Eye(), PaintProjectionTests.Down(0.25f, 0.5f), 4f, out _));
        Assert.Equal(2, projector.Paths);

        projector.Resolve(PaintProjectionTests.Down(0.25f, 0.5f));

        // The artist lets go of the symmetry toggle without letting go of the pointer.
        projector.Symmetry = null;

        var moved = projector.Resolve(PaintProjectionTests.Down(0.3f, 0.5f)).ToArray();

        Assert.Equal(2, moved.Length);

        // ⚠ Both paths moved. Under the defect the mirror is the texel `Begin` recorded, for ever.
        Assert.Equal(0.3f * Size, moved[0].X, 2);
        Assert.Equal(0.7f * Size, moved[1].X, 2);

        // And it is not where `Begin` put it, which is what the defect leaves it at.
        Assert.NotEqual(0.75f * Size, moved[1].X, 2);
    }

    /// <summary>A mirrored drag paints on both sides, and the second side is where the plane says.</summary>
    /// <remarks>
    ///     ⚠ <b>The mirror is applied to the ray and not to the atlas, so this case is the proof that
    ///     the two agree where they can be made to.</b> This fixture's layout is the identity, which
    ///     is the only arrangement in which a mirrored hit's texel is predictable from the primary
    ///     one — and on any real mesh it is not, which is exactly why <c>PaintSymmetry</c> mirrors
    ///     the ray.
    /// </remarks>
    [Fact]
    public void A_mirrored_drag_paints_the_far_side_of_the_plane() {
        PaintProjector projector = new(PaintProjectionTests.Plane(), Size, Size) {
            Symmetry = PaintSymmetry.Across(new(1f, 0f, 0f), new(0.5f, 0f, 0f))
        };

        Assert.True(projector.Begin(Eye(), PaintProjectionTests.Down(0.25f, 0.5f), 8f, out _));

        var session = Session(6f, out var image);

        session.MoveAll(projector.Resolve(PaintProjectionTests.Down(0.25f, 0.5f)));

        // 0.25 of the way across a 64-texel atlas is column 16; its mirror through x = ½ is 0.75,
        // which is column 48. Both computed from the plane, not read off the picture.
        Assert.Equal(255u, image.At(16, 32) >> 24);
        Assert.Equal(255u, image.At(48, 32) >> 24);

        // And nothing in between, which is what says these are two stamps rather than one wide one.
        Assert.Equal(0u, image.At(32, 32) >> 24);
    }

    /// <summary>A mirror whose ray leaves the mesh holds its place and lays nothing.</summary>
    /// <remarks>
    ///     ⚠ <b><c>PaintSession.MoveAll</c> throws when a move supplies a different number of paths
    ///     from the last one, and it is right to.</b> A mirror that vanished for one move would leave
    ///     that stroke with no record of it, so the drag's single undo entry would restore half of
    ///     what was painted. Holding is free because <c>BrushStroke.MoveTo</c> lays a stamp only for
    ///     a movement with a length — so this case asserts both halves: the count is stable, and the
    ///     held path deposits nothing while its ray is off the mesh.
    /// </remarks>
    [Fact]
    public void A_mirror_that_leaves_the_mesh_holds_its_place_rather_than_vanishing() {
        PaintProjector projector = new(Split(), Size, Size) {
            Symmetry = PaintSymmetry.Across(new(1f, 0f, 0f), new(0.5f, 0f, 0f))
        };

        // Both halves of the mesh are under the pointer at the start: 0.25 is on the left panel and
        // its mirror, 0.75, is on the right one.
        Assert.True(projector.Begin(Eye(), PaintProjectionTests.Down(0.25f, 0.5f), 8f, out _));
        Assert.Equal(2, projector.Paths);

        var session = Session(6f, out var image);

        session.MoveAll(projector.Resolve(PaintProjectionTests.Down(0.25f, 0.5f)));

        var settled = session.StampCount;

        // Drag right, past the point where the mirror leaves. The primary stays on the left panel
        // throughout; its mirror, at 1 − x, is in the gap for every one of these.
        foreach (var x in (float[])[0.45f, 0.47f, 0.49f]) {
            var moved = projector.Resolve(PaintProjectionTests.Down(x, 0.5f));

            Assert.Equal(2, moved.Length);
            session.MoveAll(moved);
        }

        // The primary path went on stamping…
        Assert.True(session.StampCount > settled);

        // …and the mirror did not move, so the right panel holds exactly the stamp the drag began
        // with. Column 48 is 0.75 of the atlas; had the mirror kept tracking it would have walked
        // to 0.51 — column 32 — and painted 40 on the way. The primary's own disc ends at column
        // 31 + 6, so nothing else can account for 40.
        Assert.Equal(255u, image.At(48, 32) >> 24);
        Assert.Equal(0u, image.At(40, 32) >> 24);
    }

    /// <summary>Two panels with a gap between them, so a mirrored ray has somewhere to miss.</summary>
    /// <remarks>
    ///     ⚠ <b>A hole in the mesh rather than its outer edge, because the outer edge cannot make
    ///     this case.</b> On one convex panel every ray whose mirror leaves the mesh is a ray that
    ///     has left it too — the two are symmetric — so a fixture built from one quad can only show
    ///     both paths missing at once, which is a different behaviour and not the one this asserts.
    /// </remarks>
    static PaintProjection Split() =>
        PaintProjection.Over(
            [
                new(0f, 0f, 0f), new(0.5f, 0f, 0f), new(0.5f, 1f, 0f), new(0f, 1f, 0f),
                new(0.6f, 0f, 0f), new(1f, 0f, 0f), new(1f, 1f, 0f), new(0.6f, 1f, 0f)
            ],
            [
                new(0f, 0f), new(0.5f, 0f), new(0.5f, 1f), new(0f, 1f),
                new(0.6f, 0f), new(1f, 0f), new(1f, 1f), new(0.6f, 1f)
            ],
            [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7]
        );

    /// <summary>A projected stroke across an island edge is dilated, exactly as a placed one is.</summary>
    /// <remarks>
    ///     ⚠ <b>The wiring claim, and the sabotage is built in.</b> The gutter argument is the same
    ///     mechanism <c>PaintSeamTests</c> turns off to show both halves; a projection that composited
    ///     its own stamps would paint the same picture at gutter 4 and gutter 0, because the dilation
    ///     it routed around cannot be switched off.
    /// </remarks>
    [Theory]
    [InlineData(4, 255u)]
    [InlineData(0, 0u)]
    public void A_projected_stroke_reaches_the_seam_dilation(int gutter, uint expected) {
        PaintProjector projector = new(PaintProjectionTests.Plane(), Size, Size);

        // Aimed at column 20 of the atlas, with a brush wide enough to reach the island edge at 30.
        Assert.True(projector.Begin(Eye(), PaintProjectionTests.Down(20f / Size, 0.5f), 8f, out _));

        var session = Session(16f, out var image, PaintStrokeTests.Islands(Size, Size), gutter);

        session.MoveAll(projector.Resolve(PaintProjectionTests.Down(20f / Size, 0.5f)));

        // The last covered column of the first island, which is painted either way.
        Assert.Equal(255u, image.At(29, 32) >> 24);

        // And the gutter beside it, which is painted only when the stroke is dilated into it.
        Assert.Equal(expected, image.At(30, 32) >> 24);
    }

    /// <summary>The stack is evaluated once for a whole projected drag, not once per stamp.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Doc 48 § M9's eighth exit criterion, as the property that makes it reachable
    ///         rather than as a stopwatch.</b> The composite is built inside
    ///         <c>PaintSession.Begin</c>; what this asserts is that the projector adds nothing to
    ///         that — it casts a ray per path per pointer move and never asks the stack for
    ///         anything, so the per-stamp cost carries no term in the layer count.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two and not one, because <c>PaintComposite.Evaluations</c> counts <em>slices</em>
    ///         and a composite has two halves.</b> The number that matters is the ratio: twenty
    ///         moves and dozens of stamps still cost the two the constructor spent, where a driver
    ///         that rebuilt a session per pointer move would read forty.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_projected_drag_evaluates_the_stack_once() {
        PaintProjector projector = new(PaintProjectionTests.Plane(), Size, Size);

        Assert.True(projector.Begin(Eye(), PaintProjectionTests.Down(0.2f, 0.5f), 4f, out var radius));

        // The identity conversion, spelled out: four screen pixels is four texels.
        Assert.Equal(4f, radius, 3);

        var session = Session(radius, out _);

        for (var step = 0; step < 20; step++) {
            session.MoveAll(projector.Resolve(PaintProjectionTests.Down(0.2f + (step * 0.02f), 0.5f)));
        }

        Assert.Equal(2, session.Composite.Evaluations);

        // The instrument: a drag that laid one stamp would satisfy the line above for a reason that
        // has nothing to do with the cache.
        Assert.True(session.StampCount > 20);
    }

    /// <summary>A pointer-down that misses the mesh begins nothing.</summary>
    /// <remarks>
    ///     ⚠ <b>And <c>Resolve</c> is empty rather than a position at the origin.</b> A projector
    ///     that answered <c>(0, 0)</c> for a miss would put a stamp in the corner of the atlas every
    ///     time an artist clicked the background, which is a stroke nobody asked for in a place
    ///     nobody looks.
    /// </remarks>
    [Fact]
    public void A_pointer_down_that_misses_the_mesh_starts_no_paths() {
        PaintProjector projector = new(PaintProjectionTests.Plane(), Size, Size);

        Assert.False(projector.Begin(Eye(), new(new(0.5f, 0.5f, 10f), new(0f, 0f, 1f)), 8f, out var radius));
        Assert.Equal(0f, radius);
        Assert.Equal(0, projector.Paths);
        Assert.True(projector.Resolve(PaintProjectionTests.Down(0.5f, 0.5f)).IsEmpty);
    }

    /// <summary>A mirror through a plane away from the origin reflects the position and not the direction.</summary>
    /// <remarks>
    ///     ⚠ <b>The offset cancels in a direction and does not in a point, and a plane through the
    ///     origin cannot tell the two apart.</b> Reflecting a ray's direction as though it were a
    ///     point is the classic form of this bug and is invisible in exactly the fixture anybody
    ///     would write first, so the plane here is deliberately at x = 2.
    /// </remarks>
    [Fact]
    public void A_mirror_reflects_a_ray_about_a_plane_that_is_not_through_the_origin() {
        var plane = PaintSymmetry.Across(new(1f, 0f, 0f), new(2f, 0f, 0f));
        var mirrored = plane.Mirror(new Ray(new(0.5f, 1f, 3f), new(0f, 0f, -1f)));

        Assert.Equal(3.5f, mirrored.Origin.X, 4);
        Assert.Equal(1f, mirrored.Origin.Y, 4);

        // A direction with no component along the normal is its own mirror; one carrying the offset
        // would come back as (−4, 0, −1).
        Assert.Equal(0f, mirrored.Direction.X, 4);
        Assert.Equal(-1f, mirrored.Direction.Z, 4);
    }
}
