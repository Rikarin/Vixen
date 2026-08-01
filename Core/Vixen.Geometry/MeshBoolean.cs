// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry;

/// <summary>Which boolean two solids are combined by.</summary>
public enum BooleanOperation : byte {
    /// <summary>Everything in either of them.</summary>
    Union,

    /// <summary>Everything in the first that is not in the second.</summary>
    Difference,

    /// <summary>Only what is in both.</summary>
    Intersection
}

/// <summary>Union, difference and intersection over two solids, and the cuts that are half of one.</summary>
/// <remarks>
///     <para>
///         <b>Doc 24's P6, and the phase it says to budget as though it were two.</b> Every mesh
///         boolean that produces holes produces them because a classification near zero answered
///         wrongly and the two sides of a cut stopped agreeing about which side they were on. That is
///         not a tolerance to tune; it is a question with an exact answer, and
///         <see cref="ExactPredicates.Orient3D" /> gives it.
///     </para>
///     <para>
///         ⚠ <b>A plane is three points, and they are the ones that defined it.</b> Doc 24 asks for
///         planes rather than points, and this is the shape that takes: a face's supporting plane is
///         recorded as three corners of the <i>original</i> operand and never recomputed, so
///         classifying any original vertex against any original plane is one exact predicate over
///         inputs that were never arithmetic. A normal and an offset derived in floating point would
///         be a fourth number that disagrees with the three it came from, which is precisely the
///         disagreement that opens a crack between a wall and the floor it is coplanar with.
///     </para>
///     <para>
///         ⚠ <b>A vertex made by a split <i>remembers</i> the plane it was made on, and that is what
///         keeps the cut closed.</b> Its position is arithmetic and so is inexact; its membership is
///         not. Asked which side of that plane it is on, it answers "on it" from the record rather
///         than from a subtraction — for ever, through every later split — so the two faces either
///         side of a cut can never be told apart by it. A point on a segment also lies on every plane
///         <i>both</i> its endpoints lie on, so membership propagates through a split rather than
///         being rediscovered.
///     </para>
///     <para>
///         ⚠ <b>What that does not buy: a split vertex against a plane it was not made on.</b> Three
///         planes meeting at a point is a position computed in floating point, and its side of a
///         fourth plane is a floating-point question — the fully plane-based answer is a
///         four-plane determinant, which is a predicate this engine does not have. What is exact is
///         every original vertex against every original plane, and every derived vertex against the
///         planes it was derived on, which between them are the cases a block-out is made of: coplanar
///         walls, shared edges, identical operands, and an operand sitting exactly on another's face.
///     </para>
///     <para>
///         <b>A BSP over the faces, which is the algorithm rather than the interesting part.</b> Each
///         solid is built into a tree of its own planes, each is clipped against the other's, and what
///         survives is combined per the operation. It is Naylor–Amanatides–Thibault, and it is what
///         csg.js, Quake's tooling and RealtimeCSG all are; the difference between one that works and
///         one that does not is entirely in the classification above.
///     </para>
/// </remarks>
public static class MeshBoolean {
    /// <summary>Combines two solids.</summary>
    /// <param name="left">The first, whose space the result is in.</param>
    /// <param name="right">The second.</param>
    /// <param name="operation">Which boolean.</param>
    /// <param name="transform">Where the second sits in the first's space, or null for the same space.</param>
    /// <returns>The result, or <see langword="null" /> when there is nothing left of it.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Null rather than an empty mesh when the result is nothing.</b> Subtracting a solid
    ///         from itself is a legitimate thing to ask for and its answer is that there is no solid —
    ///         which a caller has to be able to tell from "the operation failed", because one deletes
    ///         an entity and the other must not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Face groups travel and smoothing groups travel with them.</b> The wall you
    ///         subtracted a doorway from still has its material on the faces that survived, and the
    ///         new faces the cut exposed are the <i>cutter's</i> groups, shifted so they cannot
    ///         collide — which is what makes "give the reveal a different material" a selection rather
    ///         than a rebuild.
    ///     </para>
    /// </remarks>
    public static EditMesh? Apply(
        EditMesh left,
        EditMesh right,
        BooleanOperation operation,
        Matrix4x4? transform = null
    ) {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (left.IsEmpty) {
            return operation == BooleanOperation.Union ? Copy(right, transform, Shift(left)) : null;
        }

        if (right.IsEmpty) {
            return operation == BooleanOperation.Intersection ? null : new EditMesh(left);
        }

        var planes = new PlaneTable();

        var first = Read(left, planes, null, 0);
        var second = Read(right, planes, transform, Shift(left));

        var one = new BspNode();
        var two = new BspNode();

        one.Build(planes, first);
        two.Build(planes, second);

        // Naylor–Amanatides–Thibault, in the arrangement csg.js made canonical. Each step is a
        // statement about which parts of one solid survive inside or outside the other; the inversions
        // are what turn "outside" into "inside" without a second traversal.
        switch (operation) {
            case BooleanOperation.Difference:
                one.Invert(planes);
                one.ClipTo(planes, two);
                two.ClipTo(planes, one);
                two.Invert(planes);
                two.ClipTo(planes, one);
                two.Invert(planes);
                one.Build(planes, two.All());
                one.Invert(planes);

                break;

            case BooleanOperation.Intersection:
                one.Invert(planes);
                two.ClipTo(planes, one);
                two.Invert(planes);
                one.ClipTo(planes, two);
                two.ClipTo(planes, one);
                one.Build(planes, two.All());
                one.Invert(planes);

                break;

            default:
                one.ClipTo(planes, two);
                two.ClipTo(planes, one);
                two.Invert(planes);
                two.ClipTo(planes, one);
                two.Invert(planes);
                one.Build(planes, two.All());

                break;
        }

        return Assemble(one.All());
    }

