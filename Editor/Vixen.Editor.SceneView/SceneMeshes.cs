// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Engine.Transforms;
using Vixen.Rendering;

namespace Vixen.Editor.SceneView;

/// <summary>Every shaped entity in a scene, as one buffer of world-space triangles.</summary>
/// <remarks>
///     <para>
///         <b><see cref="SceneLines" /> for surfaces.</b> Same shape of object and same reason for
///         existing: one collect a frame, into one vertex list and one index list, so that however
///         many primitives a scene holds they cost one buffer write and one draw call. The geometry
///         itself comes from <see cref="MeshPrimitives" /> — this places it, colours it and joins it
///         up.
///     </para>
///     <para>
///         ⚠ <b>The vertices go through the CPU every frame, and that is the deliberate limit of
///         this path.</b> A shape's geometry never changes, so the honest arrangement is a vertex
///         buffer per shape and an instance transform per entity — which is <c>RenderSystem</c>, and
///         wiring the editor's viewport to it is a piece of work rather than a detail. Until then the
///         cost is linear in vertices and the ceiling is the tens of thousands
///         <see cref="MeshRenderer" /> is sized for, which is a block-out rather than a level.
///     </para>
///     <para>
///         ⚠ <b>Built shapes are cached by kind, not by entity.</b> A hundred cubes are one
///         <see cref="MeshData" />; rebuilding a sphere's four hundred vertices per entity per frame
///         would be the whole cost of this pass and none of its output.
///     </para>
/// </remarks>
public sealed class SceneMeshes {
    readonly List<MeshVertex> vertices = [];
    readonly List<uint> indices = [];
    readonly List<LineVertex> edges = [];
    readonly Dictionary<PrimitiveKind, MeshData> shapes = [];
    readonly Dictionary<PrimitiveKind, uint[]> wires = [];

    /// <summary>The frame's vertices, in world space.</summary>
    /// <remarks>
    ///     A span rather than an <see cref="IReadOnlyList{T}" />, which is what
    ///     <see cref="SceneLines" /> hands back: this is read by <c>MeshRenderer.Upload</c>, which
    ///     wants one, and copying a scene's worth of vertices through a list once a frame to satisfy
    ///     an interface nothing else needs would be the most expensive line in the pass.
    /// </remarks>
    public ReadOnlySpan<MeshVertex> Vertices => CollectionsMarshal.AsSpan(vertices);

    /// <summary>Three indices per triangle, into <see cref="Vertices" />.</summary>
    public ReadOnlySpan<uint> Indices => CollectionsMarshal.AsSpan(indices);

    /// <summary>The frame's wireframe, when the view mode asks for one.</summary>
    /// <remarks>
    ///     ⚠ <b>Segments rather than a fill mode, which is what makes a wireframe view cost no new
    ///     pipeline.</b> <c>MeshRenderer</c> draws with one rasterizer state and a second one for a
    ///     debug view is a change to a shipping renderer — and the editor already has a line path
    ///     that these go straight into. The picture is the same one <c>FillMode.Wireframe</c> would
    ///     draw, minus the interior diagonals of a quad's two triangles, which is arguably the better
    ///     picture.
    /// </remarks>
    public IReadOnlyList<LineVertex> Edges => edges;

    /// <summary>How many entities the last build drew.</summary>
    public int Count { get; private set; }

    /// <summary>How many triangles the last build produced.</summary>
    public int Triangles => indices.Count / 3;

    /// <summary>The colour of a shape that is not selected.</summary>
    /// <remarks>
    ///     A light neutral grey rather than white: the shading is one directional term and a white
    ///     surface facing the light clips to flat white, which is exactly where the shape stops being
    ///     readable.
    /// </remarks>
    public Color4 ShapeColour { get; set; } = new(0.60f, 0.62f, 0.66f, 1f);

    /// <summary>The colour of a selected one.</summary>
    /// <remarks>
    ///     <see cref="SceneLines.SelectedColour" />'s, so that a selected cube and the marker cross
    ///     inside it are the same colour rather than two different blues.
    /// </remarks>
    public Color4 SelectedColour { get; set; } = SelectionBlue;

    /// <summary>How many divisions a curved shape is built with.</summary>
    /// <remarks>
    ///     Lower than <see cref="MeshPrimitives.DefaultSegments" />, because these are drawn through a
    ///     buffer that is rewritten every frame and a smoother sphere is paid for once per frame
    ///     rather than once. Twenty-four is past the point where a sphere reads as a sphere.
    /// </remarks>
    public int Segments { get; set; } = 24;

