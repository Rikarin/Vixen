// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry;

/// <summary>Which shape a parametric entity is, before anybody edits it.</summary>
/// <remarks>
///     <para>
///         <b>Doc 24's Creation table, and the last five entries are the point of it.</b> Box,
///         cylinder, cone, sphere, capsule, torus and plane are the shapes every engine ships;
///         <see cref="Stairs" />, <see cref="Ramp" />, <see cref="Arch" />, <see cref="Pipe" /> and
///         <see cref="DoorFrame" /> are the ones that are tedious to build by hand, which is why Unreal
///         ships a Stairs tool and why a blockout toolset that stopped at the seven would send a
///         designer back to extruding a staircase one step at a time.
///     </para>
///     <para>
///         ⚠ <b>Deliberately not <c>PrimitiveKind</c>, and not an extension of it.</b> That enum is
///         <c>Vixen.Rendering</c>'s and says which of the built-in meshes an entity draws — it is a
///         four-byte value a thousand entities share one upload of, and its members are a file format
///         the runtime reads. These have <i>parameters</i>, produce one mesh per entity, and are the
///         editor's: a stair with ten steps and a stair with eleven are not the same geometry, so
///         nothing about the sharing that makes <c>PrimitiveKind</c> worth having applies.
///     </para>
/// </remarks>
public enum ShapeKind : byte {
    /// <summary>A rectangular box: six four-sided faces, one group each.</summary>
    Box,

    /// <summary>A flat rectangle in the ground plane, optionally divided into a grid.</summary>
    Plane,

    /// <summary>A round column with a cap at each end.</summary>
    Cylinder,

    /// <summary>A round column that comes to a point.</summary>
    Cone,

    /// <summary>A ball, as rings of quads between two fans.</summary>
    Sphere,

    /// <summary>A cylinder with a hemisphere on each end.</summary>
    Capsule,

    /// <summary>A ring of circular cross-section.</summary>
    Torus,

    /// <summary>A flight of steps, as one closed solid rather than a pile of boxes.</summary>
    Stairs,

    /// <summary>A wedge: a box with one face sloped from the ground to the full height.</summary>
    Ramp,

    /// <summary>A wall with a round-topped opening through it.</summary>
    Arch,

    /// <summary>A tube with a bore down the middle and an annulus at each end.</summary>
    Pipe,

    /// <summary>A wall with a square-topped opening through it.</summary>
    DoorFrame
}

/// <summary>What a parametric shape is made of: a kind and the handful of numbers behind it.</summary>
/// <remarks>
///     <para>
///         <b>Doc 24's D6 in one struct.</b> A shape is created with live parameters and editing one
///         rebuilds it, because the overwhelmingly common blockout edit is <i>"that corridor should be
///         a metre wider"</i> — one number, not a face selection.
///     </para>
///     <para>
///         ⚠ <b>Six fields for twelve shapes, rather than a record per kind.</b> A discriminated union
///         would be tidier in the type system and worse in every other place this has to exist: the
///         scene file would carry a tagged variant per shape, the inspector would need a drawer per
///         shape, and adding a kind would touch all of them. What the fields <i>mean</i> is per kind
///         and is documented on each; what they are is a fixed record that reads, writes and diffs the
///         same way whatever the kind is.
///     </para>
///     <para>
///         ⚠ <b><see cref="Size" /> is the whole extent in world units, and the geometry carries
///         it.</b> <c>MeshPrimitives</c> builds everything to fit the unit cube so that a transform's
///         scale is the size, which is right when a thousand entities share one upload. It is wrong
///         here: a wall built as a unit cube scaled <c>8 3 0.2</c> has a non-uniform transform, so
///         every bevel it is given afterwards is wider on one axis than another and every texel on it
///         is stretched — which is the complaint doc 24's P5 opens with. A parametric shape is one
///         mesh per entity anyway, so the size goes in the mesh and the transform stays uniform.
///     </para>
///     <para>
///         ⚠ <b>Centred in X and Z, and sitting <i>on</i> the origin in Y.</b> Everything a blockout
///         tool makes is placed on the work plane, and a shape whose origin is its centre is one that
///         arrives half-buried in the floor. It also makes the parameter mean what it says for the
///         shapes that have no meaningful centre: a stair's rise is measured from the floor it starts
///         on.
///     </para>
/// </remarks>
public record struct ShapeParameters {
    /// <summary>Which shape.</summary>
    public ShapeKind Kind { get; set; }

    /// <summary>How big it is, across each axis, in world units.</summary>
    /// <remarks>
    ///     Y is unused by <see cref="ShapeKind.Plane" /> and is the tube's diameter for
    ///     <see cref="ShapeKind.Torus" />; everything else uses all three.
    /// </remarks>
    public Vector3 Size { get; set; }

    /// <summary>How many divisions around an axis, for the shapes that have one.</summary>
    /// <remarks>
    ///     The sides of a cylinder, cone, capsule or pipe; the meridians of a sphere; the segments
    ///     round a torus; the segments in an arch's curve. Three or more; clamped by
    ///     <see cref="Clamped" />.
    /// </remarks>
    public int Sides { get; set; }

    /// <summary>How many divisions along it.</summary>
    /// <remarks>
    ///     A staircase's steps, a plane's grid, a sphere's or capsule's rings, a torus's segments
    ///     round the tube. One or more.
    /// </remarks>
    public int Steps { get; set; }

