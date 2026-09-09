// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.SceneView;
using Vixen.Geometry;

namespace Vixen.Editor.Blockout;

/// <summary>What the pointer is promising, drawn before the click that commits it.</summary>
/// <remarks>
///     <para>
///         <b>doc 24 § P4 and § Geometry both ask for one, and the two are the same job.</b> The cube
///         grid owes "the candidate cell under the pointer before you commit to it"; the loop cut owes
///         "preview follows the pointer". Both are a drawing on <c>SceneLines</c>' overlay computed
///         from a hover, which is the shape P2's element highlight established — and answering "what
///         does the pointer mean right now" twice in one mode is how the two answers come to disagree.
///     </para>
///     <para>
///         ⚠ <b>Everything here is a pure function of a hover, and none of it touches the mesh.</b>
///         A preview that ran the operation on a copy would pay a topology rebuild per pointer move
///         and, worse, would be a second implementation of the verb: the day the two disagreed the
///         designer would be shown one cut and given another. What these compute is the *input* to the
///         verb, geometrically — a cell's eight corners, and the positions a cut would insert.
///     </para>
///     <para>
///         ⚠ <b>Into the overlay channel rather than the depth-tested one.</b> A preview is coplanar
///         with the surface it is describing — the cut runs across faces, the cell sits on the work
///         plane — so depth-tested it would z-fight its way in and out of existence as the camera
///         moved. <see cref="SceneViewport.Cursor" />'s own remarks make the argument for the terrain
///         brush and it is the same argument.
///     </para>
/// </remarks>
public static class BlockoutHover {
    /// <summary>Draws a lattice region as a wire box, in the work plane's own axes.</summary>
    /// <param name="draw">Where the lines go.</param>
    /// <param name="box">Which cells.</param>
    /// <param name="plane">The work plane the lattice is on, or null for the ground.</param>
    /// <param name="colour">Its colour.</param>
    /// <exception cref="ArgumentNullException"><paramref name="draw" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>The corners go through <see cref="WorkPlane.ToWorld" /> individually rather than a
    ///     centre-plus-extents box being drawn in world axes.</b> A work plane that has been set to a
    ///     wall is rotated, and a box drawn from world axes at a rotated plane's centre is a box that
    ///     is not the cell — it lines up only while the plane is the ground, which is the case every
    ///     casual test uses.
    /// </remarks>
    public static void CubeGrid(GizmoDraw draw, GridBox box, WorkPlane? plane, Color4 colour) {
        ArgumentNullException.ThrowIfNull(draw);

        var sound = box.Sound();

        if (sound.IsEmpty) {
            return;
        }

        var step = plane?.Step is { } chosen && chosen > WorkPlane.MinimumStep ? chosen : BlockoutCubeGrid.DefaultStep;

        var low = new Vector3(sound.X * step, sound.Y * step, sound.Z * step);

        var high = new Vector3(
            (sound.X + sound.Width) * step,
            (sound.Y + sound.Height) * step,
            (sound.Z + sound.Depth) * step
        );

        Span<Vector3> corners = [
            At(plane, low.X, low.Y, low.Z), At(plane, high.X, low.Y, low.Z),
            At(plane, high.X, low.Y, high.Z), At(plane, low.X, low.Y, high.Z),
            At(plane, low.X, high.Y, low.Z), At(plane, high.X, high.Y, low.Z),
            At(plane, high.X, high.Y, high.Z), At(plane, low.X, high.Y, high.Z)
        ];

        for (var corner = 0; corner < 4; corner++) {
            var next = (corner + 1) % 4;

            draw.Line(corners[corner], corners[next], colour);
            draw.Line(corners[corner + 4], corners[next + 4], colour);
            draw.Line(corners[corner], corners[corner + 4], colour);
        }
    }