    /// <summary>The colour a selection's outline is drawn in.</summary>
    /// <remarks>
    ///     A step brighter than <see cref="SelectedColour" /> so the rim reads against the tinted
    ///     surface it surrounds, and the same hue so the two are plainly one mark.
    /// </remarks>
    public Color4 OutlineColour { get; set; } = new(0.44f, 0.68f, 1f, 1f);

    /// <summary>What "selected" is, in one place for the three things that draw it.</summary>
    /// <remarks>
    ///     ⚠ <b>Blue, and it was orange.</b> Selection is blue in every tool the editor's users
    ///     arrive from and — the reason that convention exists — it is the one hue that cannot be
    ///     confused with the scene: orange is what a warning gizmo, a baked-lighting overlay and a
    ///     great many authored materials already are, so an orange rim is a rim you have to look at
    ///     twice to know is yours. The accent the interface is drawn in is the same blue, which makes
    ///     a selected row in the outliner and the outline in the viewport read as one fact.
    /// </remarks>
    internal static Color4 SelectionBlue { get; } = new(0.25f, 0.55f, 0.95f, 1f);

    /// <summary>How wide that outline is, in render pixels.</summary>
    public float OutlineWidth { get; set; } = 2.5f;

    /// <summary>How far behind its own surface the outline's hull is pushed, in render pixels.</summary>
    /// <remarks>
    ///     ⚠ <b>Enough to lose the depth test against the object and not enough to sink into what is
    ///     behind it.</b> The hull is the object's own geometry moved outwards <i>across</i> the view,
    ///     so at every pixel the object covers, the two are at the same depth — which is a fight the
    ///     rasterizer settles differently per triangle, and the symptom is an outline that flickers in
    ///     patches across the surface of whatever is selected. A push measured in pixels rather than
    ///     in world units keeps the bias the same size at every distance, which is what stops it
    ///     disappearing when zoomed in and swallowing the object when zoomed out.
    /// </remarks>
    public float OutlineBias { get; set; } = 2f;

    /// <summary>Which way the light comes from, for the vertices that are drawn unshaded.</summary>
    /// <remarks>
    ///     ⚠ <b>Kept in step with <c>MeshRenderer.LightDirection</c> by whoever owns both.</b> The
    ///     outline is one flat colour and the renderer has one ambient term for the whole draw, so the
    ///     only way to draw an unshaded rim beside shaded surfaces is to give the rim vertices a
    ///     normal that faces the light — which needs to be the light the renderer will actually use.
    ///     A copy that drifted would leave the outline shaded on one side, which reads as the outline
    ///     being translucent.
    /// </remarks>
    public Vector3 LightDirection { get; set; } = Vector3.Normalize(new Vector3(-0.4f, -1f, -0.35f));

    /// <summary>Collects a frame's triangles, shaded, with everything shown.</summary>
    /// <param name="document">The scene being drawn.</param>
    /// <returns>How many entities were drawn.</returns>
    /// <remarks>
    ///     What a host with no view modes and no show flags asks for. The pane's own answer is
    ///     <see cref="Build(SceneDocument, SceneViewport, int)" />.
    /// </remarks>
    public int Build(SceneDocument document) => Build(document, null, 0);

    /// <summary>Collects a frame's triangles as one pane wants them.</summary>
    /// <param name="document">The scene being drawn.</param>
    /// <param name="viewport">The pane, for its show flags and its view mode, or null for neither.</param>
    /// <param name="height">How tall the pane is, in render pixels, for the outline's width.</param>
    /// <returns>How many entities were drawn.</returns>
    /// <remarks>
    ///     ⚠ <b>The outline is a second copy of the selected geometry and is only collected when the
    ///     surfaces are.</b> In a wireframe view there is nothing for a rim to be the rim <i>of</i> —
    ///     an expanded hull with no object drawn over it is a solid orange blob where the selection
    ///     used to be, which is the one place this technique fails outright rather than degrading.
    /// </remarks>
    public int Build(SceneDocument document, SceneViewport? viewport, int height) {
        ArgumentNullException.ThrowIfNull(document);

        vertices.Clear();
        indices.Clear();
        edges.Clear();
        Count = 0;

        var show = viewport?.Show ?? SceneShow.Default;
        var mode = viewport?.Modes.Current ?? ViewMode.Shaded;

        if ((show & SceneShow.Meshes) == 0) {
            return 0;
        }

        var surfaces = ViewShading.DrawsSurfaces(mode);
        var wire = ViewShading.DrawsEdges(mode);
        var normals = ViewShading.ColoursByNormal(mode);
        var plain = ViewShading.IgnoresSelectionColour(mode);

        var outline = surfaces
            && (show & SceneShow.Outline) != 0
            && viewport is not null
            && height > 0;

        var world = document.World;

        foreach (var entity in document.Entities) {
            if (!MeshShapes.TryGet(world, entity, out var kind) || !world.Has<WorldTransform>(entity)) {
                continue;
            }

            // ⚠ Editor visibility, which is not a component and is not written to the file. Hiding
            // something to work on what is behind it must not change what ships — see
            // `SceneDocument.Hidden`, and both Unreal and Unity draw the same line.
            if (document.IsHidden(entity)) {
                continue;
            }

            var mesh = Shape(kind);
            var transform = world.Read<WorldTransform>(entity).Value;
            var selected = document.Selection.Contains(entity);

            if (outline && selected) {
                Hull(mesh, transform, viewport!.Camera, height);
            }

            if (surfaces) {
                Append(mesh, transform, selected && !plain, normals);
            }

            if (wire) {
                Wire(kind, mesh, transform);
            }

            Count++;
        }

        return Count;
    }

