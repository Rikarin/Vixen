// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering;

/// <summary>One of the shapes an engine can build rather than import.</summary>
/// <remarks>
///     <para>
///         <b>A closed set, deliberately.</b> These are the shapes somebody blocks a level out with
///         and tests a material on before there is any art — not a modelling kernel. Anything that
///         needs a shape not on this list needs a mesh asset, and the moment this enum starts growing
///         parameters per member is the moment that has become true.
///     </para>
///     <para>
///         ⚠ <b>The numbers are part of the scene format.</b> A member added in the middle would
///         renumber every one after it and silently turn every saved cube into a sphere, so new
///         members go on the end. The authoring format writes the <i>name</i> for exactly this
///         reason — see the editor's shape component — but the values are still worth pinning.
///     </para>
/// </remarks>
public enum PrimitiveKind {
    /// <summary>A unit cube.</summary>
    Cube = 0,

    /// <summary>A sphere, latitude-longitude.</summary>
    Sphere = 1,

    /// <summary>A cylinder with a cap at each end.</summary>
    Cylinder = 2,

    /// <summary>A cylinder with a hemisphere at each end.</summary>
    Capsule = 3,

    /// <summary>A cone with a cap at the bottom.</summary>
    Cone = 4,

    /// <summary>A flat, subdivided square lying in the ground plane.</summary>
    Plane = 5,

    /// <summary>A single flat square standing up, facing +Z.</summary>
    Quad = 6,

    /// <summary>A ring of circular cross-section, lying in the ground plane.</summary>
    Torus = 7
}

/// <summary>The shapes the engine can build: a cube, a sphere, and the six others everybody expects.</summary>
/// <remarks>
///     <para>
///         <b>Geometry and nothing else.</b> What comes back is a <see cref="MeshData" /> — the same
///         thing an importer produces — so a primitive travels down whatever path an imported mesh
///         does, rather than being a special case that every consumer has to know about. Nothing here
///         touches a device, which is also what makes the whole of it testable without one.
///     </para>
///     <para>
///         ⚠ <b>Every primitive fits inside the unit cube centred on the origin, and there are no
///         exceptions.</b> One rule means a spawn menu can drop any of them at a point and have them
///         all arrive the same size, and it means a scale of <c>2</c> means "two metres" for all eight.
///         It costs one thing worth naming: the capsule's radius is a quarter rather than a half,
///         because a capsule one unit wide <i>and</i> one unit tall is a sphere.
///     </para>
///     <para>
///         <b>Wound counter-clockwise seen from outside</b>, which is <c>FrontFace.CounterClockwise</c>
///         in a right-handed space — the convention <c>Matrix4x4.LookAt</c> establishes for the rest of
///         the engine. Normals point out; texture coordinates run V downwards, as an image does.
///     </para>
///     <para>
///         <b>No tangents.</b> <see cref="MeshData" /> treats an empty array as "this mesh has none",
///         which is the truth here — a normal map on a primitive is a thing somebody will want and it
///         is a generator of its own, not four lines bolted onto each of these.
///     </para>
/// </remarks>
public static class MeshPrimitives {
    /// <summary>How many segments a curved surface is divided into around its axis.</summary>
    public const int DefaultSegments = 32;

    /// <summary>How many bands it is divided into along its axis.</summary>
    public const int DefaultRings = 16;

    /// <summary>The lowest either count may be and still describe a solid.</summary>
    public const int MinimumSegments = 3;

    /// <summary>Builds one of the shapes at its standard size.</summary>
    /// <param name="kind">Which shape.</param>
    /// <param name="segments">How many divisions around the axis, where that means anything.</param>
    /// <param name="rings">How many divisions along it.</param>
    /// <returns>The geometry.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A count is too low, or the kind is not one.</exception>
    public static MeshData Create(
        PrimitiveKind kind,
        int segments = DefaultSegments,
        int rings = DefaultRings
    ) => kind switch {
        PrimitiveKind.Cube => Cube(),
        PrimitiveKind.Sphere => Sphere(0.5f, segments, rings),
        PrimitiveKind.Cylinder => Cylinder(0.5f, 1f, segments),
        PrimitiveKind.Capsule => Capsule(0.25f, 1f, segments, rings),
        PrimitiveKind.Cone => Cone(0.5f, 1f, segments),
        PrimitiveKind.Plane => Plane(1f, Math.Max(1, segments / 8)),
        PrimitiveKind.Quad => Quad(1f),
        PrimitiveKind.Torus => Torus(0.35f, 0.15f, segments, Math.Max(MinimumSegments, segments / 2)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "There is no such primitive.")
    };

