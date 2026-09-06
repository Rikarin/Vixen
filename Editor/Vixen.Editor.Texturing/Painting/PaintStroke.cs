// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Terrain;

namespace Vixen.Editor.Texturing.Painting;

/// <summary>
///     One drag on a paint layer: stamps into the atlas, dilated across the seam, recorded so it can
///     be undone once.
/// </summary>
/// <remarks>
///     <para>
///         <b>Doc 48 § D13, and the chain is doc 31's.</b> <c>BrushStroke</c> turns the pointer path
///         into evenly spaced stamps — including the leftover distance carried across pointer events,
///         which is what stops a stroke being denser at a high frame rate — and
///         <c>TerrainBrush.WeightAt</c> answers what weight a stamp has at a sample. Neither of them
///         is touched. What is new is where the stamp lands, what it composites into, and the
///         dilation.
///     </para>
///     <para>
///         ⚠ <b>The undo record and the stroke's base image are the same dictionary.</b>
///         <see cref="PaintImage.Mix" /> has to read what a texel held <em>before the stroke</em>, or
///         two overlapping stamps paint darker than one; the undo record holds exactly that, for
///         exactly the texels that need it. Keeping them apart would be two sparse maps of the same
///         keys, and the second would be pure cost.
///     </para>
///     <para>
///         ⚠ <b>Sparse, for <c>TerrainStroke</c>'s reason.</b> A stroke touches a fraction of a 4K
///         atlas, so a dense before-image would be 67 MB per drag whatever the artist did. A
///         dictionary keyed by texel index is the shape the terrain stroke already settled on, and
///         <c>TryAdd</c> rather than an assignment is what makes the first crossing the one that is
///         kept.
///     </para>
///     <para>
///         ⚠ <b>Nothing here knows about a viewport, a ray or a mesh.</b> The stroke is fed texel
///         positions. A 3D surface converts a hit to UV and a UV to texels; a 2D view converts a
///         cursor to texels directly; symmetry hands the same stroke's siblings their own mirrored
///         paths. <see cref="PaintSession" /> is where those meet, and it is thirty lines because
///         this is the part with the arithmetic in it.
///     </para>
/// </remarks>
sealed class PaintStroke {
    readonly PaintImage image;
    readonly PaintCoverage coverage;
    readonly PaintBrush brush;
    readonly uint colour;
    readonly int gutter;
    readonly uint seed;
    readonly BrushStroke path;
    readonly List<BrushStamp> spaced = [];
    readonly List<(int Texel, float Reach)> pending = [];
    readonly Dictionary<int, uint> before = [];
    readonly Dictionary<int, float> reached = [];

    /// <summary>How many four-neighbour steps from coverage each dilated texel is.</summary>
    /// <remarks>
    ///     ⚠ <b>The field that makes the reach a property of the atlas rather than of the stroke —
    ///     <a href="https://github.com/Rikarin/Vixen/issues/868">#868</a>.</b> A distance from the
    ///     coverage map is the same number whichever stamp measured it, which is what lets
    ///     <see cref="Dilate" /> run round <c>r</c> from the texels at distance <c>r</c> and stop at
    ///     <see cref="gutter" /> however many stamps have crossed the seam. Kept beside
    ///     <see cref="reached" /> and not folded into it because the two answer different questions:
    ///     a reach may rise as later stamps paint the island harder, and a distance may only fall.
    ///     <para>
    ///         ⚠ <b>And "may only fall" was a claim rather than a behaviour until
    ///         <a href="https://github.com/Rikarin/Vixen/issues/896">#896</a>.</b> The write lived in
    ///         the commit loop, which the scan's <c>already &gt;= best</c> early-out jumps over — so
    ///         a distance fell only when the reach also rose, which for a uniform opaque stroke is
    ///         almost never. It is written in the scan now, where the proof of the shorter path is.
    ///     </para>
    /// </remarks>
    readonly Dictionary<int, int> distance = [];
    readonly float smoothing;
    Vector2 smoothed;
    bool started;