    /// <summary>Forgets the geometry built so far.</summary>
    /// <remarks>
    ///     For a caller that changed <see cref="Segments" /> and wants it to take effect, which is the
    ///     only thing that invalidates the cache — the shapes themselves never change.
    /// </remarks>
    public void Invalidate() {
        shapes.Clear();
        wires.Clear();
    }

    MeshData Shape(PrimitiveKind kind) {
        if (!shapes.TryGetValue(kind, out var mesh)) {
            mesh = MeshPrimitives.Create(kind, Segments, Math.Max(MeshPrimitives.MinimumSegments, Segments / 2));
            shapes[kind] = mesh;
        }

        return mesh;
    }

    /// <summary>Places one shape's geometry into the frame's buffers.</summary>
    /// <param name="mesh">The shape.</param>
    /// <param name="transform">Where the entity is.</param>
    /// <param name="selected">Whether it is selected.</param>
    /// <remarks>
    ///     ⚠ <b>Normals go through the inverse transpose and not through the matrix.</b> A cube scaled
    ///     <c>2 1 1</c> transformed by its own matrix comes out with normals that are no longer
    ///     perpendicular to the faces they belong to — the shading then slides across the object as it
    ///     is scaled, which reads as the light moving. A matrix that cannot be inverted is a zero
    ///     scale, where the entity has no visible surface anyway and any normal will do.
    /// </remarks>
    void Append(MeshData mesh, in Matrix4x4 transform, bool selected, bool byNormal = false) {
        var colour = selected ? SelectedColour : ShapeColour;
        var first = (uint) vertices.Count;

        var normals = Matrix4x4.Invert(transform, out var inverse) ? Matrix4x4.Transpose(inverse) : transform;
        var hasNormals = mesh.Normals.Length == mesh.Positions.Length;

        for (var index = 0; index < mesh.Positions.Length; index++) {
            var normal = hasNormals
                ? Vector3.Normalize(Matrix4x4.TransformDirection(mesh.Normals[index], normals))
                : Vector3.UnitY;

            vertices.Add(
                new MeshVertex(
                    Matrix4x4.TransformPosition(mesh.Positions[index], transform),
                    normal,

                    // ⚠ Remapped from −1..1 rather than clamped. Half of every normal is negative and
                    // a colour is not, so clamping would paint three of the six faces of a cube black
                    // — the picture would be legible and would say the wrong thing about which way
                    // half the scene faces.
                    byNormal
                        ? new Color4((normal.X * 0.5f) + 0.5f, (normal.Y * 0.5f) + 0.5f, (normal.Z * 0.5f) + 0.5f, 1f)
                        : colour
                )
            );
        }

        foreach (var index in mesh.Indices) {
            indices.Add(first + (uint) index);
        }
    }

    /// <summary>Appends one shape's edges as segments.</summary>
    /// <remarks>
    ///     ⚠ <b>Every edge of every triangle, deduplicated by the index pair rather than by
    ///     position.</b> A cube is twenty-four vertices — three per corner, so that its faces have
    ///     hard normals — and two faces meeting at an edge name it with two different pairs of
    ///     indices. So an edge of a cube is drawn twice, exactly on top of itself, which costs a
    ///     segment and shows nothing. Deduplicating by position instead would mean hashing floats,
    ///     which is a worse trade for a debug view.
    /// </remarks>
    void Wire(PrimitiveKind kind, MeshData mesh, in Matrix4x4 transform) {
        var pairs = Wires(kind, mesh);

        for (var index = 0; index + 1 < pairs.Length; index += 2) {
            edges.Add(
                new LineVertex(Matrix4x4.TransformPosition(mesh.Positions[pairs[index]], transform), WireColour)
            );

            edges.Add(
                new LineVertex(Matrix4x4.TransformPosition(mesh.Positions[pairs[index + 1]], transform), WireColour)
            );
        }
    }