    /// <summary>A cube, one face per direction, with hard edges.</summary>
    /// <param name="size">How long an edge is.</param>
    /// <returns>The geometry.</returns>
    /// <remarks>
    ///     ⚠ <b>Twenty-four vertices rather than eight.</b> A corner of a cube has three different
    ///     normals and three different texture coordinates, and sharing it would average them — which
    ///     is a cube lit as though it were a very lumpy sphere, and a texture smeared across the seam.
    /// </remarks>
    public static MeshData Cube(float size = 1f) {
        var half = size * 0.5f;
        var builder = new Builder(24, 36);

        Face(Vector3.UnitX, -Vector3.UnitZ, Vector3.UnitY);
        Face(-Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY);
        Face(Vector3.UnitY, Vector3.UnitX, -Vector3.UnitZ);
        Face(-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ);
        Face(Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY);
        Face(-Vector3.UnitZ, -Vector3.UnitX, Vector3.UnitY);

        return builder.Build("Cube");

        // `right` and `up` span the face and `normal` is their cross product, so the four corners
        // come out counter-clockwise when the face is looked at from outside.
        void Face(Vector3 normal, Vector3 right, Vector3 up) {
            var origin = normal * half;
            var first = builder.Count;

            builder.Add(origin - (right * half) - (up * half), normal, new Vector2(0f, 1f));
            builder.Add(origin + (right * half) - (up * half), normal, new Vector2(1f, 1f));
            builder.Add(origin + (right * half) + (up * half), normal, new Vector2(1f, 0f));
            builder.Add(origin - (right * half) + (up * half), normal, new Vector2(0f, 0f));

            builder.Triangle(first, first + 1, first + 2);
            builder.Triangle(first, first + 2, first + 3);
        }
    }

    /// <summary>A sphere, divided by latitude and longitude.</summary>
    /// <param name="radius">Its radius.</param>
    /// <param name="segments">How many divisions of longitude.</param>
    /// <param name="rings">How many bands of latitude, pole to pole.</param>
    /// <returns>The geometry.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A count is below <see cref="MinimumSegments" />.</exception>
    /// <remarks>
    ///     ⚠ <b>A seam of duplicated vertices at longitude zero, on purpose.</b> The ring's last
    ///     vertex is in the same place as its first and differs only in its texture coordinate; joining
    ///     them would save one vertex per band and wrap the whole texture backwards across the last
    ///     column of quads.
    /// </remarks>
    public static MeshData Sphere(float radius = 0.5f, int segments = DefaultSegments, int rings = DefaultRings) {
        Check(segments, rings);

        var builder = new Builder((rings + 1) * (segments + 1), rings * segments * 6);

        for (var ring = 0; ring <= rings; ring++) {
            var v = (float) ring / rings;
            var polar = MathF.PI * v;

            var y = MathF.Cos(polar);
            var span = MathF.Sin(polar);

            for (var segment = 0; segment <= segments; segment++) {
                var u = (float) segment / segments;
                var azimuth = MathUtil.TwoPi * u;

                var normal = new Vector3(span * MathF.Sin(azimuth), y, span * MathF.Cos(azimuth));

                builder.Add(normal * radius, normal, new Vector2(u, v));
            }
        }

        Band(ref builder, rings, segments, skipFirstRow: true, skipLastRow: true);
        return builder.Build("Sphere");
    }

    /// <summary>A cylinder standing on the Y axis, capped at both ends.</summary>
    /// <param name="radius">Its radius.</param>
    /// <param name="height">How tall it is, end to end.</param>
    /// <param name="segments">How many divisions around it.</param>
    /// <returns>The geometry.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The count is below <see cref="MinimumSegments" />.</exception>
    public static MeshData Cylinder(float radius = 0.5f, float height = 1f, int segments = DefaultSegments) {
        Check(segments, MinimumSegments);

        var half = height * 0.5f;
        var builder = new Builder((segments + 1) * 2 + (segments + 1) * 2 + 2, segments * 12);

        for (var ring = 0; ring < 2; ring++) {
            var y = ring == 0 ? half : -half;

            for (var segment = 0; segment <= segments; segment++) {
                var u = (float) segment / segments;
                var azimuth = MathUtil.TwoPi * u;
                var normal = new Vector3(MathF.Sin(azimuth), 0f, MathF.Cos(azimuth));

                builder.Add(new Vector3(normal.X * radius, y, normal.Z * radius), normal, new Vector2(u, ring));
            }
        }

        Band(ref builder, 1, segments, skipFirstRow: false, skipLastRow: false);

        Cap(ref builder, radius, half, segments, up: true);
        Cap(ref builder, radius, -half, segments, up: false);

        return builder.Build("Cylinder");
    }

