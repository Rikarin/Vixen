// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Engine.Transforms;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;

namespace Vixen.Editor.SceneView;

/// <summary>One run of a scene's entities that share a shape, and are therefore one draw.</summary>
/// <param name="Kind">Which primitive they are instances of.</param>
/// <param name="First">Where the run starts in <see cref="SceneMeshes.Instances" />.</param>
/// <param name="Count">How many entities it holds.</param>
/// <param name="Edges">Whether it draws their wireframe rather than their surfaces.</param>
/// <remarks>
///     A <see cref="PrimitiveKind" /> rather than a device handle, because this side of the seam has no
///     device: the collector says which shape each run wants and whoever owns the buffers turns that
///     into a <see cref="MeshShapeGeometry" />. When a block-out mesh is a mesh of its own rather than
///     a parameter, this is where its identity goes and nothing else here changes.
/// </remarks>
public readonly record struct ShapeBatch(PrimitiveKind Kind, int First, int Count, bool Edges);

/// <summary>Every shaped entity in a scene, as one instance each.</summary>
/// <remarks>
///     <para>
///         <b><see cref="SceneLines" /> for surfaces, and the shape of what it hands over is the whole
///         of its design.</b> A frame is one <see cref="MeshInstance" /> per entity — a transform, a
///         normal matrix, a colour and four style lanes — grouped into runs that share a shape. The
///         geometry itself is registered once with <see cref="MeshInstanceRenderer" /> and never
///         crosses the bus again; this places it, colours it and groups it.
///     </para>
///     <para>
///         ⚠ <b>This used to transform every vertex of every entity into world space every frame, and
///         that was the thing <c>docs/blockout-tools.md</c> § B1 called a blocker rather than a
///         performance concern.</b> The cost was linear in vertices with a cache keyed by
///         <see cref="PrimitiveKind" /> — so a hundred cubes were cheap and a hundred <em>edited</em>
///         meshes were a hundred rebuilds a frame, and "a drag that redraws at four frames a second is
///         not a slow tool, it is a tool nobody can aim". What crosses the bus now is a hundred and
///         sixty bytes an entity whether the entity is a cube or a corridor.
///     </para>
///     <para>
///         ⚠ <b>Three things that were geometry are now style lanes on an instance</b>, and each was a
///         copy of the shape's vertices before: the selection outline, the wireframe view's edges, and
///         the normal view's per-vertex colour. Selecting everything in a scene used to double the
///         frame's vertex count; it now costs one more instance per selected entity, which is the case
///         the outline is actually used in.
///     </para>
///     <para>
///         ⚠ <b>Built shapes are still cached by kind and not by entity</b>, for the reason the cache
///         always had: rebuilding a sphere's four hundred vertices per entity would be the whole cost
///         of this pass and none of its output. What changed is that the cache is now consulted once
///         per shape rather than once per entity, and only to answer what the geometry <em>is</em> —
///         see <see cref="Shape" />, which is what registers it with a device.
///     </para>
/// </remarks>
public sealed class SceneMeshes {
    readonly List<MeshInstance> instances = [];
    readonly List<ShapeBatch> batches = [];
    readonly Dictionary<PrimitiveKind, MeshData> shapes = [];

    // One bucket per shape per topology, reused across frames rather than rebuilt: a batch's
    // instances have to be contiguous, and a scene is walked in tree order rather than in shape
    // order. Cleared per build, so a kind that stops appearing costs an empty list.
    readonly Dictionary<PrimitiveKind, List<MeshInstance>> solids = [];
    readonly Dictionary<PrimitiveKind, List<MeshInstance>> wires = [];

    /// <summary>The frame's entities, one instance each, grouped by <see cref="Batches" />.</summary>
    /// <remarks>
    ///     A span rather than an <see cref="IReadOnlyList{T}" />, which is what
    ///     <see cref="SceneLines" /> hands back: this is read by <c>MeshInstanceRenderer.Upload</c>,
    ///     which wants one.
    /// </remarks>
    public ReadOnlySpan<MeshInstance> Instances => CollectionsMarshal.AsSpan(instances);

    /// <summary>Which run of them is which shape.</summary>
    public IReadOnlyList<ShapeBatch> Batches => batches;

    /// <summary>How many entities the last build drew.</summary>
    /// <remarks>
    ///     Entities, not instances: a selected entity is drawn twice — itself and its outline — and a
    ///     shaded wireframe draws every entity twice as well. What this answers is the question the
    ///     stats overlay asks, which is how much of the scene is on screen.
    /// </remarks>
    public int Count { get; private set; }