    /// <summary>How much solid material is left, in world units.</summary>
    /// <remarks>
    ///     The header above an opening, for <see cref="ShapeKind.Arch" /> and
    ///     <see cref="ShapeKind.DoorFrame" />. Unused by everything else, and the one field that is a
    ///     length rather than a ratio — because "leave forty centimetres of wall above the door" is
    ///     what a designer means, and it must not change when the wall is made taller.
    /// </remarks>
    public float Thickness { get; set; }

    /// <summary>A ratio from zero to one, for the shapes that have a hole in them.</summary>
    /// <remarks>
    ///     A pipe's bore as a fraction of its radius, a torus's tube as a fraction of its ring, an
    ///     opening's width as a fraction of the wall's. A ratio rather than a length so that widening
    ///     a wall widens its doorway with it, which is what dragging a wall wider is nearly always
    ///     meant to do.
    /// </remarks>
    public float Inner { get; set; }

    /// <summary>The values a freshly created shape of a kind should have.</summary>
    /// <param name="kind">Which shape.</param>
    /// <returns>The parameters.</returns>
    /// <remarks>
    ///     ⚠ <b>Sized for a room rather than for a unit cube.</b> A shape tool that produced a
    ///     two-centimetre stair somewhere near the origin is one whose first act is to make the
    ///     designer type four numbers. These are the sizes of the things a block-out is made of: a
    ///     wall you can walk through, a step you can climb, a column you can hide behind.
    /// </remarks>
    public static ShapeParameters Default(ShapeKind kind) =>
        kind switch {
            ShapeKind.Plane => new() { Kind = kind, Size = new(4f, 0f, 4f), Sides = 16, Steps = 1 },
            ShapeKind.Cylinder => new() { Kind = kind, Size = new(1f, 2f, 1f), Sides = 16, Steps = 1 },
            ShapeKind.Cone => new() { Kind = kind, Size = new(1f, 2f, 1f), Sides = 16, Steps = 1 },
            ShapeKind.Sphere => new() { Kind = kind, Size = new(1f, 1f, 1f), Sides = 24, Steps = 12 },
            ShapeKind.Capsule => new() { Kind = kind, Size = new(1f, 2f, 1f), Sides = 16, Steps = 6 },
            ShapeKind.Torus => new() { Kind = kind, Size = new(2f, 0.5f, 2f), Sides = 24, Steps = 12, Inner = 0.25f },
            ShapeKind.Stairs => new() { Kind = kind, Size = new(2f, 2.5f, 4f), Sides = 16, Steps = 12 },
            ShapeKind.Ramp => new() { Kind = kind, Size = new(2f, 2f, 4f), Sides = 16, Steps = 1 },

            ShapeKind.Arch => new() {
                Kind = kind, Size = new(4f, 3.2f, 0.4f), Sides = 12, Steps = 1, Thickness = 0.4f, Inner = 0.5f
            },

            ShapeKind.Pipe => new() { Kind = kind, Size = new(1f, 2f, 1f), Sides = 16, Steps = 1, Inner = 0.7f },

            ShapeKind.DoorFrame => new() {
                Kind = kind, Size = new(4f, 3f, 0.4f), Sides = 16, Steps = 1, Thickness = 0.5f, Inner = 0.55f
            },

            _ => new() { Kind = kind, Size = new(2f, 2f, 2f), Sides = 16, Steps = 1 }
        };

    /// <summary>The same parameters, with everything a generator cannot survive brought into range.</summary>
    /// <returns>The clamped parameters.</returns>
    /// <remarks>
    ///     ⚠ <b>Clamped rather than validated, and the caller is a number field somebody is dragging
    ///     through zero.</b> An inspector that threw on a negative side count would throw once per
    ///     frame while a designer scrubbed a slider past it; one that refused the edit would make the
    ///     field impossible to type a two-digit number into. So the generator always produces geometry
    ///     and the field always accepts what was typed.
    /// </remarks>
    public readonly ShapeParameters Clamped() =>
        this with {
            Size = new(
                Math.Clamp(Size.X, MinimumSize, MaximumSize),
                Math.Clamp(Size.Y, Kind == ShapeKind.Plane ? 0f : MinimumSize, MaximumSize),
                Math.Clamp(Size.Z, MinimumSize, MaximumSize)
            ),

            Sides = Math.Clamp(Sides, 3, MaximumDivisions),
            Steps = Math.Clamp(Steps, 1, MaximumDivisions),
            Thickness = Math.Clamp(Thickness, 0f, MaximumSize),
            Inner = Math.Clamp(Inner, 0.02f, 0.98f)
        };

    /// <summary>The smallest extent a shape may have on any axis.</summary>
    /// <remarks>A tenth of a millimetre, which is <c>WorkPlane.MinimumStep</c>'s floor and is there
    ///     for the same reason: a zero extent is a shape with no faces and no way back.</remarks>
    public const float MinimumSize = 1e-4f;

    /// <summary>And the largest.</summary>
    public const float MaximumSize = 1e6f;

    /// <summary>The most divisions any one parameter may ask for.</summary>
    /// <remarks>
    ///     ⚠ <b>A limit on each rather than on their product, and it is low on purpose.</b> A sphere
    ///     at 512 × 512 is a quarter of a million faces in a structure built for a wall — every
    ///     selection walk, every validate and every save would be quadratic in a number somebody typed
    ///     by accident. Block-out geometry is meant to be replaced by an artist; a shape that needs
    ///     more divisions than this is one that should be a mesh asset.
    /// </remarks>
    public const int MaximumDivisions = 256;
}