    /// <summary>Cuts a solid with a plane and keeps one side of it, or both.</summary>
    /// <param name="mesh">The solid.</param>
    /// <param name="plane">Where to cut, in the mesh's own space.</param>
    /// <param name="keepFront">Whether the half the normal points at is the one that survives.</param>
    /// <param name="cap">Whether the opening is closed with a face.</param>
    /// <returns>The result, or <see langword="null" /> when nothing survived.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 24's plane cut, and it is the boolean with a half-space as the second
    ///         operand.</b> Written as its own routine rather than by building a very large box,
    ///         because the box would have to be bigger than the mesh and choosing "bigger" is a
    ///         number nobody can defend — and because a half-space has one plane, so the classification
    ///         is one predicate per vertex rather than a tree walk.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Capping is what makes the result a solid, and it is not free.</b> The opening is a
    ///         set of edges on the cut plane which have to be walked into loops; a mesh whose cut
    ///         produces two separate openings gets two caps, and one whose opening is not a simple
    ///         loop gets what the walk could make of it. An uncapped cut is a surface rather than a
    ///         solid and is the honest answer when the caller wants one — the trim below does.
    ///     </para>
    /// </remarks>
    public static EditMesh? PlaneCut(EditMesh mesh, Plane plane, bool keepFront = false, bool cap = true) {
        ArgumentNullException.ThrowIfNull(mesh);

        if (mesh.IsEmpty || plane.Normal.IsZero) {
            return null;
        }

        var planes = new PlaneTable();
        var faces = Read(mesh, planes, null, 0);

        var normal = Vector3.Normalize(plane.Normal);
        var origin = normal * -plane.D;

        // Three points that span the plane, which is the form the exact predicate takes. They are
        // derived rather than original — a caller's plane is a normal and an offset — so a cut is the
        // one operation here whose *own* plane is not exact. What stays exact is everything after it:
        // the vertices it makes remember it, and every later question about them answers from the
        // record.
        var across = Perpendicular(normal);
        var along = Vector3.Cross(normal, across);

        // ⚠ Across then along, in that order. `PlaneTable.Add` takes its normal from
        // `(b − a) × (c − a)`, and the other order gives the plane the caller asked for facing the
        // opposite way — which silently swaps which half `keepFront` means and is invisible in any
        // test that cuts a symmetrical solid down the middle.
        var cutter = planes.Add(origin, origin + across, origin + along);
        var kept = new List<BooleanFace>();
        var rim = new List<(BooleanVertex A, BooleanVertex B)>();

        foreach (var face in faces) {
            Split(planes, cutter, face, kept, kept, keepFront ? kept : null, keepFront ? null : kept, rim);
        }

        if (kept.Count == 0) {
            return null;
        }

        // ⚠ A cut that keeps only faces lying *in* the cutting plane has kept a sheet rather than a
        // solid, and the honest answer is that there is nothing left. Cutting a box along the floor it
        // stands on and asking for the half below it is exactly this: the bottom face is coplanar and
        // survives the classification, and a caller handed one face would be handed a solid with no
        // volume rather than told the operation removed everything.
        var geometry = planes.GeometryOf(cutter);

        if (kept.TrueForAll(face => planes.GeometryOf(face.Plane) == geometry)) {
            return null;
        }

        if (cap) {
            // ⚠ The rim is collected from the *front* part of every split, so walking it in that
            // direction closes the back half — each cap edge runs opposite to the kept face that
            // shares it, which is what a closed surface means. Keeping the front half wants the other
            // direction, and reversing the edges is the whole of the difference.
            if (keepFront) {
                for (var edge = 0; edge < rim.Count; edge++) {
                    rim[edge] = (rim[edge].B, rim[edge].A);
                }
            }

            Cap(planes, keepFront ? planes.Opposite(cutter) : cutter, rim, kept);
        }

        return Assemble(kept);
    }

