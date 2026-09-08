// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Editor.Texturing.Painting;

/// <summary>
///     A depth-buffered CPU rasteriser of the stack's own mesh, textured with the atlas the brush
///     writes.
/// </summary>
/// <remarks>
///     <para>
///         <b>The pane <a href="https://github.com/Rikarin/Vixen/issues/1063">#1063</a> recommends,
///         in the half that makes a picture.</b> A plugin gets a device and an <c>Upload</c> that
///         takes <em>pixels</em> — doc 48 § D14's third bullet, deliberately — so a texturing pane
///         can present a CPU image and nothing else. That is the whole reason this exists rather
///         than a render feature: the alternative is a plugin that can register something drawing
///         geometry, which is a change to the editor's contract and not to this plugin.
///     </para>
///     <para>
///         ⚠ <b>Two passes and not one, and the split is the exit criterion rather than a
///         structure.</b> Doc 48's criterion 8 asks that a stamp on a 4K set with twelve layers
///         under it stays local, and <c>PaintComposite</c> already earns that for the atlas: the
///         stack is evaluated once per stroke and resolved per dirty rectangle. A rasteriser that
///         redrew the mesh on every pointer move would put the model's triangle count straight back
///         into the per-stamp path and lose it again — which is the obvious way to miss it, and is
///         what <see cref="Renders" /> is for. <see cref="Draw" /> is the geometry, run when the
///         camera or the pane moves; <see cref="Retexture" /> is the shading of one atlas rectangle,
///         run per stamp.
///     </para>
///     <para>
///         ⚠ <b>And "shade only the pixels showing that rectangle" is an index rather than a scan.</b>
///         A pass over every pane pixel asking whether its coordinate is in the stamp is
///         <em>also</em> independent of the layer count and of the atlas size, so a counter that
///         only measured those two would call it local and be satisfied by the thing the criterion
///         is about. <see cref="Draw" /> therefore buckets each covered pixel by which cell of the
///         atlas it reads, and <see cref="Shaded" /> counts the pixels a stamp actually visits — so
///         the assertion is against the stamp's own footprint and not against the pane.
///     </para>
///     <para>
///         ⚠ <b>Perspective-correct, because the alternative is invisible on a test fixture and
///         wrong on a model.</b> Interpolating a coordinate linearly across a triangle in screen
///         space is exact only when the triangle faces the camera; on a cylinder seen from the side
///         it slides the texture towards the silhouette, and a brush aimed through this picture
///         would land somewhere the artist did not click. Every fixture small enough to reason about
///         is a plane facing the camera, where the two agree exactly.
///     </para>
/// </remarks>
sealed class PaintMeshRaster {
    /// <summary>How many cells across the atlas the pixel index is bucketed into.</summary>
    /// <remarks>
    ///     ⚠ <b>A count and not a texel size, which is what makes it scale-free.</b> A cell of "128
    ///     texels" is the whole of a 64² atlas and a thousandth of a 4K one, so a bucket size in
    ///     texels would make the index useless at exactly one of the two ends. In cells, a stamp of
    ///     a given fraction of the atlas touches the same number of buckets whatever the resolution.
    /// </remarks>
    public const int Cells = 32;

    /// <summary>What a pixel showing nothing is: the pane's own background.</summary>
    const uint Background = 0xFF2A2624u;

    /// <summary>What the model is clay-shaded with before an atlas has been put on it.</summary>
    const uint Untextured = 0xFFA8A8A8u;

    /// <summary>How much of a surface facing away from the light is still lit.</summary>
    const float Ambient = 0.25f;

    /// <summary>Which triangle each pane pixel shows, or -1.</summary>
    int[] triangles = [];

    /// <summary>Each pane pixel's coordinate, in the unit square.</summary>
    Vector2[] coordinates = [];

    /// <summary>How lit each pane pixel is, 0…1.</summary>
    float[] shades = [];

    /// <summary>The reciprocal depth each pane pixel was won at — the z-buffer.</summary>
    float[] depths = [];

    /// <summary>The covered pixels, ordered by which cell of the atlas they read.</summary>
    int[] ordered = [];

    /// <summary>Where each cell's run starts in <see cref="ordered" />, with a tail entry.</summary>
    readonly int[] starts = new int[(Cells * Cells) + 1];

