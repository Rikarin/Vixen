// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Geometry.Uv;

namespace Vixen.Geometry.Remeshing;

/// <summary>What the atlas stage did, so the report can say it without owning the arithmetic.</summary>
/// <param name="Charts">How many super-charts were packed.</param>
/// <param name="Patches">How many patches went into them.</param>
/// <param name="SeamRatio">The seam length left, as a fraction of the output's total edge length.</param>
/// <param name="Warnings">What could not be done.</param>
readonly record struct AtlasResult(int Charts, int Patches, float SeamRatio, IReadOnlyList<string> Warnings);

/// <summary>docs/plan/41 § D13's atlas: charts off the layout, merged, then handed to the packer.</summary>
/// <remarks>
///     <para>
///         <b>A quantized quad patch is a rectangle, so there is nothing to flatten.</b> § D13: the
///         layout is already a chart decomposition with zero in-chart distortion, and the atlas is a
///         rectangle-packing problem, which is a solved one. Nothing here calls a charter, a
///         least-squares conformal solve or an ARAP iteration — the coordinates are the grid indices
///         times a cell size, exactly, and every quad in a chart is a square in it.
///     </para>
///     <para>
///         ⚠ <b>Super-charts are unions of patches on a shared lattice and are <i>not</i> required
///         to stay rectangular.</b> The requirement would look natural and would cost most of the
///         benefit — a cylinder's wall is four patches in an L or a ring and none of those unions is
///         a rectangle. It is unnecessary because doc 42's packer rasterizes island masks rather than
///         bounding boxes, so an L-shaped island packs as an L. That is the second thing giving up
///         ownership of the packer bought.
///     </para>
///     <para>
///         ⚠ <b>Gluing is exact and index-based, with no tolerance anywhere in it.</b> Two patches
///         share a side when their boundary chains are the <i>same output vertices</i> — which is
///         § D8's "the seam is an equality rather than a weld" read from the other end — and the
///         lattice transform between them is one of four quarter turns, solved from two corresponding
///         points and verified against a third. A merge that placed a patch a lattice cell out would
///         fold the chart, so it is checked rather than assumed.
///     </para>
///     <para>
///         ⚠ <b>Only quarter turns, never a reflection.</b> Both patches walk their boundary
///         anticlockwise with the patch on the left, so a legal gluing is orientation-preserving by
///         construction; admitting the four reflections as well would let a chart contain a mirrored
///         patch, whose triangles wind backwards in UV space and whose baked normals come out
///         inverted.
///     </para>
/// </remarks>
static class LayoutAtlas {
    /// <summary>The four quarter turns, as the lattice matrices <c>(a, b, c, d)</c>.</summary>
    /// <remarks>
    ///     <c>(I, J) = (a·i + b·j + tx, c·i + d·j + ty)</c>. Each has determinant one, which is what
    ///     makes the list the rotations and not the eight symmetries of the square.
    /// </remarks>
    static readonly (int A, int B, int C, int D)[] Turns = [
        (1, 0, 0, 1),
        (0, -1, 1, 0),
        (-1, 0, 0, -1),
        (0, 1, -1, 0)
    ];

    /// <summary>Where one patch sits on its super-chart's lattice.</summary>
    readonly record struct Placement(int A, int B, int C, int D, int X, int Y) {
        /// <summary>Where a local grid index lands globally.</summary>
        public (int I, int J) Apply(int i, int j) => ((A * i) + (B * j) + X, (C * i) + (D * j) + Y);
    }

    /// <summary>Two patches meeting along one side each.</summary>
    readonly record struct Seam(int One, int OneSide, int Two, int TwoSide, float Length, bool IsFeature);