    /// <summary>Where a loop cut through an edge's ring would run, as segments in the mesh's space.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="edge">An edge of the ring the cut goes across.</param>
    /// <param name="cuts">How many loops, as <c>MeshOperations.LoopCut</c> takes them.</param>
    /// <param name="slide">Where a single cut sits along the ring, from 0 to 1.</param>
    /// <param name="into">The segments. Cleared first.</param>
    /// <returns>Whether the cut would do anything.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mesh" /> or <paramref name="into" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One segment per quad the ring crosses, and never one per pair of consecutive ring
    ///         entries.</b> <c>MeshTopology.EdgeRing</c> walks <i>both</i> ways from the seed and
    ///         appends each walk, so its list is a seed followed by two chains running in opposite
    ///         directions — consecutive entries are adjacent everywhere except across the join, where
    ///         they are on opposite sides of the mesh. A preview drawn by joining the list in order is
    ///         right for most of its length and has one segment shot across the middle of the model,
    ///         which reads as a bug in the ring rather than in the drawing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which cut is which end is the same question <c>MeshOperations.LoopCut</c> answers
    ///         with its own <c>Ordered</c>, and it is mirrored here rather than reasoned about
    ///         afresh.</b> An edge is stored low-to-high and says nothing about which way a face walks
    ///         it, so pairing cut <c>k</c> on one side with cut <c>k</c> on the other would cross the
    ///         segments over whenever the two edges are stored in opposite senses — an X across every
    ///         quad at three cuts.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This paragraph used to say no shape this toolset makes reaches that branch, and
    ///         that was wrong twice over</b>
    ///         (<a href="https://github.com/Rikarin/Vixen/issues/1167">#1167</a>). Counted over every
    ///         ring seed of every <c>ShapeKind</c>, the flip fires on nine of the twelve; the old
    ///         probe covered four and <c>Box</c> was the only one of those where it does not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The half that decides whether the rule is load-bearing is narrower than "does it
    ///         fire".</b> A quad whose two crossed edges <i>both</i> flip is unharmed by removing the
    ///         rule — the strip is built from the other end and comes out the same. What crosses the
    ///         segments over is a quad where one side flips and the other does not, and
    ///         <c>DoorFrame</c> (40 quads) and <c>Arch</c> (128) are the shapes that have any.
    ///         <c>BlockoutHoverTests.The_previewed_cuts_across_one_quad_stay_parallel_when_its_edges_disagree</c>
    ///         is that fixture here, and <c>MeshOperationTests.A_loop_cut_never_leaves_a_face_with_no_area</c>
    ///         is the verb's.
    ///     </para>
    /// </remarks>
    public static bool LoopCut(EditMesh mesh, int edge, int cuts, float slide, List<(Vector3 A, Vector3 B)> into) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();
        cuts = Math.Max(cuts, 1);
        slide = Math.Clamp(slide, 0f, 1f);

        List<int> ring = [];

        MeshTopology.EdgeRing(mesh, edge, ring);

        if (ring.Count == 0) {
            return false;
        }

        var crossing = new HashSet<int>(ring);

        for (var face = 0; face < mesh.FaceCount; face++) {
            var loop = mesh.CornersOf(face);

            if (loop.Length != 4) {
                continue;
            }

            (int Edge, int From) first = (-1, -1);
            (int Edge, int From) second = (-1, -1);
            var found = 0;

            for (var corner = 0; corner < loop.Length; corner++) {
                var from = loop[corner];
                var crossed = mesh.EdgeBetween(from, loop[(corner + 1) % loop.Length]);

                if (crossed < 0 || !crossing.Contains(crossed)) {
                    continue;
                }

                if (found == 0) {
                    first = (crossed, from);
                } else if (found == 1) {
                    second = (crossed, loop[(corner + 1) % loop.Length]);
                }

                found++;
            }

            // Exactly two is what a ring crossing a quad means: one is where the ring ended and three
            // is a quad it entered twice, and neither can be cut into two quads.
            if (found != 2) {
                continue;
            }

            for (var cut = 0; cut < cuts; cut++) {
                into.Add(
                    (
                        Along(mesh, first.Edge, Ordered(mesh, first.Edge, first.From, cut, cuts), cuts, slide),
                        Along(mesh, second.Edge, Ordered(mesh, second.Edge, second.From, cut, cuts), cuts, slide)
                    )
                );
            }
        }

        return into.Count > 0;
    }

    static Vector3 At(WorkPlane? plane, float x, float y, float z) {
        var local = new Vector3(x, y, z);

        return plane?.ToWorld(local) ?? local;
    }

    /// <summary>Where cut <paramref name="index" /> sits on an edge, as a position.</summary>
    static Vector3 Along(EditMesh mesh, int edge, int index, int cuts, float slide) {
        var (a, b) = mesh.Edges[edge];
        var along = cuts == 1 ? slide : (index + 1) / (float)(cuts + 1);

        return Vector3.Lerp(mesh.Positions[a], mesh.Positions[b], along);
    }

    /// <summary>Which cut along an edge is the one nearest a given end — <c>LoopCut</c>'s own rule.</summary>
    static int Ordered(EditMesh mesh, int edge, int from, int cut, int cuts) =>
        mesh.Edges[edge].A == from ? cut : cuts - 1 - cut;
}