    /// <summary>Begins a stroke on a paint layer.</summary>
    /// <param name="image">The layer's pixels, written in place.</param>
    /// <param name="coverage">Which texels belong to an island.</param>
    /// <param name="brush">The brush.</param>
    /// <param name="colour">What is being painted, packed <c>0xAABBGGRR</c>.</param>
    /// <param name="gutter">
    ///     How many texels the stamp is dilated past an island's edge. Zero disables the dilation,
    ///     which is what makes the seam test able to show both halves.
    /// </param>
    /// <param name="smoothing">
    ///     How much the input path lags the pointer, 0…1 — doc 48 § D13's "smoothing is a filter on
    ///     the input points", and the only stroke-level effect that belongs to the stroke rather than
    ///     to the surface.
    /// </param>
    /// <param name="seed">What the jitter and the random rotations derive from.</param>
    /// <exception cref="ArgumentException">The coverage map is not the image's size.</exception>
    public PaintStroke(
        PaintImage image,
        PaintCoverage coverage,
        PaintBrush brush,
        uint colour,
        int gutter = 4,
        float smoothing = 0f,
        uint seed = 0x9E3779B9u
    ) {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(coverage);
        ArgumentOutOfRangeException.ThrowIfNegative(gutter);

        if (coverage.Width != image.Width || coverage.Height != image.Height) {
            throw new ArgumentException(
                $"The coverage map is {coverage.Width}×{coverage.Height} and the paint layer is "
                + $"{image.Width}×{image.Height}. A mismatch dilates across the wrong seam, which is a "
                + "hairline in a different place rather than a failure.",
                nameof(coverage)
            );
        }

        this.image = image;
        this.coverage = coverage;
        this.brush = brush;
        this.colour = colour;
        this.gutter = gutter;
        this.seed = seed;
        this.smoothing = Math.Clamp(smoothing, 0f, 0.999f);

        path = new(brush.Kernel, seed);
    }

    /// <summary>Everything the stroke has touched, including its dilation.</summary>
    public PaintRect Rect { get; private set; } = PaintRect.Empty;

    /// <summary>How many stamps it has laid down.</summary>
    public int StampCount { get; private set; }

    /// <summary>How many texels the record holds.</summary>
    public int RecordedTexels => before.Count;

    /// <summary>How many texel weights have been evaluated, over the whole stroke.</summary>
    /// <remarks>
    ///     ⚠ <b>The measurement doc 48's exit criterion 8 is actually about.</b> "Under 16 ms per
    ///     stamp" is a wall-clock budget, and a wall-clock budget calibrated on an idle machine is
    ///     this repository's largest flake source. The property underneath it is that a stamp's work
    ///     is its own footprint and nothing else — not the atlas, not the number of layers beneath
    ///     it — and that is a counter. <c>PaintCostTests</c> gates the counter and reports the
    ///     milliseconds beside it as evidence rather than as an assertion.
    /// </remarks>
    public long WeightsEvaluated { get; private set; }

    /// <summary>How many texels the dilation has written outside an island.</summary>
    public int DilatedTexels { get; private set; }

    /// <summary>How many texels either loop has <em>looked at</em>, over the whole stroke.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The counter <see cref="WeightsEvaluated" /> is not, and the difference is what
    ///         <a href="https://github.com/Rikarin/Vixen/issues/871">#871</a> found.</b> A weight is
    ///         evaluated only for a texel an island covers, and only inside the stamp's footprint —
    ///         so a counter of weights measures the cheaper of a stamp's two loops. The dilation
    ///         scans the footprint grown by the gutter on every side, up to <see cref="gutter" />
    ///         times. Exit criterion 8 was gated on the smaller number.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>How much larger depends on the coverage, and #871's own "roughly 45 000" is the
    ///         ceiling rather than the usual case.</b> <see cref="Dilate" /> stops as soon as a round
    ///         finds nothing to fill, so over an atlas an island covers entirely it runs <em>once</em>
    ///         — measured at radius 48 and gutter 4, 10 810 scanned texels a stamp against 9 216
    ///         weights. The 45 000 is what an atlas with real seams in it costs, where each of the
    ///         four rounds reaches new gutter texels. Either way it is the bigger of the two, and
    ///         either way it was in no counter.
    ///     </para>
    ///     <para>
    ///         <b>So this counts every texel visited, in both loops, covered or not.</b> It is still
    ///         a property expressed as work rather than as elapsed time, and its closed form —
    ///         footprint plus <c>gutter × (footprint + 2·gutter)²</c> — mentions the atlas and the
    ///         stack in neither term, which is the claim the criterion is about.
    ///     </para>
    /// </remarks>
    public long TexelsScanned { get; private set; }

    /// <summary>Whether anything has been painted.</summary>
    public bool IsEmpty => before.Count == 0;

    /// <summary>How many bytes the record occupies, before and after together.</summary>
    public long Bytes => (long)before.Count * (sizeof(uint) + sizeof(int)) * 2;