    /// <summary>A cylinder with a hemisphere on each end.</summary>
    /// <param name="radius">The radius of the tube and of both caps.</param>
    /// <param name="height">How tall the whole thing is, cap tip to cap tip.</param>
    /// <param name="segments">How many divisions around it.</param>
    /// <param name="rings">How many bands each hemisphere has.</param>
    /// <returns>The geometry.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A count is too low, or the height is under the diameter.</exception>
    /// <remarks>
    ///     ⚠ <b>The two hemispheres and the tube are one strip of rings, not three meshes stacked.</b>
    ///     Built separately they meet at a ring of coincident vertices with the same normals, which
    ///     costs a duplicate ring and shows as a hairline seam under specular lighting. Here the
    ///     hemisphere's equator <i>is</i> the tube's end.
    /// </remarks>
    public static MeshData Capsule(
        float radius = 0.25f,
        float height = 1f,
        int segments = DefaultSegments,
        int rings = DefaultRings
    ) {
        Check(segments, rings);

        if (height < radius * 2f) {
            throw new ArgumentOutOfRangeException(
                nameof(height),
                height,
                $"A capsule of radius {radius} is at least {radius * 2f} tall; below that it is a sphere."
            );
        }

        // Half the *cylindrical* section, which is what the two hemispheres are pushed apart by. Zero
        // is legal and is exactly a sphere, which is why the check above is `<` and not `<=`.
        var offset = (height * 0.5f) - radius;

        // One extra ring: the hemispheres each get `rings` bands and the seam between them is a row
        // of its own, so that the tube is a band rather than a zero-height degenerate one.
        var total = rings * 2 + 1;
        var builder = new Builder((total + 1) * (segments + 1), total * segments * 6);

        for (var ring = 0; ring <= total; ring++) {
            // The upper hemisphere, then its mirror. The middle row is emitted twice — once as the
            // bottom of the top cap and once as the top of the bottom cap — with the same normal and
            // a different Y, which is what makes the tube.
            var top = ring <= rings;
            var band = top ? ring : ring - rings - 1;
            var polar = MathF.PI * 0.5f * band / rings;

            var y = MathF.Cos(polar);
            var span = MathF.Sin(polar);

            if (!top) {
                y = -MathF.Cos(MathF.PI * 0.5f * (rings - band) / rings);
                span = MathF.Sin(MathF.PI * 0.5f * (rings - band) / rings);
            }

            var centre = top ? offset : -offset;

            for (var segment = 0; segment <= segments; segment++) {
                var u = (float) segment / segments;
                var azimuth = MathUtil.TwoPi * u;
                var normal = new Vector3(span * MathF.Sin(azimuth), y, span * MathF.Cos(azimuth));

                builder.Add(
                    new Vector3(normal.X * radius, (normal.Y * radius) + centre, normal.Z * radius),
                    normal,
                    new Vector2(u, (float) ring / total)
                );
            }
        }

        Band(ref builder, total, segments, skipFirstRow: true, skipLastRow: true);
        return builder.Build("Capsule");
    }

    /// <summary>A cone standing on the Y axis, capped at the bottom.</summary>
    /// <param name="radius">The radius of its base.</param>
    /// <param name="height">How tall it is, base to tip.</param>
    /// <param name="segments">How many divisions around it.</param>
    /// <returns>The geometry.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The count is below <see cref="MinimumSegments" />.</exception>
    /// <remarks>
    ///     ⚠ <b>The tip is a row of vertices rather than one.</b> They are all in the same place and
    ///     carry different normals — the normal of the slope beneath each one — because a single
    ///     apex would have to pick one, and the cone would be lit as though a spotlight were on one
    ///     side of it.
    /// </remarks>
    public static MeshData Cone(float radius = 0.5f, float height = 1f, int segments = DefaultSegments) {
        Check(segments, MinimumSegments);

        var half = height * 0.5f;
        var builder = new Builder((segments + 1) * 2 + segments + 2, segments * 6);

        for (var ring = 0; ring < 2; ring++) {
            for (var segment = 0; segment <= segments; segment++) {
                var u = (float) segment / segments;
                var azimuth = MathUtil.TwoPi * u;

                var sin = MathF.Sin(azimuth);
                var cos = MathF.Cos(azimuth);

                // The slope's normal: horizontal by the height, vertical by the radius. A tall thin
                // cone comes out nearly horizontal and a flat one nearly straight up, which is what
                // the ratio of the two is.
                var normal = Vector3.Normalize(new Vector3(sin * height, radius, cos * height));

                builder.Add(
                    ring == 0
                        ? new Vector3(0f, half, 0f)
                        : new Vector3(sin * radius, -half, cos * radius),
                    normal,
                    new Vector2(u, ring)
                );
            }
        }

        Band(ref builder, 1, segments, skipFirstRow: true, skipLastRow: false);

        Cap(ref builder, radius, -half, segments, up: false);
        return builder.Build("Cone");
    }