    /// <summary>What a wireframe view's edges are drawn in.</summary>
    /// <remarks>
    ///     Brighter than <see cref="ShapeColour" /> and not the selection's, because in a wireframe
    ///     view every edge is on the silhouette of something and a wire the colour of the surface it
    ///     replaced would be a picture of nothing.
    /// </remarks>
    public Color4 WireColour { get; set; } = new(0.78f, 0.82f, 0.88f, 0.9f);

    /// <summary>A shape's unique edges as index pairs, built once per kind.</summary>
    uint[] Wires(PrimitiveKind kind, MeshData mesh) {
        if (wires.TryGetValue(kind, out var cached)) {
            return cached;
        }

        HashSet<(int From, int To)> seen = [];
        List<uint> pairs = [];

        for (var index = 0; index + 2 < mesh.Indices.Length; index += 3) {
            Edge(mesh.Indices[index], mesh.Indices[index + 1]);
            Edge(mesh.Indices[index + 1], mesh.Indices[index + 2]);
            Edge(mesh.Indices[index + 2], mesh.Indices[index]);
        }

        cached = pairs.ToArray();
        wires[kind] = cached;

        return cached;

        void Edge(int from, int to) {
            var key = from < to ? (from, to) : (to, from);

            if (seen.Add(key)) {
                pairs.Add((uint) key.Item1);
                pairs.Add((uint) key.Item2);
            }
        }
    }

    /// <summary>Appends the expanded copy of a shape that shows round it as an outline.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>An inverted hull, built where the vertices already are.</b> The textbook outline is
    ///         a stencil pass and a post effect over it, which needs a second render pass, a stencil
    ///         format the target does not have and a shader neither of which exists in this path.
    ///         What this path <i>does</i> have is every vertex on the processor with the camera in
    ///         hand, which makes the hull exact rather than approximate: the expansion is along the
    ///         part of the normal that lies <i>across</i> the view, scaled by how many world units a
    ///         pixel is at that vertex — so the rim is the width it was asked for, in pixels, at every
    ///         distance and in both projections.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A vertex whose normal points at the eye is not expanded at all.</b> It is not on
    ///         the silhouette, so expanding it would push the front face of the object outwards
    ///         through its own surface — an orange bloom over the middle of whatever is selected. The
    ///         cross-view component being near zero is exactly the test for "this is facing me".
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The hull's normals face the light rather than the surface.</b> The renderer has
    ///         one ambient term for the whole draw, so the only way to have a flat rim beside shaded
    ///         surfaces is to make the rim's lambert term one everywhere — see
    ///         <see cref="LightDirection" /> for why that number has to be the renderer's.
    ///     </para>
    /// </remarks>
    void Hull(MeshData mesh, in Matrix4x4 transform, EditorCamera camera, int height) {
        var first = (uint) vertices.Count;
        var normals = Matrix4x4.Invert(transform, out var inverse) ? Matrix4x4.Transpose(inverse) : transform;
        var hasNormals = mesh.Normals.Length == mesh.Positions.Length;

        // The direction a lambert term of one is achieved by, which is the direction the light comes
        // from. `MeshRenderer` shades with `dot(normal, -light)`.
        var facing = LightDirection.LengthSquared() > MathUtil.ZeroTolerance
            ? -Vector3.Normalize(LightDirection)
            : Vector3.UnitY;

        for (var index = 0; index < mesh.Positions.Length; index++) {
            var point = Matrix4x4.TransformPosition(mesh.Positions[index], transform);

            var normal = hasNormals
                ? Vector3.Normalize(Matrix4x4.TransformDirection(mesh.Normals[index], normals))
                : Vector3.UnitY;

            var toEye = camera.IsOrthographic
                ? -camera.Forward
                : Direction(camera.Position - point, -camera.Forward);

            var across = normal - (toEye * Vector3.Dot(normal, toEye));
            var scale = camera.WorldPerPixel(point, height);

            if (across.LengthSquared() > MathUtil.ZeroTolerance) {
                point += Vector3.Normalize(across) * (scale * OutlineWidth);
            }

            vertices.Add(new MeshVertex(point - (toEye * (scale * OutlineBias)), facing, OutlineColour));
        }

        foreach (var index in mesh.Indices) {
            indices.Add(first + (uint) index);
        }
    }

    /// <summary>A unit vector, or a fallback when there is nothing to normalise.</summary>
    static Vector3 Direction(Vector3 value, Vector3 fallback) =>
        value.LengthSquared() > MathUtil.ZeroTolerance ? Vector3.Normalize(value) : fallback;
}
