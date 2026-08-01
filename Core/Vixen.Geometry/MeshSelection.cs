// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry;

/// <summary>Which of a mesh's three tables a selection indexes.</summary>
/// <remarks>
///     ⚠ <b>Three rather than four, and the missing one is Object.</b> The editor's element modes have
///     a fourth — see <c>BlockoutElement</c> — because "the whole entity" is one of the choices a
///     designer makes. A mesh has no opinion about entities, so a kernel enum with an Object member
///     would be a value every switch here has to reject.
/// </remarks>
public enum MeshElementKind : byte {
    /// <summary>Shared positions.</summary>
    Vertex,

    /// <summary>Edges, indexed into <see cref="EditMesh.Edges" />.</summary>
    Edge,

    /// <summary>Faces.</summary>
    Face
}

/// <summary>What is selected in one mesh: a kind, and the indices of that kind.</summary>
/// <remarks>
///     <para>
///         <b>Doc 24's P2, and it is in the kernel rather than in the editor for the reason the
///         queries are.</b> Converting a face selection to the vertices it covers is a walk over the
///         corner table; growing an edge selection is a walk over the incidence tables. Both are
///         arithmetic with a right answer, both are what a test can assert against a cube, and neither
///         needs a viewport.
///     </para>
///     <para>
///         ⚠ <b>One set and a kind, not three sets.</b> A designer in vertex mode has vertices
///         selected; switching to face mode converts, and what they had before is gone — which is
///         exactly what Blender, ProBuilder and Unreal's PolyGroup editing all do. Three sets kept in
///         parallel would mean the mode you are not in holds a selection that drifts out of agreement
///         with the one you are, and the first operation to read the wrong one is a bug nobody can
///         reproduce.
///     </para>
///     <para>
///         ⚠ <b>Coarser-to-finer takes everything and finer-to-coarser takes only what is fully
///         covered.</b> Three faces converted to vertices is every corner of them; those vertices
///         converted back is the three faces again, because each has all its corners. A rule that took
///         partially covered faces would make the round trip grow the selection every time it was
///         made, and switching modes twice is something people do without meaning anything by it.
///     </para>
///     <para>
///         <b>The version is what drawing caches from.</b> A highlight is rebuilt when the selection
///         changes and not once a frame, and an event per change would be a subscription the frame
///         loop already has in the form of "it is redrawn anyway".
///     </para>
/// </remarks>
public sealed class MeshSelection {
    readonly HashSet<int> indices = [];
    readonly List<int> ordered = [];

    /// <summary>Which table <see cref="Indices" /> indexes.</summary>
    public MeshElementKind Kind { get; private set; }

    /// <summary>What is selected, in the order it was selected in.</summary>
    /// <remarks>
    ///     ⚠ <b>Ordered, because "weld to last" and "the active element" both mean the most recent
    ///     one.</b> A hash set's order is the hash's, which would make the snap base doc 24's D4 calls
    ///     "the active element" a different element on every rebuild.
    /// </remarks>
    public IReadOnlyList<int> Indices => ordered;

    /// <summary>How many elements are selected.</summary>
    public int Count => ordered.Count;

    /// <summary>Whether nothing is.</summary>
    public bool IsEmpty => ordered.Count == 0;

    /// <summary>The most recently added element, or <c>-1</c>.</summary>
    public int Active => ordered.Count > 0 ? ordered[^1] : -1;

    /// <summary>How many times this has changed.</summary>
    public int Version { get; private set; }

    /// <summary>Whether an element is selected.</summary>
    /// <param name="index">Its index in the table <see cref="Kind" /> names.</param>
    /// <returns>Whether it is.</returns>
    public bool Contains(int index) => indices.Contains(index);

    /// <summary>Selects one more element.</summary>
    /// <param name="index">Which.</param>
    /// <returns>Whether it was not already selected.</returns>
    public bool Add(int index) {
        if (!indices.Add(index)) {
            return false;
        }

        ordered.Add(index);
        Version++;

        return true;
    }

