// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry;

/// <summary>What a mesh's structure answers: loops, rings, neighbours and coplanar regions.</summary>
/// <remarks>
///     <para>
///         <b>Doc 24's P2 selection table, as functions rather than as gestures.</b> "Alt-click an
///         edge" is a binding; <i>what</i> it selects is a walk over the edge table, and the walk is
///         the part that can be wrong. Keeping it here means a loop round a cylinder is a test with a
///         cylinder in it rather than a click somebody has to perform.
///     </para>
///     <para>
///         ⚠ <b>Every query answers with indices and takes none of its own state.</b> A selection is
///         the editor's — see <see cref="MeshSelection" /> — and these are what it is changed by. A
///         query that mutated a selection would be one that could not be composed: grow-then-loop and
///         loop-then-grow are both things a designer does.
///     </para>
///     <para>
///         ⚠ <b>The edge table is what makes all of this cheap, and it is why doc 24's D2 keeps
///         one.</b> A loop is a walk of one step per edge in it, and a step is two spans and a
///         comparison. Without an explicit table each step is a search over every face in the mesh.
///     </para>
/// </remarks>
public static class MeshTopology {
    /// <summary>How nearly parallel two faces must be to count as coplanar for a selection.</summary>
    /// <remarks>
    ///     ⚠ <b>Looser than <see cref="EditMesh.DefaultCoplanarTolerance" />, and deliberately.</b>
    ///     Grouping decides what a mesh <i>is</i> and wants to be strict; "select every coplanar face"
    ///     is a designer saying "this wall" about geometry that has been dragged, and a wall whose
    ///     halves differ by a fifth of a degree is still one wall to whoever is looking at it. The
    ///     cosine of about two degrees.
    /// </remarks>
    public const float DefaultCoplanarTolerance = 0.9994f;

    /// <summary>The edge loop an edge is part of.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="edge">Which edge to start from.</param>
    /// <param name="into">The edge indices, including the one given. Cleared first.</param>
    /// <remarks>
    ///     <para>
    ///         <b>The edge table's whole reason for existing, in doc 24's own words.</b> A loop runs
    ///         straight on through each position it meets, and "straight on" is defined by the
    ///         structure rather than by the geometry: at a position where four edges meet, the
    ///         continuation is the one edge that shares no face with the one arrived on.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It stops where the valence is not four, which is why a loop round a cylinder's
    ///         side closes and one that reaches a pole ends there.</b> Any other rule has to guess,
    ///         and a loop that guesses is one that occasionally selects a path through a mesh that
    ///         nobody can describe — which is worse than one that stops short, because stopping short
    ///         is visible.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both directions from the seed, and a closed loop is walked once.</b> Reaching the
    ///         seed again ends the walk rather than starting it over; without that a ring round a
    ///         torus never terminates.
    ///     </para>
    /// </remarks>
    public static void EdgeLoop(EditMesh mesh, int edge, List<int> into) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();

        if ((uint) edge >= (uint) mesh.Edges.Count) {
            return;
        }

        var seen = new HashSet<int> { edge };

        into.Add(edge);

        Walk(mesh.Edges[edge].A);
        Walk(mesh.Edges[edge].B);