    /// <summary>Cuts a solid by another one's surface and keeps the part outside it.</summary>
    /// <param name="mesh">The solid to cut.</param>
    /// <param name="cutter">The one to cut it with.</param>
    /// <param name="transform">Where the cutter sits in the mesh's space, or null for the same space.</param>
    /// <returns>The result, or <see langword="null" /> when nothing survived.</returns>
    /// <remarks>
    ///     ⚠ <b>Doc 24's own words for why this is not just a subtract: "cheaper, and what most 'cut a
    ///     doorway' actually wants".</b> A subtract closes the hole it makes with the cutter's inside
    ///     surface, so subtracting a box from a wall gives you a doorway with a reveal. A trim removes
    ///     the material and leaves the opening bare, which is what somebody trimming a wall against a
    ///     terrain or a roofline means — and it is the difference between a wall with a hole in it and
    ///     a wall with a box-shaped tunnel through it.
    /// </remarks>
    public static EditMesh? Trim(EditMesh mesh, EditMesh cutter, Matrix4x4? transform = null) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(cutter);

        if (mesh.IsEmpty || cutter.IsEmpty) {
            return mesh.IsEmpty ? null : new EditMesh(mesh);
        }

        var planes = new PlaneTable();

        var kept = new BspNode();
        var against = new BspNode();

        kept.Build(planes, Read(mesh, planes, null, 0));
        against.Build(planes, Read(cutter, planes, transform, Shift(mesh)));

        // Everything of the first solid that is outside the second, and nothing of the second — which
        // is a subtract with its last two steps left off.
        kept.Invert(planes);
        kept.ClipTo(planes, against);
        kept.Invert(planes);

        var faces = kept.All();