    /// <summary>Takes one out.</summary>
    /// <param name="index">Which.</param>
    /// <returns>Whether it was in.</returns>
    public bool Remove(int index) {
        if (!indices.Remove(index)) {
            return false;
        }

        ordered.Remove(index);
        Version++;

        return true;
    }

    /// <summary>Selects an element if it is not, and deselects it if it is.</summary>
    /// <param name="index">Which.</param>
    /// <returns>Whether it ended up selected.</returns>
    /// <remarks>What <c>Shift</c>+click does, for the reason <c>SceneViewport.Select</c> gives: one
    ///     modifier for both halves of one idea.</remarks>
    public bool Toggle(int index) => !Remove(index) && Add(index);

    /// <summary>Replaces everything with one element.</summary>
    /// <param name="index">Which.</param>
    public void Set(int index) {
        Clear();
        Add(index);
    }

    /// <summary>Replaces everything with a set.</summary>
    /// <param name="values">The indices.</param>
    public void Set(IEnumerable<int> values) {
        ArgumentNullException.ThrowIfNull(values);

        Clear();

        foreach (var value in values) {
            Add(value);
        }
    }

    /// <summary>Adds a set to what is selected.</summary>
    /// <param name="values">The indices.</param>
    public void Union(IEnumerable<int> values) {
        ArgumentNullException.ThrowIfNull(values);

        foreach (var value in values) {
            Add(value);
        }
    }

    /// <summary>Deselects everything.</summary>
    public void Clear() {
        if (ordered.Count == 0) {
            return;
        }

        indices.Clear();
        ordered.Clear();
        Version++;
    }

    /// <summary>Selects every element of the current kind.</summary>
    /// <param name="mesh">The mesh.</param>
    public void All(EditMesh mesh) {
        ArgumentNullException.ThrowIfNull(mesh);

        Clear();

        for (var index = 0; index < Total(mesh, Kind); index++) {
            Add(index);
        }
    }

    /// <summary>Selects what is not selected, and deselects what is.</summary>
    /// <param name="mesh">The mesh.</param>
    public void Invert(EditMesh mesh) {
        ArgumentNullException.ThrowIfNull(mesh);

        var was = new HashSet<int>(indices);

        Clear();

        for (var index = 0; index < Total(mesh, Kind); index++) {
            if (!was.Contains(index)) {
                Add(index);
            }
        }
    }

    /// <summary>Changes which kind of element is selected, converting what is selected to it.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="kind">The kind to change to.</param>
    /// <remarks>See this type's own remarks for the rule: coarser to finer takes everything, finer to
    ///     coarser takes only what is fully covered.</remarks>
    public void Convert(EditMesh mesh, MeshElementKind kind) {
        ArgumentNullException.ThrowIfNull(mesh);

        if (kind == Kind) {
            return;
        }

        var taken = Converted(mesh, kind);

        Kind = kind;
        Set(taken);
    }

    /// <summary>Takes in everything touching what is already selected.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <remarks>
    ///     ⚠ <b>Touching means "shares the thing one rank down", which is a different join per
    ///     kind.</b> Faces grow across shared <i>edges</i> and not across shared corners, so a
    ///     selection does not leap the diagonal where two walls meet at a point; vertices and edges
    ///     grow through shared positions, because that is the only join they have.
    /// </remarks>
    public void Grow(EditMesh mesh) {
        ArgumentNullException.ThrowIfNull(mesh);

        var taken = new List<int>(ordered);

        switch (Kind) {
            case MeshElementKind.Vertex:
                foreach (var position in ordered) {
                    foreach (var edge in mesh.EdgesAt(position)) {
                        taken.Add(mesh.Edges[edge].Other(position));
                    }
                }

                break;

            case MeshElementKind.Edge:
                foreach (var index in ordered) {
                    var edge = mesh.Edges[index];

                    taken.AddRange(mesh.EdgesAt(edge.A));
                    taken.AddRange(mesh.EdgesAt(edge.B));
                }

                break;

            default:
                foreach (var face in ordered) {
                    var loop = mesh.CornersOf(face);

                    for (var corner = 0; corner < loop.Length; corner++) {
                        var edge = mesh.EdgeBetween(loop[corner], loop[(corner + 1) % loop.Length]);

                        if (edge >= 0) {
                            taken.AddRange(mesh.FacesOf(edge));
                        }
                    }
                }

                break;
        }

        Union(taken);
    }