    /// <summary>A flat square in the ground plane, facing up.</summary>
    /// <param name="size">How long an edge is.</param>
    /// <param name="divisions">How many quads along each edge.</param>
    /// <returns>The geometry.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is less than one division.</exception>
    /// <remarks>
    ///     Subdivided rather than two triangles, because a ground plane is the thing people put a
    ///     vertex-lit or displaced material on first, and neither has anything to work with on a quad.
    ///     <see cref="PrimitiveKind.Quad" /> is the two-triangle one.
    /// </remarks>
    public static MeshData Plane(float size = 1f, int divisions = 4) {
        ArgumentOutOfRangeException.ThrowIfLessThan(divisions, 1);

        var half = size * 0.5f;
        var step = size / divisions;
        var builder = new Builder((divisions + 1) * (divisions + 1), divisions * divisions * 6);

        for (var row = 0; row <= divisions; row++) {
            for (var column = 0; column <= divisions; column++) {
                builder.Add(
                    new Vector3(-half + (column * step), 0f, -half + (row * step)),
                    Vector3.UnitY,
                    new Vector2((float) column / divisions, (float) row / divisions)
                );
            }
        }

        Band(ref builder, divisions, divisions, skipFirstRow: false, skipLastRow: false);
        return builder.Build("Plane");
    }

    /// <summary>A single square standing in the XY plane, facing +Z.</summary>
    /// <param name="size">How long an edge is.</param>
    /// <returns>The geometry.</returns>
    public static MeshData Quad(float size = 1f) {
        var half = size * 0.5f;
        var builder = new Builder(4, 6);

        builder.Add(new Vector3(-half, -half, 0f), Vector3.UnitZ, new Vector2(0f, 1f));
        builder.Add(new Vector3(half, -half, 0f), Vector3.UnitZ, new Vector2(1f, 1f));
        builder.Add(new Vector3(half, half, 0f), Vector3.UnitZ, new Vector2(1f, 0f));
        builder.Add(new Vector3(-half, half, 0f), Vector3.UnitZ, new Vector2(0f, 0f));

        builder.Triangle(0, 1, 2);
        builder.Triangle(0, 2, 3);

        return builder.Build("Quad");
    }

    /// <summary>A torus lying in the ground plane.</summary>
    /// <param name="radius">From the centre of the hole to the centre of the tube.</param>
    /// <param name="tube">The radius of the tube itself.</param>
    /// <param name="segments">How many divisions around the ring.</param>
    /// <param name="sides">How many divisions around the tube.</param>
    /// <returns>The geometry.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A count is below <see cref="MinimumSegments" />.</exception>
    public static MeshData Torus(
        float radius = 0.35f,
        float tube = 0.15f,
        int segments = DefaultSegments,
        int sides = DefaultRings
    ) {
        Check(segments, sides);

        var builder = new Builder((segments + 1) * (sides + 1), segments * sides * 6);

        for (var segment = 0; segment <= segments; segment++) {
            var u = (float) segment / segments;
            var azimuth = MathUtil.TwoPi * u;

            // Where this slice of the tube is, and which way is "outwards" in it.
            var outward = new Vector3(MathF.Sin(azimuth), 0f, MathF.Cos(azimuth));
            var centre = outward * radius;

            for (var side = 0; side <= sides; side++) {
                var v = (float) side / sides;
                var around = MathUtil.TwoPi * v;

                var normal = (outward * MathF.Cos(around)) + (Vector3.UnitY * MathF.Sin(around));

                builder.Add(centre + (normal * tube), normal, new Vector2(u, v));
            }
        }

        // Rows run around the ring and columns around the tube, which is the transpose of what
        // `Band` walks — so the strip is emitted here rather than borrowed.
        for (var segment = 0; segment < segments; segment++) {
            for (var side = 0; side < sides; side++) {
                var a = (segment * (sides + 1)) + side;
                var b = a + sides + 1;

                builder.Triangle(a, b, a + 1);
                builder.Triangle(b, b + 1, a + 1);
            }
        }

        return builder.Build("Torus");
    }

