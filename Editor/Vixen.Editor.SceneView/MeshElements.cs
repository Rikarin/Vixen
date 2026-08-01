// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering;

namespace Vixen.Editor.SceneView;

/// <summary>Two shared positions, and the segment between them.</summary>
/// <param name="A">The lower of the two position indices.</param>
/// <param name="B">The higher.</param>
/// <remarks>
///     ⚠ <b>Ordered, so that the edge a triangle walks one way and its neighbour walks the other is
///     one edge.</b> An unordered pair would give a cube twenty-four edges where it has twelve, and
///     every one of them would be drawn twice and selectable twice.
/// </remarks>
public readonly record struct MeshEdge(int A, int B);

/// <summary>The vertices, edges and faces of a mesh, as things that can be pointed at.</summary>
/// <remarks>
///     <para>
///         <b>Derived, not authored, and that distinction is the whole of what this type is.</b>
///         <see cref="MeshData" /> is a drawing structure: its vertices are <i>corners</i>, split
///         wherever a normal or a texture coordinate had to be, so a cube's eight corners are
///         twenty-four entries and its twelve edges do not exist at all. Sub-object selection asks
///         about the other graph — doc 24's D2 calls it the position graph — where a cube corner is
///         one thing you can drag and an edge is one thing you can bevel.
///     </para>
///     <para>
///         ⚠ <b>This is not <c>EditMesh</c> and must not grow into it.</b> Doc 24's P1 builds the
///         authored structure in <c>Core/Vixen.Geometry</c>: n-gon faces, an edge table that names up
///         to two faces and reports more, attribute layers, face groups. What is here is the smallest
///         thing that lets a pointer name an element of the geometry the editor <i>already draws</i>,
///         so that <a href="../../docs/plan/24-blockout-tools.md">B4</a>'s question is answerable
///         before P1 exists. When it does, an <c>EditMesh</c>'s triangulation is what this is built
///         from — or replaces it outright — and nothing above changes, because
///         <see cref="SubObjectPicker" /> only ever reads positions, triangles and edges.
///     </para>
///     <para>
///         ⚠ <b>Positions are welded within a tolerance rather than by exact equality.</b> A cube's
///         corner really is the same three floats three times over, but a sphere's seam is
///         <c>cos 0</c> against <c>cos 2π</c> and differs in the last bits — so exact welding leaves a
///         line of doubled vertices down every curved primitive, invisible until somebody drags one
///         half of it. The tolerance is relative to the mesh's own size, because a tolerance in metres
///         is one that is wrong for anything not built at the scale it was chosen for.
///     </para>
/// </remarks>
public sealed class MeshElements {
    /// <summary>How far apart two positions may be and still be one, as a fraction of the bounds.</summary>
    /// <remarks>
    ///     ⚠ <b>Far below anything a designer would call close.</b> This is for floating-point noise
    ///     in a generator, not for cleaning up a mesh — welding by <i>distance</i> is a verb in doc
    ///     24's inventory, it has a settings popover, and it belongs to the user rather than to the
    ///     structure they are pointing at. A tenth of a millimetre on a metre box is four orders of
    ///     magnitude below the nearest pair any primitive here produces.
    /// </remarks>
    public const float DefaultWeldTolerance = 1e-4f;

    readonly Vector3[] positions;
    readonly int[] triangles;
    readonly MeshEdge[] edges;

    MeshElements(Vector3[] positions, int[] triangles, MeshEdge[] edges) {
        this.positions = positions;
        this.triangles = triangles;
        this.edges = edges;
    }

    /// <summary>The shared positions, each one exactly once.</summary>
    public ReadOnlySpan<Vector3> Positions => positions;

    /// <summary>Three <see cref="Positions" /> indices per face, in the mesh's winding order.</summary>
    public ReadOnlySpan<int> Triangles => triangles;

    /// <summary>Every edge of every face, each one exactly once.</summary>
    public ReadOnlySpan<MeshEdge> Edges => edges;

    /// <summary>How many faces there are.</summary>
    public int FaceCount => triangles.Length / 3;

    /// <summary>How many edges there are.</summary>
    public int EdgeCount => edges.Length;

    /// <summary>How many shared positions there are.</summary>
    public int PositionCount => positions.Length;

