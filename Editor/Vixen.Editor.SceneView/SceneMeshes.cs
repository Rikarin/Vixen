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
    readonly Dictionary<PrimitiveKind, MeshData> shapes = [];

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

    /// <summary>How many entities the last build drew.</summary>
    public int Count { get; private set; }

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
    ///     inside it are the same colour rather than two different oranges.
    /// </remarks>
    public Color4 SelectedColour { get; set; } = new(1f, 0.62f, 0.15f, 1f);

    /// <summary>How many divisions a curved shape is built with.</summary>
    /// <remarks>
    ///     Lower than <see cref="MeshPrimitives.DefaultSegments" />, because these are drawn through a
    ///     buffer that is rewritten every frame and a smoother sphere is paid for once per frame
    ///     rather than once. Twenty-four is past the point where a sphere reads as a sphere.
    /// </remarks>
    public int Segments { get; set; } = 24;

    /// <summary>Collects a frame's triangles.</summary>
    /// <param name="document">The scene being drawn.</param>
    /// <returns>How many entities were drawn.</returns>
    public int Build(SceneDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        vertices.Clear();
        indices.Clear();
        Count = 0;

        var world = document.World;

        foreach (var entity in document.Entities) {
            if (!MeshShapes.TryGet(world, entity, out var kind) || !world.Has<WorldTransform>(entity)) {
                continue;
            }

            Append(Shape(kind), world.Read<WorldTransform>(entity).Value, document.Selection.Contains(entity));
            Count++;
        }

        return Count;
    }

    /// <summary>Forgets the geometry built so far.</summary>
    /// <remarks>
    ///     For a caller that changed <see cref="Segments" /> and wants it to take effect, which is the
    ///     only thing that invalidates the cache — the shapes themselves never change.
    /// </remarks>
    public void Invalidate() => shapes.Clear();

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
    void Append(MeshData mesh, in Matrix4x4 transform, bool selected) {
        var colour = selected ? SelectedColour : ShapeColour;
        var first = (uint) vertices.Count;

        var normals = Matrix4x4.Invert(transform, out var inverse) ? Matrix4x4.Transpose(inverse) : transform;
        var hasNormals = mesh.Normals.Length == mesh.Positions.Length;

        for (var index = 0; index < mesh.Positions.Length; index++) {
            var normal = hasNormals
                ? Vector3.Normalize(Matrix4x4.TransformDirection(mesh.Normals[index], normals))
                : Vector3.UnitY;

            vertices.Add(
                new MeshVertex(Matrix4x4.TransformPosition(mesh.Positions[index], transform), normal, colour)
            );
        }

        foreach (var index in mesh.Indices) {
            indices.Add(first + (uint) index);
        }
    }
}