    /// <summary>Builds the atlas and writes it into the output's texture-coordinate layer.</summary>
    /// <param name="output">The remeshed mesh. Its coordinates are written.</param>
    /// <param name="grids">Every patch that produced a grid.</param>
    /// <param name="settings">The resolution, the margin and the seam budget.</param>
    /// <returns>What the stage did.</returns>
    public static AtlasResult Build(EditMesh output, PatchGrid[] grids, AtlasSettings settings) {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(grids);
        ArgumentNullException.ThrowIfNull(settings);

        var warnings = new List<string>();

        if (grids.Length == 0 || output.FaceCount == 0) {
            return new(0, 0, 1f, ["The layout produced no patch grids, so there is no atlas to derive."]);
        }

        var locals = Locals(grids);
        var seams = Seams(output, grids);
        var total = TotalLength(output);
        var cells = Cells(output, grids);

        var charts = Merge(grids, locals, seams, cells, settings, total, out var placements);
        var islands = Normalise(Islands(output, grids, charts, placements, cells));

        if (islands.Count == 0) {
            return new(0, grids.Length, 1f, ["No chart covered a face, so no atlas was packed."]);
        }

        var pack = new PackSettings {
            Resolution = Math.Max(settings.Resolution, 1),
            Margin = Math.Max(settings.Margin, 0),
            Overflow = PackOverflow.Scale
        };

        IReadOnlyList<UvPlacement> packed;

        try {
            packed = UvUnwrap.Pack([.. islands.Select(entry => entry.Island)], pack);
        } catch (InvalidOperationException error) {
            return new(islands.Count, grids.Length, Ratio(seams, charts, total), [$"The packer refused: {error.Message}"]);
        } catch (ArgumentOutOfRangeException error) {
            return new(islands.Count, grids.Length, Ratio(seams, charts, total), [$"The packer refused: {error.Message}"]);
        }

        var coordinates = new Vector2[output.CornerCount];

        for (var index = 0; index < islands.Count; index++) {
            var (island, corners) = islands[index];
            var placement = packed[index];

            for (var at = 0; at < corners.Count; at++) {
                coordinates[corners[at]] = placement.Apply(island, island.Coordinates[at]);
            }
        }

        output.SetTexCoords(coordinates);

        return new(islands.Count, grids.Length, Ratio(seams, charts, total), warnings);
    }

    /// <summary>Every grid vertex's local index, per patch.</summary>
    static Dictionary<int, (int I, int J)>[] Locals(PatchGrid[] grids) {
        var locals = new Dictionary<int, (int I, int J)>[grids.Length];

        for (var patch = 0; patch < grids.Length; patch++) {
            var grid = grids[patch];
            var map = new Dictionary<int, (int I, int J)>((grid.Wide + 1) * (grid.Tall + 1));

            for (var i = 0; i <= grid.Wide; i++) {
                for (var j = 0; j <= grid.Tall; j++) {
                    // ⚠ TryAdd, not the indexer. A patch whose boundary walks one output vertex
                    // twice — a cone tip, a collapsed arc — would otherwise have its later visit
                    // overwrite the earlier one, and the gluing would then solve for a transform
                    // against a coordinate the neighbour never agreed to.
                    map.TryAdd(grid.Vertices[i][j], (i, j));
                }
            }

            locals[patch] = map;
        }

        return locals;
    }