        return faces.Count == 0 ? null : Assemble(faces);
    }

    /// <summary>How far a second operand's face groups are moved so they cannot collide with the first's.</summary>
    static int Shift(EditMesh mesh) {
        var highest = 0;

        foreach (var face in mesh.Faces) {
            highest = Math.Max(highest, face.Group);
        }

        return highest + 1;
    }

    static EditMesh Copy(EditMesh mesh, Matrix4x4? transform, int shift) {
        var made = new EditMesh();

        MeshOperations.Append(made, mesh, transform ?? Matrix4x4.Identity);

        for (var face = 0; face < made.FaceCount; face++) {
            made.SetGroup(face, made.Faces[face].Group + shift);
        }

        return made;
    }

    /// <summary>Turns a mesh into faces with planes, in whichever space the caller wants them.</summary>
    static List<BooleanFace> Read(EditMesh mesh, PlaneTable planes, Matrix4x4? transform, int shift) {
        var faces = new List<BooleanFace>(mesh.FaceCount);
        var matrix = transform ?? Matrix4x4.Identity;

        for (var face = 0; face < mesh.FaceCount; face++) {
            var entry = mesh.Faces[face];

            if (entry.Count < 3) {
                continue;
            }

            var corners = new BooleanVertex[entry.Count];

            for (var corner = 0; corner < entry.Count; corner++) {
                var position = mesh.Positions[mesh.Corners[entry.Start + corner]];

                corners[corner] = new(transform is null ? position : Matrix4x4.TransformPosition(position, matrix), []);
            }

            // ⚠ The plane's three points come from the face's own corners and are chosen so that they
            // are not collinear — the first, and the two that span the most area with it. A face whose
            // corners are all in a line has no plane and is dropped, which is the only geometry this
            // routine refuses.
            if (!Spanning(corners, out var a, out var b, out var c)) {
                continue;
            }

            var plane = planes.Add(corners[a].Position, corners[b].Position, corners[c].Position);
            var geometry = planes.GeometryOf(plane);

            for (var corner = 0; corner < corners.Length; corner++) {
                corners[corner] = corners[corner] with { Planes = [geometry] };
            }

            faces.Add(new(corners, plane, entry.Group + shift, entry.Smoothing));
        }

        return faces;
    }

    /// <summary>Three corners of a face that are not collinear, preferring the ones furthest apart.</summary>
    static bool Spanning(BooleanVertex[] corners, out int a, out int b, out int c) {
        a = 0;
        b = -1;
        c = -1;

        var best = 0f;

        for (var second = 1; second < corners.Length; second++) {
            for (var third = second + 1; third < corners.Length; third++) {
                var area = Vector3.Cross(
                    corners[second].Position - corners[0].Position,
                    corners[third].Position - corners[0].Position
                ).LengthSquared();

                if (area > best) {
                    best = area;
                    b = second;
                    c = third;
                }
            }
        }

        return best > 0f && b >= 0 && c >= 0;
    }

    /// <summary>Any unit vector at a right angle to another one.</summary>
    static Vector3 Perpendicular(Vector3 normal) {
        var axis = MathF.Abs(normal.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;

        return Vector3.Normalize(Vector3.Cross(normal, axis));
    }

    /// <summary>Splits one face against a plane, into whichever of the four buckets each part belongs.</summary>
    /// <param name="planes">The plane table both the face and the cutter are recorded in.</param>
    /// <param name="plane">Which plane to cut against.</param>
    /// <param name="face">The face.</param>
    /// <param name="coplanarFront">Where a face in this plane facing the same way goes.</param>
    /// <param name="coplanarBack">And one facing the other way.</param>
    /// <param name="front">Where the part in front of the plane goes.</param>
    /// <param name="back">And the part behind it.</param>
    /// <param name="rim">The edges the split produced, on the plane, for a cap. Null to not collect them.</param>
    internal static void Split(
        PlaneTable planes,
        int plane,
        BooleanFace face,
        List<BooleanFace>? coplanarFront,
        List<BooleanFace>? coplanarBack,
        List<BooleanFace>? front,
        List<BooleanFace>? back,
        List<(BooleanVertex A, BooleanVertex B)>? rim = null
    ) {
        const int On = 0;
        const int Front = 1;
        const int Back = 2;

        var sides = new int[face.Corners.Length];
        var all = 0;

        for (var corner = 0; corner < face.Corners.Length; corner++) {
            var side = planes.Side(plane, face.Corners[corner]);

            sides[corner] = side > 0 ? Front : side < 0 ? Back : On;
            all |= sides[corner];
        }

        switch (all) {
            case On:
                // ⚠ A coplanar face goes with whichever side its own normal agrees with, and this is
                // the case every point-based boolean gets wrong. A block-out is made of coplanar
                // faces — every wall meets every floor flush with something — so "which of the two
                // surfaces sitting in this plane is the outside" has to be answered by the *direction*
                // they face rather than by a distance that is zero for both.
                (planes.Agree(plane, face.Plane) ? coplanarFront : coplanarBack)?.Add(face);
                break;

            case Front:
                front?.Add(face);
                break;

            case Back:
                back?.Add(face);
                break;

            default:
                List<BooleanVertex> ahead = [];
                List<BooleanVertex> behind = [];

                for (var corner = 0; corner < face.Corners.Length; corner++) {
                    var next = (corner + 1) % face.Corners.Length;

                    var here = face.Corners[corner];
                    var there = face.Corners[next];

                    if (sides[corner] != Back) {
                        ahead.Add(here);
                    }

                    if (sides[corner] != Front) {
                        behind.Add(here);
                    }

                    if ((sides[corner] | sides[next]) != (Front | Back)) {
                        continue;
                    }

                    var made = planes.Between(plane, here, there);

                    ahead.Add(made);
                    behind.Add(made);
                }

                if (ahead.Count >= 3) {
                    front?.Add(face with { Corners = [.. ahead] });
                }

                if (behind.Count >= 3) {
                    back?.Add(face with { Corners = [.. behind] });
                }

                Rim(planes, plane, ahead, rim);

                break;
        }
    }

    /// <summary>The one edge of a split part that lies in the cutting plane, for a cap to be built from.</summary>
    static void Rim(
        PlaneTable planes,
        int plane,
        List<BooleanVertex> part,
        List<(BooleanVertex A, BooleanVertex B)>? rim
    ) {
        if (rim is null || part.Count < 3) {
            return;
        }

        var geometry = planes.GeometryOf(plane);

        for (var corner = 0; corner < part.Count; corner++) {
            var next = (corner + 1) % part.Count;

            if (part[corner].Lies(geometry) && part[next].Lies(geometry)) {
                rim.Add((part[corner], part[next]));
            }
        }
    }

    /// <summary>Closes an opening by walking its rim edges into loops.</summary>
    /// <remarks>
    ///     ⚠ <b>Walked rather than fanned from a centroid.</b> A fan is right for a convex opening and
    ///     wrong for every other one, and a cut through an L-shaped room produces an L-shaped opening.
    ///     The walk gives the loop back as an n-gon, which <c>EditMesh.Triangulate</c> ear-clips
    ///     correctly whatever shape it is.
    /// </remarks>
    static void Cap(
        PlaneTable planes,
        int plane,
        List<(BooleanVertex A, BooleanVertex B)> rim,
        List<BooleanFace> into
    ) {
        if (rim.Count < 3) {
            return;
        }

        Dictionary<Quantised, List<int>> starts = [];

        for (var edge = 0; edge < rim.Count; edge++) {
            var key = Quantised.Of(rim[edge].A.Position);

            if (!starts.TryGetValue(key, out var list)) {
                list = [];
                starts[key] = list;
            }

            list.Add(edge);
        }

        var used = new bool[rim.Count];

        for (var edge = 0; edge < rim.Count; edge++) {
            if (used[edge]) {
                continue;
            }

            List<BooleanVertex> loop = [];

            var at = edge;
            var guard = 0;

            while (!used[at] && guard++ <= rim.Count) {
                used[at] = true;
                loop.Add(rim[at].A);

                if (!starts.TryGetValue(Quantised.Of(rim[at].B.Position), out var candidates)) {
                    break;
                }

                var moved = false;

                foreach (var candidate in candidates) {
                    if (used[candidate]) {
                        continue;
                    }

                    at = candidate;
                    moved = true;

                    break;
                }

                if (!moved) {
                    break;
                }
            }

            if (loop.Count >= 3) {
                // ⚠ The cap is wound against the half being kept, because it is the new *outside* of
                // the solid: the opening faces away from the material that is left. Group `CapGroup`
                // so that "select the face the cut made" is one click.
                into.Add(new([.. loop], plane, CapGroup, 0));
            }
        }
    }

    /// <summary>The face group a plane cut's cap goes in.</summary>
    /// <remarks>High enough that it does not collide with a hand-made group on either operand, and
    ///     the same number every time so that a material assigned to a cut face survives the next
    ///     cut.</remarks>
    public const int CapGroup = 1000;

    /// <summary>Turns the surviving faces back into a mesh, welded and cleaned.</summary>
    static EditMesh? Assemble(List<BooleanFace> faces) {
        var mesh = new EditMesh();

        if (faces.Count == 0) {
            return null;
        }

        Dictionary<Quantised, int> positions = [];
        List<int> loop = [];

        foreach (var face in faces) {
            loop.Clear();

            foreach (var corner in face.Corners) {
                var key = Quantised.Of(corner.Position);

                if (!positions.TryGetValue(key, out var index)) {
                    index = mesh.AddPosition(corner.Position);
                    positions[key] = index;
                }

                // ⚠ Consecutive duplicates dropped as they arrive rather than afterwards. A split that
                // lands exactly on a corner produces the same position twice in a row, which is a
                // zero-length edge — and a zero-length edge in the edge table is a neighbour
                // relationship that is true of nothing.
                if (loop.Count == 0 || loop[^1] != index) {
                    loop.Add(index);
                }
            }

            if (loop.Count >= 3 && loop[0] == loop[^1]) {
                loop.RemoveAt(loop.Count - 1);
            }

            if (loop.Count >= 3) {
                mesh.AddFace([.. loop], face.Group, face.Smoothing);
            }
        }

        if (mesh.IsEmpty) {
            return null;
        }

        // Faces of no area are the residue of a cut that grazed a corner, and they are dropped rather
        // than kept: one has no normal, so every question about which way it faces is unanswerable.
        List<int> degenerate = [];

        for (var face = 0; face < mesh.FaceCount; face++) {
            if (mesh.Area(face) <= 0f) {
                degenerate.Add(face);
            }
        }

        if (degenerate.Count > 0) {
            MeshOperations.Delete(mesh, degenerate);
        }

        // ⚠ And the T-junctions a cut makes by construction. A plane splits every face it crosses and
        // no face it merely touches, so the face beside a cut face keeps an edge whose middle is now
        // somebody else's corner — which every BSP boolean produces and most of them ignore. An
        // editable mesh cannot: the next extrude walks an edge table that says the two are strangers.
        MeshOperations.Stitch(mesh);

        return mesh.IsEmpty ? null : mesh;
    }

    /// <summary>A position rounded to a grid fine enough to be a hash key and coarse enough to weld.</summary>
    /// <remarks>
    ///     ⚠ <b>Only ever used to <i>join</i> two positions, never to decide a side.</b> Welding is
    ///     where a tolerance is honest — two corners a nanometre apart are one corner and a designer
    ///     will never say otherwise — and classification is where it is not, which is why that question
    ///     goes to the exact predicate instead.
    /// </remarks>
    readonly record struct Quantised(long X, long Y, long Z) {
        const float Grid = 1e5f;

        public static Quantised Of(Vector3 point) =>
            new(
                (long) MathF.Round(point.X * Grid),
                (long) MathF.Round(point.Y * Grid),
                (long) MathF.Round(point.Z * Grid)
            );
    }
}