    /// <summary>Joins a grid of rows and columns into triangles.</summary>
    /// <param name="builder">Where the indices go.</param>
    /// <param name="rows">How many bands there are, one fewer than the rows of vertices.</param>
    /// <param name="columns">Ditto, across.</param>
    /// <param name="skipFirstRow">Whether the top row is a pole, whose upper triangles are degenerate.</param>
    /// <param name="skipLastRow">Ditto, at the bottom.</param>
    /// <remarks>
    ///     ⚠ <b>The degenerate triangles at a pole are skipped rather than emitted.</b> Both of a
    ///     quad's triangles collapse to a line there, and a driver is entitled to rasterise nothing —
    ///     but they still cost index bandwidth, still appear in a triangle count somebody is reading,
    ///     and still turn up as zero-area faces in anything that walks the mesh afterwards.
    /// </remarks>
    static void Band(ref Builder builder, int rows, int columns, bool skipFirstRow, bool skipLastRow) {
        var stride = columns + 1;

        for (var row = 0; row < rows; row++) {
            for (var column = 0; column < columns; column++) {
                var a = (row * stride) + column;
                var c = a + stride;

                if (row > 0 || !skipFirstRow) {
                    builder.Triangle(a, c, a + 1);
                }

                if (row < rows - 1 || !skipLastRow) {
                    builder.Triangle(a + 1, c, c + 1);
                }
            }
        }
    }

    /// <summary>A disc closing one end of a cylinder or a cone.</summary>
    /// <param name="builder">Where it goes.</param>
    /// <param name="radius">How wide.</param>
    /// <param name="y">At what height.</param>
    /// <param name="segments">How many divisions.</param>
    /// <param name="up">Whether it faces up, which decides both the normal and the winding.</param>
    static void Cap(ref Builder builder, float radius, float y, int segments, bool up) {
        var normal = up ? Vector3.UnitY : -Vector3.UnitY;
        var centre = builder.Count;

        builder.Add(new Vector3(0f, y, 0f), normal, new Vector2(0.5f, 0.5f));

        for (var segment = 0; segment <= segments; segment++) {
            var azimuth = MathUtil.TwoPi * segment / segments;

            var sin = MathF.Sin(azimuth);
            var cos = MathF.Cos(azimuth);

            builder.Add(
                new Vector3(sin * radius, y, cos * radius),
                normal,
                new Vector2((sin * 0.5f) + 0.5f, (cos * 0.5f) + 0.5f)
            );
        }

        for (var segment = 0; segment < segments; segment++) {
            var first = centre + 1 + segment;

            // The two ends wind opposite ways round, because "counter-clockwise seen from outside"
            // is clockwise in the same coordinates once you are underneath it.
            if (up) {
                builder.Triangle(centre, first, first + 1);
            } else {
                builder.Triangle(centre, first + 1, first);
            }
        }
    }

    static void Check(int segments, int rings) {
        ArgumentOutOfRangeException.ThrowIfLessThan(segments, MinimumSegments);
        ArgumentOutOfRangeException.ThrowIfLessThan(rings, MinimumSegments);
    }

    /// <summary>Collects vertices and triangles, and works out the bounds on the way.</summary>
    /// <remarks>
    ///     A struct over three lists rather than a class, because it never escapes the method that
    ///     made it — and the lists are pre-sized, so building a sphere is one allocation per attribute
    ///     rather than the six or seven a doubling list would do.
    /// </remarks>
    struct Builder(int vertices, int indices) {
        readonly List<Vector3> positions = new(vertices);
        readonly List<Vector3> normals = new(vertices);
        readonly List<Vector2> texCoords = new(vertices);
        readonly List<int> triangles = new(indices);

        Vector3 low = new(float.MaxValue);
        Vector3 high = new(float.MinValue);

        public readonly int Count => positions.Count;

        public void Add(Vector3 position, Vector3 normal, Vector2 texCoord) {
            positions.Add(position);
            normals.Add(normal);
            texCoords.Add(texCoord);

            low = Vector3.Min(low, position);
            high = Vector3.Max(high, position);
        }

        public readonly void Triangle(int a, int b, int c) {
            triangles.Add(a);
            triangles.Add(b);
            triangles.Add(c);
        }

        public readonly MeshData Build(string name) => new() {
            Name = name,
            Positions = [.. positions],
            Normals = [.. normals],
            TexCoords = [.. texCoords],
            Indices = [.. triangles],
            Bounds = positions.Count > 0 ? new BoundingBox(low, high) : default
        };
    }
}
