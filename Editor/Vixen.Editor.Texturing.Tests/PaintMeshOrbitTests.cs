// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Texturing.Painting;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>What one frame of an orbit pays for, and the three things it no longer pays for.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/1107">#1107</a> and
///         <a href="https://github.com/Rikarin/Vixen/issues/1115">#1115</a>, which are the same pass
///         from three directions.</b> The fill is split across row bands so an orbit is not one
///         thread's work; the atlas goes on in the geometry pass rather than overwriting a clay
///         picture a line later; and the atlas index — the largest of the three, and not where
///         #1107 expected to find it — is built when a stamp asks for it rather than when the camera
///         moves. Every one of them is invisible in a frame, because the pictures are meant to be
///         the same bytes, so every assertion here is a counter or a byte comparison and none of
///         them is a clock.
///     </para>
///     <para>
///         ⚠ <b>The band count is driven rather than observed, which is the only way this suite can
///         fail.</b> <c>PaintMeshRaster.BandCount</c> answers one on a small pane and one on a
///         single-core machine, so a comparison of two ordinary draws would be a comparison of the
///         serial raster with itself — green against a banded fill that dropped every boundary row.
///         The overload taking a band count is what production calls with the computed number, and
///         what this calls with one and with thirty-seven.
///     </para>
/// </remarks>
public class PaintMeshOrbitTests {
    /// <summary>Every row belongs to exactly one band, at every size and every count.</summary>
    /// <remarks>
    ///     ⚠ <b>The partition is asserted directly because neither of its two failures shows up in a
    ///     picture reliably.</b> A dropped row leaves a line of the <em>previous</em> frame across
    ///     the pane, which on a still model is the same pixels it would have drawn; a shared row is
    ///     two threads writing one depth slot, which is wrong on some frames and not others. Walking
    ///     the partition over a spread of heights and counts — including counts above the height,
    ///     which <c>BandCount</c> cannot produce but a caller can — says it exactly.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(16)]
    [InlineData(37)]
    [InlineData(40)]
    [InlineData(4096)]
    public void The_bands_partition_every_row_exactly_once(int bands) {
        foreach (var height in (int[])[1, 2, 7, 16, 17, 64, 720, 900, 2160]) {
            var seen = new int[height];

            for (var band = 0; band < bands; band++) {
                var (low, high) = PaintMeshRaster.Rows(band, bands, height);

                for (var row = low; row <= high; row++) {
                    Assert.InRange(row, 0, height - 1);

                    seen[row]++;
                }
            }

            for (var row = 0; row < height; row++) {
                Assert.True(
                    seen[row] == 1,
                    $"row {row} of {height} is owned by {seen[row]} of {bands} bands. One is a seam of "
                    + "the previous frame and two is a race on the depth buffer."
                );
            }
        }
    }

    /// <summary>A draw split across thirty-seven bands is the same bytes as one that is not split.</summary>
    /// <remarks>
    ///     ⚠ <b>The same bytes and not "close", and that is a claim about determinism as much as
    ///     about coverage.</b> Two fragments at the same depth are resolved by which was projected
    ///     first; a band visits its fragments in projection order, so a coplanar seam decides the
    ///     same way at every band count. A raster that resolved ties by whichever thread arrived
    ///     first would pass a tolerance and fail this.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_banded_draw_is_the_same_bytes_as_a_serial_one(bool textured) {
        const int Wide = 401;
        const int Tall = 277;

        var mesh = Shell(12);
        PaintImage? atlas = textured ? Chequer(64) : null;

        var serial = Drawn(mesh, atlas, Wide, Tall, 1);
        var banded = Drawn(mesh, atlas, Wide, Tall, 37);

        Assert.Equal(1, serial.Bands);
        Assert.Equal(37, banded.Bands);

        // The instrument: a pane the model misses would agree byte for byte for a reason that has
        // nothing to do with the bands.
        Assert.True(
            serial.Covered > Wide * Tall / 8,
            $"{serial.Covered} of {Wide * Tall} pane pixels covered — the model is not on screen."
        );

        Assert.Equal(serial.Covered, banded.Covered);
        Assert.Equal(serial.Examined, banded.Examined);

        var one = Pixels(serial);
        var many = Pixels(banded);
        var differing = 0;

        for (var index = 0; index < one.Length; index++) {
            if (one[index] != many[index]) {
                differing++;
            }
        }