/// <summary>One vertex of a face being cut: where it is, and which planes it is known to lie on.</summary>
/// <param name="Position">Where, which for a derived vertex is arithmetic and so is inexact.</param>
/// <param name="Planes">
///     The geometric planes it lies on, which for a derived vertex is a record rather than a
///     computation — see <see cref="MeshBoolean" />.
/// </param>
readonly record struct BooleanVertex(Vector3 Position, int[] Planes) {
    /// <summary>Whether this vertex is known to lie on a plane.</summary>
    public bool Lies(int geometry) {
        foreach (var plane in Planes) {
            if (plane == geometry) {
                return true;
            }
        }

        return false;
    }
}

/// <summary>One face while it is being cut, with everything an <c>EditMesh</c> will want back.</summary>
sealed record BooleanFace(BooleanVertex[] Corners, int Plane, int Group, int Smoothing);

/// <summary>Every plane either operand's faces lie in, each stored as the points that defined it.</summary>
/// <remarks>
///     <para>
///         <b>Two identities per plane, and both are load-bearing.</b> An <i>oriented</i> plane is
///         which way a face looks; a <i>geometric</i> plane is where the surface is. A face's front is
///         a statement about the first, and a vertex's membership is a statement about the second — so
///         a wall and the floor it is flush with share a geometric plane while facing opposite ways,
///         which is exactly the case a boolean has to get right.
///     </para>
///     <para>
///         ⚠ <b>Deduplicated exactly, not by a tolerance.</b> Two planes are the same when each one's
///         three defining points are coplanar with the other's, which is three calls to the predicate.
///         The floating-point normal is used only to bucket the candidates so the comparison is not
///         quadratic — a wrong bucket costs a duplicate plane, which is slower and still correct,
///         where a wrong *comparison* would be a crack.
///     </para>
/// </remarks>
sealed class PlaneTable {
    readonly List<Vector3> a = [];
    readonly List<Vector3> b = [];
    readonly List<Vector3> c = [];
    readonly List<Vector3> normals = [];
    readonly List<float> offsets = [];
    readonly List<int> geometry = [];
    readonly List<int> opposites = [];
    readonly Dictionary<long, List<int>> buckets = [];