    /// <summary>Which patches meet along which sides, and how long and how featured the meeting is.</summary>
    /// <remarks>
    ///     ⚠ <b>Matched on the output vertices themselves.</b> Two patches sharing a side hold the
    ///     same arc indices and therefore the same run of output positions — § D8 — so the test is
    ///     one sequence against another reversed, with no geometry in it at all. Matching by position
    ///     would be a weld with a tolerance, which is the thing the extraction went to trouble to
    ///     avoid needing.
    /// </remarks>
    static List<Seam> Seams(EditMesh output, PatchGrid[] grids) {
        var seams = new List<Seam>();
        var byEnds = new Dictionary<(int Low, int High), List<(int Patch, int Side)>>();

        for (var patch = 0; patch < grids.Length; patch++) {
            for (var side = 0; side < 4; side++) {
                var chain = grids[patch].Sides[side];

                if (chain.Length < 2) {
                    continue;
                }

                var key = (Math.Min(chain[0], chain[^1]), Math.Max(chain[0], chain[^1]));

                if (!byEnds.TryGetValue(key, out var bucket)) {
                    byEnds[key] = bucket = [];
                }

                bucket.Add((patch, side));
            }
        }

        // In key order, so the seam list — and therefore every merge decision that reads it — is a
        // function of the mesh rather than of a hash. § D14.
        foreach (var key in byEnds.Keys.OrderBy(entry => entry.Low).ThenBy(entry => entry.High)) {
            var bucket = byEnds[key];

            for (var one = 0; one < bucket.Count; one++) {
                for (var two = one + 1; two < bucket.Count; two++) {
                    var (p, sp) = bucket[one];
                    var (q, sq) = bucket[two];

                    if (p == q || !Matches(grids[p].Sides[sp], grids[q].Sides[sq])) {
                        continue;
                    }

                    seams.Add(
                        new(
                            p,
                            sp,
                            q,
                            sq,
                            Length(output, grids[p].Sides[sp]),
                            grids[p].IsFeature[sp] || grids[q].IsFeature[sq]
                        )
                    );
                }
            }
        }

        return seams;
    }

    /// <summary>Whether two boundary chains are the same run of output vertices, either way round.</summary>
    static bool Matches(int[] one, int[] two) {
        if (one.Length != two.Length) {
            return false;
        }

        var forward = true;
        var backward = true;

        for (var at = 0; at < one.Length; at++) {
            forward &= one[at] == two[at];
            backward &= one[at] == two[two.Length - 1 - at];
        }

        return forward || backward;
    }

    /// <summary>A chain's world length.</summary>
    static float Length(EditMesh output, int[] chain) {
        var total = 0f;

        for (var at = 0; at + 1 < chain.Length; at++) {
            total += Vector3.Distance(output.Positions[chain[at]], output.Positions[chain[at + 1]]);
        }

        return total;
    }

    /// <summary>Every output edge's length added up, which every seam ratio is a fraction of.</summary>
    static float TotalLength(EditMesh output) {
        var total = 0f;

        foreach (var edge in output.Edges) {
            total += Vector3.Distance(output.Positions[edge.A], output.Positions[edge.B]);
        }

        return total;
    }

    /// <summary>Each patch's mean quad edge, which is its chart's cell size in world units.</summary>
    /// <remarks>
    ///     ⚠ <b>Coordinates come out in world units, so texel density is uniform across the atlas
    ///     without anybody asking for it.</b> A cell size taken per patch and a coordinate that is
    ///     the grid index times it means a quad of side <c>L</c> occupies <c>L</c> of UV space
    ///     wherever it is — which is what <c>PackSettings.TexelDensity</c> exists to impose on
    ///     islands that did not arrive that way, and doc 42 § D9's reason for it is that non-uniform
    ///     density is invisible in the atlas and glaring in the game.
    /// </remarks>
    static float[] Cells(EditMesh output, PatchGrid[] grids) {
        var cells = new float[grids.Length];

        for (var patch = 0; patch < grids.Length; patch++) {
            var grid = grids[patch];
            var total = 0f;
            var count = 0;

            for (var i = 0; i <= grid.Wide; i++) {
                for (var j = 0; j <= grid.Tall; j++) {
                    if (i < grid.Wide) {
                        total += Vector3.Distance(
                            output.Positions[grid.Vertices[i][j]],
                            output.Positions[grid.Vertices[i + 1][j]]
                        );

                        count++;
                    }

                    if (j < grid.Tall) {
                        total += Vector3.Distance(
                            output.Positions[grid.Vertices[i][j]],
                            output.Positions[grid.Vertices[i][j + 1]]
                        );

                        count++;
                    }
                }
            }

            // A patch of no extent still needs a positive cell, or every one of its coordinates is
            // the same point and the packer is handed an island of zero size.
            cells[patch] = count > 0 && total > 0f ? total / count : 1f;
        }

        return cells;
    }