    /// <summary>Derives the elements of a mesh.</summary>
    /// <param name="mesh">The mesh, whose indices are triangles.</param>
    /// <param name="tolerance">
    ///     How far apart two positions may be and still be one, as a fraction of the bounds' diagonal.
    ///     Zero or less welds by exact equality.
    /// </param>
    /// <returns>The elements.</returns>
    /// <remarks>
    ///     ⚠ <b>A degenerate triangle — one that names the same position twice after welding — is kept
    ///     as a face and contributes no edge.</b> Dropping it would renumber every face after it, so a
    ///     selection made against the drawn mesh would name a different one; and an edge from a
    ///     position to itself is a segment with no direction, which every downstream distance test
    ///     divides by zero on.
    /// </remarks>
    public static MeshElements From(MeshData mesh, float tolerance = DefaultWeldTolerance) {
        ArgumentNullException.ThrowIfNull(mesh);

        var extent = mesh.Bounds.Maximum - mesh.Bounds.Minimum;
        var epsilon = tolerance <= 0f ? 0f : extent.Length() * tolerance;

        var shared = Weld(mesh.Positions, epsilon, out var remap);
        var triangles = new int[mesh.Indices.Length / 3 * 3];

        for (var index = 0; index < triangles.Length; index++) {
            triangles[index] = remap[mesh.Indices[index]];
        }

        return new MeshElements(shared, triangles, Edged(triangles));
    }

    /// <summary>The unique edges of a triangle list.</summary>
    static MeshEdge[] Edged(int[] triangles) {
        var found = new HashSet<MeshEdge>();
        var ordered = new List<MeshEdge>();

        for (var index = 0; index + 2 < triangles.Length; index += 3) {
            Add(triangles[index], triangles[index + 1]);
            Add(triangles[index + 1], triangles[index + 2]);
            Add(triangles[index + 2], triangles[index]);
        }

        return [.. ordered];

        void Add(int a, int b) {
            if (a == b) {
                return;
            }

            var edge = a < b ? new MeshEdge(a, b) : new MeshEdge(b, a);

            // A list beside the set, because the order edges are discovered in is the order a test
            // and a drawn overlay both read them in, and a hash set's is whatever the hash says.
            if (found.Add(edge)) {
                ordered.Add(edge);
            }
        }
    }

    /// <summary>Collapses coincident positions, and says where each original one went.</summary>
    /// <remarks>
    ///     ⚠ <b>A grid of cells the size of the tolerance, and the twenty-seven around each one are
    ///     searched.</b> Hashing the rounded position alone is one line shorter and wrong in exactly
    ///     the case this exists for: two positions a hair apart either side of a cell boundary land in
    ///     different buckets and are never compared, so the seam it was written to close stays open on
    ///     whichever meshes happen to straddle one.
    /// </remarks>
    static Vector3[] Weld(Vector3[] source, float epsilon, out int[] remap) {
        remap = new int[source.Length];

        var shared = new List<Vector3>(source.Length);

        if (epsilon <= 0f) {
            var exact = new Dictionary<Vector3, int>(source.Length);

            for (var index = 0; index < source.Length; index++) {
                if (!exact.TryGetValue(source[index], out var at)) {
                    at = shared.Count;
                    shared.Add(source[index]);
                    exact[source[index]] = at;
                }

                remap[index] = at;
            }

            return [.. shared];
        }

        var cells = new Dictionary<(long X, long Y, long Z), List<int>>();
        var squared = epsilon * epsilon;

        for (var index = 0; index < source.Length; index++) {
            var position = source[index];
            var cell = Cell(position, epsilon);
            var at = -1;

            for (var x = -1; at < 0 && x <= 1; x++) {
                for (var y = -1; at < 0 && y <= 1; y++) {
                    for (var z = -1; at < 0 && z <= 1; z++) {
                        if (!cells.TryGetValue((cell.X + x, cell.Y + y, cell.Z + z), out var bucket)) {
                            continue;
                        }

                        foreach (var candidate in bucket) {
                            if (Vector3.DistanceSquared(shared[candidate], position) <= squared) {
                                at = candidate;
                                break;
                            }
                        }
                    }
                }
            }

            if (at < 0) {
                at = shared.Count;
                shared.Add(position);

                if (!cells.TryGetValue(cell, out var bucket)) {
                    cells[cell] = bucket = [];
                }

                bucket.Add(at);
            }

            remap[index] = at;
        }

        return [.. shared];
    }

    static (long X, long Y, long Z) Cell(Vector3 position, float size) =>
        ((long) MathF.Floor(position.X / size), (long) MathF.Floor(position.Y / size),
            (long) MathF.Floor(position.Z / size));
}