/// <summary>Builds the shapes doc 24's Creation table names, as editable meshes.</summary>
/// <remarks>
///     <para>
///         <b>Quads wherever a quad is what the shape is made of, which is nearly everywhere.</b>
///         <c>EditMeshes.From(PrimitiveKind)</c> exists and welds a triangle soup back into a mesh;
///         what it cannot do is give back the four-sided faces the soup was built from, and an edge
///         loop, an edge ring and a loop cut are all statements about four-sided faces — see
///         <see cref="MeshTopology.EdgeRing" />. A cylinder made editable through the renderer's
///         primitive has no rings to cut; one built here does.
///     </para>
///     <para>
///         ⚠ <b>Face groups are assigned by what the face <i>is</i>, not by which way it points.</b> A
///         staircase's treads are group 0 and its risers group 1 whatever angle the flight is at, so
///         "select every tread" is one click and a material assigned to the treads survives the flight
///         being made steeper. <see cref="EditMesh.Regroup" /> is the other answer and is the right one
///         for a mesh that arrived from somewhere else; a generator knows better than a tolerance can.
///     </para>
///     <para>
///         ⚠ <b>Nothing here sets normals or texture coordinates.</b> A generated shape is flat shaded
///         by <c>EditMeshes.ToMeshData</c> from the faces themselves, and its UVs are
///         <see cref="MeshSurfaces" />' — which projects them in world space by default, so a wall and
///         the floor it stands on have squares of the same size without either of them carrying a
///         mapping the generator guessed at.
///     </para>
/// </remarks>
public static class MeshShapes {
    /// <summary>Builds a shape.</summary>
    /// <param name="parameters">Which shape and how big.</param>
    /// <returns>The mesh, in the shape's own space.</returns>
    /// <remarks>The parameters are clamped first, so this never throws and never produces a mesh with
    ///     no faces — see <see cref="ShapeParameters.Clamped" /> for why that is the caller's
    ///     interest.</remarks>
    public static EditMesh Create(in ShapeParameters parameters) {
        var shape = parameters.Clamped();
        var mesh = Built(shape);

        // ⚠ Every shape here numbers its faces with the six named groups below, so the result carries
        // an assignment rather than a coplanarity guess and has to say so — a remesh reads a group
        // boundary as a crease and an unwrap reads it as a chart boundary, and a cylinder whose wall
        // and cap were one group would lose the rim both stages exist to keep.
        mesh.GroupSource = MeshGroupSource.Assigned;

        return mesh;
    }

    static EditMesh Built(in ShapeParameters shape) =>
        shape.Kind switch {
            ShapeKind.Plane => Plane(shape),
            ShapeKind.Cylinder => Cylinder(shape),
            ShapeKind.Cone => Cone(shape),
            ShapeKind.Sphere => Sphere(shape),
            ShapeKind.Capsule => Capsule(shape),
            ShapeKind.Torus => Torus(shape),
            ShapeKind.Stairs => Stairs(shape),
            ShapeKind.Ramp => Ramp(shape),
            ShapeKind.Arch => Opening(shape, round: true),
            ShapeKind.Pipe => Pipe(shape),
            ShapeKind.DoorFrame => Opening(shape, round: false),
            _ => Box(shape)
        };

    /// <summary>Builds a shape of a kind at its default size.</summary>
    /// <param name="kind">Which shape.</param>
    /// <returns>The mesh.</returns>
    public static EditMesh Create(ShapeKind kind) => Create(ShapeParameters.Default(kind));