    /// <summary>How many triangles the last build asked for.</summary>
    /// <remarks>
    ///     ⚠ <b>Asked for, not drawn.</b> The renderer reports what it drew, which is the truncated
    ///     count when a frame overflowed the instance ring — see <c>MeshInstanceRenderer.Triangles</c>.
    ///     This is the collector's own number and is what a test about what a view mode collects
    ///     asserts on.
    /// </remarks>
    public int Triangles { get; private set; }

    /// <summary>How many times the shape cache has been thrown away.</summary>
    /// <remarks>
    ///     What lets whoever registered this collector's geometry with a device notice that a shape it
    ///     uploaded is no longer the shape this would build — see <see cref="Invalidate" />. A number
    ///     rather than an event, because the consumer is a frame loop that is already asking.
    /// </remarks>
    public int Revision { get; private set; }

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
    ///     inside it are the same colour rather than two different ambers.
    /// </remarks>
    public Color4 SelectedColour { get; set; } = new(1f, 0.62f, 0.15f, 1f);

    /// <summary>How many divisions a curved shape is built with.</summary>
    /// <remarks>
    ///     ⚠ <b>Higher would now be affordable and is deliberately unchanged.</b> This used to be
    ///     lower than <see cref="MeshPrimitives.DefaultSegments" /> because a smoother sphere was paid
    ///     for once per frame through a buffer that was rewritten every frame; the geometry is uploaded
    ///     once now, so the same number buys a fixed cost instead of a recurring one. It stays where it
    ///     is because what it decides is also what <c>ScenePicker</c> and <c>SceneProbe</c> test
    ///     against, and those still walk triangles — changing it is a change to what a click hits.
    /// </remarks>
    public int Segments { get; set; } = 24;

    /// <summary>The colour a selection's outline is drawn in.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Blue, where <see cref="SelectedColour" /> is amber, and the two disagreeing is
    ///         the point rather than an oversight.</b> They answer different questions: the tint says
    ///         <i>which</i> objects are selected and the rim says <i>where each one ends</i>. A rim
    ///         in the tint's own hue is a rim that vanishes into the surface it is drawn around —
    ///         which is exactly the case it exists for, an object against a background of its own
    ///         colour. The complementary hue is the one that cannot.
    ///     </para>
    ///     <para>
    ///         It is also the accent the interface is drawn in, so a highlighted row in the outliner
    ///         and the outline in the viewport read as one fact.
    ///     </para>
    /// </remarks>
    public Color4 OutlineColour { get; set; } = new(0.25f, 0.55f, 0.95f, 1f);

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

    /// <summary>What a wireframe view's edges are drawn in.</summary>
    /// <remarks>
    ///     Brighter than <see cref="ShapeColour" /> and not the selection's, because in a wireframe
    ///     view every edge is on the silhouette of something and a wire the colour of the surface it
    ///     replaced would be a picture of nothing.
    /// </remarks>
    public Color4 WireColour { get; set; } = new(0.78f, 0.82f, 0.88f, 0.9f);

    /// <summary>Collects a frame's instances, shaded, with everything shown.</summary>
    /// <param name="document">The scene being drawn.</param>
    /// <returns>How many entities were drawn.</returns>
    /// <remarks>
    ///     What a host with no view modes and no show flags asks for. The pane's own answer is
    ///     <see cref="Build(SceneDocument, SceneViewport)" />.
    /// </remarks>
    public int Build(SceneDocument document) => Build(document, null);