    /// <summary>Moves the pointer, painting whatever stamps that earned.</summary>
    /// <param name="texel">Where the pointer is now, in texels of the atlas.</param>
    /// <param name="regions">
    ///     Appended one rectangle <em>per stamp</em>, or <see langword="null" /> to collect none.
    /// </param>
    /// <returns>The rectangle this move dirtied, for a view to re-upload.</returns>
    /// <remarks>
    ///     <para>
    ///         The first call always stamps, for <c>BrushStroke</c>'s reason: an artist who clicks
    ///         without dragging expects one stamp rather than none.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The return is the union and <paramref name="regions" /> is not, and the second is
    ///         the one a recomposite must use</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/871">#871</a>. One pointer move can
    ///         earn many stamps: a pointer that jumps half the atlas between two frames lays a line
    ///         of them, and the union of that line is a rectangle covering everything between the
    ///         two ends. Recompositing the union makes a stamp's cost a function of how fast the
    ///         artist moved, which is the opposite of what "independent of atlas size" claims.
    ///     </para>
    /// </remarks>
    public PaintRect MoveTo(Vector2 texel, List<PaintRect>? regions = null) {
        smoothed = started ? Vector2.Lerp(smoothed, texel, 1f - smoothing) : texel;
        started = true;

        spaced.Clear();
        path.MoveTo(smoothed, spaced);

        var dirty = PaintRect.Empty;

        foreach (var stamp in spaced) {
            var painted = Apply(Jittered(stamp, StampCount));

            if (!painted.IsEmpty) {
                regions?.Add(painted);
            }

            dirty = dirty.Union(painted);
            StampCount++;
        }

        Rect = Rect.Union(dirty);

        return dirty;
    }

    /// <summary>Puts the layer back the way it was before the stroke.</summary>
    /// <returns>What the stroke had touched.</returns>
    public PaintRect Undo() {
        foreach (var (texel, value) in before) {
            image[texel] = value;
        }

        return Rect;
    }

    /// <summary>Captures what the stroke left, so it can be redone.</summary>
    /// <returns>The record.</returns>
    /// <remarks>
    ///     Taken at pointer-up rather than accumulated, for <c>TerrainStroke.Capture</c>'s reason:
    ///     the after image is needed once and building it as the stroke ran would double the record's
    ///     cost for a stroke nobody undoes, which is almost all of them.
    /// </remarks>
    public PaintStrokeRedo Capture() {
        Dictionary<int, uint> after = new(before.Count);

        foreach (var texel in before.Keys) {
            after[texel] = image[texel];
        }

        return new(image, after, Rect);
    }

    /// <summary>A stamp with this brush's jitter applied to it.</summary>
    /// <remarks>
    ///     ⚠ <b>From a hash of the stamp's index, not from a generator</b> — <c>BrushStroke</c>'s
    ///     rule, and the reason is the same one: undo and redo have to reproduce the stroke, and a
    ///     jitter drawn from shared state repaints something else the second time. Each of the three
    ///     jitters takes its own stream so that turning one off does not shift the others.
    /// </remarks>
    PaintStamp Jittered(BrushStamp stamp, int index) {
        var radius = brush.Radius * (1f - (Unit(index, 3) * Math.Clamp(brush.SizeJitter, 0f, 1f)));
        var angle = stamp.Rotation + (((Unit(index, 1) * 2f) - 1f) * brush.AngleJitter);
        var centre = stamp.Centre;

        if (brush.PositionJitter > 0f) {
            var direction = Unit(index, 2) * MathF.Tau;
            var distance = MathF.Sqrt(Unit(index, 4)) * Math.Clamp(brush.PositionJitter, 0f, 1f) * brush.Radius;
            var (sin, cos) = MathF.SinCos(direction);

            centre += new Vector2(cos, sin) * distance;
        }

        return new(centre, angle, MathF.Max(radius, 1e-3f), Math.Clamp(brush.Flow, 0f, 1f) * stamp.Flow);
    }

    /// <summary>Composites one stamp into the layer and dilates it past the seam.</summary>
    PaintRect Apply(PaintStamp stamp) {
        var footprint = brush.FootprintOf(stamp, image.Width, image.Height);
        var kernel = brush.KernelFor(stamp);
        var cap = Math.Clamp(brush.Opacity, 0f, 1f);

        for (var y = footprint.Y; y < footprint.EndY; y++) {
            for (var x = footprint.X; x < footprint.EndX; x++) {
                var index = (y * image.Width) + x;

                // Counted before the coverage test, because this loop visited the texel whether or
                // not an island covers it — see `TexelsScanned`.
                TexelsScanned++;

                // ⚠ Only where the surface is. A stamp that painted the gutter directly would put
                // colour where no triangle samples it and would then be *overwritten* by the
                // dilation below, so the two would disagree about the same texels.
                if (!coverage.IsCovered(index)) {
                    continue;
                }

                var weight = PaintBrush.Weight(kernel, new(x + 0.5f, y + 0.5f), stamp, brush.Alpha);

                WeightsEvaluated++;

                if (!(weight > 0f)) {
                    continue;
                }

                reached.TryGetValue(index, out var already);

                var reach = Math.Min(cap, already + (weight * (1f - already)));

                if (reach <= already) {
                    continue;
                }

                Record(index);
                reached[index] = reach;
                image[index] = PaintImage.Mix(before[index], colour, reach);
            }
        }

        var dilated = Dilate(footprint);

        return footprint.Union(dilated);
    }

