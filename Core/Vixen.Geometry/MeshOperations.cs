// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry;

/// <summary>One face, as the loop and the two per-face numbers a rebuild needs to put it back.</summary>
/// <param name="Loop">Its corners, as position indices, in winding order.</param>
/// <param name="Group">Which group it is in.</param>
/// <param name="Smooth">Which smoothing group it is in, or zero for a face with hard edges.</param>
/// <remarks>
///     <para>
///         <b>What an operation assembles before it writes anything.</b> Every verb here rebuilds the
///         face table wholesale rather than editing it in place — see
///         <see cref="MeshOperations.Replace" /> for why that is the honest way round.
///     </para>
///     <para>
///         ⚠ <b>Both numbers travel, and forgetting the second is silent.</b> A verb that carried the
///         group and dropped the smoothing would produce a mesh that is materialled correctly and
///         faceted — which looks like a shading bug in the renderer rather than like the extrude that
///         caused it. The default is there so that a caller <i>making</i> a face need not say, and
///         every caller <i>copying</i> one is passing what it copied.
///     </para>
/// </remarks>
public readonly record struct MeshLoop(int[] Loop, int Group, int Smooth = 0);

/// <summary>Doc 24's geometry verbs, as functions over an <see cref="EditMesh" />.</summary>
/// <remarks>
///     <para>
///         <b>Doc 24's P3.</b> Extrude, inset, bevel, loop cut, bridge, weld, dissolve, subdivide, fill
///         and flip — the inventory's Geometry table, as arithmetic rather than as gestures. The
///         editor's half is which faces are selected and what a drag's distance is; the part that can
///         be <i>wrong</i> is all here, where a cube and an assertion can reach it.
///     </para>
///     <para>
///         ⚠ <b>Every one of them rebuilds the face table and leaves the positions alone.</b> A
///         position index is what a selection holds, what an undo entry records and what a drag in
///         flight is writing to; renumbering the positions under a running gesture is the defect doc
///         24's D3 is written to prevent. Faces are renumbered freely, which is exactly why a topology
///         change drops the element selection — see <c>MeshEdit.Reconcile</c>.
///     </para>
///     <para>
///         ⚠ <b>Positions no face uses are left behind rather than compacted.</b>
///         <see cref="EditMesh.Validate" /> reports them as orphans and <see cref="Compact" /> is what
///         removes them, run by the caller at a moment when nothing holds an index — which is after a
///         gesture rather than inside one.
///     </para>
///     <para>
///         ⚠ <b>Each returns the faces it made, and none of them touches a selection.</b> A verb that
///         moved the selection to its own result could not be composed: "extrude, then inset the
///         result" and "extrude, then inset what I had" are both things a designer does, and only the
///         caller knows which.
///     </para>
/// </remarks>
public static class MeshOperations {
    /// <summary>How near two positions must be for a weld to merge them, as a fraction of the bounds.</summary>
    /// <remarks>Far coarser than <see cref="EditMesh.DefaultWeldTolerance" />, because this is the verb
    ///     a designer runs rather than the noise filter a generator's output goes through.</remarks>
    public const float DefaultMergeTolerance = 1e-3f;

    // ── Extrude, inset, offset ──────────────────────────────────────────────────────────────────