    /// <summary>Gives back everything on the edge of what is selected.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <remarks>
    ///     ⚠ <b>Not the inverse of <see cref="Grow" /> and it cannot be.</b> Growing a whole closed
    ///     mesh changes nothing, so shrinking afterwards would have to know it had; what this does
    ///     instead is drop every element with an unselected neighbour, which is the answer that makes
    ///     grow-shrink-grow settle rather than oscillate.
    /// </remarks>
    public void Shrink(EditMesh mesh) {
        ArgumentNullException.ThrowIfNull(mesh);

        List<int> kept = [];

        switch (Kind) {
            case MeshElementKind.Vertex:
                foreach (var position in ordered) {
                    var inside = true;

                    foreach (var edge in mesh.EdgesAt(position)) {
                        inside &= Contains(mesh.Edges[edge].Other(position));
                    }

                    if (inside) {
                        kept.Add(position);
                    }
                }

                break;

            case MeshElementKind.Edge:
                foreach (var index in ordered) {
                    var edge = mesh.Edges[index];

                    if (Surrounded(edge.A) && Surrounded(edge.B)) {
                        kept.Add(index);
                    }
                }

                break;

            default:
                foreach (var face in ordered) {
                    var loop = mesh.CornersOf(face);
                    var inside = true;

                    for (var corner = 0; corner < loop.Length; corner++) {
                        var edge = mesh.EdgeBetween(loop[corner], loop[(corner + 1) % loop.Length]);

                        if (edge < 0) {
                            continue;
                        }

                        foreach (var neighbour in mesh.FacesOf(edge)) {
                            inside &= Contains(neighbour);
                        }

                        // A boundary edge is an edge of the selection as surely as an unselected
                        // neighbour is: there is nothing beyond it, so the face is on the rim.
                        inside &= mesh.FacesOf(edge).Length > 1;
                    }

                    if (inside) {
                        kept.Add(face);
                    }
                }

                break;
        }

        Set(kept);

        bool Surrounded(int position) {
            foreach (var edge in mesh.EdgesAt(position)) {
                if (!Contains(edge)) {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>Changes which kind is selected, without a mesh to convert through.</summary>
    /// <param name="kind">The kind to change to.</param>
    /// <remarks>
    ///     ⚠ <b>Clears, where <see cref="Convert" /> converts, and the difference is honest.</b>
    ///     Converting a face selection to vertices needs the corner table; without a mesh there is
    ///     nothing to look them up in, and keeping the indices while relabelling what they mean would
    ///     leave face numbers being read as position numbers.
    /// </remarks>
    public void SetKind(MeshElementKind kind) {
        if (kind == Kind) {
            return;
        }

        Kind = kind;
        Clear();
    }

    /// <summary>Drops anything naming an element the mesh no longer has.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <returns>Whether anything was dropped.</returns>
    /// <remarks>
    ///     ⚠ <b>What a topology change leaves behind, and doc 24's P2 exit turns on it.</b> A position
    ///     move keeps every index meaningful, so the selection survives the undo of one; an extrude
    ///     renumbers the tables, so an index kept across it names a different element — which draws a
    ///     highlight round geometry nobody chose. Dropping what has gone out of range is the cheap half
    ///     of that; the editor is what decides whether the rest is still what was meant.
    /// </remarks>
    public bool Validate(EditMesh mesh) {
        ArgumentNullException.ThrowIfNull(mesh);

        var total = Total(mesh, Kind);
        var dropped = false;

        for (var index = ordered.Count - 1; index >= 0; index--) {
            if ((uint) ordered[index] < (uint) total) {
                continue;
            }

            indices.Remove(ordered[index]);
            ordered.RemoveAt(index);
            dropped = true;
        }

        if (dropped) {
            Version++;
        }

        return dropped;
    }

    /// <summary>Which shared positions the selection covers, whatever kind it is of.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="into">The position indices, each once. Cleared first.</param>
    /// <remarks>
    ///     <b>What a gizmo drags.</b> Moving a face means moving its corners, and moving an edge means
    ///     moving its two ends — so every element mode's transform is one loop over positions, which is
    ///     what makes <c>EditMeshCommand.Moved</c> one shape rather than three.
    /// </remarks>
    public void Positions(EditMesh mesh, List<int> into) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();

        var seen = new HashSet<int>();

        foreach (var index in ordered) {
            switch (Kind) {
                case MeshElementKind.Vertex:
                    Take(index);
                    break;

                case MeshElementKind.Edge when (uint) index < (uint) mesh.Edges.Count:
                    Take(mesh.Edges[index].A);
                    Take(mesh.Edges[index].B);

                    break;

                case MeshElementKind.Face when (uint) index < (uint) mesh.FaceCount:
                    foreach (var corner in mesh.CornersOf(index)) {
                        Take(corner);
                    }

                    break;

                default:
                    break;
            }
        }

        void Take(int position) {
            if (seen.Add(position)) {
                into.Add(position);
            }
        }
    }

    /// <summary>Where the selection is, as the average of the positions it covers.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <returns>The centre in the mesh's own space, or null when nothing is selected.</returns>
    /// <remarks>
    ///     ⚠ <b>The average of the <i>positions</i>, not of the elements.</b> A face selection whose
    ///     faces share corners would otherwise weight a shared corner once per face, and the pivot for
    ///     a scale would sit off the geometry by an amount that depends on the triangulation.
    /// </remarks>
    public Vector3? Centre(EditMesh mesh) {
        ArgumentNullException.ThrowIfNull(mesh);

        List<int> covered = [];

        Positions(mesh, covered);

        if (covered.Count == 0) {
            return null;
        }

        var total = Vector3.Zero;

        foreach (var position in covered) {
            total += mesh.Positions[position];
        }

        return total / covered.Count;
    }

    /// <summary>How many elements of a kind a mesh has.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="kind">Which kind.</param>
    /// <returns>The count.</returns>
    public static int Total(EditMesh mesh, MeshElementKind kind) {
        ArgumentNullException.ThrowIfNull(mesh);

        return kind switch {
            MeshElementKind.Vertex => mesh.PositionCount,
            MeshElementKind.Edge => mesh.Edges.Count,
            _ => mesh.FaceCount
        };
    }

    /// <summary>What this selection would be as another kind, without changing it.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="kind">The kind to read it as.</param>
    /// <returns>The indices.</returns>
    /// <remarks>What an operation that acts on faces does when the designer is in edge mode: extrude
    ///     with two edges selected is an edge extrude, and with the four edges of a quad it is not.</remarks>
    public IReadOnlyList<int> Converted(EditMesh mesh, MeshElementKind kind) {
        ArgumentNullException.ThrowIfNull(mesh);

        if (kind == Kind) {
            return ordered;
        }

        List<int> covered = [];

        Positions(mesh, covered);

        if (kind == MeshElementKind.Vertex) {
            return covered;
        }

        var positions = new HashSet<int>(covered);
        List<int> taken = [];

        if (kind == MeshElementKind.Edge) {
            for (var edge = 0; edge < mesh.Edges.Count; edge++) {
                if (positions.Contains(mesh.Edges[edge].A) && positions.Contains(mesh.Edges[edge].B)) {
                    taken.Add(edge);
                }
            }

            return taken;
        }

        for (var face = 0; face < mesh.FaceCount; face++) {
            var whole = true;

            foreach (var corner in mesh.CornersOf(face)) {
                whole &= positions.Contains(corner);
            }

            if (whole) {
                taken.Add(face);
            }
        }

        return taken;
    }
}