    /// <summary>Sweeps a closed outline along a straight line and caps both ends.</summary>
    /// <param name="outline">The outline, anticlockwise in the plane <paramref name="across" /> and
    ///     <paramref name="up" /> span. Three points or more.</param>
    /// <param name="origin">Where the near cap's outline origin is.</param>
    /// <param name="across">The outline's first axis, as a unit vector.</param>
    /// <param name="up">Its second, as a unit vector perpendicular to the first.</param>
    /// <param name="along">How far and which way to sweep. Must point the way
    ///     <paramref name="across" /> crossed into <paramref name="up" /> does.</param>
    /// <param name="capGroup">Which group the two caps go in.</param>
    /// <param name="sides">Which group each swept side goes in, one per outline edge, or empty for
    ///     <paramref name="capGroup" /> plus one.</param>
    /// <returns>The mesh: two n-gons and a quad per outline edge.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>The one routine three of the level-design shapes are, and the poly-shape tool as
    ///         well.</b> Doc 24's poly shape is "click a polygon on the work plane, then drag the
    ///         height", which is this with the outline a designer clicked; a staircase is this with a
    ///         staircase-shaped outline; a ramp is this with a triangle.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The near cap is the outline reversed and the far cap is not.</b> An anticlockwise
    ///         outline seen from the far end is clockwise seen from the near one, so writing both in
    ///         the same order produces a solid with one of its ends inside out — which validates
    ///         clean, draws with a hole in it under back-face culling, and is the single most common
    ///         way a generator goes wrong.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A self-intersecting outline produces a self-intersecting solid rather than an
    ///         error.</b> Deciding whether a polygon a designer clicked crosses itself is a question
    ///         with a real answer and no cheap one, and refusing at the end of a gesture is worse than
    ///         producing something visibly wrong that <c>Ctrl+Z</c> takes away. The caps are triangulated
    ///         by <see cref="EditMesh.Triangulate()" />, which falls back to a fan for exactly this case.
    ///     </para>
    /// </remarks>
    public static EditMesh Sweep(
        ReadOnlySpan<Vector2> outline,
        Vector3 origin,
        Vector3 across,
        Vector3 up,
        Vector3 along,
        int capGroup = 0,
        ReadOnlySpan<int> sides = default
    ) {
        var mesh = new EditMesh();
        var count = outline.Length;

        if (count < 3) {
            return mesh;
        }

        for (var point = 0; point < count; point++) {
            mesh.AddPosition(origin + (across * outline[point].X) + (up * outline[point].Y));
        }

        for (var point = 0; point < count; point++) {
            mesh.AddPosition(origin + along + (across * outline[point].X) + (up * outline[point].Y));
        }

        var near = new int[count];
        var far = new int[count];

        for (var point = 0; point < count; point++) {
            near[point] = count - 1 - point;
            far[point] = count + point;
        }

        mesh.AddFace(near, capGroup);
        mesh.AddFace(far, capGroup);

        Span<int> quad = stackalloc int[4];

        for (var edge = 0; edge < count; edge++) {
            var next = (edge + 1) % count;

            quad[0] = edge;
            quad[1] = next;
            quad[2] = count + next;
            quad[3] = count + edge;

            mesh.AddFace(quad, edge < sides.Length ? sides[edge] : capGroup + 1);
        }

        // The caller named the cap's group and may have named every side's, so these are assignments.
        mesh.GroupSource = MeshGroupSource.Assigned;

        return mesh;
    }

    /// <summary>The outline of a flight of steps, in run and rise.</summary>
    /// <param name="run">How far the flight goes forward in total.</param>
    /// <param name="rise">How far it goes up in total.</param>
    /// <param name="steps">How many steps. One or more.</param>
    /// <param name="into">The outline. Cleared first.</param>
    /// <remarks>
    ///     Anticlockwise, starting at the bottom of the back edge: forward along the floor, up the
    ///     tall end, then down the treads and risers alternately. What <see cref="Sweep" /> wants, and
    ///     what makes a staircase one closed solid rather than a pile of boxes with faces buried
    ///     inside each other.
    /// </remarks>
    public static void StairProfile(float run, float rise, int steps, List<Vector2> into) {
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();

        var count = Math.Max(steps, 1);
        var tread = run / count;
        var riser = rise / count;

        into.Add(new(0f, 0f));
        into.Add(new(run, 0f));
        into.Add(new(run, rise));

        // ⚠ Down from the top rather than up from the bottom, because the outline has to stay
        // anticlockwise and the floor edge already ran forward. The last point a loop like this would
        // add is the origin, which is where it started — so the final riser is left to the closing
        // edge rather than written twice.
        for (var step = count - 1; step >= 0; step--) {
            into.Add(new(step * tread, (step + 1) * riser));

            if (step > 0) {
                into.Add(new(step * tread, step * riser));
            }
        }
    }

    static EditMesh Box(in ShapeParameters shape) {
        var mesh = new EditMesh();
        var half = shape.Size * 0.5f;

        for (var corner = 0; corner < 8; corner++) {
            mesh.AddPosition(
                new(
                    (corner & 1) == 0 ? -half.X : half.X,
                    (corner & 2) == 0 ? 0f : shape.Size.Y,
                    (corner & 4) == 0 ? -half.Z : half.Z
                )
            );
        }

        // Named by their four corners in the order that walks each side anticlockwise seen from
        // outside, and grouped so that "the top" and "the sides" are each one click.
        Quad(mesh, 1, 3, 7, 5, GroupRight);
        Quad(mesh, 0, 4, 6, 2, GroupLeft);
        Quad(mesh, 2, 6, 7, 3, GroupTop);
        Quad(mesh, 0, 1, 5, 4, GroupBottom);
        Quad(mesh, 4, 5, 7, 6, GroupBack);
        Quad(mesh, 0, 2, 3, 1, GroupFront);

        return mesh;
    }

    static EditMesh Plane(in ShapeParameters shape) {
        var mesh = new EditMesh();
        var half = shape.Size * 0.5f;
        var cells = shape.Steps;

        for (var row = 0; row <= cells; row++) {
            for (var column = 0; column <= cells; column++) {
                mesh.AddPosition(
                    new(
                        -half.X + (shape.Size.X * column / cells),
                        0f,
                        -half.Z + (shape.Size.Z * row / cells)
                    )
                );
            }
        }

        var stride = cells + 1;

        for (var row = 0; row < cells; row++) {
            for (var column = 0; column < cells; column++) {
                // Anticlockwise seen from above, which is the way a floor is looked at.
                Quad(
                    mesh,
                    (row * stride) + column,
                    ((row + 1) * stride) + column,
                    ((row + 1) * stride) + column + 1,
                    (row * stride) + column + 1,
                    GroupTop
                );
            }
        }

        return mesh;
    }