    /// <summary>Collects a frame's instances as one pane wants them.</summary>
    /// <param name="document">The scene being drawn.</param>
    /// <param name="viewport">The pane, for its show flags and its view mode, or null for neither.</param>
    /// <returns>How many entities were drawn.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The outline is a second instance of the selected entity and is only collected
    ///         when the surfaces are.</b> In a wireframe view there is nothing for a rim to be the rim
    ///         <i>of</i> — an expanded hull with no object drawn over it is a solid blob where the
    ///         selection used to be, which is the one place this technique fails outright rather than
    ///         degrading.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No pane height, where this used to take one.</b> The hull's expansion is measured
    ///         in pixels and so needs to know how many world units a pixel is — which was computed here
    ///         when every vertex went through this method, and is now computed per vertex by the shader
    ///         from <c>MeshInstanceView</c>. The pane's height reaches it through that instead.
    ///     </para>
    /// </remarks>
    public int Build(SceneDocument document, SceneViewport? viewport) {
        ArgumentNullException.ThrowIfNull(document);

        instances.Clear();
        batches.Clear();

        foreach (var bucket in solids.Values) {
            bucket.Clear();
        }

        foreach (var bucket in wires.Values) {
            bucket.Clear();
        }

        Count = 0;
        Triangles = 0;

        var show = viewport?.Show ?? SceneShow.Default;
        var mode = viewport?.Modes.Current ?? ViewMode.Shaded;

        if ((show & SceneShow.Meshes) == 0) {
            return 0;
        }

        var surfaces = ViewShading.DrawsSurfaces(mode);
        var wire = ViewShading.DrawsEdges(mode);
        var normals = ViewShading.ColoursByNormal(mode);
        var plain = ViewShading.IgnoresSelectionColour(mode);

        var outline = surfaces && (show & SceneShow.Outline) != 0;

        // The style lanes the shader reads, assembled once rather than per entity: everything that
        // varies per entity is the transform and the colour.
        var surfaceStyle = new Vector4(0f, 0f, 0f, normals ? 1f : 0f);
        var outlineStyle = new Vector4(OutlineWidth, OutlineBias, 1f, 0f);
        var wireStyle = new Vector4(0f, 0f, 1f, 0f);

        var world = document.World;

        foreach (var entity in document.Entities) {
            if (!PrimitiveShapes.TryGet(world, entity, out var kind) || !world.Has<WorldTransform>(entity)) {
                continue;
            }

            // ⚠ Editor visibility, which is not a component and is not written to the file. Hiding
            // something to work on what is behind it must not change what ships — see
            // `SceneDocument.Hidden`, and both Unreal and Unity draw the same line.
            if (document.IsHidden(entity)) {
                continue;
            }

            var transform = world.Read<WorldTransform>(entity).Value;
            var selected = document.Selection.Contains(entity);

            // ⚠ One matrix inverse per entity, shared by its surface, its outline and its wires. The
            // three instances an entity can produce differ only in colour and style, so this is built
            // once and copied — building each would be three inverses of one transform.
            var placement = MeshInstance.Of(transform, ShapeColour);

            if (surfaces) {
                var colour = selected && !plain ? SelectedColour : ShapeColour;
                Add(solids, kind, placement with { Colour = colour, Style = surfaceStyle });
            }

            if (outline && selected) {
                Add(solids, kind, placement with { Colour = OutlineColour, Style = outlineStyle });
            }

            if (wire) {
                Add(wires, kind, placement with { Colour = WireColour, Style = wireStyle });
            }

            Count++;
        }

        // ⚠ The batch order does not matter and the grouping does. One batch per shape and topology is
        // what makes a shaded wireframe of a hundred cubes two draws; which of the two goes first is
        // settled by the depth buffer, because both pipelines test depth and an outline is biased away
        // from the eye rather than relying on being drawn second.
        Emit(solids, edges: false);
        Emit(wires, edges: true);

        return Count;
    }

    /// <summary>Forgets the geometry built so far.</summary>
    /// <remarks>
    ///     For a caller that changed <see cref="Segments" /> and wants it to take effect, which is the
    ///     only thing that invalidates the cache — the shapes themselves never change.
    ///     <see cref="Revision" /> moves with it, because a shape already uploaded to a device is now
    ///     the wrong shape and nothing else would say so.
    /// </remarks>
    public void Invalidate() {
        shapes.Clear();
        Revision++;
    }

    /// <summary>The geometry of one shape, built once and cached.</summary>
    /// <param name="kind">Which primitive.</param>
    /// <returns>Its vertices, normals and triangles, in the shape's own space.</returns>
    /// <remarks>
    ///     ⚠ <b>Public because the device side needs the geometry this collector's batches name.</b>
    ///     A <see cref="ShapeBatch" /> says "these entities are cubes", and the thing holding the
    ///     buffers has to be able to ask what a cube is without knowing how one is built or agreeing
    ///     separately about <see cref="Segments" /> — which is the disagreement that draws the picking
    ///     ray against one sphere and the pixels against another.
    /// </remarks>
    public MeshData Shape(PrimitiveKind kind) {
        if (!shapes.TryGetValue(kind, out var mesh)) {
            mesh = MeshPrimitives.Create(kind, Segments, Math.Max(MeshPrimitives.MinimumSegments, Segments / 2));
            shapes[kind] = mesh;
        }

        return mesh;
    }

    static void Add(Dictionary<PrimitiveKind, List<MeshInstance>> buckets, PrimitiveKind kind, MeshInstance instance) {
        if (!buckets.TryGetValue(kind, out var bucket)) {
            bucket = [];
            buckets[kind] = bucket;
        }

        bucket.Add(instance);
    }

    void Emit(Dictionary<PrimitiveKind, List<MeshInstance>> buckets, bool edges) {
        foreach (var (kind, bucket) in buckets) {
            if (bucket.Count == 0) {
                continue;
            }

            batches.Add(new(kind, instances.Count, bucket.Count, edges));
            instances.AddRange(bucket);

            if (!edges) {
                Triangles += Shape(kind).TriangleCount * bucket.Count;
            }
        }
    }
}