    /// <summary>Records a plane through three points, or finds the one that is already there.</summary>
    public int Add(Vector3 first, Vector3 second, Vector3 third) {
        var normal = Vector3.Cross(second - first, third - first);

        if (normal.IsZero) {
            return -1;
        }

        normal = Vector3.Normalize(normal);

        var key = Bucket(normal);

        if (buckets.TryGetValue(key, out var candidates)) {
            foreach (var plane in candidates) {
                if (Same(plane, first, second, third) && Vector3.Dot(normals[plane], normal) > 0f) {
                    return plane;
                }
            }
        } else {
            candidates = [];
            buckets[key] = candidates;
        }

        var made = normals.Count;

        a.Add(first);
        b.Add(second);
        c.Add(third);
        normals.Add(normal);
        offsets.Add(-Vector3.Dot(normal, first));
        opposites.Add(-1);

        // The geometric identity is shared with whichever plane already occupies this surface facing
        // the other way, and minted otherwise.
        var shared = -1;

        if (buckets.TryGetValue(Bucket(-normal), out var others)) {
            foreach (var plane in others) {
                if (Same(plane, first, second, third)) {
                    shared = geometry[plane];
                    opposites[plane] = made;
                    opposites[made] = plane;

                    break;
                }
            }
        }

        geometry.Add(shared >= 0 ? shared : made);
        candidates.Add(made);

        return made;
    }