    static EditMesh Cylinder(in ShapeParameters shape) {
        var mesh = new EditMesh();

        Ring(mesh, shape.Sides, shape.Size.X * 0.5f, shape.Size.Z * 0.5f, 0f);
        Ring(mesh, shape.Sides, shape.Size.X * 0.5f, shape.Size.Z * 0.5f, shape.Size.Y);

        Belt(mesh, shape.Sides, 0, shape.Sides, GroupSide);

        Cap(mesh, shape.Sides, shape.Sides, up: true, GroupTop);
        Cap(mesh, shape.Sides, 0, up: false, GroupBottom);

        return mesh;
    }

    static EditMesh Cone(in ShapeParameters shape) {
        var mesh = new EditMesh();

        Ring(mesh, shape.Sides, shape.Size.X * 0.5f, shape.Size.Z * 0.5f, 0f);

        var apex = mesh.AddPosition(new(0f, shape.Size.Y, 0f));

        Span<int> triangle = stackalloc int[3];

        for (var side = 0; side < shape.Sides; side++) {
            triangle[0] = side;
            triangle[1] = apex;
            triangle[2] = (side + 1) % shape.Sides;

            mesh.AddFace(triangle, GroupSide);
        }

        Cap(mesh, shape.Sides, 0, up: false, GroupBottom);

        return mesh;
    }

    static EditMesh Sphere(in ShapeParameters shape) {
        var mesh = new EditMesh();
        var radius = shape.Size * 0.5f;
        var rings = Math.Max(shape.Steps, 2);

        // ⚠ The poles are their own positions and the rings between them are shared, which is what
        // makes the quads quads. A sphere built as one grid with a doubled seam would look identical
        // and have a run of edges down it that no loop walk crosses.
        var north = mesh.AddPosition(new(0f, shape.Size.Y, 0f));
        var south = mesh.AddPosition(new(0f, 0f, 0f));

        for (var ring = 1; ring < rings; ring++) {
            var pitch = MathF.PI * ring / rings;
            var y = MathF.Cos(pitch);
            var scale = MathF.Sin(pitch);

            for (var side = 0; side < shape.Sides; side++) {
                var angle = side / (float) shape.Sides * MathF.Tau;

                mesh.AddPosition(
                    new(
                        MathF.Cos(angle) * scale * radius.X,
                        radius.Y + (y * radius.Y),
                        MathF.Sin(angle) * scale * radius.Z
                    )
                );
            }
        }

        var first = 2;

        for (var ring = 0; ring + 2 < rings; ring++) {
            Belt(mesh, shape.Sides, first + ((ring + 1) * shape.Sides), first + (ring * shape.Sides), GroupSide);
        }

        Fan(mesh, shape.Sides, first, north, forward: false, GroupSide);
        Fan(mesh, shape.Sides, first + ((rings - 2) * shape.Sides), south, forward: true, GroupSide);

        return mesh;
    }

    static EditMesh Capsule(in ShapeParameters shape) {
        var mesh = new EditMesh();
        var radius = new Vector2(shape.Size.X * 0.5f, shape.Size.Z * 0.5f);

        // ⚠ The straight part is what is left after the two hemispheres, and it is allowed to be
        // nothing. A capsule shorter than it is wide is a sphere with an elliptical waist rather than
        // an error, which is what a designer scrubbing the height field through the diameter expects.
        var cap = MathF.Min(radius.X, radius.Y);
        var straight = MathF.Max(shape.Size.Y - (cap * 2f), 0f);
        var rings = Math.Max(shape.Steps, 2);

        var north = mesh.AddPosition(new(0f, shape.Size.Y, 0f));
        var south = mesh.AddPosition(new(0f, 0f, 0f));

        // ⚠ Both ends of the straight section get a ring of their own, which is why there are twice
        // `rings` bands rather than one short of it. Sharing one would put the waist's quads between
        // the top of one hemisphere and the bottom of the other, and a capsule with a body would come
        // out as two cones joined at the middle.
        for (var ring = 1; ring <= rings * 2; ring++) {
            var upper = ring <= rings;
            var pitch = MathF.PI * 0.5f * (upper ? ring : ring - rings - 1) / rings;

            var y = upper ? cap + straight + (MathF.Cos(pitch) * cap) : cap - (MathF.Sin(pitch) * cap);
            var scale = upper ? MathF.Sin(pitch) : MathF.Cos(pitch);

            for (var side = 0; side < shape.Sides; side++) {
                var angle = side / (float) shape.Sides * MathF.Tau;

                mesh.AddPosition(new(MathF.Cos(angle) * scale * radius.X, y, MathF.Sin(angle) * scale * radius.Y));
            }
        }

        var first = 2;
        var bands = rings * 2;

        for (var ring = 0; ring + 1 < bands; ring++) {
            Belt(mesh, shape.Sides, first + ((ring + 1) * shape.Sides), first + (ring * shape.Sides), GroupSide);
        }

        Fan(mesh, shape.Sides, first, north, forward: false, GroupSide);
        Fan(mesh, shape.Sides, first + ((bands - 1) * shape.Sides), south, forward: true, GroupSide);

        return mesh;
    }