    /// <summary>What atlas width the index was bucketed for, or zero for none.</summary>
    int indexedWidth;

    /// <summary>And its height.</summary>
    int indexedHeight;

    /// <summary>The picture, or null before the first <see cref="Draw" />.</summary>
    /// <remarks>
    ///     ⚠ <b>Reused across draws at one size rather than allocated per redraw.</b> A pane redraw
    ///     is every frame of an orbit, and the picture at a docked pane's size is a few megabytes —
    ///     an allocation per frame is the same defect <c>TexturingModule.patch</c> exists to avoid,
    ///     one dimension larger.
    /// </remarks>
    public PaintImage? Picture { get; private set; }

    /// <summary>How wide the picture is, in pane pixels.</summary>
    public int Width { get; private set; }

    /// <summary>How tall.</summary>
    public int Height { get; private set; }

    /// <summary>How many geometry passes have run: doc 48's exit criterion, counted.</summary>
    /// <remarks>
    ///     ⚠ <b>The counter a stroke is asserted against, in <c>PaintComposite.Evaluations</c>'
    ///     shape.</b> A pane that redraws the mesh per pointer move is the way this path misses the
    ///     criterion, and it is invisible in a picture — the frames look identical. Only a count
    ///     says it.
    /// </remarks>
    public int Renders { get; private set; }

    /// <summary>How many pane pixels have been shaded, over every pass.</summary>
    /// <remarks>
    ///     Both the full pass and the per-stamp one add to it, so a <see cref="Retexture" /> that
    ///     quietly fell back to shading the whole pane is visible here rather than only in a clock.
    /// </remarks>
    public long Shaded { get; private set; }

    /// <summary>How many pane pixels show a triangle at all.</summary>
    public int Covered { get; private set; }

    /// <summary>Draws the mesh's geometry into the pane's buffers, at a size.</summary>
    /// <param name="mesh">The mesh, in its own space.</param>
    /// <param name="camera">Where it is seen from.</param>
    /// <param name="width">How wide the pane is, in pixels.</param>
    /// <param name="height">How tall.</param>
    /// <exception cref="ArgumentNullException">The mesh or the camera is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The pane has no area.</exception>
    /// <remarks>
    ///     ⚠ <b>What this writes is a <em>clay</em> picture and no atlas at all.</b> What it leaves
    ///     behind is the coordinate, the shade and the triangle at every pixel; putting an atlas on
    ///     them is <see cref="Texture" />, which is what lets a stroke repaint the picture without
    ///     the model being touched. The clay pass is what a stack with a model bound and no paint
    ///     layer yet shows, and it is <a href="https://github.com/Rikarin/Vixen/issues/1063">#1063</a>'s
    ///     first milestone: a picture, before anything textures it.
    /// </remarks>
    public void Draw(PaintProjection mesh, PaintCamera camera, int width, int height) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        Resize(width, height);

        Array.Fill(triangles, -1);
        Array.Clear(depths);

        var forward = camera.Forward;

        for (var triangle = 0; triangle < mesh.Triangles; triangle++) {
            mesh.Triangle(triangle, out var a, out var b, out var c, out var ua, out var ub, out var uc);

            if (!camera.Project(a, width, height, out var pa, out var za)
                || !camera.Project(b, width, height, out var pb, out var zb)
                || !camera.Project(c, width, height, out var pc, out var zc)) {
                // A triangle with a corner behind the eye. See `PaintCamera.Project` for why it is
                // dropped rather than clipped, and what that costs.
                continue;
            }

            Fill(triangle, Shade(a, b, c, forward), pa, pb, pc, za, zb, zc, ua, ub, uc);
        }

        Covered = 0;

        // ⚠ A flat picture and not an empty one, which is the difference between a milestone and a
        // black pane. A stack with a model bound and no paint layer yet has nothing to texture with
        // — and that is the state an artist is in immediately before they add the layer they mean to
        // paint on, so it is the state in which the pane has to prove the binding worked.
        for (var pixel = 0; pixel < triangles.Length; pixel++) {
            if (triangles[pixel] < 0) {
                Picture![pixel] = Background;

                continue;
            }

            Picture![pixel] = PaintImage.Pack(
                PaintImage.Channel(Untextured, 0) * shades[pixel],
                PaintImage.Channel(Untextured, 1) * shades[pixel],
                PaintImage.Channel(Untextured, 2) * shades[pixel],
                1f
            );

            Covered++;
        }