        Assert.True(
            differing == 0,
            $"{differing} of {one.Length} pixels differ between a one-band draw and a thirty-seven-band "
            + "one. A band boundary that drops or shares a row is what this looks like."
        );

        // And the coordinate buffer, which is what the brush aims through — a picture can agree
        // while the layout under it does not, and that is the half an artist finds by painting in
        // the wrong place.
        for (var y = 0; y < Tall; y++) {
            for (var x = 0; x < Wide; x++) {
                Assert.Equal(serial.Triangle(x, y), banded.Triangle(x, y));
                Assert.Equal(serial.Coordinate(x, y), banded.Coordinate(x, y));
            }
        }
    }

    /// <summary>A textured camera move shades the pane once, and shades it the same.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/1115">#1115</a>, as the counter
    ///         the issue names.</b> <c>Draw</c> writing a clay picture and <c>Texture</c> overwriting
    ///         every pixel of it is two full-pane shades per camera move, of which one is thrown
    ///         away — and <c>Shaded</c> is where that shows: twice the pane before, once after.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The count alone would be satisfied by a pass that had stopped shading</b>, so the
    ///         picture the one-pass route produces is compared against the picture the two-pass route
    ///         produces, byte for byte. Both halves are needed and neither is sufficient.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_textured_draw_shades_the_pane_once_and_paints_what_two_passes_painted() {
        const int Wide = 320;
        const int Tall = 240;

        var mesh = Shell(12);
        var atlas = Chequer(64);
        var camera = Framed(mesh);

        PaintMeshRaster two = new();

        var before = two.Shaded;

        two.Draw(mesh, camera, Wide, Tall);
        two.Texture(atlas);

        var twice = two.Shaded - before;

        PaintMeshRaster one = new();

        one.Draw(mesh, camera, Wide, Tall, atlas);

        Assert.Equal(2L * Wide * Tall, twice);
        Assert.Equal((long)Wide * Tall, one.Shaded);

        // The instrument: a model that missed the pane would make both routes agree on a picture of
        // nothing at all.
        Assert.True(
            one.Covered > Wide * Tall / 8,
            $"{one.Covered} of {Wide * Tall} pane pixels covered — the model is not on screen."
        );

        Assert.Equal(two.Covered, one.Covered);
        Assert.Equal(Pixels(two), Pixels(one));
    }

    /// <summary>The clay picture is what a stack with a model and no paint layer still shows.</summary>
    /// <remarks>
    ///     ⚠ <b>#1115's own argument, asserted rather than trusted.</b> The obvious way to stop
    ///     shading a discarded picture is to stop shading in <c>Draw</c> — and that would leave the
    ///     state an artist is in immediately before they add their first paint layer showing the
    ///     pane's background and nothing else, which is #1063's first milestone deleted.
    /// </remarks>
    [Fact]
    public void A_draw_with_no_atlas_still_puts_a_clay_model_on_the_pane() {
        var mesh = Shell(12);
        PaintMeshRaster raster = new();

        raster.Draw(mesh, Framed(mesh), 320, 240, null);

        var picture = raster.Picture;

        Assert.NotNull(picture);

        var shades = new HashSet<uint>();

        for (var index = 0; index < picture.Width * picture.Height; index++) {
            shades.Add(picture[index]);
        }

        Assert.True(
            raster.Covered > 320 * 240 / 8,
            $"{raster.Covered} pane pixels covered — the model is not on screen."
        );

        // More than one covered colour, because a clay model is lit: a picture of one flat value is
        // a fill rather than a render, and would satisfy any assertion about coverage alone.
        Assert.True(
            shades.Count > 4,
            $"the clay picture has {shades.Count} distinct colours, so it is a fill rather than a render."
        );
    }

    /// <summary>Halving the model on screen quarters the pixels it covers, at every band count.</summary>
    /// <remarks>
    ///     ⚠ <b>The closed-form oracle, because the output is a picture.</b> A front-facing quad's
    ///     projected area falls as the square of the distance, so doubling the dolly must quarter
    ///     <c>Covered</c> — a property of the projection that no counter about passes, pixels or
    ///     bands can express. It is checked at one band and at thirty-seven so that a fill which had
    ///     started losing a row per boundary would break the ratio at one of the two and not the
    ///     other.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(37)]
    public void Doubling_the_dolly_quarters_the_pixels_the_model_covers(int bands) {
        const int Wide = 512;
        const int Tall = 512;

        var mesh = Quad();
        PaintCamera camera = new();

        camera.Frame(mesh.Bounds);

        var near = camera.Distance;

        PaintMeshRaster raster = new();

        raster.Draw(mesh, camera, Wide, Tall, null, bands);

        var close = raster.Covered;

        camera.Distance = near * 2f;
        raster.Draw(mesh, camera, Wide, Tall, null, bands);

        var far = raster.Covered;

        Assert.True(close > 50_000, $"{close} pixels covered — the quad is not framed.");

        // The perimeter is the slack: the two rectangles round their edges independently, and a
        // quad about 380 pixels across rounds by at most its own perimeter of pixels.
        var expected = close / 4;

        Assert.InRange(far, expected - 800, expected + 800);
    }

    /// <summary>What <c>BandCount</c> answers, so that a pane below the floor is still drawn.</summary>
    /// <remarks>
    ///     ⚠ <b>A pane under the floor answers one rather than nothing</b>, which is what makes the
    ///     banded path the only path: the same two methods draw a 64-pixel fixture and a maximised
    ///     4K pane, and only the dispatch differs.
    /// </remarks>
    [Fact]
    public void A_small_pane_is_one_band_and_a_maximised_one_is_many() {
        Assert.Equal(1, PaintMeshRaster.BandCount(64, 64));
        Assert.Equal(1, PaintMeshRaster.BandCount(255, 255));

        var wide = PaintMeshRaster.BandCount(3840, 2160);

        Assert.True(
            wide > 1 || Environment.ProcessorCount <= 1,
            $"a 3840×2160 pane is {wide} band on a machine reporting {Environment.ProcessorCount} processors."
        );

        // Never more bands than there are rows to give them, whatever the processor count.
        Assert.True(PaintMeshRaster.BandCount(3840, 2160) <= 2160);
    }

    /// <summary>An orbit buckets no index, and a stroke buckets exactly one.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/1107">#1107</a>'s largest number,
    ///         and the issue does not name it.</b> The issue expected the pane's pixels to dominate a
    ///         camera move and proposed a cap, a parallel raster and a coarser drag draw. Measured at
    ///         1600×900 over eighteen thousand triangles, the counting sort that buckets covered
    ///         pixels by atlas cell was about eight of the pass's thirteen milliseconds — more than
    ///         the projection, the fill and the shade together — and its only reader is
    ///         <c>Retexture</c>, which an orbit never calls.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A count of bucketings and not of draws, because the two move in opposite
    ///         directions here.</b> An orbit is many draws and no stamps and must bucket nothing; a
    ///         stroke is many stamps and no draws and must bucket once. A counter that only watched
    ///         <c>Renders</c> would be satisfied by either arrangement.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_orbit_buckets_no_index_and_the_first_stamp_of_a_stroke_buckets_one() {
        const int Wide = 320;
        const int Tall = 240;

        var mesh = Shell(12);
        var atlas = Chequer(64);
        var camera = Framed(mesh);

        PaintMeshRaster raster = new();

        raster.Draw(mesh, camera, Wide, Tall, atlas);

        Assert.Equal(0, raster.Indexings);

        for (var move = 0; move < 8; move++) {
            camera.Orbit(4f, 1f, Tall);
            raster.Draw(mesh, camera, Wide, Tall, atlas);
        }

        Assert.Equal(9, raster.Renders);

        Assert.True(
            raster.Indexings == 0,
            $"{raster.Indexings} bucketings over nine draws and no stamp. The counting sort is back on "
            + "the camera path, which is eight milliseconds a pointer move at a docked pane's size."
        );

        // The instrument: a pane the model misses would bucket nothing for a reason that has nothing
        // to do with when the buckets are built.
        Assert.True(
            raster.Covered > Wide * Tall / 8,
            $"{raster.Covered} of {Wide * Tall} pane pixels covered — the model is not on screen."
        );

        var moved = raster.Retexture(atlas, new(8, 8, 12, 12));

        Assert.Equal(1, raster.Indexings);

        Assert.False(moved.IsEmpty, "the stamp moved no pane pixel, so the buckets were never read.");

        for (var stamp = 0; stamp < 16; stamp++) {
            raster.Retexture(atlas, new(8 + stamp, 8, 12, 12));
        }

        Assert.True(
            raster.Indexings == 1,
            $"{raster.Indexings} bucketings over seventeen stamps. Rebuilding per stamp puts the pane's "
            + "pixel count back into the per-stamp path, which is doc 48's exit criterion 8."
        );

        // And a camera move makes it stale again, or the next stroke would patch through buckets
        // describing where the model used to be.
        camera.Orbit(20f, 5f, Tall);
        raster.Draw(mesh, camera, Wide, Tall, atlas);
        raster.Retexture(atlas, new(8, 8, 12, 12));

        Assert.Equal(2, raster.Indexings);
    }

    /// <summary>Draws a mesh once at a size and a band count.</summary>
    /// <param name="mesh">The model.</param>
    /// <param name="atlas">What it wears, or null for clay.</param>
    /// <param name="width">How wide the pane is.</param>
    /// <param name="height">How tall.</param>
    /// <param name="bands">How many row bands to split the fill across.</param>
    /// <returns>The rasteriser, drawn.</returns>
    static PaintMeshRaster Drawn(PaintProjection mesh, PaintImage? atlas, int width, int height, int bands) {
        PaintMeshRaster raster = new();

        raster.Draw(mesh, Framed(mesh), width, height, atlas, bands);

        return raster;
    }

    /// <summary>A camera framing a mesh, tilted so nothing faces the pane squarely.</summary>
    /// <param name="mesh">The model.</param>
    /// <returns>The camera.</returns>
    /// <remarks>
    ///     ⚠ <b>Turned off the axes on purpose.</b> A model seen straight on projects to triangles
    ///     whose rows line up with the pane's, which is where a band boundary is least likely to cut
    ///     one — the case would then be asserting that bands agree about geometry no band divides.
    /// </remarks>
    static PaintCamera Framed(PaintProjection mesh) {
        PaintCamera camera = new();

        camera.Frame(mesh.Bounds);

        camera.Yaw = 0.7f;
        camera.Pitch = 0.4f;

        return camera;
    }

    /// <summary>The picture as a copy, so a later draw cannot move it.</summary>
    /// <param name="raster">The rasteriser.</param>
    /// <returns>Its pixels, row-major.</returns>
    static uint[] Pixels(PaintMeshRaster raster) {
        var picture = raster.Picture!;
        var pixels = new uint[picture.Width * picture.Height];

        for (var index = 0; index < pixels.Length; index++) {
            pixels[index] = picture[index];
        }

        return pixels;
    }

    /// <summary>An open half-cylinder, so the pane carries a silhouette and overlapping depths.</summary>
    /// <param name="segments">How many quads round it.</param>
    /// <returns>The projection.</returns>
    /// <remarks>
    ///     ⚠ <b>Curved and open rather than a quad, because a quad has no depth for the z-buffer to
    ///     resolve.</b> A band boundary crossing a silhouette is where a fill that miscounted its
    ///     rows shows; a single flat facing quad would draw identically under a raster that had
    ///     dropped the depth test altogether.
    /// </remarks>
    static PaintProjection Shell(int segments) {
        List<Vector3> points = [];
        List<Vector2> layout = [];
        List<int> indices = [];

        for (var step = 0; step <= segments; step++) {
            var t = (float)step / segments;
            var (sin, cos) = MathF.SinCos(t * MathF.PI * 1.5f);

            points.Add(new(cos, -1f, sin));
            points.Add(new(cos, 1f, sin));
            layout.Add(new(t, 0f));
            layout.Add(new(t, 1f));
        }

        for (var step = 0; step < segments; step++) {
            var corner = step * 2;

            indices.AddRange([corner, corner + 1, corner + 3]);
            indices.AddRange([corner, corner + 3, corner + 2]);
        }

        return PaintProjection.Over([.. points], [.. layout], [.. indices]);
    }

    /// <summary>A quad in the z = 0 plane whose layout is the whole unit square.</summary>
    /// <returns>The projection.</returns>
    static PaintProjection Quad() =>
        PaintProjection.Over(
            [new(-1f, -1f, 0f), new(1f, -1f, 0f), new(1f, 1f, 0f), new(-1f, 1f, 0f)],
            [new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f)],
            [0, 1, 2, 0, 2, 3]
        );

    /// <summary>A chequered atlas, so a coordinate that moved shows as a colour that moved.</summary>
    /// <param name="size">How many texels across.</param>
    /// <returns>The image.</returns>
    static PaintImage Chequer(int size) {
        PaintImage image = new(size, size);

        for (var y = 0; y < size; y++) {
            for (var x = 0; x < size; x++) {
                image[(y * size) + x] = ((x / 4) + (y / 4)) % 2 == 0 ? 0xFF3070E0u : 0xFFE0A030u;
            }
        }

        return image;
    }
}