    static EditMesh Torus(in ShapeParameters shape) {
        var mesh = new EditMesh();

        // ⚠ The tube is taken out of the extent rather than added to it, which is what makes `Size`
        // mean the same thing here as it does everywhere else: the whole width of the ring including
        // its tube. A torus whose ring radius <i>was</i> the half-extent would stick out of its own
        // box by a tube radius on every side, and dragging its width would move the outside faster
        // than the number said.
        var half = new Vector2(shape.Size.X * 0.5f, shape.Size.Z * 0.5f);
        var tube = MathF.Min(half.X, half.Y) * shape.Inner;
        var ring = new Vector2(half.X - tube, half.Y - tube);
        var segments = Math.Max(shape.Steps, 3);

        for (var side = 0; side < shape.Sides; side++) {
            var yaw = side / (float) shape.Sides * MathF.Tau;
            var outward = new Vector3(MathF.Cos(yaw), 0f, MathF.Sin(yaw));

            for (var segment = 0; segment < segments; segment++) {
                var pitch = segment / (float) segments * MathF.Tau;

                mesh.AddPosition(
                    new(
                        outward.X * (ring.X + (MathF.Cos(pitch) * tube)),
                        tube + (MathF.Sin(pitch) * tube),
                        outward.Z * (ring.Y + (MathF.Cos(pitch) * tube))
                    )
                );
            }
        }

        for (var side = 0; side < shape.Sides; side++) {
            var next = (side + 1) % shape.Sides;

            for (var segment = 0; segment < segments; segment++) {
                var over = (segment + 1) % segments;

                Quad(
                    mesh,
                    (side * segments) + segment,
                    (side * segments) + over,
                    (next * segments) + over,
                    (next * segments) + segment,
                    GroupSide
                );
            }
        }

        return mesh;
    }

    static EditMesh Pipe(in ShapeParameters shape) {
        var mesh = new EditMesh();

        var outer = new Vector2(shape.Size.X * 0.5f, shape.Size.Z * 0.5f);
        var inner = outer * shape.Inner;
        var sides = shape.Sides;

        Ring(mesh, sides, outer.X, outer.Y, 0f);
        Ring(mesh, sides, outer.X, outer.Y, shape.Size.Y);
        Ring(mesh, sides, inner.X, inner.Y, 0f);
        Ring(mesh, sides, inner.X, inner.Y, shape.Size.Y);

        Belt(mesh, sides, 0, sides, GroupSide);

        // ⚠ The bore's quads are wound the other way round, because its surface faces the axis. A pipe
        // whose inside was wound like its outside is one you can see straight through from any angle,
        // which reads as the bore not having been cut at all.
        Belt(mesh, sides, sides * 3, sides * 2, GroupBore);

        Annulus(mesh, sides, sides, sides * 3, up: true, GroupTop);
        Annulus(mesh, sides, 0, sides * 2, up: false, GroupBottom);

        return mesh;
    }

    static EditMesh Stairs(in ShapeParameters shape) {
        List<Vector2> outline = [];

        StairProfile(shape.Size.Z, shape.Size.Y, shape.Steps, outline);

        // The outline is (run, rise), so it lives in the +Z/+Y plane and the sweep runs along −X —
        // which is what `across` crossed into `up` gives, and is the whole of the winding argument.
        var mesh = Sweep(
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(outline),
            new(shape.Size.X * 0.5f, 0f, -shape.Size.Z * 0.5f),
            Vector3.UnitZ,
            Vector3.UnitY,
            new(-shape.Size.X, 0f, 0f),
            GroupSide,
            StairGroups(outline.Count)
        );

        return mesh;
    }

    /// <summary>Which group each edge of a staircase outline sweeps into.</summary>
    /// <remarks>
    ///     The first two edges are the floor and the tall end; after that they alternate tread, riser,
    ///     tread, riser. Grouping them by <i>what they are</i> is what makes "select every tread" one
    ///     click and what makes a material on the treads survive the flight being made steeper.
    /// </remarks>
    static int[] StairGroups(int points) {
        var groups = new int[points];

        groups[0] = GroupBottom;
        groups[1] = GroupBack;

        for (var edge = 2; edge < points; edge++) {
            groups[edge] = (edge % 2) == 0 ? GroupTop : GroupFront;
        }

        return groups;
    }

    static EditMesh Ramp(in ShapeParameters shape) {
        Span<Vector2> outline = [
            new(0f, 0f),
            new(shape.Size.Z, 0f),
            new(shape.Size.Z, shape.Size.Y)
        ];

        Span<int> groups = [GroupBottom, GroupBack, GroupTop];

        return Sweep(
            outline,
            new(shape.Size.X * 0.5f, 0f, -shape.Size.Z * 0.5f),
            Vector3.UnitZ,
            Vector3.UnitY,
            new(-shape.Size.X, 0f, 0f),
            GroupSide,
            groups
        );
    }