    /// <summary>The same surface, facing the other way.</summary>
    public int Opposite(int plane) {
        if (opposites[plane] >= 0) {
            return opposites[plane];
        }

        var made = Add(a[plane], c[plane], b[plane]);

        opposites[plane] = made;
        opposites[made] = plane;

        return made;
    }

    /// <summary>Which surface an oriented plane is on, whichever way it faces.</summary>
    public int GeometryOf(int plane) => geometry[plane];

    /// <summary>Whether two oriented planes face the same way.</summary>
    public bool Agree(int left, int right) =>
        left == right || (geometry[left] == geometry[right] && Vector3.Dot(normals[left], normals[right]) > 0f);

    /// <summary>Which side of a plane a vertex is on: +1 in front, −1 behind, 0 on it.</summary>
    /// <remarks>
    ///     ⚠ <b>The record first, and the predicate only if there is no record.</b> A vertex made by
    ///     splitting on this surface answers zero without any arithmetic at all — which is what makes
    ///     the two faces either side of a cut agree about it for ever, through every later split. The
    ///     predicate's sign is negated because <c>Orient3D</c> is positive on the side the normal
    ///     points <i>away</i> from.
    /// </remarks>
    public int Side(int plane, BooleanVertex vertex) =>
        vertex.Lies(geometry[plane]) ? 0 : -ExactPredicates.Orient3D(a[plane], b[plane], c[plane], vertex.Position);

    /// <summary>The vertex where a segment crosses a plane.</summary>
    /// <remarks>
    ///     ⚠ <b>It inherits the planes both ends lie on, as well as the one it was made on.</b> A
    ///     plane is flat, so every point of a segment whose ends are both on it is on it too — and
    ///     carrying that through a split is what stops a vertex on a shared edge forgetting one of its
    ///     two surfaces the first time something cuts across it.
    /// </remarks>
    public BooleanVertex Between(int plane, BooleanVertex from, BooleanVertex to) {
        var normal = normals[plane];
        var offset = offsets[plane];

        var above = Vector3.Dot(normal, from.Position) + offset;
        var below = Vector3.Dot(normal, to.Position) + offset;
        var span = above - below;

        var at = MathF.Abs(span) > 1e-20f ? Math.Clamp(above / span, 0f, 1f) : 0.5f;
        var position = Vector3.Lerp(from.Position, to.Position, at);

        List<int> shared = [geometry[plane]];

        foreach (var carried in from.Planes) {
            if (carried != geometry[plane] && to.Lies(carried)) {
                shared.Add(carried);
            }
        }

        return new(position, [.. shared]);
    }

    /// <summary>Whether a recorded plane is the same surface as three given points.</summary>
    static bool Same(PlaneTable table, int plane, Vector3 first, Vector3 second, Vector3 third) =>
        ExactPredicates.Orient3D(table.a[plane], table.b[plane], table.c[plane], first) == 0
        && ExactPredicates.Orient3D(table.a[plane], table.b[plane], table.c[plane], second) == 0
        && ExactPredicates.Orient3D(table.a[plane], table.b[plane], table.c[plane], third) == 0;