    /// <summary>
    ///     Grows what the stamp painted outward past the island's edge, into texels no triangle
    ///     covers.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The defect this exists to prevent only appears after mipping.</b> A stroke that
    ///         crosses a UV island edge paints up to the last covered texel and stops. At mip 0 that
    ///         is correct and looks correct. At mip 1 a 2×2 box straddling the border averages the
    ///         painted texel with an unpainted one, at mip 2 with three of them, and the result is a
    ///         hairline along every seam that appears at a distance and is invisible while the artist
    ///         is looking at the thing they painted.
    ///     </para>
    ///     <para>
    ///         <b>It is <c>MapBaker.Dilate</c>'s idea one layer down</b>, and the three rules are
    ///         taken from it rather than rediscovered: four-neighbour rounds so the reach is exactly
    ///         <see cref="gutter" /> texels; each round committed after it finishes, so a texel
    ///         filled early in the scan cannot seed one later in the same round and give the halo a
    ///         lopsided bias; and a texel an island covers is never written, so one island's gutter
    ///         cannot overwrite the island beside it in the atlas.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The first of those three was false for a stroke of more than one stamp, and the
    ///         third is not what made it matter —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/868">#868</a>.</b> Coverage is what
    ///         stops a gutter reaching the island beside it, so an over-reach could never overwrite a
    ///         neighbour whatever its length; what it could do is make the halo a function of the
    ///         brush and the stamp spacing. <see cref="reached" /> is stroke-wide, so stamp N seeded
    ///         its rounds from texels stamp N−1 had already dilated and advanced a further
    ///         <see cref="gutter" />, up to the stamp footprint's own bound of
    ///         <c>radius + gutter</c>. That is a wider halo, more recorded texels, a larger dirty
    ///         rectangle for the view to re-upload — and a number an author set that did not decide
    ///         any of them. <see cref="distance" /> is what makes a round measure from coverage
    ///         instead, and <c>PaintDilationReachTests</c> asserts the reach against a
    ///         breadth-first distance rather than against a column number.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The coverage is dilated, not the colour.</b> A dilated texel takes its
    ///         neighbour's <em>reach</em> and derives its colour through the same
    ///         <see cref="PaintImage.Mix" /> from its own before-value, so a second stamp crossing
    ///         the same gutter composites consistently instead of building up on a value the first
    ///         stamp had already blended.
    ///     </para>
    /// </remarks>
    PaintRect Dilate(PaintRect footprint) {
        if (gutter <= 0 || footprint.IsEmpty) {
            return PaintRect.Empty;
        }

        var grown = footprint.Grow(gutter).Clip(image.Width, image.Height);
        var written = PaintRect.Empty;

        for (var round = 0; round < gutter; round++) {
            pending.Clear();

            for (var y = grown.Y; y < grown.EndY; y++) {
                for (var x = grown.X; x < grown.EndX; x++) {
                    var index = (y * image.Width) + x;

                    // ⚠ The loop exit criterion 8's counter used not to see at all, and the larger
                    // of a stamp's two: one round over the grown footprint already exceeds the whole
                    // footprint scan, and an atlas with real islands in it runs up to `gutter` of
                    // them. #871.
                    TexelsScanned++;

                    if (coverage.IsCovered(index)) {
                        continue;
                    }

                    var best = Neighbour(x - 1, y, x >= 1, round);
                    best = MathF.Max(best, Neighbour(x + 1, y, x + 1 < image.Width, round));
                    best = MathF.Max(best, Neighbour(x, y - 1, y >= 1, round));
                    best = MathF.Max(best, Neighbour(x, y + 1, y + 1 < image.Height, round));

                    if (!(best > 0f)) {
                        continue;
                    }

                    // ⚠ Here, before the early-out, and not in the commit loop below — #896. A
                    // round that got a source has *proved* this texel is `round + 1` from coverage,
                    // whether or not it also has colour to add; recording that only where the reach
                    // rose left a texel an earlier stamp reached the long way round holding the long
                    // distance, and for a uniform opaque stroke, where every stamp contributes reach
                    // 1, the guard below fires on almost every one. `Neighbour` only lets a texel at
                    // distance `r` seed round `r`, so the stale number breaks the chain and the
                    // gutter stops short.
                    //
                    // Writing it mid-scan cannot bias this round: the value written is `round + 1`
                    // and a neighbour is read at `== round`, so nothing filled here becomes a source
                    // until the next round — which is the same "committed after the round finishes"
                    // rule the reaches keep.
                    var steps = round + 1;

                    distance[index] = distance.TryGetValue(index, out var known)
                        ? Math.Min(known, steps)
                        : steps;

                    if (reached.TryGetValue(index, out var already) && already >= best) {
                        continue;
                    }

                    pending.Add((index, best));
                }
            }

            if (pending.Count == 0) {
                break;
            }

            foreach (var (index, reach) in pending) {
                if (!reached.ContainsKey(index)) {
                    DilatedTexels++;
                }

                Record(index);
                reached[index] = reach;
                image[index] = PaintImage.Mix(before[index], colour, reach);

                var x = index % image.Width;
                var y = index / image.Width;

                written = written.Union(new(x, y, 1, 1));
            }
        }

        return written;
    }