    /// <summary>A wall with an opening through it, square-topped or round.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>One routine for the arch and the door frame, because they differ in one list.</b> The
    ///         opening's rim is a polyline from the bottom of one jamb, up and over, to the bottom of
    ///         the other; a door frame's goes straight across the top and an arch's follows a
    ///         half-ellipse. Everything else — the two faces, the reveal, the outer rim — is written
    ///         against that list and does not care which it was given.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Built face by face rather than as three prisms welded together.</b> A wall made of a
    ///         left post, a right post and a lintel is three closed solids sharing two pairs of
    ///         coincident faces buried inside it — which validates clean, draws with z-fighting where
    ///         the pieces meet, and leaves a bevel of the outer corner cutting into geometry nobody can
    ///         see.
    ///     </para>
    /// </remarks>
    static EditMesh Opening(in ShapeParameters shape, bool round) {
        var mesh = new EditMesh();

        var half = shape.Size.X * 0.5f;
        var depth = shape.Size.Z * 0.5f;
        var height = shape.Size.Y;

        var width = shape.Size.X * shape.Inner * 0.5f;

        // ⚠ The header is a length and is clamped against the wall rather than the other way round: a
        // designer who asks for half a metre of wall above a doorway in a wall that is forty
        // centimetres tall gets a very short doorway, not a shape that has turned inside out.
        var top = Math.Clamp(height - shape.Thickness, MinimumOpening, height - MinimumOpening);

        // Where the straight jambs stop and the head begins. A square head springs at its own top; a
        // round one springs as far below the crown as the opening is wide, and a crown that would fall
        // below the floor makes the whole opening the curve — which is the shape somebody asking for a
        // very wide arch in a short wall meant.
        var spring = round ? MathF.Max(top - width, 0f) : top;
        var banded = spring > MinimumOpening;

        List<Vector2> rim = [new(-width, 0f), new(-width, spring)];

        if (round) {
            for (var segment = 1; segment < shape.Sides; segment++) {
                var angle = MathF.PI * (1f - (segment / (float) shape.Sides));

                rim.Add(new(MathF.Cos(angle) * width, spring + (MathF.Sin(angle) * (top - spring))));
            }
        }

        rim.Add(new(width, spring));
        rim.Add(new(width, 0f));

        var front = new int[rim.Count];
        var back = new int[rim.Count];

        for (var point = 0; point < rim.Count; point++) {
            front[point] = mesh.AddPosition(new(rim[point].X, rim[point].Y, -depth));
            back[point] = mesh.AddPosition(new(rim[point].X, rim[point].Y, depth));
        }

        // ⚠ A position at the top of the wall above every point of the head, and this is what makes
        // the wall one shell. The band above the opening is one quad per segment of the head, so the
        // wall's own top edge is cut at the same X values — a top face that ran straight across would
        // share an edge with faces that do not share it back, which is a crack the edge table reports
        // and a renderer draws as a seam.
        var crestFront = new int[rim.Count];
        var crestBack = new int[rim.Count];

        for (var point = 1; point + 1 < rim.Count; point++) {
            crestFront[point] = mesh.AddPosition(new(rim[point].X, height, -depth));
            crestBack[point] = mesh.AddPosition(new(rim[point].X, height, depth));
        }

        var leftFoot = mesh.AddPosition(new(-half, 0f, -depth));
        var leftFootBack = mesh.AddPosition(new(-half, 0f, depth));
        var leftHead = mesh.AddPosition(new(-half, height, -depth));
        var leftHeadBack = mesh.AddPosition(new(-half, height, depth));

        var rightFoot = mesh.AddPosition(new(half, 0f, -depth));
        var rightFootBack = mesh.AddPosition(new(half, 0f, depth));
        var rightHead = mesh.AddPosition(new(half, height, -depth));
        var rightHeadBack = mesh.AddPosition(new(half, height, depth));

        var leftSpring = banded ? mesh.AddPosition(new(-half, spring, -depth)) : leftFoot;
        var leftSpringBack = banded ? mesh.AddPosition(new(-half, spring, depth)) : leftFootBack;
        var rightSpring = banded ? mesh.AddPosition(new(half, spring, -depth)) : rightFoot;
        var rightSpringBack = banded ? mesh.AddPosition(new(half, spring, depth)) : rightFootBack;

        var last = rim.Count - 1;

        // The two faces of the wall. The front faces −Z, so it is wound the other way round from the
        // back — which is the whole of what an eye-check of a generated wall is looking for.
        if (banded) {
            Quad(mesh, leftFoot, leftSpring, front[1], front[0], GroupFront);
            Quad(mesh, front[last], front[last - 1], rightSpring, rightFoot, GroupFront);

            Quad(mesh, leftFootBack, back[0], back[1], leftSpringBack, GroupBack);
            Quad(mesh, back[last], rightFootBack, rightSpringBack, back[last - 1], GroupBack);
        }

        Quad(mesh, leftSpring, leftHead, crestFront[1], front[1], GroupFront);
        Quad(mesh, front[last - 1], crestFront[last - 1], rightHead, rightSpring, GroupFront);

        Quad(mesh, leftSpringBack, back[1], crestBack[1], leftHeadBack, GroupBack);
        Quad(mesh, back[last - 1], rightSpringBack, rightHeadBack, crestBack[last - 1], GroupBack);

        for (var segment = 1; segment + 2 < rim.Count; segment++) {
            Quad(mesh, front[segment], crestFront[segment], crestFront[segment + 1], front[segment + 1], GroupFront);
            Quad(mesh, back[segment], back[segment + 1], crestBack[segment + 1], crestBack[segment], GroupBack);
        }

        // The reveal: the opening's own surface, facing into it.
        for (var segment = 0; segment < last; segment++) {
            Quad(mesh, front[segment], front[segment + 1], back[segment + 1], back[segment], GroupBore);
        }

        // And the outside: the two pieces of floor either side of the opening, the two ends, and the
        // top — the last of which is one quad per span between the cuts the head put in it.
        Quad(mesh, leftFoot, front[0], back[0], leftFootBack, GroupBottom);
        Quad(mesh, front[last], rightFoot, rightFootBack, back[last], GroupBottom);

        if (banded) {
            Quad(mesh, leftFoot, leftFootBack, leftSpringBack, leftSpring, GroupLeft);
            Quad(mesh, rightFoot, rightSpring, rightSpringBack, rightFootBack, GroupRight);
        }

        Quad(mesh, leftSpring, leftSpringBack, leftHeadBack, leftHead, GroupLeft);
        Quad(mesh, rightSpring, rightHead, rightHeadBack, rightSpringBack, GroupRight);

        var spanFront = leftHead;
        var spanBack = leftHeadBack;

        for (var point = 1; point + 1 < rim.Count; point++) {
            Quad(mesh, spanFront, spanBack, crestBack[point], crestFront[point], GroupTop);

            spanFront = crestFront[point];
            spanBack = crestBack[point];
        }

        Quad(mesh, spanFront, spanBack, rightHeadBack, rightHead, GroupTop);

        return mesh;
    }