    /// <summary>§ D13's greedy merge: seeded from the largest, bounded by the seam budget.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Charts grow by absorbing single patches rather than by merging two charts.</b> Two
    ///         grown charts can only be joined by re-placing every patch of one of them onto the
    ///         other's lattice, and the pair that would have to be tried is every pair — whereas
    ///         growing reaches the same unions in one pass, which is what "greedy, seeded from the
    ///         largest" describes.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>"A union reachable only by joining two seeds is not reachable" is true of greedy
    ///         growth in general and has <i>no instances here</i>, which was measured rather than
    ///         argued.</b> Chart-into-chart merging was implemented — compose the gluing with the
    ///         inverse of the mover's own placement, carry every one of its patches across, test the
    ///         lot against the anchor's occupancy — and it changed nothing at all: box 14, sphere 16,
    ///         cylinder 39, stairs 25 and plate 12 charts at a budget of zero, before and after, and
    ///         the same five counts again at the default budget. It was not kept.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The reason is the shape of the seam graph, and it is worth knowing before anybody
    ///         tries this again.</b> Instrumented at a budget of zero, the number of seams whose two
    ///         patches ended in <i>different</i> charts is zero on every fixture — so growth had
    ///         already reached the connected components of the graph <see cref="Seams" /> builds, and
    ///         there was no pair left to join. A seed absorbs through <i>any</i> of its members, so
    ///         "two seeds" never traps a component in half. What limits the chart count is that
    ///         <see cref="Matches" /> pairs two patches only when a whole side equals a whole side:
    ///         patches meeting along a <i>partial</i> side never become a seam and so are never
    ///         adjacent to merge across. Joining charts is the wrong lever; admitting partial sides is
    ///         the right one, and it is a change to the gluing rather than to the merge order.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>At any non-zero budget the question does not even arise.</b> Growth stops the
    ///         instant <see cref="Ratio" /> is under the budget, so a second pass has nothing owed to
    ///         it by construction — it would run only in the configuration where it was measured to
    ///         gain nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The candidate order is where "seams prefer feature lines" actually lives.</b>
    ///         Merging across a <i>non</i>-feature side first is what leaves the feature sides
    ///         standing when the budget is met — the preference is expressed by which seams are spent
    ///         rather than by a constraint on which may be, so it degrades into "merge anyway" on a
    ///         model that is all features rather than refusing to merge at all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Then by cell-size agreement, and that is the distortion term.</b> A super-chart
    ///         has one cell size, so absorbing a patch whose quads are twice the size stretches it by
    ///         two. Ordering by the mismatch spends the cheap merges first, so a budget that stops
    ///         early stops having done the harmless ones.
    ///     </para>
    /// </remarks>
    static int[] Merge(
        PatchGrid[] grids,
        Dictionary<int, (int I, int J)>[] locals,
        List<Seam> seams,
        float[] cells,
        AtlasSettings settings,
        float total,
        out Placement[] placements
    ) {
        var charts = new int[grids.Length];
        Array.Fill(charts, -1);
        placements = new Placement[grids.Length];

        var adjacency = new List<Seam>[grids.Length];

        for (var patch = 0; patch < grids.Length; patch++) {
            adjacency[patch] = [];
        }

        foreach (var seam in seams) {
            adjacency[seam.One].Add(seam);
            adjacency[seam.Two].Add(seam);
        }

        // Largest first, ties on the patch index. § D14 again: "largest" is a float comparison and
        // two patches of identical size are the common case on a symmetric model.
        var order = Enumerable.Range(0, grids.Length)
            .OrderByDescending(patch => grids[patch].Wide * grids[patch].Tall)
            .ThenBy(patch => patch)
            .ToArray();

        var next = 0;
        var budget = Math.Clamp(settings.SeamBudget, 0f, 1f);

        foreach (var seed in order) {
            if (charts[seed] >= 0) {
                continue;
            }

            var chart = next++;
            charts[seed] = chart;
            placements[seed] = new(1, 0, 0, 1, 0, 0);

            var occupied = new HashSet<(int I, int J)>();

            Occupy(grids[seed], placements[seed], occupied);

            var members = new List<int> { seed };

            while (Ratio(seams, charts, total) > budget) {
                var best = Best(grids, locals, adjacency, charts, placements, cells, occupied, members, chart, settings);

                if (best is not { } chosen) {
                    break;
                }

                charts[chosen.Patch] = chart;
                placements[chosen.Patch] = chosen.Placement;
                members.Add(chosen.Patch);
                Occupy(grids[chosen.Patch], chosen.Placement, occupied);
            }
        }

        return charts;
    }

