// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Texturing.Painting;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>
///     The picture and the brush agree about the model: the rasteriser closed against the raycast
///     that aims through it.
/// </summary>
/// <remarks>
///     <para>
///         <b>What makes this suite worth more than a pixel comparison</b> is that every assertion
///         here is between <em>two</em> pieces of the 3D paint path rather than between one of them
///         and a number written down beside it. <c>PaintCamera.Project</c> decides what the artist
///         sees and <c>PaintCamera.Ray</c> decides what the brush hits; a suite that checked each
///         against its own expected value would be green for a pair that were consistently wrong by
///         half a pixel, which is the failure that reads as a brush painting next to the pointer.
///     </para>
///     <para>
///         ⚠ <b>The fixture is deliberately tilted and deliberately not axis-aligned.</b> A quad
///         facing the camera is where linear and perspective-correct interpolation agree exactly, so
///         it cannot tell a rasteriser that divides by depth from one that does not — and it is also
///         where a half-pixel offset in the projection is smallest. Both defects are visible only on
///         a surface that is going away from the eye.
///     </para>
/// </remarks>
public class PaintMeshRasterTests {
    /// <summary>How far a coordinate may differ before it is a different texel of a 512² atlas.</summary>
    /// <remarks>
    ///     Two thousandths of the unit square is one texel of a 512² atlas. ⚠ The defects this
    ///     bounds are measured rather than guessed: on the fixture below, dropping the half-pixel
    ///     from the ray moves the worst coordinate by 0.026 and interpolating linearly instead of
    ///     perspective-correctly moves it by 0.161 — thirteen and eighty times this. The tolerance
    ///     is well inside both rather than drawn around them.
    /// </remarks>
    const float Tolerance = 2e-3f;

    /// <summary>Every pixel the rasteriser covered reads the coordinate a ray through it finds.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The instrument is the tilt.</b> Both defects this can catch — a projection that
    ///         is not the ray's inverse, and an interpolation that is not perspective-correct — are
    ///         identically zero on a quad parallel to the film plane, which is what every fixture
    ///         small enough to reason about looks like. The quad here recedes from the camera by
    ///         about half again from one edge to the other, which puts the midpoint of a linearly
    ///         interpolated coordinate a tenth of the unit square from the correct one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every covered pixel and not a sample of them</b>, because the disagreement a
    ///         half-pixel offset produces is largest exactly where the coordinate gradient is
    ///         steepest, and a sample taken on a grid can miss that entirely.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_pixel_the_rasteriser_covered_reads_the_coordinate_a_ray_through_it_finds() {
        var mesh = Tilted();
        var camera = Framed(mesh);
        PaintMeshRaster raster = new();

        // Odd dimensions and not a square: a centring bug that divides by two lands exactly on a
        // half-pixel, which an even, square pane can hide by symmetry.
        raster.Draw(mesh, camera, 129, 97);

        Assert.True(raster.Covered > 1_500, $"{raster.Covered} pixels covered — the fixture is not on screen.");

        var worst = 0f;
        var missed = 0;

        for (var y = 0; y < raster.Height; y++) {
            for (var x = 0; x < raster.Width; x++) {
                if (raster.Triangle(x, y) < 0) {
                    continue;
                }

                // ⚠ A miss is counted rather than asserted against, and only at the silhouette can
                // there be one: coverage is decided by three half-space tests and the raycast by an
                // intersection, so a sample point within a float of the edge can fall either way.
                // A ray that had stopped being the projection's inverse misses nearly everything, so
                // the count is still the assertion — it is the *rate* that has to be argued.
                if (!mesh.TryHit(camera.Ray(x, y, raster.Width, raster.Height), out var hit)) {
                    missed++;

                    continue;
                }

                worst = MathF.Max(worst, (hit.Coordinate - raster.Coordinate(x, y)).Length());
            }
        }

        Assert.True(worst < Tolerance, $"the worst pixel disagrees by {worst:F5} of the unit square.");