    /// <summary>The smallest an opening or the wall around it may be squeezed to.</summary>
    const float MinimumOpening = 1e-3f;

    static void Ring(EditMesh mesh, int sides, float radiusX, float radiusZ, float y) {
        for (var side = 0; side < sides; side++) {
            var angle = side / (float) sides * MathF.Tau;

            mesh.AddPosition(new(MathF.Cos(angle) * radiusX, y, MathF.Sin(angle) * radiusZ));
        }
    }

    /// <summary>Quads between two rings of the same size, wound outwards.</summary>
    /// <remarks><paramref name="lower" /> and <paramref name="upper" /> are the first position of
    ///     each ring; swapping them is what turns a surface inside out, which is how a pipe's bore is
    ///     built from the same routine as its outside.</remarks>
    static void Belt(EditMesh mesh, int sides, int lower, int upper, int group) {
        for (var side = 0; side < sides; side++) {
            var next = (side + 1) % sides;

            Quad(mesh, lower + side, upper + side, upper + next, lower + next, group);
        }
    }

    /// <summary>A ring closed off with one n-gon.</summary>
    /// <remarks>
    ///     ⚠ <b>An upward cap walks the ring backwards.</b> <see cref="Ring" /> lays its positions out
    ///     in increasing angle, which read as a face is anticlockwise seen from <i>below</i> — so
    ///     writing a top cap in the order the positions were made produces a lid facing into the solid.
    /// </remarks>
    static void Cap(EditMesh mesh, int sides, int first, bool up, int group) {
        var loop = new int[sides];

        for (var side = 0; side < sides; side++) {
            loop[side] = first + (up ? sides - 1 - side : side);
        }

        mesh.AddFace(loop, group);
    }

    /// <summary>A ring of quads between two rings of different radius, closing a tube's end.</summary>
    static void Annulus(EditMesh mesh, int sides, int outer, int inner, bool up, int group) {
        for (var side = 0; side < sides; side++) {
            var next = (side + 1) % sides;

            if (up) {
                Quad(mesh, outer + side, inner + side, inner + next, outer + next, group);
            } else {
                Quad(mesh, outer + side, outer + next, inner + next, inner + side, group);
            }
        }
    }

    static void Fan(EditMesh mesh, int sides, int first, int apex, bool forward, int group) {
        Span<int> triangle = stackalloc int[3];

        for (var side = 0; side < sides; side++) {
            var next = (side + 1) % sides;

            triangle[0] = apex;
            triangle[1] = first + (forward ? side : next);
            triangle[2] = first + (forward ? next : side);

            mesh.AddFace(triangle, group);
        }
    }

    static void Quad(EditMesh mesh, int a, int b, int c, int d, int group) {
        Span<int> loop = [a, b, c, d];

        mesh.AddFace(loop, group);
    }

    /// <summary>The face group a shape's top surface goes in.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Six names, shared by every shape here, and that is what makes them worth having.</b>
    ///         "Select the top" means the same thing on a box, a staircase's treads and a cylinder's
    ///         cap, so a material assigned to group <see cref="GroupTop" /> on one entity means the
    ///         same on the next — which is what a palette of blockout materials is for.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Numbers rather than an enum, because a group is an <c>int</c> a user can renumber.</b>
    ///         <see cref="EditMesh.SetGroup" /> takes anything, <see cref="MeshOperations" /> invents
    ///         groups as it goes, and a mesh that arrived from a DCC has whatever its author used.
    ///         These are what a <i>generator</i> starts from, not a closed set.
    ///     </para>
    /// </remarks>
    public const int GroupTop = 0;

    /// <summary>The group a shape's underside goes in.</summary>
    public const int GroupBottom = 1;

    /// <summary>The group its sides go in — a cylinder's wall, a staircase's cheeks.</summary>
    public const int GroupSide = 2;

    /// <summary>The group its front goes in: −Z, and a staircase's risers.</summary>
    public const int GroupFront = 3;

    /// <summary>And its back: +Z, and the tall end of a flight of steps.</summary>
    public const int GroupBack = 4;

    /// <summary>Its −X face.</summary>
    public const int GroupLeft = 5;

    /// <summary>Its +X face.</summary>
    public const int GroupRight = 6;

    /// <summary>The inside of a hole through it: a pipe's bore, a doorway's reveal.</summary>
    /// <remarks>Its own group rather than <see cref="GroupSide" /> because it is the one surface of a
    ///     block-out that is usually a different material from everything around it — a doorway's
    ///     reveal is trim, and a pipe's bore is dark.</remarks>
    public const int GroupBore = 7;
}
