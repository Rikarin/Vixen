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
///         is about. Each covered pixel is therefore bucketed by which cell of the atlas it reads,
///         and <see cref="Shaded" /> counts the pixels a stamp actually visits — so the assertion is
///         against the stamp's own footprint and not against the pane.
///     </para>
///     <para>
///         ⚠ <b>And the bucketing itself is on the <em>stamp</em> path rather than the camera one,
///         which is the largest single number
///         <a href="https://github.com/Rikarin/Vixen/issues/1107">#1107</a> turned up and not the
///         one it went looking for.</b> The counting sort is two scattered passes over the pane; at
///         1600×900 over an eighteen-thousand-triangle model it was about eight of a thirteen-
///         millisecond pass, more than the projection, the fill and the shade together. Its only
///         reader is <see cref="Retexture" />, so an orbit was rebuilding an index it never looked
///         in. <see cref="Draw" /> now marks it stale and the first stamp of a stroke pays for it.
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

    /// <summary>How few pane pixels are still worth rasterising on one thread.</summary>
    /// <remarks>
    ///     ⚠ <b>A floor and not a switch, so that the banded path is the only path.</b> A pane below
    ///     it is drawn as a single band by the same two methods a maximised one is drawn by — what
    ///     changes is the dispatch and nothing else, which is what stops the two from being two
    ///     rasterisers that can disagree. 256² is the smallest pane on which one <c>Parallel.For</c>
    ///     is worth its own scheduling on this repository's machines.
    /// </remarks>
    const int ParallelFloor = 256 * 256;

    /// <summary>The fewest rows a band may own.</summary>
    /// <remarks>
    ///     ⚠ <b>Rows and not a fraction, because a band's cost is its rows and its overhead is not.</b>
    ///     Bands of one row on a 2160-pixel pane would be two thousand work items over a scan of the
    ///     fragment list each, which is the shape that makes a parallel raster slower than the serial
    ///     one it replaced.
    /// </remarks>
    const int MinimumBandRows = 16;

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

    /// <summary>Every projected, clipped triangle piece the pane can see, with its setup spent.</summary>
    /// <remarks>
    ///     ⚠ <b>What makes a banded raster affordable, and the trap
    ///     <a href="https://github.com/Rikarin/Vixen/issues/1107">#1107</a> names by name.</b> The
    ///     obvious scanline-band raster re-runs each triangle's setup once <em>per band</em> — the
    ///     view transform, the near clip, the shade, the projection and the pane intersection — and
    ///     that setup is not negligible: #1107's own measurement found the camera basis dominating a
    ///     redraw on a model-sized mesh, ahead of every pixel. Spending it once into this and letting
    ///     the bands read it is what stops sixteen threads from doing sixteen times the setup.
    /// </remarks>
    Fragment[] fragments = [];

    /// <summary>Each fragment's first and last pane row, two entries per fragment.</summary>
    /// <remarks>
    ///     ⚠ <b>Beside <see cref="fragments" /> rather than read out of it, and the reason is
    ///     bandwidth.</b> Every band asks every fragment whether it reaches its rows; over forty
    ///     bands that is forty passes, and a pass over the fragments themselves streams eighty-odd
    ///     bytes each where a pass over this streams eight. On an eighteen-thousand-triangle model
    ///     that is the difference between a rejection test that costs nothing and one that costs
    ///     more than the pixels it saves.
    /// </remarks>
    int[] spans = [];

    /// <summary>How many entries of <see cref="fragments" /> the last projection filled.</summary>
    int fragmentCount;

    /// <summary>What atlas width the index describes, or zero for none.</summary>
    int indexedWidth;

    /// <summary>And its height.</summary>
    int indexedHeight;

    /// <summary>Whether the buckets describe geometry that has since moved.</summary>
    /// <remarks>
    ///     ⚠ <b>What makes the index lazy, which is
    ///     <a href="https://github.com/Rikarin/Vixen/issues/1107">#1107</a>'s largest single number
    ///     and not where the issue expected to find it.</b> Measured at 1600×900 over an
    ///     eighteen-thousand-triangle model: the whole pass is about 13 ms and the counting sort is
    ///     about 8 of them — more than the fill, the shade and the projection together. And the only
    ///     reader of the buckets is <see cref="Retexture" />, which runs per <em>stamp</em>: an orbit
    ///     rebuilds an index it never looks in. So <see cref="Draw" /> marks it stale and the first
    ///     stamp of a stroke pays for it once.
    /// </remarks>
    bool indexStale = true;

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

    /// <summary>How many pane pixels the last geometry pass asked about.</summary>
    /// <remarks>
    ///     ⚠ <b>Work rather than a clock, and the only instrument that can see
    ///     <see cref="Bounds" /> stop bounding.</b> A box taken from the clamped extremes of three
    ///     corners is the whole pane whenever one corner sits on the near plane, and the picture it
    ///     produces is pixel-identical to a tight box — every extra pixel is rejected by the
    ///     barycentric test. So the cliff is invisible in a frame, invisible in a pass count, and a
    ///     wall-clock budget for it would be this repository's commonest flake. One add per
    ///     triangle says it exactly.
    /// </remarks>
    public long Examined { get; private set; }

    /// <summary>How many row bands the last geometry pass was split across. One is serial.</summary>
    /// <remarks>
    ///     ⚠ <b>What the pass actually ran with, so that "it went parallel" is a reading rather than
    ///     an assumption.</b> A comparison of a banded picture against a serial one is satisfied by a
    ///     <see cref="BandCount" /> that has quietly started answering one everywhere — the two sides
    ///     are then the same code — and nothing in the picture would say so.
    /// </remarks>
    public int Bands { get; private set; }

    /// <summary>How many times the atlas index has been bucketed, over every pass.</summary>
    /// <remarks>
    ///     ⚠ <b>The counter that says the counting sort is off the camera path, and it is not a
    ///     restatement of <see cref="Renders" />.</b> An orbit is many draws and no stamps; a stroke
    ///     is many stamps and no draws. Only a count of the <em>bucketings</em> can tell "the index
    ///     is built when a stamp needs it" from "the index is built when the geometry moves", and
    ///     the two are eight milliseconds a pointer move apart at a docked pane's size.
    /// </remarks>
    public int Indexings { get; private set; }

    /// <summary>How many row bands a pane of a size is worth splitting into.</summary>
    /// <param name="width">How wide the pane is, in pixels.</param>
    /// <param name="height">How tall.</param>
    /// <returns>The band count, never below one.</returns>
    public static int BandCount(int width, int height) {
        var workers = Environment.ProcessorCount;

        if (workers <= 1 || (long)width * height < ParallelFloor) {
            return 1;
        }

        // Four bands a worker rather than one, because a band's cost is the triangles that reach its
        // rows and a model does not spread itself evenly down the pane — a silhouette puts most of
        // the fill in the middle third, and one band per core would leave the outer cores idle for
        // most of the pass.
        return Math.Clamp(height / MinimumBandRows, 1, workers * 4);
    }

    /// <summary>Which rows one band of a pane owns.</summary>
    /// <param name="band">Which band, from zero.</param>
    /// <param name="bands">How many there are.</param>
    /// <param name="height">How tall the pane is, in pixels.</param>
    /// <returns>Its first and last row, inclusive. The last is under the first for an empty band.</returns>
    /// <remarks>
    ///     ⚠ <b>Every row belongs to exactly one band, and that is the property rather than a
    ///     consequence.</b> A partition that dropped a row would leave a horizontal seam of the
    ///     previous frame across the pane, and one that shared a row between two bands would be two
    ///     threads writing one depth slot — a race whose symptom is a few wrong pixels on some frames
    ///     and not others. Both are invisible in a picture and neither is a crash, so
    ///     <c>PaintMeshRasterTests</c> asserts the partition itself over many sizes rather than
    ///     hoping a rendered frame shows it.
    /// </remarks>
    public static (int Low, int High) Rows(int band, int bands, int height) {
        var count = Math.Max(bands, 1);

        return ((int)((long)band * height / count), (int)((long)(band + 1) * height / count) - 1);
    }

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
    public void Draw(PaintProjection mesh, PaintCamera camera, int width, int height) =>
        Draw(mesh, camera, width, height, null);

    /// <summary>Draws the mesh's geometry and shades it, at a size, in one pass over the pane.</summary>
    /// <param name="mesh">The mesh, in its own space.</param>
    /// <param name="camera">Where it is seen from.</param>
    /// <param name="width">How wide the pane is, in pixels.</param>
    /// <param name="height">How tall.</param>
    /// <param name="atlas">What to put on it, or null for the clay picture.</param>
    /// <exception cref="ArgumentNullException">The mesh or the camera is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The pane has no area.</exception>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/1115">#1115</a>: a
    ///         <c>Draw</c> followed by a <see cref="Texture" /> shaded the whole pane twice and threw
    ///         the first away.</b> The clay pass writes a colour at every pane pixel and the texture
    ///         pass overwrites every one of them, so an orbit over a stack that has anything to
    ///         texture with — which is every state after the first paint layer, the ordinary one —
    ///         paid a full-pane shade for nothing. <see cref="Shaded" /> is the instrument: it was
    ///         twice the pane per camera move and is now once.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An overload rather than a <c>Draw</c> that stopped writing the picture, because
    ///         the clay pass is not dead code.</b> A stack with a model bound and no paint layer has
    ///         no atlas at all, and the flat grey model is what says the binding worked —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1063">#1063</a>'s first milestone. It
    ///         is what a null <paramref name="atlas" /> still draws. ⚠ And <see cref="Texture" /> is
    ///         untouched on purpose: <see cref="Retexture" /> is measured against it by
    ///         <c>PaintMeshRasterTests.A_partial_retexture_leaves_the_picture_a_whole_one_would_have_made</c>,
    ///         and a <c>Draw</c> that had stopped writing the picture would have left both sides of
    ///         that comparison equally wrong.
    ///     </para>
    /// </remarks>
    public void Draw(PaintProjection mesh, PaintCamera camera, int width, int height, PaintImage? atlas) =>
        Draw(mesh, camera, width, height, atlas, BandCount(width, height));

    /// <summary>Draws and shades the mesh across a given number of row bands.</summary>
    /// <param name="mesh">The mesh, in its own space.</param>
    /// <param name="camera">Where it is seen from.</param>
    /// <param name="width">How wide the pane is, in pixels.</param>
    /// <param name="height">How tall.</param>
    /// <param name="atlas">What to put on it, or null for the clay picture.</param>
    /// <param name="bands">How many row bands to split the fill across. One is serial.</param>
    /// <exception cref="ArgumentNullException">The mesh or the camera is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The pane has no area, or the band count is under one.</exception>
    /// <remarks>
    ///     ⚠ <b>The band count is a parameter so that the property can be <em>asserted</em> rather
    ///     than argued.</b> A banded draw and a serial one are meant to be the same bytes — that is
    ///     the whole claim a parallel raster makes — and there is no way to check it if the count is
    ///     decided privately from <c>Environment.ProcessorCount</c> and the pane's area: a test could
    ///     only compare a draw against itself. ⚠ This is <em>not</em> a mechanism whose caller passes
    ///     the default: the overload above computes <see cref="BandCount" /> and passes it, and that
    ///     is the only production route in.
    /// </remarks>
    public void Draw(
        PaintProjection mesh,
        PaintCamera camera,
        int width,
        int height,
        PaintImage? atlas,
        int bands
    ) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bands);

        Resize(width, height);

        Array.Fill(triangles, -1);
        Array.Clear(depths);

        Project(mesh, camera, width, height);

        var covered = 0;

        Bands = bands;

        if (bands <= 1) {
            Rasterise(0, height - 1);
            covered = Colour(atlas, 0, height - 1);
        } else {
            Parallel.For(
                0,
                bands,
                band => {
                    var (low, high) = Rows(band, bands, height);

                    Rasterise(low, high);
                }
            );

            // ⚠ Two dispatches and not one, because the shade of a pixel is not decided until every
            // band has finished filling. A band's rows are its own, but a *triangle* is not: one
            // that straddles the boundary is filled by both, so a thread that shaded its own rows on
            // the way past would read a depth slot its neighbour had not yet won.
            Parallel.For(
                0,
                bands,
                band => {
                    var (low, high) = Rows(band, bands, height);

                    Interlocked.Add(ref covered, Colour(atlas, low, high));
                }
            );
        }

        Covered = covered;
        Shaded += triangles.Length;
        Renders++;

        if (atlas is not null) {
            // What the picture wears now, so that a stamp against this atlas is a partial repaint
            // rather than a whole one — the buckets for it are built when something asks.
            indexedWidth = atlas.Width;
            indexedHeight = atlas.Height;
        }

        // The geometry has moved, so every covered pixel reads a different texel.
        indexStale = true;
    }

    /// <summary>Puts an atlas on the drawn geometry, everywhere.</summary>
    /// <param name="atlas">The picture the brush writes: a composite's result, or a layer.</param>
    /// <returns>The whole pane, as a rectangle, or empty when nothing has been drawn.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="atlas" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Also where a change of atlas size is <em>noticed</em>, though no longer where it is
    ///     acted on.</b> A cell is decided by the <em>texel</em> a pixel reads rather than by its
    ///     coordinate, so that the bucket a stamp looks in and the texel it compares against cannot
    ///     round differently — and that makes the index a function of the resolution. A stack edited
    ///     to a different base size therefore marks the buckets stale here, and the first
    ///     <see cref="Retexture" /> against the new size rebuilds them.
    /// </remarks>
    public PaintRect Texture(PaintImage atlas) {
        ArgumentNullException.ThrowIfNull(atlas);

        if (Picture is not { } picture) {
            return PaintRect.Empty;
        }

        if (atlas.Width != indexedWidth || atlas.Height != indexedHeight) {
            indexedWidth = atlas.Width;
            indexedHeight = atlas.Height;
            indexStale = true;
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

        if (indexStale) {
            // The first stamp after a camera move, a resize or a new mesh. Every later one in the
            // stroke finds the buckets already built — which is what keeps the counting sort out of
            // the per-stamp path that doc 48's exit criterion 8 is about.
            Index(indexedWidth, indexedHeight);
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
        indexStale = true;
    }

    /// <summary>The pane pixels a projected triangle can cover, as a box.</summary>
    /// <param name="pa">Its first corner, in pane pixels.</param>
    /// <param name="pb">Its second.</param>
    /// <param name="pc">Its third.</param>
    /// <param name="width">How wide the pane is, in pixels.</param>
    /// <param name="height">How tall it is.</param>
    /// <param name="lowX">The leftmost pixel the triangle can reach.</param>
    /// <param name="lowY">The topmost.</param>
    /// <param name="highX">The rightmost.</param>
    /// <param name="highY">The bottommost.</param>
    /// <returns>Whether the triangle reaches the pane at all.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Taking the extremes of the three corners and clamping them is not this, and the
    ///         difference is a cost cliff rather than a wrong picture.</b> A corner the near clip
    ///         wrote onto the plane projects to tens of thousands of pixels — the plane is a
    ///         ten-thousandth of the framed radius, so the divide is by a very small number — and
    ///         one such corner sends the clamped box to the whole pane whatever the triangle
    ///         actually covers. <see cref="Fill" /> then runs its barycentric test over every pane
    ///         pixel to reject nearly all of them, per triangle, and a few hundred straddling
    ///         triangles is a hundred million tests a frame. Before
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1105">#1105</a> those triangles were
    ///         dropped and cost nothing, so the clip that stopped the geometry vanishing is what
    ///         opened this.
    ///     </para>
    ///     <para>
    ///         So the box comes from the triangle's <em>intersection</em> with the pane, found by
    ///         Sutherland–Hodgman against the four edges. ⚠ The polygon it computes is thrown away
    ///         and only its extent is kept: filling the clipped shape instead would need the corner
    ///         attributes carried through the clip, and a screen-space lerp of them is wrong under
    ///         perspective. The barycentric weights over the <em>original</em> corners stay exact,
    ///         and the box merely stops asking about pixels no weight can accept.
    ///     </para>
    /// </remarks>
    static bool Bounds(
        Vector2 pa,
        Vector2 pb,
        Vector2 pc,
        int width,
        int height,
        out int lowX,
        out int lowY,
        out int highX,
        out int highY
    ) {
        Span<Vector2> polygon = stackalloc Vector2[8];
        Span<Vector2> clipped = stackalloc Vector2[8];

        polygon[0] = pa;
        polygon[1] = pb;
        polygon[2] = pc;

        var count = 3;

        lowX = 0;
        lowY = 0;
        highX = 0;
        highY = 0;

        for (var side = 0; side < 4; side++) {
            var limit = side switch {
                0 => 0f,
                1 => width - 1f,
                2 => 0f,
                _ => height - 1f,
            };

            var written = 0;

            for (var index = 0; index < count; index++) {
                var from = polygon[index];
                var to = polygon[index == count - 1 ? 0 : index + 1];
                var here = Keeps(from, side, limit);

                if (here) {
                    clipped[written] = from;
                    written++;
                }

                if (here == Keeps(to, side, limit)) {
                    continue;
                }

                var start = side < 2 ? from.X : from.Y;
                var end = side < 2 ? to.X : to.Y;

                // The two sides differ, so the denominator cannot be zero.
                clipped[written] = from + ((to - from) * ((limit - start) / (end - start)));
                written++;
            }

            count = written;

            if (count == 0) {
                return false;
            }

            clipped[..count].CopyTo(polygon);
        }

        var least = polygon[0];
        var most = polygon[0];

        for (var index = 1; index < count; index++) {
            least = Vector2.Min(least, polygon[index]);
            most = Vector2.Max(most, polygon[index]);
        }

        // Every corner is inside the pane by construction, so the conversion is always in range.
        lowX = (int)MathF.Floor(Math.Clamp(least.X, 0f, width - 1f));
        lowY = (int)MathF.Floor(Math.Clamp(least.Y, 0f, height - 1f));
        highX = (int)MathF.Ceiling(Math.Clamp(most.X, 0f, width - 1f));
        highY = (int)MathF.Ceiling(Math.Clamp(most.Y, 0f, height - 1f));

        return true;
    }

    /// <summary>Whether one corner is on the kept side of one pane edge.</summary>
    /// <param name="point">The corner, in pane pixels.</param>
    /// <param name="side">Which edge: left, right, top, then bottom.</param>
    /// <param name="limit">Where that edge is.</param>
    /// <returns>Whether the corner survives that edge.</returns>
    static bool Keeps(Vector2 point, int side, float limit) => side switch {
        0 => point.X >= limit,
        1 => point.X <= limit,
        2 => point.Y >= limit,
        _ => point.Y <= limit,
    };

    /// <summary>Cuts a triangle against the near plane, in the camera's own frame.</summary>
    /// <param name="corners">
    ///     The three corners as <c>PaintCamera.ToView</c> answers, on the way in; the polygon that
    ///     survives, on the way out. At least four long.
    /// </param>
    /// <param name="layout">Their coordinates, in and out, in step with <paramref name="corners" />.</param>
    /// <param name="near">How far in front of the eye the plane is.</param>
    /// <returns>How many corners the polygon has: nought, three or four, and never one or two.</returns>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/1105">#1105</a>: what the
    ///         rasteriser did before this was <em>drop</em> a triangle with any corner behind the
    ///         eye.</b> ⚠ That could not paint anything wrong — a pane pixel with no triangle takes
    ///         no stroke, and the brush's raycast never goes through the projection at all — so the
    ///         symptom was geometry vanishing rather than paint landing in the wrong place. It is
    ///         reachable wherever the eye can get inside the surface, which is every open shell, room
    ///         interior and character's mouth: <c>PaintCamera.Distance</c>'s floor is a fraction of
    ///         the framed <em>sphere</em> and only keeps the eye outside a convex model.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Sutherland–Hodgman against one plane, which is one case and not the four the
    ///         old remark feared.</b> Walking the three edges and emitting a corner when it is in
    ///         front plus a crossing whenever an edge changes side covers "one in", "two in" and
    ///         "all in" without naming any of them — and a triangle wholly behind emits nothing,
    ///         which is the only way to get fewer than three back.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A crossing's depth is written as <paramref name="near" /> and not as the lerp's
    ///         answer.</b> The two differ by a float's worth, and the rasteriser divides by that
    ///         depth: a corner that landed a rounding <em>short</em> of the plane would be a corner
    ///         the projection was told never to receive, at a reciprocal depth several times larger
    ///         than the one the clip was chosen to bound.
    ///     </para>
    /// </remarks>

    static int Clip(Span<Vector3> corners, Span<Vector2> layout, float near) {
        Span<Vector3> points = stackalloc Vector3[3];
        Span<Vector2> coordinates = stackalloc Vector2[3];

        corners[..3].CopyTo(points);
        layout[..3].CopyTo(coordinates);

        var count = 0;

        for (var edge = 0; edge < 3; edge++) {
            var next = edge == 2 ? 0 : edge + 1;
            var from = points[edge];
            var to = points[next];
            var starts = from.Z > near;
            var ends = to.Z > near;

            if (starts) {
                corners[count] = from;
                layout[count] = coordinates[edge];
                count++;
            }

            if (starts == ends) {
                continue;
            }

            // The two sides differ, so the denominator cannot be zero.
            var t = (near - from.Z) / (to.Z - from.Z);

            corners[count] = new(from.X + ((to.X - from.X) * t), from.Y + ((to.Y - from.Y) * t), near);
            layout[count] = coordinates[edge] + ((coordinates[next] - coordinates[edge]) * t);
            count++;
        }

        return count;
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

    /// <summary>Projects, clips and shades every triangle into <see cref="fragments" />.</summary>
    /// <param name="mesh">The mesh, in its own space.</param>
    /// <param name="camera">Where it is seen from.</param>
    /// <param name="width">How wide the pane is, in pixels.</param>
    /// <param name="height">How tall.</param>
    /// <remarks>
    ///     ⚠ <b>Serial, and deliberately so.</b> It is one pass over the triangles, it appends in
    ///     order and the order is what makes the fill deterministic — two fragments at the same depth
    ///     resolve by which was projected first, so a projection that raced would give a different
    ///     picture on some frames and not others at every coplanar seam.
    /// </remarks>
    void Project(PaintProjection mesh, PaintCamera camera, int width, int height) {
        Examined = 0;
        fragmentCount = 0;

        // Two per triangle is the most a near-plane cut can produce: `Clip` answers three or four
        // corners, and a four-corner polygon fans into two.
        var capacity = (int)Math.Min((long)mesh.Triangles * 2L, int.MaxValue);

        if (fragments.Length < capacity) {
            fragments = new Fragment[capacity];
            spans = new int[capacity * 2];
        }

        var forward = camera.Forward;
        var near = camera.Near;

        // ⚠ Once, and not once per corner. See `PaintCamera.Basis`: the camera's four axes are
        // computed properties over `MathF.SinCos`, and reading them inside this loop was the
        // dominant cost of a redraw on a model-sized mesh — #1107.
        var basis = camera.Basis;

        Span<Vector3> corners = stackalloc Vector3[4];
        Span<Vector2> layout = stackalloc Vector2[4];
        Span<Vector2> pane = stackalloc Vector2[4];

        for (var triangle = 0; triangle < mesh.Triangles; triangle++) {
            mesh.Triangle(triangle, out var a, out var b, out var c, out var ua, out var ub, out var uc);

            corners[0] = basis.Of(a);
            corners[1] = basis.Of(b);
            corners[2] = basis.Of(c);
            layout[0] = ua;
            layout[1] = ub;
            layout[2] = uc;

            var count = Clip(corners, layout, near);

            if (count < 3) {
                // Wholly behind the eye. A cut triangle keeps at least three corners, so this is the
                // only case in which nothing is drawn at all.
                continue;
            }

            var shade = Shade(a, b, c, forward);

            for (var corner = 0; corner < count; corner++) {
                pane[corner] = PaintCamera.ToPane(corners[corner], width, height);
            }

            // ⚠ A fan and not a strip, and the shade is the *unclipped* triangle's. Flat shading
            // reads the plane the three original corners lie in, which is the plane every piece of
            // the cut polygon is still in — so the two or three pieces cannot disagree about how lit
            // one triangle is, which is what a shade recomputed per piece would risk at a sliver.
            for (var corner = 1; corner + 1 < count; corner++) {
                Accept(
                    triangle,
                    shade,
                    pane[0],
                    pane[corner],
                    pane[corner + 1],
                    corners[0].Z,
                    corners[corner].Z,
                    corners[corner + 1].Z,
                    layout[0],
                    layout[corner],
                    layout[corner + 1]
                );
            }
        }
    }

    /// <summary>Keeps one projected triangle piece, with its per-triangle setup spent.</summary>
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
    ///     ⚠ <b><see cref="Examined" /> is added here and not in <see cref="Fill" />, which is what
    ///     keeps it the number it was.</b> It counts the pixels the pass <em>asks about</em> — the
    ///     instrument that can see <see cref="Bounds" /> stop bounding — and a band that clipped the
    ///     box to its own rows would count the same triangle once per band it reaches, so the same
    ///     picture would report a different figure at a different band count.
    /// </remarks>
    void Accept(
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

        if (!Bounds(pa, pb, pc, Width, Height, out var lowX, out var lowY, out var highX, out var highY)) {
            return;
        }

        Examined += (long)(highX - lowX + 1) * (highY - lowY + 1);

        fragments[fragmentCount] = new() {
            A = pa,
            B = pb,
            C = pc,
            Ua = ua,
            Ub = ub,
            Uc = uc,
            InvA = 1f / za,
            InvB = 1f / zb,
            InvC = 1f / zc,
            Inverse = 1f / area,
            Shade = shade,
            Triangle = triangle,
            LowX = lowX,
            HighX = highX
        };

        spans[fragmentCount * 2] = lowY;
        spans[(fragmentCount * 2) + 1] = highY;
        fragmentCount++;
    }

    /// <summary>Fills every fragment that reaches a band of rows, depth-tested.</summary>
    /// <param name="lowRow">The band's first row.</param>
    /// <param name="highRow">Its last, inclusive.</param>
    /// <remarks>
    ///     ⚠ <b>What makes this safe to run on several threads is that a band owns its rows and every
    ///     buffer here is addressed by <c>row × Width + column</c>.</b> No two bands can reach one
    ///     slot of <c>depths</c>, <c>triangles</c>, <c>shades</c> or <c>coordinates</c>, so there is
    ///     nothing to interlock and nothing to tear — and the fragments are read-only by now. ⚠ It is
    ///     also <em>deterministic</em>: fragments are visited in projection order within a band, so a
    ///     depth tie resolves the same way it did serially and the picture is the same bytes at every
    ///     band count.
    /// </remarks>
    void Rasterise(int lowRow, int highRow) {
        if (highRow < lowRow) {
            return;
        }

        for (var index = 0; index < fragmentCount; index++) {
            var top = spans[index * 2];
            var bottom = spans[(index * 2) + 1];

            if (bottom < lowRow || top > highRow) {
                continue;
            }

            Fill(in fragments[index], Math.Max(top, lowRow), Math.Min(bottom, highRow));
        }
    }

    /// <summary>Fills one fragment across a range of rows.</summary>
    /// <param name="fragment">The projected triangle piece.</param>
    /// <param name="fromRow">The first row to write.</param>
    /// <param name="toRow">The last, inclusive.</param>
    /// <remarks>
    ///     ⚠ <b>The sample point of pixel <c>n</c> is <c>n</c> exactly, and not <c>n + ½</c>.</b>
    ///     <c>PaintCamera.Ray</c> casts through a pixel's centre and <c>PaintCamera.ToPane</c> is
    ///     its exact inverse, so the projected frame is already centre-based — adding a half here
    ///     would offset the picture from the brush by half a pixel, which is invisible everywhere
    ///     except the silhouette and at the seam between two islands.
    /// </remarks>
    void Fill(in Fragment fragment, int fromRow, int toRow) {
        var pa = fragment.A;
        var pb = fragment.B;
        var pc = fragment.C;
        var inverse = fragment.Inverse;
        var invA = fragment.InvA;
        var invB = fragment.InvB;
        var invC = fragment.InvC;

        for (var y = fromRow; y <= toRow; y++) {
            for (var x = fragment.LowX; x <= fragment.HighX; x++) {
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
                triangles[slot] = fragment.Triangle;
                shades[slot] = fragment.Shade;

                coordinates[slot] =
                    ((fragment.Ua * weightA * invA)
                        + (fragment.Ub * weightB * invB)
                        + (fragment.Uc * weightC * invC))
                    / reciprocal;
            }
        }
    }

    /// <summary>Writes the picture for a band of rows, from an atlas or as clay.</summary>
    /// <param name="atlas">What the model wears, or null for the clay picture.</param>
    /// <param name="lowRow">The band's first row.</param>
    /// <param name="highRow">Its last, inclusive.</param>
    /// <returns>How many of those rows' pixels show a triangle.</returns>
    /// <remarks>
    ///     ⚠ <b>A flat picture and not an empty one when there is no atlas</b>, which is the
    ///     difference between a milestone and a black pane. A stack with a model bound and no paint
    ///     layer yet has nothing to texture with — and that is the state an artist is in immediately
    ///     before they add the layer they mean to paint on, so it is the state in which the pane has
    ///     to prove the binding worked.
    /// </remarks>
    int Colour(PaintImage? atlas, int lowRow, int highRow) {
        if (highRow < lowRow || Picture is not { } picture) {
            return 0;
        }

        var covered = 0;
        var last = ((highRow + 1) * Width) - 1;

        for (var pixel = lowRow * Width; pixel <= last; pixel++) {
            if (triangles[pixel] < 0) {
                picture[pixel] = Background;

                continue;
            }

            picture[pixel] = atlas is null ? Clay(pixel) : Sample(atlas, pixel);
            covered++;
        }

        return covered;
    }

    /// <summary>One pane pixel's colour with no atlas on the model: clay, at the shade it is lit.</summary>
    /// <param name="pixel">The pane pixel, row-major.</param>
    /// <returns>The colour, opaque.</returns>
    uint Clay(int pixel) {
        var shade = shades[pixel];

        return PaintImage.Pack(
            PaintImage.Channel(Untextured, 0) * shade,
            PaintImage.Channel(Untextured, 1) * shade,
            PaintImage.Channel(Untextured, 2) * shade,
            1f
        );
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
        indexStale = false;
        Indexings++;

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

    /// <summary>One projected triangle piece, with everything the fill needs already computed.</summary>
    /// <remarks>
    ///     ⚠ <b>A struct in a pre-sized array rather than a record in a list, and the reason is that
    ///     an orbit is a frame loop.</b> A pane redraw happens per pointer move; a model-sized mesh
    ///     produces tens of thousands of these, and an allocation each would be the defect
    ///     <c>Picture</c>'s own remark exists to avoid, one dimension smaller and one order more
    ///     often. The array is grown to the mesh's worst case once and reused.
    /// </remarks>
    struct Fragment {
        /// <summary>Its first corner, in pane pixels.</summary>
        public Vector2 A;

        /// <summary>Its second.</summary>
        public Vector2 B;

        /// <summary>Its third.</summary>
        public Vector2 C;

        /// <summary>The first corner's coordinate, in the unit square.</summary>
        public Vector2 Ua;

        /// <summary>The second's.</summary>
        public Vector2 Ub;

        /// <summary>The third's.</summary>
        public Vector2 Uc;

        /// <summary>The first corner's reciprocal depth.</summary>
        public float InvA;

        /// <summary>The second's.</summary>
        public float InvB;

        /// <summary>The third's.</summary>
        public float InvC;

        /// <summary>One over the projected area, which the barycentric weights divide by.</summary>
        public float Inverse;

        /// <summary>How lit the whole triangle is, 0…1.</summary>
        public float Shade;

        /// <summary>Which triangle of the mesh it is a piece of.</summary>
        public int Triangle;

        /// <summary>The leftmost pane pixel it can reach.</summary>
        public int LowX;

        /// <summary>The rightmost.</summary>
        public int HighX;
    }
}