        Shaded += triangles.Length;
        Renders++;
        Index();
    }

    /// <summary>Puts an atlas on the drawn geometry, everywhere.</summary>
    /// <param name="atlas">The picture the brush writes: a composite's result, or a layer.</param>
    /// <returns>The whole pane, as a rectangle, or empty when nothing has been drawn.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="atlas" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Also where the index is rebuilt when the atlas changes size.</b> A cell is decided by
    ///     the <em>texel</em> a pixel reads rather than by its coordinate, so that the bucket a stamp
    ///     looks in and the texel it compares against cannot round differently — and that makes the
    ///     index a function of the resolution. A stack edited to a different base size therefore
    ///     re-buckets here, on the pass that was already going to touch every pixel.
    /// </remarks>
    public PaintRect Texture(PaintImage atlas) {
        ArgumentNullException.ThrowIfNull(atlas);

        if (Picture is not { } picture) {
            return PaintRect.Empty;
        }

        if (atlas.Width != indexedWidth || atlas.Height != indexedHeight) {
            Index(atlas.Width, atlas.Height);
        }

        for (var pixel = 0; pixel < triangles.Length; pixel++) {
            picture[pixel] = triangles[pixel] < 0 ? Background : Sample(atlas, pixel);
        }

        Shaded += triangles.Length;

        return new(0, 0, Width, Height);
    }

    /// <summary>Repaints only the pane pixels showing one rectangle of the atlas.</summary>
    /// <param name="atlas">The atlas, whose texels in that rectangle have changed.</param>
    /// <param name="region">The rectangle, in atlas texels.</param>
    /// <returns>What that moved on the pane, in pane pixels — empty when it moved nothing.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="atlas" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>The per-stamp half, and the reason the geometry is kept.</b> A stamp dirties a disc
    ///         of the atlas; the pixels showing it are whichever of the pane's the model happens to
    ///         put there, so the answer is a scattered set and the returned rectangle is its
    ///         bounding box — which is what <c>IEditorGraphics.Update</c> takes.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An atlas of a size the index was not built for falls back to the whole pane
    ///         rather than answering from a stale bucketing.</b> That is a real state — a stack's
    ///         base resolution is an edit an artist makes with the pointer up — and the alternative
    ///         is a pane that goes on showing the old texels in the places the new size moved.
    ///     </para>
    /// </remarks>
    public PaintRect Retexture(PaintImage atlas, PaintRect region) {
        ArgumentNullException.ThrowIfNull(atlas);

        if (Picture is not { } picture) {
            return PaintRect.Empty;
        }

        if (atlas.Width != indexedWidth || atlas.Height != indexedHeight) {
            return Texture(atlas);
        }

        var rect = region.Clip(atlas.Width, atlas.Height);

        if (rect.IsEmpty) {
            return PaintRect.Empty;
        }

        var lowColumn = Cell(rect.X, atlas.Width);
        var highColumn = Cell(rect.EndX - 1, atlas.Width);
        var lowRow = Cell(rect.Y, atlas.Height);
        var highRow = Cell(rect.EndY - 1, atlas.Height);

        var minimumX = int.MaxValue;
        var minimumY = int.MaxValue;
        var maximumX = int.MinValue;
        var maximumY = int.MinValue;

        for (var row = lowRow; row <= highRow; row++) {
            var first = starts[(row * Cells) + lowColumn];
            var last = starts[(row * Cells) + highColumn + 1];

            // ⚠ One run per cell *row* rather than one per cell: the buckets of a row are contiguous
            // in `ordered` by construction, so the columns between the two ends are already covered
            // and asking for each of them separately would be the same walk with more bookkeeping.
            for (var slot = first; slot < last; slot++) {
                var pixel = ordered[slot];

                Texel(pixel, atlas.Width, atlas.Height, out var x, out var y);

                if (!rect.Contains(x, y)) {
                    continue;
                }

                picture[pixel] = Sample(atlas, pixel);
                Shaded++;

                var column = pixel % Width;
                var line = pixel / Width;

                minimumX = Math.Min(minimumX, column);
                minimumY = Math.Min(minimumY, line);
                maximumX = Math.Max(maximumX, column);
                maximumY = Math.Max(maximumY, line);
            }
        }

        return maximumX < minimumX
            ? PaintRect.Empty
            : new(minimumX, minimumY, maximumX - minimumX + 1, maximumY - minimumY + 1);
    }

    /// <summary>Which triangle a pane pixel shows.</summary>
    /// <param name="x">Its column.</param>
    /// <param name="y">Its row.</param>
    /// <returns>The triangle, or -1 for background and for a pixel outside the pane.</returns>
    public int Triangle(int x, int y) =>
        x < 0 || y < 0 || x >= Width || y >= Height ? -1 : triangles[(y * Width) + x];

    /// <summary>What coordinate a pane pixel reads, in the unit square.</summary>
    /// <param name="x">Its column.</param>
    /// <param name="y">Its row.</param>
    /// <returns>The coordinate, or the origin for a pixel showing nothing.</returns>
    public Vector2 Coordinate(int x, int y) =>
        x < 0 || y < 0 || x >= Width || y >= Height ? Vector2.Zero : coordinates[(y * Width) + x];

    /// <summary>Which cell of the index a texel falls in, along one axis.</summary>
    /// <param name="texel">The texel's column or row.</param>
    /// <param name="extent">How many there are along that axis.</param>
    /// <returns>The cell, always in range.</returns>
    static int Cell(int texel, int extent) =>
        Math.Clamp(texel * Cells / Math.Max(extent, 1), 0, Cells - 1);

    /// <summary>Grows the buffers to a pane size, keeping them when it has not changed.</summary>
    /// <param name="width">How wide.</param>
    /// <param name="height">How tall.</param>
    void Resize(int width, int height) {
        if (Width == width && Height == height && Picture is not null) {
            return;
        }

        Width = width;
        Height = height;
        Picture = new(width, height);

        var pixels = width * height;

        triangles = new int[pixels];
        coordinates = new Vector2[pixels];
        shades = new float[pixels];
        depths = new float[pixels];
        ordered = new int[pixels];

        // The index describes buffers that have just been replaced, so it describes nothing.
        indexedWidth = 0;
        indexedHeight = 0;
    }

    /// <summary>How lit one triangle is, flat, from a light on the camera.</summary>
    /// <param name="a">Its first corner.</param>
    /// <param name="b">Its second.</param>
    /// <param name="c">Its third.</param>
    /// <param name="forward">Which way the camera looks. Unit.</param>
    /// <returns>The shade, 0…1.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The edges are normalised <em>before</em> the cross product, and that is the whole
    ///         of why this works on a small model.</b> A cross product falls as the square of the
    ///         model — two edges of a 0.001-unit triangle give a normal of length 1e-6, which is
    ///         exactly where <c>Vector3.Normalize</c> gives up and answers a zero vector. The cross
    ///         of two <em>unit</em> vectors is the sine of the angle between them, which is a
    ///         property of the shape and not of its size, so the same triangle shades identically at
    ///         every scale. This repository has shipped the other version.
    ///     </para>
    ///     <para>
    ///         The absolute value is what lights the inside of an open shell. A texturing pane shows
    ///         one mesh with nothing else in the scene, and a black back face reads as a hole rather
    ///         than as a surface facing away.
    ///     </para>
    /// </remarks>
    static float Shade(Vector3 a, Vector3 b, Vector3 c, Vector3 forward) {
        var first = b - a;
        var second = c - a;
        var one = first.Length();
        var two = second.Length();

        if (!(one > 0f) || !(two > 0f) || !float.IsFinite(one) || !float.IsFinite(two)) {
            return 1f;
        }

        var normal = Vector3.Cross(first / one, second / two);
        var length = normal.Length();

        if (!(length > 1e-4f)) {
            // Collinear corners: no plane, so no tilt to read. It covers no pixels either.
            return 1f;
        }

        return Ambient + ((1f - Ambient) * MathF.Abs(Vector3.Dot(normal / length, forward)));
    }

    /// <summary>Fills one projected triangle into the buffers, depth-tested.</summary>
    /// <param name="triangle">Which triangle it is.</param>
    /// <param name="shade">How lit it is.</param>
    /// <param name="pa">Its first corner on the pane.</param>
    /// <param name="pb">Its second.</param>
    /// <param name="pc">Its third.</param>
    /// <param name="za">The first corner's depth.</param>
    /// <param name="zb">The second's.</param>
    /// <param name="zc">The third's.</param>
    /// <param name="ua">The first corner's coordinate.</param>
    /// <param name="ub">The second's.</param>
    /// <param name="uc">The third's.</param>
    /// <remarks>
    ///     ⚠ <b>The sample point of pixel <c>n</c> is <c>n</c> exactly, and not <c>n + ½</c>.</b>
    ///     <c>PaintCamera.Ray</c> casts through a pixel's centre and <c>PaintCamera.Project</c> is
    ///     its exact inverse, so the projected frame is already centre-based — adding a half here
    ///     would offset the picture from the brush by half a pixel, which is invisible everywhere
    ///     except the silhouette and at the seam between two islands.
    /// </remarks>
    void Fill(
        int triangle,
        float shade,
        Vector2 pa,
        Vector2 pb,
        Vector2 pc,
        float za,
        float zb,
        float zc,
        Vector2 ua,
        Vector2 ub,
        Vector2 uc
    ) {
        var area = ((pb.X - pa.X) * (pc.Y - pa.Y)) - ((pb.Y - pa.Y) * (pc.X - pa.X));

        if (!(MathF.Abs(area) > 1e-9f) || !float.IsFinite(area)) {
            return;
        }

        var lowX = Math.Max((int)MathF.Floor(MathF.Min(pa.X, MathF.Min(pb.X, pc.X))), 0);
        var lowY = Math.Max((int)MathF.Floor(MathF.Min(pa.Y, MathF.Min(pb.Y, pc.Y))), 0);
        var highX = Math.Min((int)MathF.Ceiling(MathF.Max(pa.X, MathF.Max(pb.X, pc.X))), Width - 1);
        var highY = Math.Min((int)MathF.Ceiling(MathF.Max(pa.Y, MathF.Max(pb.Y, pc.Y))), Height - 1);

        var inverse = 1f / area;
        var invA = 1f / za;
        var invB = 1f / zb;
        var invC = 1f / zc;

        for (var y = lowY; y <= highY; y++) {
            for (var x = lowX; x <= highX; x++) {
                var weightA = (((pb.X - x) * (pc.Y - y)) - ((pb.Y - y) * (pc.X - x))) * inverse;
                var weightB = (((pc.X - x) * (pa.Y - y)) - ((pc.Y - y) * (pa.X - x))) * inverse;
                var weightC = 1f - weightA - weightB;

                if (weightA < 0f || weightB < 0f || weightC < 0f) {
                    continue;
                }

                // The z-buffer holds *reciprocal* depth, which is the quantity that is linear in
                // screen space — and the nearer surface is the larger one, so the test is a maximum.
                var reciprocal = (weightA * invA) + (weightB * invB) + (weightC * invC);
                var slot = (y * Width) + x;

                if (!(reciprocal > depths[slot])) {
                    continue;
                }

                depths[slot] = reciprocal;
                triangles[slot] = triangle;
                shades[slot] = shade;
                coordinates[slot] =
                    ((ua * weightA * invA) + (ub * weightB * invB) + (uc * weightC * invC)) / reciprocal;
            }
        }
    }

    /// <summary>Buckets the covered pixels by which cell of the atlas they read.</summary>
    /// <param name="atlasWidth">The atlas width the cells are decided in.</param>
    /// <param name="atlasHeight">Its height.</param>
    /// <remarks>
    ///     A counting sort: one pass to count, one to place. Both are over the pane and neither is
    ///     over the atlas, so the index costs the same at 64² and at 4096².
    /// </remarks>
    void Index(int atlasWidth, int atlasHeight) {
        Array.Clear(starts);

        indexedWidth = atlasWidth;
        indexedHeight = atlasHeight;

        for (var pixel = 0; pixel < triangles.Length; pixel++) {
            if (triangles[pixel] < 0) {
                continue;
            }

            Texel(pixel, atlasWidth, atlasHeight, out var x, out var y);

            starts[(Cell(y, atlasHeight) * Cells) + Cell(x, atlasWidth) + 1]++;
        }

        for (var cell = 1; cell < starts.Length; cell++) {
            starts[cell] += starts[cell - 1];
        }

        Span<int> cursor = stackalloc int[Cells * Cells];

        starts.AsSpan(0, Cells * Cells).CopyTo(cursor);

        for (var pixel = 0; pixel < triangles.Length; pixel++) {
            if (triangles[pixel] < 0) {
                continue;
            }

            Texel(pixel, atlasWidth, atlasHeight, out var x, out var y);

            ordered[cursor[(Cell(y, atlasHeight) * Cells) + Cell(x, atlasWidth)]++] = pixel;
        }
    }

    /// <summary>Re-buckets for whatever atlas the index was last built against.</summary>
    /// <remarks>
    ///     ⚠ <b>Nothing at all when no atlas has been applied yet</b>, which is the state right after
    ///     the first <see cref="Draw" />: <see cref="Texture" /> is what learns the resolution, and
    ///     bucketing against a guessed one would be an index a later call silently trusted.
    /// </remarks>
    void Index() {
        if (indexedWidth > 0 && indexedHeight > 0) {
            Index(indexedWidth, indexedHeight);
        }
    }

    /// <summary>Which texel of an atlas a pane pixel reads.</summary>
    /// <param name="pixel">The pane pixel, row-major.</param>
    /// <param name="atlasWidth">The atlas width.</param>
    /// <param name="atlasHeight">Its height.</param>
    /// <param name="x">Its column, always inside the atlas.</param>
    /// <param name="y">Its row.</param>
    /// <remarks>
    ///     ⚠ <b>Clamped, and the same clamped answer is what <see cref="Retexture" /> tests against
    ///     the dirty rectangle.</b> A coordinate outside the unit square — an authored layout that
    ///     tiles, a corner the packer put over the edge — reads the border texel; if membership were
    ///     decided on the raw coordinate instead, that pixel would sample a texel it is never told
    ///     has changed, and one column of the pane would stay stale for the whole stroke.
    /// </remarks>
    void Texel(int pixel, int atlasWidth, int atlasHeight, out int x, out int y) {
        var coordinate = coordinates[pixel];

        x = Math.Clamp((int)MathF.Floor(coordinate.X * atlasWidth), 0, atlasWidth - 1);
        y = Math.Clamp((int)MathF.Floor(coordinate.Y * atlasHeight), 0, atlasHeight - 1);
    }

    /// <summary>One pane pixel's colour: the texel it reads, at the shade it is lit.</summary>
    /// <param name="atlas">The atlas.</param>
    /// <param name="pixel">The pane pixel, row-major.</param>
    /// <returns>The colour, opaque.</returns>
    /// <remarks>
    ///     ⚠ <b>Composited over the background rather than shaded straight, because the atlas is a
    ///     paint layer and most of it is transparent.</b> A stroke's coverage lives in alpha, and a
    ///     pane that multiplied colour by shade and threw the alpha away would show a black model
    ///     with a coloured stroke on it — where what an artist needs to see is the stroke's own edge
    ///     against the surface, which is exactly the thing the alpha carries.
    /// </remarks>
    uint Sample(PaintImage atlas, int pixel) {
        Texel(pixel, atlas.Width, atlas.Height, out var x, out var y);

        var texel = atlas.At(x, y);
        var alpha = PaintImage.Channel(texel, 3);
        var shade = shades[pixel];

        var r = ((PaintImage.Channel(texel, 0) * alpha) + (PaintImage.Channel(Background, 0) * (1f - alpha)))
            * shade;

        var g = ((PaintImage.Channel(texel, 1) * alpha) + (PaintImage.Channel(Background, 1) * (1f - alpha)))
            * shade;

        var b = ((PaintImage.Channel(texel, 2) * alpha) + (PaintImage.Channel(Background, 2) * (1f - alpha)))
            * shade;

        return PaintImage.Pack(r, g, b, 1f);
    }
}