        Assert.True(
            missed <= raster.Covered / 100,
            $"{missed} of {raster.Covered} covered pixels cast a ray that misses the mesh, which is more "
            + "than a silhouette's worth."
        );
    }

    /// <summary>The nearer surface wins every pixel the two share.</summary>
    /// <remarks>
    ///     ⚠ <b>The two quads carry coordinates that cannot be confused</b> — the near one is the
    ///     whole unit square and the far one is a sixteenth of it in a corner — so "the near one
    ///     won" is a claim about a coordinate rather than about a triangle index, which a rasteriser
    ///     that happened to visit them in the right order would satisfy with no depth buffer at all.
    ///     The far quad is drawn <em>first</em> for that reason and a second case draws it last.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_nearer_surface_wins_the_pixels_the_two_share(bool nearFirst) {
        var mesh = Stacked(nearFirst);
        PaintCamera camera = new();

        camera.Frame(mesh.Bounds);

        PaintMeshRaster raster = new();

        raster.Draw(mesh, camera, 96, 96);

        var middle = raster.Coordinate(48, 48);

        Assert.True(raster.Triangle(48, 48) >= 0, "the middle of the pane shows nothing at all.");

        // The near quad's layout is the unit square, so its middle is (0.5, 0.5); the far one's is a
        // sixteenth in the corner, whose middle is (0.125, 0.125). ⚠ A range and not an equality,
        // because the middle of a 96-pixel pane is between two pixels — the sample point is half a
        // pixel off centre, which on this framing is two hundredths of the unit square. The two
        // candidates are three tenths apart, so the range still names exactly one of them.
        Assert.InRange(middle.X, 0.4f, 0.6f);
        Assert.InRange(middle.Y, 0.4f, 0.6f);
    }

    /// <summary>A partial retexture leaves exactly the picture a whole one would have made.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The strongest thing this file says, and it is what a locality counter alone cannot
    ///         say.</b> <c>Retexture</c> looks a stamp up in a bucketing built from the texel each
    ///         pixel reads; the trap is that the bucket and the texel can round differently, and the
    ///         symptom is one column of pixels that never updates for the whole stroke — which no
    ///         assertion about how <em>few</em> pixels were shaded could ever see.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The layout is stretched and the atlas is not a power of two, because the trap is
    ///         a rounding one.</b> With a coordinate that maps to a whole number of texels, and a
    ///         texel count that divides the cell count, the two roundings agree by construction and
    ///         the fixture proves nothing. 100 texels across 32 cells is where they part.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_partial_retexture_leaves_the_picture_a_whole_one_would_have_made() {
        var mesh = Tilted();
        var camera = Framed(mesh);
        PaintMeshRaster raster = new();

        raster.Draw(mesh, camera, 111, 83);

        PaintImage atlas = new(100, 70, 0xFF102030u);

        raster.Texture(atlas);

        // Something in the middle of the atlas, and something at its very edge — the edge is where a
        // coordinate outside the unit square clamps, and clamping is the other half of the same trap.
        PaintRect[] stamps = [new(41, 29, 7, 5), new(96, 0, 4, 3), new(0, 66, 3, 4)];

        foreach (var stamp in stamps) {
            for (var y = stamp.Y; y < stamp.EndY; y++) {
                for (var x = stamp.X; x < stamp.EndX; x++) {
                    atlas[(y * atlas.Width) + x] = 0xFF00FF00u;
                }
            }

            raster.Retexture(atlas, stamp);
        }

        var patched = Pixels(raster);

        raster.Texture(atlas);

        Assert.Equal(patched, Pixels(raster));
    }

    /// <summary>A stamp shades its own footprint and not the pane.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 48's exit criterion 8, in the half this pane adds to it.</b> The atlas side is
    ///         already gated by <c>PaintCostTests</c>; what a 3D view puts back into the per-stamp
    ///         path is the picture, and a redraw of the model per pointer move is the obvious way to
    ///         lose it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both halves, because the cheap half alone cannot fail.</b> A <c>Retexture</c>
    ///         that returned an empty rectangle and shaded nothing would satisfy any bound; so the
    ///         assertion below is bracketed — the shaded count is above zero <em>and</em> under a
    ///         fraction of the pane, and the pixels it moved really did change colour.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_stamp_shades_its_own_footprint_and_not_the_pane() {
        var mesh = Facing();
        var camera = Framed(mesh);
        PaintMeshRaster raster = new();

        raster.Draw(mesh, camera, 256, 256);

        PaintImage atlas = new(1024, 1024, 0xFF102030u);

        raster.Texture(atlas);

        var before = raster.Shaded;
        var wasThere = raster.Picture!.At(128, 128);

        // A sixty-fourth of the atlas across, in the middle of it — which the quad shows in the
        // middle of the pane.
        PaintRect stamp = new(496, 496, 32, 32);

        for (var y = stamp.Y; y < stamp.EndY; y++) {
            for (var x = stamp.X; x < stamp.EndX; x++) {
                atlas[(y * atlas.Width) + x] = 0xFF00FF00u;
            }
        }

        var moved = raster.Retexture(atlas, stamp);
        var shaded = raster.Shaded - before;

        Assert.False(moved.IsEmpty, "the stamp moved no pane pixel at all, so the bound below is vacuous.");
        Assert.True(shaded > 0L, "nothing was shaded.");

        // The closed form: the quad faces the camera and carries the whole unit square, so the
        // atlas maps to the pane uniformly — a stamp 32 of 1024 texels across is a thirty-second of
        // the model in each direction and a thousandth of its pixels. The bound is four times that,
        // which is slack for the rounding at the stamp's boundary and nothing else.
        Assert.True(
            shaded <= raster.Covered / 256L,
            $"{shaded} pixels shaded for a stamp covering a thousandth of a {raster.Covered}-pixel model."
        );

        Assert.NotEqual(wasThere, raster.Picture.At(128, 128));

        // ⚠ And the geometry was not touched, which is the counter the criterion is actually about.
        Assert.Equal(1, raster.Renders);
    }

    /// <summary>The same model at three scales draws the same picture.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The rule this repository has broken most recently.</b> A cross product falls as
    ///         the <em>square</em> of the model, and <c>Vector3.Normalize</c> answers zero below an
    ///         absolute 1e-6 — so a flat shade computed from an un-normalised edge pair is correct at
    ///         unit scale and black at a millimetre. A near plane, a framing distance or a
    ///         degeneracy epsilon written in units does the same thing at the other end.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The small scale is chosen to <em>cross</em> the threshold rather than to look
    ///         small, and that arithmetic is the whole value of the case.</b> The fixture's edges are
    ///         about 3.3 units long, so at 1e-4 they are 3.3e-4 and their cross product is 1.1e-7 —
    ///         under the absolute 1e-6 at which <c>Vector3.Normalize</c> answers zero. At 1e-3 it
    ///         would be 1.1e-5, comfortably above it, and the case would pass against a rasteriser
    ///         carrying exactly the defect. At the other end 1e+3 is where a near plane or a dolly
    ///         step written in units stops meaning anything.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(1e-4f)]
    [InlineData(1f)]
    [InlineData(1e+3f)]
    public void The_same_model_at_three_scales_draws_the_same_picture(float scale) {
        var mesh = Tilted(scale);
        var camera = Framed(mesh);
        PaintMeshRaster raster = new();

        raster.Draw(mesh, camera, 64, 64);

        PaintImage atlas = new(32, 32, 0xFFFFFFFFu);

        raster.Texture(atlas);

        var reference = Reference.Value;

        // ⚠ Nearly and not exactly, and the slack is arithmetic rather than tolerance for a defect.
        // Multiplying a position by 1e+3 is not exact in binary, so a sample point within a float of
        // a triangle's edge can fall either way at one scale and not at another. What cannot survive
        // it is a model that has gone black, gone missing, or lost its shading: those move every
        // pixel, and this moves a handful at the silhouette.
        Assert.InRange(raster.Covered, reference.Covered - 4, reference.Covered + 4);

        var picture = Pixels(raster);
        var differing = 0;

        for (var index = 0; index < picture.Length; index++) {
            if (picture[index] != reference.Picture[index]) {
                differing++;
            }
        }

        Assert.True(
            differing <= 8,
            $"{differing} of {picture.Length} pixels differ from the unit-scale render at scale {scale}."
        );
    }

    /// <summary>The picture the unit-scale fixture draws, which the other two scales must match.</summary>
    /// <remarks>
    ///     ⚠ <b>Computed rather than written down, and the theory above includes the scale it was
    ///     computed at.</b> A golden array here would be a second claim about the rasteriser that
    ///     nothing keeps up to date; the property being asserted is that the three agree, and a
    ///     reference one of them produced states exactly that and nothing more.
    /// </remarks>
    static readonly Lazy<(int Covered, uint[] Picture)> Reference = new(() => {
        var mesh = Tilted();
        var camera = Framed(mesh);
        PaintMeshRaster raster = new();

        raster.Draw(mesh, camera, 64, 64);
        raster.Texture(new(32, 32, 0xFFFFFFFFu));

        return (raster.Covered, Pixels(raster));
    });

    /// <summary>A quad tilted away from the camera, whose layout is the whole unit square.</summary>
    /// <param name="scale">How big the model is, in its own units.</param>
    /// <returns>The projection.</returns>
    /// <remarks>
    ///     One edge is about half again as far from the eye as the other, which puts the midpoint of
    ///     a linearly interpolated coordinate a tenth of the unit square away from the correct one —
    ///     fifty times the tolerance, and identically zero on a quad that faces the camera.
    /// </remarks>
    static PaintProjection Tilted(float scale = 1f) =>
        PaintProjection.Over(
            [
                new Vector3(-1f, -1f, 1f) * scale, new Vector3(1f, -1f, -1.6f) * scale,
                new Vector3(1f, 1f, -1.6f) * scale, new Vector3(-1f, 1f, 1f) * scale
            ],
            [new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f)],
            [0, 1, 2, 0, 2, 3]
        );

    /// <summary>A quad square to the camera, whose layout is the whole unit square.</summary>
    /// <returns>The projection.</returns>
    static PaintProjection Facing() =>
        PaintProjection.Over(
            [new(-1f, -1f, 0f), new(1f, -1f, 0f), new(1f, 1f, 0f), new(-1f, 1f, 0f)],
            [new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f)],
            [0, 1, 2, 0, 2, 3]
        );

    /// <summary>A big far quad and a small near one over the middle of it.</summary>
    /// <param name="nearFirst">Whether the near one is written into the index list first.</param>
    /// <returns>The projection.</returns>
    static PaintProjection Stacked(bool nearFirst) {
        Vector3[] points = [
            new(-2f, -2f, -1f), new(2f, -2f, -1f), new(2f, 2f, -1f), new(-2f, 2f, -1f),
            new(-1f, -1f, 1f), new(1f, -1f, 1f), new(1f, 1f, 1f), new(-1f, 1f, 1f)
        ];

        // The far quad's corner sixteenth against the near quad's whole square, so which one a pixel
        // shows is legible from the coordinate alone.
        Vector2[] layout = [
            new(0f, 0f), new(0.25f, 0f), new(0.25f, 0.25f), new(0f, 0.25f),
            new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f)
        ];

        int[] far = [0, 1, 2, 0, 2, 3];
        int[] near = [4, 5, 6, 4, 6, 7];

        return PaintProjection.Over(points, layout, nearFirst ? [.. near, .. far] : [.. far, .. near]);
    }

    /// <summary>A camera looking straight down the z axis at a mesh, framed on it.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <returns>The camera.</returns>
    static PaintCamera Framed(PaintProjection mesh) {
        PaintCamera camera = new();

        camera.Frame(mesh.Bounds);

        return camera;
    }

    /// <summary>The rendered picture's texels, as a comparable array.</summary>
    /// <param name="raster">The rasteriser.</param>
    /// <returns>The texels, row-major.</returns>
    static uint[] Pixels(PaintMeshRaster raster) {
        var picture = raster.Picture!;
        var texels = new uint[picture.Width * picture.Height];

        for (var index = 0; index < texels.Length; index++) {
            texels[index] = picture[index];
        }

        return texels;
    }
}