    /// <summary>The best patch the chart can absorb next, or nothing when it is closed.</summary>
    static (int Patch, Placement Placement)? Best(
        PatchGrid[] grids,
        Dictionary<int, (int I, int J)>[] locals,
        List<Seam>[] adjacency,
        int[] charts,
        Placement[] placements,
        float[] cells,
        HashSet<(int I, int J)> occupied,
        List<int> members,
        int chart,
        AtlasSettings settings
    ) {
        (int Patch, Placement Placement)? best = null;
        var rank = (Feature: 2, Mismatch: float.PositiveInfinity, Area: 0, Index: int.MaxValue);

        foreach (var inside in members) {
            foreach (var seam in adjacency[inside]) {
                var outside = seam.One == inside ? seam.Two : seam.One;

                // Already in a chart — this one or another — so it is not available to absorb.
                if (charts[outside] >= 0) {
                    continue;
                }

                var placement = Glue(grids, locals, placements[inside], inside, outside, seam);

                if (placement is not { } sited || Overlaps(grids[outside], sited, occupied)) {
                    continue;
                }

                var feature = settings.PreferFeatureSeams && seam.IsFeature ? 1 : 0;
                var mismatch = MathF.Abs(MathF.Log(cells[outside] / cells[inside]));
                var area = grids[outside].Wide * grids[outside].Tall;
                var candidate = (feature, mismatch, area, outside);

                if (Better(candidate, rank)) {
                    rank = candidate;
                    best = (outside, sited);
                }
            }
        }

        return best;
    }

    /// <summary>The candidate order: cheap seams first, then agreeing densities, then bulk.</summary>
    static bool Better(
        (int Feature, float Mismatch, int Area, int Index) candidate,
        (int Feature, float Mismatch, int Area, int Index) best
    ) {
        if (candidate.Feature != best.Feature) {
            return candidate.Feature < best.Feature;
        }

        if (candidate.Mismatch != best.Mismatch) {
            return candidate.Mismatch < best.Mismatch;
        }

        return candidate.Area != best.Area ? candidate.Area > best.Area : candidate.Index < best.Index;
    }

    /// <summary>Solves for where a neighbour sits on the lattice, or refuses.</summary>
    /// <remarks>
    ///     ⚠ <b>Two points solve it and a third checks it.</b> A shared side of two vertices — an
    ///     arc that quantized to one quad — is satisfied by two of the four turns at once, and taking
    ///     the first would place half the model's patches by coin toss. Where a third point exists it
    ///     settles the question; where it does not, the overlap test downstream is what catches the
    ///     wrong choice, and a refusal costs one seam rather than a folded chart.
    /// </remarks>
    static Placement? Glue(
        PatchGrid[] grids,
        Dictionary<int, (int I, int J)>[] locals,
        Placement anchor,
        int inside,
        int outside,
        Seam seam
    ) {
        var chain = seam.One == inside ? grids[inside].Sides[seam.OneSide] : grids[inside].Sides[seam.TwoSide];

        if (chain.Length < 2) {
            return null;
        }

        var here = locals[inside];
        var there = locals[outside];

        // Three sample points along the chain: both ends, and the middle where there is one.
        Span<int> samples = [chain[0], chain[^1], chain[chain.Length / 2]];
        var count = chain.Length > 2 ? 3 : 2;

        Span<(int I, int J)> global = stackalloc (int, int)[3];
        Span<(int I, int J)> local = stackalloc (int, int)[3];

        for (var at = 0; at < count; at++) {
            if (!here.TryGetValue(samples[at], out var mine) || !there.TryGetValue(samples[at], out var theirs)) {
                return null;
            }

            global[at] = anchor.Apply(mine.I, mine.J);
            local[at] = theirs;
        }

        foreach (var (a, b, c, d) in Turns) {
            var x = global[0].I - ((a * local[0].I) + (b * local[0].J));
            var y = global[0].J - ((c * local[0].I) + (d * local[0].J));
            var placement = new Placement(a, b, c, d, x, y);
            var agrees = true;

            for (var at = 1; at < count; at++) {
                agrees &= placement.Apply(local[at].I, local[at].J) == global[at];
            }

            if (agrees) {
                return placement;
            }
        }

        return null;
    }