    /// <summary>What one neighbour contributes to a texel being filled in <paramref name="round" />.</summary>
    /// <remarks>
    ///     ⚠ <b>Gated on the neighbour's distance, which is the whole of
    ///     <a href="https://github.com/Rikarin/Vixen/issues/868">#868</a>.</b> Round <c>r</c> fills
    ///     the texels at distance <c>r + 1</c>, so its sources are the covered texels when
    ///     <c>r</c> is zero and the texels at distance <c>r</c> after that. Ungated — reading
    ///     <see cref="reached" /> alone, which is stroke-wide and already holds what the previous
    ///     stamp dilated — round zero of stamp N started from the far edge of stamp N−1's halo, and
    ///     the reach grew by a further <see cref="gutter" /> per stamp until the footprint stopped
    ///     it. By induction over this gate, a texel filled in round <c>r</c> is at most
    ///     <c>r + 1</c> steps from coverage, so nothing is ever written further than
    ///     <see cref="gutter" />.
    /// </remarks>
    float Neighbour(int x, int y, bool inside, int round) {
        if (!inside) {
            return 0f;
        }

        var index = (y * image.Width) + x;

        if (!reached.TryGetValue(index, out var reach)) {
            return 0f;
        }

        return coverage.IsCovered(index)
            ? round == 0 ? reach : 0f
            : distance.TryGetValue(index, out var steps) && steps == round ? reach : 0f;
    }

    /// <summary>
    ///     Remembers what a texel held before the stroke, once.
    /// </summary>
    /// <remarks>
    ///     <c>TryAdd</c> rather than an assignment, for <c>TerrainStroke.Extend</c>'s reason: the
    ///     first crossing holds the value the stroke started from, and re-recording on a later
    ///     crossing would make undo restore the middle of the stroke — and, here, would also make
    ///     <see cref="PaintImage.Mix" /> composite onto its own output.
    /// </remarks>
    void Record(int index) => before.TryAdd(index, image[index]);

    /// <summary>A 0…1 number from the stroke's seed, a stamp's index and a stream.</summary>
    float Unit(int index, uint stream) {
        var hash = seed ^ (uint)index ^ (stream * 0x9E3779B9u);

        hash ^= hash >> 16;
        hash *= 0x7FEB352Du;
        hash ^= hash >> 15;
        hash *= 0x846CA68Bu;
        hash ^= hash >> 16;

        return hash / (float)uint.MaxValue;
    }
}

/// <summary>What a stroke left, so it can be put back after an undo.</summary>
/// <remarks>
///     A separate type from <see cref="PaintStroke" /> for <c>TerrainStrokeRedo</c>'s reason: the
///     stroke exists while the pointer is down and this exists for as long as the undo stack holds
///     the entry.
/// </remarks>
sealed class PaintStrokeRedo {
    readonly PaintImage image;
    readonly Dictionary<int, uint> after;

    internal PaintStrokeRedo(PaintImage image, Dictionary<int, uint> after, PaintRect rect) {
        this.image = image;
        this.after = after;
        Rect = rect;
    }

    /// <summary>Everything the stroke touched.</summary>
    public PaintRect Rect { get; }

    /// <summary>How many texels the record holds.</summary>
    public int RecordedTexels => after.Count;

    /// <summary>Puts the stroke's result back.</summary>
    /// <returns>What it touched.</returns>
    public PaintRect Redo() {
        foreach (var (texel, value) in after) {
            image[texel] = value;
        }

        return Rect;
    }
}