    /// <summary>Pulls a set of faces out along their normal, walling in the gap behind them.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="faces">Which faces. Duplicates are ignored.</param>
    /// <param name="distance">How far, along the region's area-weighted normal.</param>
    /// <param name="individually">Whether each face is extruded on its own rather than as one region.</param>
    /// <returns>The faces that moved, in the renumbered table — which is what stays selected.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 24's P3 says extrude first and alone until it is right, because every other verb
    ///         is judged against how that one feels.</b> The construction: every position the region
    ///         covers is duplicated, the region's faces are rebuilt over the duplicates, each rim edge
    ///         gets a quad joining the old pair to the new pair, and the duplicates are then moved.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The rim is the boundary of the <i>region</i>, which is the whole difference
    ///         between a region extrude and an individual one.</b> An edge with a selected face on one
    ///         side and nothing selected on the other is a rim edge and gets a wall; an edge between
    ///         two selected faces is interior and its corners simply move. Extruding four faces as a
    ///         region gives one box; individually gives four boxes with walls between them. Blender
    ///         calls the second "extrude individual", both are wanted, and confusing them is the
    ///         commonest complaint about implementations that have only one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The <i>copies</i> move and the originals stay.</b> The other way round leaves the
    ///         walls attached to geometry that has moved and the rest of the mesh attached to geometry
    ///         that has not — a hole where the extrude started, which is the classic way this
    ///         operation is got wrong.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A distance of zero still builds the walls.</b> Not a degenerate case to guard
    ///         against: it is exactly what a drag's first frame is, and a verb that did nothing until
    ///         the pointer had moved would have walls appearing a frame late and an undo entry that
    ///         does not match what the designer saw.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<int> Extrude(
        EditMesh mesh,
        IReadOnlyCollection<int> faces,
        float distance,
        bool individually = false
    ) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(faces);

        if (individually) {
            return Separately(mesh, faces, (each, one) => Extrude(each, one, distance));
        }

        var region = Region(mesh, faces);

        if (region.Count == 0) {
            return [];
        }

        var along = Facing(mesh, region) * distance;

        return Raise(mesh, region, position => position + along);
    }

    /// <summary>Pulls a set of faces along a direction rather than along their own normal.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="faces">Which faces.</param>
    /// <param name="offset">How far and which way, in the mesh's own space.</param>
    /// <returns>The faces that moved.</returns>
    /// <remarks>Doc 24's inventory asks for extrude "along the normal and along an axis" — the same
    ///     verb with the direction supplied, which is what a gizmo drag hands it.</remarks>
    public static IReadOnlyList<int> ExtrudeAlong(EditMesh mesh, IReadOnlyCollection<int> faces, Vector3 offset) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(faces);

        var region = Region(mesh, faces);

        return region.Count == 0 ? [] : Raise(mesh, region, position => position + offset);
    }

    /// <summary>Shrinks a set of faces towards their own centre, walling in the ring left behind.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="faces">Which faces.</param>
    /// <param name="amount">How far in, in world units.</param>
    /// <param name="individually">Whether each face is inset on its own rather than as one region.</param>
    /// <returns>The inner faces, which is what stays selected.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>The same construction as an extrude, with the corners moved sideways instead of
    ///         out.</b> Doc 24's inventory gives inset its own key and says per-face and as-a-region
    ///         are different answers and both are wanted — pressing <c>I</c> twice is how you say the
    ///         second.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Towards the region's centre <i>in the region's plane</i>, not towards each face's
    ///         own middle.</b> The component along the normal is dropped, or an inset of a curved
    ///         region pulls its corners off the surface.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An amount past the middle collapses the ring rather than turning it inside
    ///         out.</b> A designer dragging an inset too far is asking for a point, not for a bow tie —
    ///         and a bow tie is geometry no triangulation is right for.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<int> Inset(
        EditMesh mesh,
        IReadOnlyCollection<int> faces,
        float amount,
        bool individually = false
    ) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(faces);

        if (individually) {
            return Separately(mesh, faces, (each, one) => Inset(each, one, amount));
        }

        var region = Region(mesh, faces);

        if (region.Count == 0) {
            return [];
        }

        var centre = Middle(mesh, region);
        var normal = Facing(mesh, region);

        return Raise(mesh, region, position => Towards(position, centre, normal, amount));
    }

    // ── Bevel ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Replaces each edge with a strip of faces, cutting the corner off.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="edges">Which edges.</param>
    /// <param name="width">How far back from the edge each side is pulled, in world units.</param>
    /// <param name="segments">How many faces across the bevel. One is a chamfer.</param>
    /// <param name="unresolved">How many corners the bevel could not resolve.</param>
    /// <returns>The faces the bevel made.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Doc 24 says this is the verb that looks small and is not, and it is right.</b> A
    ///         bevel with segments on an edge that meets three other bevelled edges at a vertex is a
    ///         miniature research problem: the corner needs a patch whose shape depends on all four
    ///         bevels at once. The honest first version bevels edges <i>independently</i> and reports
    ///         where it could not resolve a corner, which is what <paramref name="unresolved" /> is —
    ///         rather than producing a self-intersecting corner silently.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Only a manifold edge can be bevelled, and a non-manifold one is counted rather than
    ///         refused.</b> "Cut the corner between these two faces" has no meaning where three faces
    ///         meet, and a mesh under construction has those constantly — see <see cref="MeshReport" />,
    ///         which exists to make exactly this answerable.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The width is clamped to a fraction of the shortest edge at each corner.</b> A
    ///         bevel wider than the geometry it is cutting turns the face inside out, and a designer
    ///         dragging one is far more often overshooting than asking for that.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<int> Bevel(
        EditMesh mesh,
        IReadOnlyCollection<int> edges,
        float width,
        int segments,
        out int unresolved
    ) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(edges);

        unresolved = 0;
        segments = Math.Max(segments, 1);

        var chosen = new List<int>();

        foreach (var edge in edges.Distinct().Order()) {
            if ((uint) edge >= (uint) mesh.Edges.Count) {
                continue;
            }

            if (mesh.FacesOf(edge).Length != 2) {
                unresolved++;
                continue;
            }

            chosen.Add(edge);
        }

        if (chosen.Count == 0) {
            return [];
        }

        // ⚠ Every corner where two chosen edges meet is a corner this version cannot patch. Counted
        // before anything is built, so the caller can say "seven corners were left square" rather than
        // the designer finding them.
        var touched = new Dictionary<int, int>();

        foreach (var edge in chosen) {
            Touch(mesh.Edges[edge].A);
            Touch(mesh.Edges[edge].B);
        }

        foreach (var (_, count) in touched) {
            if (count > 1) {
                unresolved++;
            }
        }

        var table = Table(mesh);
        List<int> made = [];
        var group = Highest(mesh) + 1;

        foreach (var edge in chosen) {
            var (a, b) = mesh.Edges[edge];
            var pair = mesh.FacesOf(edge);

            // The two faces pulled back from the edge, each towards its own far side, and the strip
            // between the two new pairs of positions.
            var first = Pull(mesh, table, pair[0], a, b, width);
            var second = Pull(mesh, table, pair[1], a, b, width);

            for (var step = 0; step < segments; step++) {
                var low = Blend(mesh, first.A, second.A, step / (float) segments);
                var high = Blend(mesh, first.A, second.A, (step + 1) / (float) segments);
                var lowB = Blend(mesh, first.B, second.B, step / (float) segments);
                var highB = Blend(mesh, first.B, second.B, (step + 1) / (float) segments);

                made.Add(table.Count);
                table.Add(new([low, lowB, highB, high], group));
            }

            group++;
        }

        Replace(mesh, table);
        return made;

        void Touch(int position) => touched[position] = touched.GetValueOrDefault(position) + 1;
    }

    // ── Loop cut and subdivide ──────────────────────────────────────────────────────────────────

    /// <summary>Cuts a ring of quads in half, across the ring the edge is part of.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="edge">An edge of the ring to cut across.</param>
    /// <param name="cuts">How many cuts. One puts a single loop half way along.</param>
    /// <param name="slide">Where the cut sits along the ring, from 0 to 1. A half is the middle.</param>
    /// <returns>The faces the cut produced.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 24's <c>Ctrl+R</c>, and the ring is what decides where it goes.</b>
    ///         <see cref="MeshTopology.EdgeRing" /> answers which edges the cut crosses; each of those
    ///         edges gets a new position at the slide parameter, and each quad the ring runs through is
    ///         replaced by two.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Only through quads, which is what a ring is defined over.</b> A ring that reached
    ///         a triangle stops there, so a loop cut through a mesh with a triangle in the strip cuts
    ///         up to it and no further — visible, and better than a cut that wandered.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The slide is a parameter rather than a second operation.</b> Doc 24's inventory
    ///         says "slide before committing", and a cut that always landed in the middle and was then
    ///         moved would be two topology changes in the undo history for one gesture.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<int> LoopCut(EditMesh mesh, int edge, int cuts = 1, float slide = 0.5f) {
        ArgumentNullException.ThrowIfNull(mesh);

        cuts = Math.Max(cuts, 1);
        slide = Math.Clamp(slide, 0f, 1f);

        List<int> ring = [];

        MeshTopology.EdgeRing(mesh, edge, ring);

        if (ring.Count == 0) {
            return [];
        }

        // One new position per cut per crossed edge, keyed by the edge so the two faces either side of
        // it agree about which position the cut runs through.
        var inserted = new Dictionary<(int Edge, int Cut), int>();

        foreach (var crossed in ring) {
            var (a, b) = mesh.Edges[crossed];

            for (var cut = 0; cut < cuts; cut++) {
                var along = cuts == 1 ? slide : (cut + 1) / (float) (cuts + 1);

                inserted[(crossed, cut)] = mesh.AddPosition(
                    Vector3.Lerp(mesh.Positions[a], mesh.Positions[b], along)
                );
            }
        }

        var crossing = new HashSet<int>(ring);
        var table = new List<MeshLoop>();
        List<int> made = [];

        for (var face = 0; face < mesh.FaceCount; face++) {
            var loop = mesh.CornersOf(face).ToArray();

            if (loop.Length != 4 || !Split(mesh, loop, crossing, out var first, out var second)) {
                table.Add(new(loop, mesh.Faces[face].Group, mesh.Faces[face].Smoothing));
                continue;
            }

            // The quad becomes cuts + 1 quads, walking from the first crossed edge to the second.
            var group = mesh.Faces[face].Group;
            var low = first.Edge;
            var high = second.Edge;

            var previousLow = first.From;
            var previousHigh = second.To;

            for (var cut = 0; cut < cuts; cut++) {
                var atLow = inserted[(low, Ordered(mesh, low, first.From, cut, cuts))];
                var atHigh = inserted[(high, Ordered(mesh, high, second.To, cut, cuts))];

                made.Add(table.Count);
                table.Add(new([previousLow, atLow, atHigh, previousHigh], group));

                previousLow = atLow;
                previousHigh = atHigh;
            }

            made.Add(table.Count);
            table.Add(new([previousLow, first.To, second.From, previousHigh], group));
        }

        Replace(mesh, table);
        return made;
    }

    /// <summary>Splits each face into one face per corner, round a new middle.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="faces">Which faces.</param>
    /// <param name="count">How many times to do it.</param>
    /// <returns>The faces the subdivision produced.</returns>
    /// <remarks>
    ///     ⚠ <b>Catmull-Clark's topology without its smoothing, which is what a block-out wants.</b>
    ///     Each face becomes a quad per corner, meeting at a new middle position, with new positions at
    ///     each edge's midpoint. Moving any of them towards a limit surface is what makes a subdivision
    ///     surface, and doc 24's scope table puts those on the other side of the line.
    /// </remarks>
    public static IReadOnlyList<int> Subdivide(EditMesh mesh, IReadOnlyCollection<int> faces, int count = 1) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(faces);

        var chosen = Region(mesh, faces);
        List<int> made = [];

        for (var pass = 0; pass < Math.Max(count, 1) && chosen.Count > 0; pass++) {
            var selected = new HashSet<int>(chosen);
            var midpoints = new Dictionary<int, int>();
            var table = new List<MeshLoop>();

            made = [];

            // ⚠ The midpoints are made before anything is rebuilt, so a neighbour that is *not* being
            // subdivided can be given the same ones. Without that the shared edge is split on one side
            // and whole on the other — a T-junction, which `Validate` reports as two boundary edges
            // and which draws as a crack in the surface the first time anything moves.
            foreach (var face in chosen) {
                var loop = mesh.CornersOf(face).ToArray();

                for (var corner = 0; corner < loop.Length; corner++) {
                    Midpoint(mesh, midpoints, loop[corner], loop[(corner + 1) % loop.Length]);
                }
            }

            for (var face = 0; face < mesh.FaceCount; face++) {
                var loop = mesh.CornersOf(face).ToArray();

                if (!selected.Contains(face)) {
                    table.Add(new(Stitched(mesh, loop, midpoints), mesh.Faces[face].Group, mesh.Faces[face].Smoothing));
                    continue;
                }

                var group = mesh.Faces[face].Group;
                var centre = mesh.AddPosition(Average(mesh, loop));

                for (var corner = 0; corner < loop.Length; corner++) {
                    var previous = loop[(corner + loop.Length - 1) % loop.Length];
                    var next = loop[(corner + 1) % loop.Length];

                    made.Add(table.Count);
                    table.Add(
                        new(
                            [Midpoint(mesh, midpoints, previous, loop[corner]), loop[corner], Midpoint(mesh, midpoints, loop[corner], next), centre],
                            group
                        )
                    );
                }
            }

            Replace(mesh, table);
            chosen = [.. made];
        }

        return made;
    }

    // ── Bridge, fill, flip ──────────────────────────────────────────────────────────────────────

    /// <summary>Joins two faces with a tube, removing both.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="first">One face.</param>
    /// <param name="second">The other. It must have the same number of corners.</param>
    /// <returns>The faces the bridge made, or nothing when the two do not correspond.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 24's <c>Ctrl+E</c> — two faces or two edge loops.</b> A quad per pair of
    ///         corresponding corners, and the two original faces go, which is what turns two holes into
    ///         a corridor.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The corners are paired by which is nearest, and the second loop is walked
    ///         backwards.</b> Two faces looking at each other wind in opposite directions, so pairing
    ///         them in order gives a tube with a half twist in it — the defect that looks like the
    ///         bridge working until somebody looks at it from the side.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Faces with different corner counts are declined rather than fitted.</b> Bridging a
    ///         triangle to a hexagon means deciding which of the hexagon's corners share a pair, and
    ///         every rule for that is wrong for some pair of shapes. Subdividing until they match is
    ///         the designer's call.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<int> Bridge(EditMesh mesh, int first, int second) {
        ArgumentNullException.ThrowIfNull(mesh);

        if ((uint) first >= (uint) mesh.FaceCount || (uint) second >= (uint) mesh.FaceCount || first == second) {
            return [];
        }

        var from = mesh.CornersOf(first).ToArray();
        var to = mesh.CornersOf(second).ToArray();

        if (from.Length != to.Length) {
            return [];
        }

        // Reversed, because two faces looking at each other wind opposite ways; then rotated so that
        // the nearest pair of corners line up, which is what stops the tube twisting.
        Array.Reverse(to);

        var offset = Nearest(mesh, from, to);
        var table = new List<MeshLoop>();

        for (var face = 0; face < mesh.FaceCount; face++) {
            if (face != first && face != second) {
                table.Add(new(mesh.CornersOf(face).ToArray(), mesh.Faces[face].Group, mesh.Faces[face].Smoothing));
            }
        }

        var group = Highest(mesh) + 1;
        List<int> made = [];

        for (var corner = 0; corner < from.Length; corner++) {
            var next = (corner + 1) % from.Length;

            made.Add(table.Count);
            table.Add(
                new(
                    [
                        from[corner],
                        from[next],
                        to[(next + offset) % to.Length],
                        to[(corner + offset) % to.Length]
                    ],
                    group
                )
            );
        }

        Replace(mesh, table);
        return made;
    }

    /// <summary>Puts a face across a hole.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="edge">A boundary edge of the hole.</param>
    /// <returns>The face, or <c>-1</c> when the rim is not a closed loop.</returns>
    /// <remarks>
    ///     <b>Doc 24's <c>F</c>, and "make face from selection" is the same code with the loop supplied
    ///     rather than walked.</b> The rim comes from <see cref="MeshTopology.BoundaryLoop" />, which
    ///     declines a rim that pinches — where three boundary edges meet, "round" has more than one
    ///     meaning and choosing one is guessing at what the designer built.
    /// </remarks>
    public static int FillHole(EditMesh mesh, int edge) {
        ArgumentNullException.ThrowIfNull(mesh);

        List<int> rim = [];

        if (!MeshTopology.BoundaryLoop(mesh, edge, rim) || rim.Count < 3) {
            return -1;
        }

        // ⚠ Wound against the rim rather than along it. The single face on each boundary edge already
        // walks the rim one way, so a cap wound the same way faces into the mesh — which is a hole
        // that looks filled from outside and is inside out from within.
        rim.Reverse();

        return mesh.AddFace([.. rim], Highest(mesh) + 1);
    }

    /// <summary>Turns a set of faces inside out.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="faces">Which faces, or every face when the set is empty.</param>
    /// <returns>How many were flipped.</returns>
    /// <remarks>What <see cref="MeshReport.Reversed" /> is the diagnosis for: the kernel reports which
    ///     way each face walks its edges and refuses to guess which of two neighbours is wrong.</remarks>
    public static int Flip(EditMesh mesh, IReadOnlyCollection<int>? faces = null) {
        ArgumentNullException.ThrowIfNull(mesh);

        var chosen = faces is null or { Count: 0 }
            ? [.. Enumerable.Range(0, mesh.FaceCount)]
            : Region(mesh, faces);

        if (chosen.Count == 0) {
            return 0;
        }

        var selected = new HashSet<int>(chosen);
        var table = new List<MeshLoop>();

        for (var face = 0; face < mesh.FaceCount; face++) {
            var loop = mesh.CornersOf(face).ToArray();

            if (selected.Contains(face)) {
                Array.Reverse(loop);
            }

            table.Add(new(loop, mesh.Faces[face].Group, mesh.Faces[face].Smoothing));
        }

        Replace(mesh, table);
        return chosen.Count;
    }

    // ── Weld, dissolve, delete ──────────────────────────────────────────────────────────────────

    /// <summary>Merges a set of positions into one.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="positions">Which positions.</param>
    /// <param name="at">Where the merged position goes, or null for their average.</param>
    /// <returns>How many positions were merged away.</returns>
    /// <remarks>
    ///     <b>Doc 24's <c>M ⋯</c>: to centre, to last, to the cursor.</b> All three are this with a
    ///     different <paramref name="at" />; "by distance" is <see cref="MergeByDistance" />, which is
    ///     the one that has to search.
    ///     <para>
    ///         ⚠ <b>Faces that collapse to fewer than three distinct corners are removed.</b> A
    ///         triangle whose two corners were welded together is a line, and a face table with one in
    ///         it is a table every operation afterwards has to test for.
    ///     </para>
    /// </remarks>
    public static int Weld(EditMesh mesh, IReadOnlyCollection<int> positions, Vector3? at = null) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(positions);

        var chosen = positions.Where(position => (uint) position < (uint) mesh.PositionCount).Distinct().Order().ToArray();

        if (chosen.Length < 2) {
            return 0;
        }

        var keep = chosen[0];
        var remap = new Dictionary<int, int>();

        foreach (var position in chosen) {
            remap[position] = keep;
        }

        mesh.MovePosition(keep, at ?? Average(mesh, chosen));

        Remap(mesh, remap);
        return chosen.Length - 1;
    }

    /// <summary>Merges every pair of positions nearer than a distance.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="distance">How near counts, in world units.</param>
    /// <returns>How many positions were merged away.</returns>
    /// <remarks>
    ///     ⚠ <b>An absolute distance, unlike the tolerances everywhere else in this kernel.</b> This
    ///     is the verb with a settings popover: a designer typing "one centimetre" means a centimetre,
    ///     and scaling it by the mesh's bounds would make the same number mean different things in two
    ///     rooms of the same level.
    /// </remarks>
    public static int MergeByDistance(EditMesh mesh, float distance) {
        ArgumentNullException.ThrowIfNull(mesh);

        if (distance <= 0f) {
            return 0;
        }

        var remap = new Dictionary<int, int>();
        var squared = distance * distance;

        for (var position = 0; position < mesh.PositionCount; position++) {
            if (remap.ContainsKey(position)) {
                continue;
            }

            for (var other = position + 1; other < mesh.PositionCount; other++) {
                if (!remap.ContainsKey(other)
                    && Vector3.DistanceSquared(mesh.Positions[position], mesh.Positions[other]) <= squared) {
                    remap[other] = position;
                }
            }
        }

        if (remap.Count == 0) {
            return 0;
        }

        Remap(mesh, remap);
        return remap.Count;
    }

    /// <summary>Removes an edge and keeps the surface, merging the faces either side of it.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="edges">Which edges.</param>
    /// <returns>How many were dissolved.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 24's distinction, in one sentence: dissolve removes an element and keeps the
    ///         surface, delete makes a hole.</b> Dissolving the diagonal of a triangulated quad is how
    ///         a block-out made of triangles becomes one made of quads, which is what makes loops and
    ///         rings work on it afterwards.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Only a manifold edge whose two faces are distinct can be dissolved.</b> A boundary
    ///         edge has no second face to merge with and a non-manifold one has too many; both are
    ///         skipped rather than refused, because a designer selecting a loop and pressing dissolve
    ///         has selected some of each.
    ///     </para>
    /// </remarks>
    public static int Dissolve(EditMesh mesh, IReadOnlyCollection<int> edges) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(edges);

        var dissolved = 0;

        // One at a time, re-reading the edge table between each: merging two faces renumbers it, so a
        // batch resolved up front would dissolve whichever edges happened to inherit the numbers.
        foreach (var pair in edges.Select(edge => Pair(mesh, edge)).Where(pair => pair is not null).ToArray()) {
            var edge = mesh.EdgeBetween(pair!.Value.A, pair.Value.B);

            if (edge < 0 || mesh.FacesOf(edge).Length != 2) {
                continue;
            }

            var touching = mesh.FacesOf(edge);
            var merged = Merge(mesh, touching[0], touching[1], pair.Value.A, pair.Value.B);

            if (merged is null) {
                continue;
            }

            var table = new List<MeshLoop>();

            for (var face = 0; face < mesh.FaceCount; face++) {
                if (face != touching[0] && face != touching[1]) {
                    table.Add(new(mesh.CornersOf(face).ToArray(), mesh.Faces[face].Group, mesh.Faces[face].Smoothing));
                }
            }

            table.Add(new(merged, mesh.Faces[touching[0]].Group, mesh.Faces[touching[0]].Smoothing));

            Replace(mesh, table);
            dissolved++;
        }

        return dissolved;
    }

    /// <summary>Removes faces, leaving a hole.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="faces">Which faces.</param>
    /// <returns>How many were removed.</returns>
    public static int Delete(EditMesh mesh, IReadOnlyCollection<int> faces) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(faces);

        var chosen = Region(mesh, faces);

        if (chosen.Count == 0) {
            return 0;
        }

        var gone = new HashSet<int>(chosen);
        var table = new List<MeshLoop>();

        for (var face = 0; face < mesh.FaceCount; face++) {
            if (!gone.Contains(face)) {
                table.Add(new(mesh.CornersOf(face).ToArray(), mesh.Faces[face].Group, mesh.Faces[face].Smoothing));
            }
        }

        Replace(mesh, table);
        return chosen.Count;
    }

    /// <summary>Inserts every position that lies on an edge into the face that owns it.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="tolerance">How far off an edge a position may be and still count as on it.</param>
    /// <returns>How many corners were inserted.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>The general form of the fix <see cref="Subdivide" /> needed, and what a boolean needs
    ///         everywhere.</b> A T-junction is an edge split on one side and whole on the other: the
    ///         two faces no longer share an edge, <see cref="EditMesh.Validate" /> reports both halves
    ///         as boundary edges, and the surface draws with a crack down it that opens and closes as
    ///         the camera moves. Nothing about the geometry is wrong — the vertex is exactly on the
    ///         line — which is why it survives every check that is not this one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A cut produces them by construction rather than by accident.</b> Splitting a solid
    ///         with a plane cuts every face the plane crosses and no face it merely touches — so the
    ///         face beside a cut face keeps an edge whose middle is now somebody else's corner. Every
    ///         BSP boolean has this and most of them ignore it, because a renderer that only ever sees
    ///         triangles mostly gets away with it. An editable mesh does not: the next extrude walks an
    ///         edge table that says two faces are strangers.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Distance to the segment rather than collinearity, and it is a real tolerance.</b>
    ///         The vertex being inserted was computed by an intersection, so it is on the line to
    ///         within a rounding rather than exactly — this is the one question in a boolean that is
    ///         honestly approximate, and it is asked about positions that are already welded to each
    ///         other rather than about which side of a plane something is on.
    ///     </para>
    /// </remarks>
    public static int Stitch(EditMesh mesh, float tolerance = DefaultStitchTolerance) {
        ArgumentNullException.ThrowIfNull(mesh);

        if (mesh.IsEmpty) {
            return 0;
        }

        var positions = mesh.PositionCount;
        var inserted = 0;

        List<MeshLoop> table = [];
        List<int> loop = [];
        List<(float At, int Position)> found = [];

        for (var face = 0; face < mesh.FaceCount; face++) {
            var entry = mesh.Faces[face];

            loop.Clear();

            for (var corner = 0; corner < entry.Count; corner++) {
                var from = mesh.Corners[entry.Start + corner];
                var to = mesh.Corners[entry.Start + ((corner + 1) % entry.Count)];

                loop.Add(from);
                found.Clear();

                var start = mesh.Positions[from];
                var end = mesh.Positions[to];
                var along = end - start;
                var length = along.LengthSquared();

                if (length <= 0f) {
                    continue;
                }

                var low = Vector3.Min(start, end) - new Vector3(tolerance);
                var high = Vector3.Max(start, end) + new Vector3(tolerance);

                for (var position = 0; position < positions; position++) {
                    if (position == from || position == to) {
                        continue;
                    }

                    var point = mesh.Positions[position];

                    if (point.X < low.X || point.Y < low.Y || point.Z < low.Z
                        || point.X > high.X || point.Y > high.Y || point.Z > high.Z) {
                        continue;
                    }

                    var at = Vector3.Dot(point - start, along) / length;

                    if (at <= 0f || at >= 1f) {
                        continue;
                    }

                    if ((point - (start + (along * at))).LengthSquared() <= tolerance * tolerance) {
                        found.Add((at, position));
                    }
                }

                if (found.Count == 0) {
                    continue;
                }

                found.Sort((left, right) => left.At.CompareTo(right.At));

                foreach (var (_, position) in found) {
                    if (loop[^1] == position) {
                        continue;
                    }

                    loop.Add(position);
                    inserted++;
                }
            }

            table.Add(new([.. loop], entry.Group, entry.Smoothing));
        }

        if (inserted > 0) {
            Replace(mesh, table);
        }

        return inserted;
    }

    /// <summary>How far off an edge a position may be and still be taken to be on it.</summary>
    /// <remarks>A hundredth of a millimetre, which is well below anything a designer places and well
    ///     above the error an intersection of two planes a hundred metres apart accumulates.</remarks>
    public const float DefaultStitchTolerance = 1e-5f;

    /// <summary>Removes positions no face uses, and says where the rest went.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <returns>The new index of each old position, or <c>-1</c> for one that was dropped.</returns>
    /// <remarks>
    ///     ⚠ <b>Run between gestures rather than inside one, and the return value is why it is not
    ///     run automatically.</b> Every position index a caller holds — a selection, an undo entry, a
    ///     drag in flight — has to be put through the map, and the only moment at which nothing holds
    ///     one is between operations. Every verb here leaves orphans deliberately for that reason.
    /// </remarks>
    public static int[] Compact(EditMesh mesh) {
        ArgumentNullException.ThrowIfNull(mesh);

        var used = new bool[mesh.PositionCount];

        foreach (var corner in mesh.Corners) {
            used[corner] = true;
        }

        var map = new int[mesh.PositionCount];
        List<Vector3> kept = [];

        for (var position = 0; position < mesh.PositionCount; position++) {
            if (used[position]) {
                map[position] = kept.Count;
                kept.Add(mesh.Positions[position]);
            } else {
                map[position] = -1;
            }
        }

        if (kept.Count == mesh.PositionCount) {
            return map;
        }

        var table = new List<MeshLoop>();

        for (var face = 0; face < mesh.FaceCount; face++) {
            var loop = mesh.CornersOf(face).ToArray();

            for (var corner = 0; corner < loop.Length; corner++) {
                loop[corner] = map[loop[corner]];
            }

            table.Add(new(loop, mesh.Faces[face].Group, mesh.Faces[face].Smoothing));
        }

        mesh.Clear();

        foreach (var position in kept) {
            mesh.AddPosition(position);
        }

        foreach (var entry in table) {
            mesh.AddFace(entry.Loop, entry.Group, entry.Smooth);
        }

        return map;
    }

    // ── Whole meshes ────────────────────────────────────────────────────────────────────────────

    /// <summary>Takes a set of faces out of a mesh and returns them as a mesh of their own.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="faces">Which faces.</param>
    /// <param name="keep">Whether the originals stay — which makes this a copy rather than a move.</param>
    /// <returns>The new mesh, or null when nothing was selected.</returns>
    /// <remarks>Doc 24's <c>P</c>: to a new entity, or in place. Which of the two it is, is the
    ///     editor's decision about what to do with what comes back.</remarks>
    public static EditMesh? Detach(EditMesh mesh, IReadOnlyCollection<int> faces, bool keep = false) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(faces);

        var chosen = Region(mesh, faces);

        if (chosen.Count == 0) {
            return null;
        }

        var taken = new EditMesh { GroupSource = mesh.GroupSource };
        var moved = new Dictionary<int, int>();

        foreach (var face in chosen) {
            var loop = mesh.CornersOf(face).ToArray();

            for (var corner = 0; corner < loop.Length; corner++) {
                if (!moved.TryGetValue(loop[corner], out var at)) {
                    moved[loop[corner]] = at = taken.AddPosition(mesh.Positions[loop[corner]]);
                }

                loop[corner] = at;
            }

            taken.AddFace(loop, mesh.Faces[face].Group, mesh.Faces[face].Smoothing);
        }

        if (!keep) {
            Delete(mesh, chosen);
        }

        return taken;
    }

    /// <summary>Puts one mesh's geometry into another, offset by a transform.</summary>
    /// <param name="mesh">The mesh to add to.</param>
    /// <param name="other">The mesh to add.</param>
    /// <param name="transform">Where the other one goes, in this one's space.</param>
    /// <returns>The faces that arrived.</returns>
    /// <remarks>
    ///     ⚠ <b>The groups are shifted rather than kept, so two walls do not become one group.</b>
    ///     Doc 24's "merge objects" is what makes a room one mesh before baking, and a merge that
    ///     collapsed the groups would make every select-by-group afterwards take the whole room.
    /// </remarks>
    public static IReadOnlyList<int> Append(EditMesh mesh, EditMesh other, Matrix4x4 transform) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(other);

        var shift = Highest(mesh) + 1;
        var offset = mesh.PositionCount;

        foreach (var position in other.Positions) {
            mesh.AddPosition(Matrix4x4.TransformPosition(position, transform));
        }

        List<int> made = [];

        for (var face = 0; face < other.FaceCount; face++) {
            var loop = other.CornersOf(face).ToArray();

            for (var corner = 0; corner < loop.Length; corner++) {
                loop[corner] += offset;
            }

            made.Add(mesh.AddFace(loop, other.Faces[face].Group + shift, other.Faces[face].Smoothing));
        }

        // An assignment coming in makes the result an assignment: the appended faces' ids mean what
        // they meant, and the ones already here still mean what they meant.
        if (other.GroupSource is MeshGroupSource.Assigned) {
            mesh.GroupSource = MeshGroupSource.Assigned;
        }

        return made;
    }

    // ── The shared machinery ────────────────────────────────────────────────────────────────────

    /// <summary>Replaces a mesh's face table wholesale, keeping its positions.</summary>
    /// <remarks>
    ///     ⚠ <b>Wholesale rather than in place, and it is the honest way round.</b> Every operation
    ///     here inserts and removes faces at once; doing that in place means a face table that is
    ///     renumbering under the loop reading it, which is the defect that produces geometry looking
    ///     correct and failing three operations later. Building the answer and then writing it is one
    ///     rebuild of the edge table instead of one per face.
    /// </remarks>
    static void Replace(EditMesh mesh, List<MeshLoop> table) {
        mesh.Clear(keepPositions: true);

        foreach (var entry in table) {
            if (Distinct(entry.Loop) >= 3) {
                mesh.AddFace(entry.Loop, entry.Group, entry.Smooth);
            }
        }
    }

    /// <summary>A mesh's faces as a table something can be added to.</summary>
    static List<MeshLoop> Table(EditMesh mesh) {
        var table = new List<MeshLoop>(mesh.FaceCount);

        for (var face = 0; face < mesh.FaceCount; face++) {
            table.Add(new(mesh.CornersOf(face).ToArray(), mesh.Faces[face].Group, mesh.Faces[face].Smoothing));
        }

        return table;
    }

    /// <summary>How many different positions a loop names.</summary>
    static int Distinct(int[] loop) {
        var seen = new HashSet<int>(loop);
        return seen.Count;
    }

    /// <summary>The faces of a set that are really in the mesh, each once, in order.</summary>
    static List<int> Region(EditMesh mesh, IReadOnlyCollection<int> faces) {
        var seen = new HashSet<int>();
        List<int> region = [];

        foreach (var face in faces) {
            if ((uint) face < (uint) mesh.FaceCount && seen.Add(face)) {
                region.Add(face);
            }
        }

        region.Sort();
        return region;
    }

    /// <summary>Runs a per-face operation over a set, one face at a time.</summary>
    /// <remarks>
    ///     ⚠ <b>Highest face first, so an earlier face's renumbering does not move a later one.</b>
    ///     Every verb here rebuilds the face table, and the faces before the one being worked on keep
    ///     their indices while the ones after do not.
    /// </remarks>
    static List<int> Separately(
        EditMesh mesh,
        IReadOnlyCollection<int> faces,
        Func<EditMesh, int[], IReadOnlyList<int>> operation
    ) {
        List<int> made = [];

        foreach (var face in Region(mesh, faces).AsEnumerable().Reverse()) {
            made.AddRange(operation(mesh, [face]));
        }

        return made;
    }

    /// <summary>A region's normal, weighted by area.</summary>
    /// <remarks>
    ///     ⚠ <b>Area-weighted, so a region of one big wall and three slivers goes the way the wall
    ///     faces.</b> An unweighted average is decided by how finely the region happens to be
    ///     triangulated, which is a fact about the mesh's history rather than about its shape.
    /// </remarks>
    static Vector3 Facing(EditMesh mesh, List<int> region) {
        var total = Vector3.Zero;

        foreach (var face in region) {
            total += mesh.Normal(face) * mesh.Area(face);
        }

        return total.LengthSquared() > 0f ? Vector3.Normalize(total) : Vector3.UnitY;
    }

    /// <summary>The average of the positions a region covers.</summary>
    static Vector3 Middle(EditMesh mesh, List<int> region) {
        var seen = new HashSet<int>();
        var total = Vector3.Zero;

        foreach (var face in region) {
            foreach (var corner in mesh.CornersOf(face)) {
                if (seen.Add(corner)) {
                    total += mesh.Positions[corner];
                }
            }
        }

        return seen.Count == 0 ? Vector3.Zero : total / seen.Count;
    }

    /// <summary>The average of some of a mesh's positions.</summary>
    static Vector3 Average(EditMesh mesh, int[] positions) {
        var total = Vector3.Zero;

        foreach (var position in positions) {
            total += mesh.Positions[position];
        }

        return positions.Length == 0 ? Vector3.Zero : total / positions.Length;
    }

    /// <summary>Where a position goes when a region is inset towards a centre.</summary>
    static Vector3 Towards(Vector3 position, Vector3 centre, Vector3 normal, float amount) {
        var arm = position - centre;
        var height = normal * Vector3.Dot(arm, normal);
        var across = arm - height;
        var reach = across.Length();

        return reach <= amount || reach <= 0f
            ? centre + height
            : position - (across / reach * amount);
    }

    /// <summary>The largest group id in use, or −1.</summary>
    static int Highest(EditMesh mesh) {
        var highest = -1;

        foreach (var face in mesh.Faces) {
            highest = Math.Max(highest, face.Group);
        }

        return highest;
    }

    /// <summary>Detaches a region, walls in its rim, and moves the copies.</summary>
    static List<int> Raise(EditMesh mesh, List<int> region, Func<Vector3, Vector3> move) {
        var selected = new HashSet<int>(region);

        // The rim: an edge of a region face with no other selected face across it. A boundary edge is
        // on the rim too — the border of an open surface is as much a rim as the border with an
        // unselected neighbour.
        List<(int From, int To)> rim = [];

        foreach (var face in region) {
            var loop = mesh.CornersOf(face);

            for (var corner = 0; corner < loop.Length; corner++) {
                var from = loop[corner];
                var to = loop[(corner + 1) % loop.Length];
                var edge = mesh.EdgeBetween(from, to);

                if (edge < 0) {
                    continue;
                }

                var inside = false;

                foreach (var neighbour in mesh.FacesOf(edge)) {
                    inside |= neighbour != face && selected.Contains(neighbour);
                }

                if (!inside) {
                    rim.Add((from, to));
                }
            }
        }

        var copies = new Dictionary<int, int>();

        foreach (var face in region) {
            foreach (var corner in mesh.CornersOf(face).ToArray()) {
                if (!copies.ContainsKey(corner)) {
                    copies[corner] = mesh.AddPosition(mesh.Positions[corner]);
                }
            }
        }

        var table = new List<MeshLoop>();

        for (var face = 0; face < mesh.FaceCount; face++) {
            if (!selected.Contains(face)) {
                table.Add(new(mesh.CornersOf(face).ToArray(), mesh.Faces[face].Group, mesh.Faces[face].Smoothing));
            }
        }

        List<int> made = [];

        foreach (var face in region) {
            var loop = mesh.CornersOf(face).ToArray();

            for (var corner = 0; corner < loop.Length; corner++) {
                loop[corner] = copies[loop[corner]];
            }

            made.Add(table.Count);
            table.Add(new(loop, mesh.Faces[face].Group, mesh.Faces[face].Smoothing));
        }

        var group = Highest(mesh) + 1;

        foreach (var (from, to) in rim) {
            // Walked the way the owning face walks it, so the wall faces outwards. An edge is stored
            // low-to-high, which says nothing about winding — a wall built from the stored order is
            // inside out for about half of them.
            table.Add(new([from, to, copies[to], copies[from]], group++));
        }

        Replace(mesh, table);

        foreach (var (_, copy) in copies) {
            mesh.MovePosition(copy, move(mesh.Positions[copy]));
        }

        return made;
    }

    /// <summary>Rewrites every corner through a map, dropping faces that collapse.</summary>
    static void Remap(EditMesh mesh, Dictionary<int, int> remap) {
        var table = new List<MeshLoop>();

        for (var face = 0; face < mesh.FaceCount; face++) {
            var loop = mesh.CornersOf(face).ToArray();
            List<int> kept = [];

            foreach (var corner in loop) {
                var at = remap.GetValueOrDefault(corner, corner);

                // Consecutive duplicates collapse: a quad with two welded corners is a triangle, not a
                // quad with a zero-length edge in it.
                if (kept.Count == 0 || kept[^1] != at) {
                    kept.Add(at);
                }
            }

            if (kept.Count > 2 && kept[0] == kept[^1]) {
                kept.RemoveAt(kept.Count - 1);
            }

            if (kept.Count >= 3) {
                table.Add(new([.. kept], mesh.Faces[face].Group, mesh.Faces[face].Smoothing));
            }
        }

        Replace(mesh, table);
    }

    /// <summary>The two faces of an edge merged into one loop, or null when they cannot be.</summary>
    static int[]? Merge(EditMesh mesh, int first, int second, int a, int b) {
        var one = mesh.CornersOf(first).ToArray();
        var two = mesh.CornersOf(second).ToArray();

        // ⚠ Either orientation, because an edge is stored low-to-high and that says nothing about
        // which way round a face walks it — see `MeshEdge`. Whichever face walks it from A to B is
        // the one the merged loop starts in, and its neighbour necessarily walks it the other way.
        var start = Find(one, a, b);
        var other = Find(two, b, a);

        if (start < 0 || other < 0) {
            (one, two) = (two, one);

            start = Find(one, a, b);
            other = Find(two, b, a);
        }

        if (start < 0 || other < 0) {
            return null;
        }

        List<int> loop = [];

        // From the far end of the shared edge round the first face, then round the second, so the
        // shared edge is the only thing that disappears.
        for (var step = 1; step < one.Length; step++) {
            loop.Add(one[(start + step) % one.Length]);
        }

        for (var step = 1; step < two.Length; step++) {
            loop.Add(two[(other + step) % two.Length]);
        }

        return loop.Count >= 3 ? [.. loop] : null;

        static int Find(int[] loop, int from, int to) {
            for (var corner = 0; corner < loop.Length; corner++) {
                if (loop[corner] == from && loop[(corner + 1) % loop.Length] == to) {
                    return corner;
                }
            }

            return -1;
        }
    }

    /// <summary>An edge's two positions, read before anything renumbers the table.</summary>
    static (int A, int B)? Pair(EditMesh mesh, int edge) =>
        (uint) edge < (uint) mesh.Edges.Count ? (mesh.Edges[edge].A, mesh.Edges[edge].B) : null;

    /// <summary>A loop with the midpoints of any of its edges that have been split put back into it.</summary>
    /// <remarks>
    ///     <b>What keeps a partial subdivision closed.</b> The neighbour becomes an n-gon with an extra
    ///     corner rather than a quad with a T-junction against it — which is what every modelling tool
    ///     does, and is why an n-gon kernel is worth having at all.
    /// </remarks>
    static int[] Stitched(EditMesh mesh, int[] loop, Dictionary<int, int> midpoints) {
        List<int>? stitched = null;

        for (var corner = 0; corner < loop.Length; corner++) {
            var next = loop[(corner + 1) % loop.Length];
            var edge = mesh.EdgeBetween(loop[corner], next);

            if (edge < 0 || !midpoints.TryGetValue(edge, out var middle)) {
                stitched?.Add(loop[corner]);
                continue;
            }

            stitched ??= [.. loop[..corner]];

            stitched.Add(loop[corner]);
            stitched.Add(middle);
        }

        return stitched is null ? loop : [.. stitched];
    }

    /// <summary>A midpoint position for an edge, made once and shared by both faces on it.</summary>
    static int Midpoint(EditMesh mesh, Dictionary<int, int> made, int a, int b) {
        var edge = mesh.EdgeBetween(a, b);
        var key = edge >= 0 ? edge : -1 - (a * mesh.PositionCount) - b;

        if (!made.TryGetValue(key, out var at)) {
            made[key] = at = mesh.AddPosition(Vector3.Lerp(mesh.Positions[a], mesh.Positions[b], 0.5f));
        }

        return at;
    }

    /// <summary>Which two of a quad's edges a ring crosses, and which way round they run.</summary>
    static bool Split(
        EditMesh mesh,
        int[] loop,
        HashSet<int> ring,
        out (int Edge, int From, int To) first,
        out (int Edge, int From, int To) second
    ) {
        first = default;
        second = default;

        var found = 0;

        for (var corner = 0; corner < loop.Length; corner++) {
            var from = loop[corner];
            var to = loop[(corner + 1) % loop.Length];
            var edge = mesh.EdgeBetween(from, to);

            if (edge < 0 || !ring.Contains(edge)) {
                continue;
            }

            if (found == 0) {
                first = (edge, from, to);
            } else if (found == 1) {
                second = (edge, from, to);
            }

            found++;
        }

        // Exactly two, which is what a ring crossing a quad means. One is the end of the ring and
        // three is a quad the ring entered twice, and neither can be cut into two quads.
        return found == 2;
    }

    /// <summary>Which cut along an edge is the one nearest a given end.</summary>
    /// <remarks>
    ///     ⚠ <b>Both faces of a crossed edge have to agree which insertion is which.</b> The
    ///     insertions were made along the edge's stored direction — low to high — so a face walking it
    ///     the other way has to read them backwards, or the two halves of the cut meet in an X.
    /// </remarks>
    static int Ordered(EditMesh mesh, int edge, int from, int cut, int cuts) =>
        mesh.Edges[edge].A == from ? cut : cuts - 1 - cut;

    /// <summary>Which rotation of one loop against another pairs their nearest corners.</summary>
    static int Nearest(EditMesh mesh, int[] from, int[] to) {
        var best = float.MaxValue;
        var chosen = 0;

        for (var offset = 0; offset < to.Length; offset++) {
            var total = 0f;

            for (var corner = 0; corner < from.Length; corner++) {
                total += Vector3.DistanceSquared(
                    mesh.Positions[from[corner]],
                    mesh.Positions[to[(corner + offset) % to.Length]]
                );
            }

            if (total < best) {
                best = total;
                chosen = offset;
            }
        }

        return chosen;
    }

    /// <summary>A face's two corners of an edge, pulled back into the face by a width.</summary>
    static (int A, int B) Pull(EditMesh mesh, List<MeshLoop> table, int face, int a, int b, float width) {
        var loop = mesh.CornersOf(face).ToArray();

        var first = mesh.AddPosition(Back(mesh, loop, a, b, width));
        var second = mesh.AddPosition(Back(mesh, loop, b, a, width));

        // The face keeps its shape and loses the strip along the edge: its two corners on that edge
        // are replaced by the pulled-back pair, in the entry the rebuild will write.
        for (var index = 0; index < table.Count; index++) {
            if (index != face) {
                continue;
            }

            var rebuilt = table[index].Loop.ToArray();

            for (var corner = 0; corner < rebuilt.Length; corner++) {
                if (rebuilt[corner] == a) {
                    rebuilt[corner] = first;
                } else if (rebuilt[corner] == b) {
                    rebuilt[corner] = second;
                }
            }

            table[index] = table[index] with { Loop = rebuilt };
        }

        return (first, second);
    }

    /// <summary>A corner moved back into its face, away from one of its edges.</summary>
    /// <remarks>
    ///     ⚠ <b>Along the other edge at that corner, and clamped to a fraction of it.</b> A bevel
    ///     wider than the geometry it is cutting turns the face inside out, and a designer dragging one
    ///     is far more often overshooting than asking for that.
    /// </remarks>
    static Vector3 Back(EditMesh mesh, int[] loop, int corner, int away, float width) {
        var at = Array.IndexOf(loop, corner);

        if (at < 0) {
            return mesh.Positions[corner];
        }

        var previous = loop[(at + loop.Length - 1) % loop.Length];
        var next = loop[(at + 1) % loop.Length];
        var other = previous == away ? next : previous;

        var span = mesh.Positions[other] - mesh.Positions[corner];
        var length = span.Length();

        return length <= 0f
            ? mesh.Positions[corner]
            : mesh.Positions[corner] + (span / length * MathF.Min(width, length * 0.45f));
    }

    /// <summary>A position part of the way between two, added to the mesh.</summary>
    static int Blend(EditMesh mesh, int a, int b, float along) =>
        along <= 0f ? a
        : along >= 1f ? b
        : mesh.AddPosition(Vector3.Lerp(mesh.Positions[a], mesh.Positions[b], along));
}