        void Walk(int from) {
            var current = edge;
            var at = from;

            while (Continuation(mesh, current, at) is { } next && seen.Add(next)) {
                into.Add(next);
                at = mesh.Edges[next].Other(at);
                current = next;
            }
        }
    }

    /// <summary>The edge ring an edge is part of.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="edge">Which edge to start from.</param>
    /// <param name="into">The edge indices, including the one given. Cleared first.</param>
    /// <remarks>
    ///     <para>
    ///         <b>A loop runs <i>along</i> a strip of quads and a ring runs <i>across</i> it.</b> The
    ///         rungs of the ladder rather than its rails — which is what a loop cut is inserted along
    ///         and what a bridge joins.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Only through quads, and that is the definition rather than a limitation.</b> "The
    ///         opposite edge" is a phrase about a four-sided face; in a triangle no edge is opposite
    ///         another and in a hexagon two are equally opposite. A ring that guessed at those would
    ///         wander, so it stops — and a block-out mesh made of extrusions is quads nearly
    ///         everywhere, which is where rings are wanted.
    ///     </para>
    /// </remarks>
    public static void EdgeRing(EditMesh mesh, int edge, List<int> into) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();

        if ((uint) edge >= (uint) mesh.Edges.Count) {
            return;
        }

        var seen = new HashSet<int> { edge };

        into.Add(edge);

        foreach (var face in mesh.FacesOf(edge)) {
            Walk(edge, face);
        }

        void Walk(int from, int through) {
            var current = from;
            var face = through;

            while (Across(mesh, current, face) is { } next && seen.Add(next)) {
                into.Add(next);

                // The face on the far side of the edge just crossed to, which is where the ring
                // continues. A boundary edge has only the one, and the walk ends.
                var touching = mesh.FacesOf(next);
                var beyond = -1;

                foreach (var candidate in touching) {
                    if (candidate != face) {
                        beyond = candidate;
                        break;
                    }
                }

                if (beyond < 0) {
                    return;
                }

                current = next;
                face = beyond;
            }
        }
    }

    /// <summary>Every face reachable from one by crossing edges, while staying coplanar with it.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="face">Which face to start from.</param>
    /// <param name="into">The face indices, including the one given. Cleared first.</param>
    /// <param name="tolerance">How nearly parallel two neighbours must be, as a cosine.</param>
    /// <remarks>
    ///     ⚠ <b>Against the seed's normal, not against each neighbour's.</b> Comparing each face with
    ///     the one it was reached from lets a gently curved surface be walked one small step at a time
    ///     until the far end is at right angles to the near end — a dome selected as a plane. The seed
    ///     is what the designer pointed at, so it is what "coplanar with" means.
    /// </remarks>
    public static void Coplanar(EditMesh mesh, int face, List<int> into, float tolerance = DefaultCoplanarTolerance) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();

        if ((uint) face >= (uint) mesh.FaceCount) {
            return;
        }

        var facing = mesh.Normal(face);
        var seen = new HashSet<int> { face };
        var queue = new Queue<int>();
        var neighbours = new List<int>();

        queue.Enqueue(face);
        into.Add(face);

        while (queue.Count > 0) {
            Neighbours(mesh, queue.Dequeue(), neighbours);

            foreach (var neighbour in neighbours) {
                if (Vector3.Dot(mesh.Normal(neighbour), facing) >= tolerance && seen.Add(neighbour)) {
                    into.Add(neighbour);
                    queue.Enqueue(neighbour);
                }
            }
        }
    }

    /// <summary>Every face in a group.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="group">The group id.</param>
    /// <param name="into">The face indices. Cleared first.</param>
    /// <remarks>Unreal's "select PolyGroup", and it is a filter rather than a walk: a group is
    ///     connected when <see cref="EditMesh.Regroup" /> made it and need not stay so after an edit.</remarks>
    public static void Group(EditMesh mesh, int group, List<int> into) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();

        for (var face = 0; face < mesh.FaceCount; face++) {
            if (mesh.Faces[face].Group == group) {
                into.Add(face);
            }
        }
    }

    /// <summary>Every face joined to one through shared edges, however the surface turns.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="face">Which face to start from.</param>
    /// <param name="into">The face indices. Cleared first.</param>
    /// <remarks>The connected shell — what "select linked" means, and what tells a designer that the
    ///     wall they are about to delete is joined to the floor.</remarks>
    public static void Shell(EditMesh mesh, int face, List<int> into) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();

        if ((uint) face >= (uint) mesh.FaceCount) {
            return;
        }

        var seen = new HashSet<int> { face };
        var queue = new Queue<int>();
        var neighbours = new List<int>();

        queue.Enqueue(face);
        into.Add(face);

        while (queue.Count > 0) {
            Neighbours(mesh, queue.Dequeue(), neighbours);

            foreach (var neighbour in neighbours) {
                if (seen.Add(neighbour)) {
                    into.Add(neighbour);
                    queue.Enqueue(neighbour);
                }
            }
        }
    }

    /// <summary>The boundary loop an open edge is part of.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="edge">A boundary edge — one with a single face.</param>
    /// <param name="into">The position indices round the hole, in order. Cleared first.</param>
    /// <returns>Whether the walk closed, which is whether the loop is a hole rather than a slit.</returns>
    /// <remarks>
    ///     <b>What "fill hole" fills and what "bridge" joins.</b> The walk steps from boundary edge to
    ///     boundary edge round the rim; a position where three boundary edges meet — a pinched surface
    ///     — has no single continuation, and the walk stops rather than choosing one.
    /// </remarks>
    public static bool BoundaryLoop(EditMesh mesh, int edge, List<int> into) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();

        if ((uint) edge >= (uint) mesh.Edges.Count || mesh.FacesOf(edge).Length != 1) {
            return false;
        }

        // ⚠ Walked the way the rim's own face walks it, rather than from the edge's stored A to its
        // stored B. An edge is stored low-to-high, which says nothing about winding — and a caller
        // putting a face across the hole has to wind it *against* the rim or the cap faces inwards.
        // A loop whose direction depended on how the positions happened to be numbered would make
        // that impossible to do correctly.
        var owner = mesh.FacesOf(edge)[0];
        var (start, at) = Walked(mesh, owner, mesh.Edges[edge]);
        var came = edge;

        into.Add(start);

        while (at != start) {
            into.Add(at);

            var next = -1;
            var found = 0;

            foreach (var candidate in mesh.EdgesAt(at)) {
                if (candidate == came || mesh.FacesOf(candidate).Length != 1) {
                    continue;
                }

                next = candidate;
                found++;
            }

            // Either the rim ran out — an open slit rather than a hole — or three boundary edges meet
            // here and "round" has more than one meaning. Neither is a loop, and neither is an error
            // in the mesh: both are things a block-out under construction genuinely is.
            if (found != 1) {
                return false;
            }

            came = next;
            at = mesh.Edges[next].Other(at);

            // A rim that walks back over itself, which a figure-eight boundary does. Bounded by the
            // edge count so a structure this walk cannot describe costs a walk rather than a hang.
            if (into.Count > mesh.Edges.Count) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Which way round a face walks one of its edges.</summary>
    static (int From, int To) Walked(EditMesh mesh, int face, MeshEdge edge) {
        var loop = mesh.CornersOf(face);

        for (var corner = 0; corner < loop.Length; corner++) {
            var from = loop[corner];
            var to = loop[(corner + 1) % loop.Length];

            if (from == edge.A && to == edge.B) {
                return (edge.A, edge.B);
            }

            if (from == edge.B && to == edge.A) {
                return (edge.B, edge.A);
            }
        }

        return (edge.A, edge.B);
    }

    /// <summary>Which faces share an edge with one.</summary>
    /// <remarks>
    ///     ⚠ <b>Through the face's own edges rather than through the faces at its corners.</b> Two
    ///     walls meeting along nothing but a single shared corner are not neighbours, and treating
    ///     them as such is what makes a coplanar selection leak across a diagonal join into geometry
    ///     the designer can see is separate.
    /// </remarks>
    static void Neighbours(EditMesh mesh, int face, List<int> into) {
        into.Clear();

        var loop = mesh.CornersOf(face);

        for (var corner = 0; corner < loop.Length; corner++) {
            var edge = mesh.EdgeBetween(loop[corner], loop[(corner + 1) % loop.Length]);

            if (edge < 0) {
                continue;
            }

            foreach (var candidate in mesh.FacesOf(edge)) {
                if (candidate != face) {
                    into.Add(candidate);
                }
            }
        }
    }

    /// <summary>Where an edge loop goes next, at one of its ends.</summary>
    /// <remarks>
    ///     ⚠ <b>Valence four, and the continuation is the edge sharing no face with this one.</b> At a
    ///     regular position the four edges pair up across the two axes of the quad grid; the one edge
    ///     with no face in common with the one arrived on is the far side of that crossing.
    /// </remarks>
    static int? Continuation(EditMesh mesh, int edge, int at) {
        var meeting = mesh.EdgesAt(at);

        if (meeting.Length != 4) {
            return null;
        }

        foreach (var candidate in meeting) {
            if (candidate == edge || Adjacent(mesh, edge, candidate)) {
                continue;
            }

            return candidate;
        }

        return null;
    }

    /// <summary>Whether two edges are on a common face.</summary>
    static bool Adjacent(EditMesh mesh, int first, int second) {
        foreach (var face in mesh.FacesOf(first)) {
            foreach (var other in mesh.FacesOf(second)) {
                if (face == other) {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The edge opposite one, across a four-sided face.</summary>
    static int? Across(EditMesh mesh, int edge, int face) {
        if ((uint) face >= (uint) mesh.FaceCount || mesh.Faces[face].Count != 4) {
            return null;
        }

        var loop = mesh.CornersOf(face);

        for (var corner = 0; corner < 4; corner++) {
            var candidate = mesh.EdgeBetween(loop[corner], loop[(corner + 1) % 4]);

            if (candidate != edge) {
                continue;
            }

            // Two corners along, which in a quad is the far side.
            return mesh.EdgeBetween(loop[(corner + 2) % 4], loop[(corner + 3) % 4]);
        }

        return null;
    }
}