    /// <summary>Whether a placed patch would land on cells the chart already holds.</summary>
    static bool Overlaps(PatchGrid grid, Placement placement, HashSet<(int I, int J)> occupied) {
        for (var i = 0; i < grid.Wide; i++) {
            for (var j = 0; j < grid.Tall; j++) {
                if (occupied.Contains(Cell(placement, i, j))) {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Records a placed patch's cells.</summary>
    static void Occupy(PatchGrid grid, Placement placement, HashSet<(int I, int J)> occupied) {
        for (var i = 0; i < grid.Wide; i++) {
            for (var j = 0; j < grid.Tall; j++) {
                occupied.Add(Cell(placement, i, j));
            }
        }
    }

    /// <summary>A quad's global cell, which is the lower corner of its transformed unit square.</summary>
    /// <remarks>
    ///     The quad spans local <c>[i, i+1] × [j, j+1]</c>; a quarter turn sends that square to
    ///     another unit square, whose lower corner is the componentwise minimum of the two opposite
    ///     transformed corners. Taking the transform of <c>(i, j)</c> alone would name a different
    ///     cell for each turn and the occupancy test would then miss half its overlaps.
    /// </remarks>
    static (int I, int J) Cell(Placement placement, int i, int j) {
        var (i0, j0) = placement.Apply(i, j);
        var (i1, j1) = placement.Apply(i + 1, j + 1);

        return (Math.Min(i0, i1), Math.Min(j0, j1));
    }

    /// <summary>How much seam is left, as a fraction of every output edge added up.</summary>
    static float Ratio(List<Seam> seams, int[] charts, float total) {
        if (total <= 0f) {
            return 0f;
        }

        var left = 0f;

        foreach (var seam in seams) {
            if (charts[seam.One] < 0 || charts[seam.Two] < 0 || charts[seam.One] != charts[seam.Two]) {
                left += seam.Length;
            }
        }

        return left / total;
    }

    /// <summary>Divides every island by the largest one, so the atlas does not depend on model size.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One divisor for every chart, which is what keeps the texel density uniform while
    ///         making the whole thing scale-free.</b> The coordinates arrive in world units — a quad
    ///         of side <c>L</c> occupies <c>L</c> of the plane — which is exactly the property doc 42
    ///         § D9 wants and is also a dependence on how big the model is. Dividing each island by
    ///         its <i>own</i> size would remove the dependence and the property together, and hand
    ///         the packer a set of islands all the same size.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Without this the packer's answer moves with the model, which looks exactly like
    ///         a packing bug and is not one.</b> The packer is told the atlas resolution and it
    ///         reasons in texels, so an island a thousand times larger is tried in a different order,
    ///         at a different rotation, against a different overflow rescale. Measured: the same box
    ///         at a thousandth scale placed one chart at <c>(0.004, 0.004)</c> and at unit scale
    ///         placed it at <c>(0.994, 0.004)</c> — the same layout, mirrored, because the islands
    ///         were sorted differently.
    ///     </para>
    /// </remarks>
    static List<(UvIsland Island, List<int> Corners)> Normalise(List<(UvIsland Island, List<int> Corners)> islands) {
        var extent = 0f;

        foreach (var (island, _) in islands) {
            extent = MathF.Max(extent, MathF.Max(island.Size.X, island.Size.Y));
        }

        if (extent <= 0f || !float.IsFinite(extent)) {
            return islands;
        }

        var divisor = 1f / extent;
        var scaled = new List<(UvIsland, List<int>)>(islands.Count);

        foreach (var (island, corners) in islands) {
            scaled.Add(
                (
                    island with {
                        Coordinates = [.. island.Coordinates.Select(coordinate => coordinate * divisor)],
                        Minimum = island.Minimum * divisor,
                        Maximum = island.Maximum * divisor,
                        Scale = divisor
                    },
                    corners
                )
            );
        }

        return scaled;
    }

    /// <summary>One island per chart: its coordinates, and the output corners they belong to.</summary>
    /// <remarks>
    ///     ⚠ <b>Per corner rather than per position, which is the whole reason a seam is free
    ///     here.</b> A vertex on a chart boundary stands in faces of two charts and carries two
    ///     different coordinates; <see cref="EditMesh" />'s corner layer holds both for nothing,
    ///     where a per-position layer would have to split the vertex and change the mesh.
    /// </remarks>
    static List<(UvIsland Island, List<int> Corners)> Islands(
        EditMesh output,
        PatchGrid[] grids,
        int[] charts,
        Placement[] placements,
        float[] cells
    ) {
        var count = charts.Length == 0 ? 0 : charts.Max() + 1;
        var islands = new List<(UvIsland, List<int>)>(count);

        for (var chart = 0; chart < count; chart++) {
            var members = new List<int>();

            for (var patch = 0; patch < grids.Length; patch++) {
                if (charts[patch] == chart) {
                    members.Add(patch);
                }
            }

            if (members.Count == 0) {
                continue;
            }

            // One cell size for the chart, the mean of its patches' own weighted by their quads —
            // which is the arithmetic form of "the chart is as dense as most of it is".
            var weight = 0f;
            var scale = 0f;

            foreach (var patch in members) {
                var quads = grids[patch].Wide * grids[patch].Tall;

                scale += cells[patch] * quads;
                weight += quads;
            }

            var cell = weight > 0f ? scale / weight : 1f;

            var coordinates = new List<Vector2>();
            var corners = new List<int>();
            var minimum = new Vector2(float.MaxValue);
            var maximum = new Vector2(float.MinValue);

            foreach (var patch in members) {
                var grid = grids[patch];
                var placement = placements[patch];

                for (var i = 0; i < grid.Wide; i++) {
                    for (var j = 0; j < grid.Tall; j++) {
                        var face = grid.FirstFace + (i * grid.Tall) + j;
                        var entry = output.Faces[face];

                        Span<(int I, int J)> loop = [(i, j), (i + 1, j), (i + 1, j + 1), (i, j + 1)];

                        // A quad is two triangles, fanned from its first corner. The packer's masks
                        // are rasterized from triangles, so an island is a triangle list.
                        Span<int> fan = [0, 1, 2, 0, 2, 3];

                        foreach (var slot in fan) {
                            var (li, lj) = loop[slot];
                            var (gi, gj) = placement.Apply(li, lj);
                            var uv = new Vector2(gi * cell, gj * cell);

                            coordinates.Add(uv);
                            corners.Add(entry.Start + slot);
                            minimum = Vector2.Min(minimum, uv);
                            maximum = Vector2.Max(maximum, uv);
                        }
                    }
                }
            }

            if (corners.Count == 0) {
                continue;
            }

            islands.Add((new(coordinates, corners, minimum, maximum, 1f), corners));
        }

        return islands;
    }
}