    bool Same(int plane, Vector3 first, Vector3 second, Vector3 third) => Same(this, plane, first, second, third);

    /// <summary>A coarse key for a direction, so the exact comparison runs over a handful of candidates.</summary>
    static long Bucket(Vector3 normal) {
        const float Grid = 64f;

        var x = (long) MathF.Round(normal.X * Grid);
        var y = (long) MathF.Round(normal.Y * Grid);
        var z = (long) MathF.Round(normal.Z * Grid);

        return ((x + 128) * 1_000_000) + ((y + 128) * 1_000) + z + 128;
    }
}

/// <summary>One node of the tree a solid is built into: a plane, its faces, and what is either side.</summary>
sealed class BspNode {
    int plane = -1;
    BspNode? front;
    BspNode? back;
    readonly List<BooleanFace> faces = [];

    /// <summary>Adds faces to the tree, choosing this node's plane from the first of them.</summary>
    /// <remarks>
    ///     ⚠ <b>The first face's plane rather than a balanced choice, which is csg.js's arrangement
    ///     and is deliberate here.</b> A splitting heuristic that minimises cuts makes a smaller tree
    ///     and makes the result depend on the order the faces arrived in — so a boolean would give
    ///     different geometry for the same two solids depending on which was authored first. Block-out
    ///     operands are tens of faces, where the tree's shape costs nothing and its determinism is
    ///     worth having.
    /// </remarks>
    public void Build(PlaneTable planes, List<BooleanFace> input) {
        if (input.Count == 0) {
            return;
        }

        if (plane < 0) {
            plane = input[0].Plane;
        }

        List<BooleanFace> ahead = [];
        List<BooleanFace> behind = [];

        foreach (var face in input) {
            MeshBoolean.Split(planes, plane, face, faces, faces, ahead, behind);
        }

        if (ahead.Count > 0) {
            front ??= new();
            front.Build(planes, ahead);
        }

        if (behind.Count > 0) {
            back ??= new();
            back.Build(planes, behind);
        }
    }

    /// <summary>Removes every part of this tree's faces that is inside another solid.</summary>
    public void ClipTo(PlaneTable planes, BspNode other) {
        var kept = other.Clip(planes, faces);

        faces.Clear();
        faces.AddRange(kept);

        front?.ClipTo(planes, other);
        back?.ClipTo(planes, other);
    }

    /// <summary>What is left of some faces once the parts inside this solid are taken away.</summary>
    /// <remarks>
    ///     ⚠ <b>A node with no back child drops everything behind it, and that is the whole
    ///     definition of "inside".</b> The tree is a convex decomposition of the solid's interior:
    ///     falling out of the back of every plane with nothing below means the point is enclosed by
    ///     all of them.
    /// </remarks>
    public List<BooleanFace> Clip(PlaneTable planes, List<BooleanFace> input) {
        if (plane < 0) {
            return [.. input];
        }

        List<BooleanFace> ahead = [];
        List<BooleanFace> behind = [];

        foreach (var face in input) {
            MeshBoolean.Split(planes, plane, face, ahead, behind, ahead, behind);
        }

        var kept = front is null ? ahead : front.Clip(planes, ahead);

        if (back is not null) {
            kept.AddRange(back.Clip(planes, behind));
        }

        return kept;
    }

    /// <summary>Turns the solid inside out.</summary>
    public void Invert(PlaneTable planes) {
        for (var face = 0; face < faces.Count; face++) {
            faces[face] = Flip(planes, faces[face]);
        }

        if (plane >= 0) {
            plane = planes.Opposite(plane);
        }

        (front, back) = (back, front);

        front?.Invert(planes);
        back?.Invert(planes);
    }

    /// <summary>Every face in the tree.</summary>
    public List<BooleanFace> All() {
        List<BooleanFace> into = [];

        Collect(into);

        return into;
    }

    void Collect(List<BooleanFace> into) {
        into.AddRange(faces);

        front?.Collect(into);
        back?.Collect(into);
    }

    static BooleanFace Flip(PlaneTable planes, BooleanFace face) {
        var corners = new BooleanVertex[face.Corners.Length];

        for (var corner = 0; corner < corners.Length; corner++) {
            corners[corner] = face.Corners[corners.Length - 1 - corner];
        }

        return face with { Corners = corners, Plane = planes.Opposite(face.Plane) };
    }
}
