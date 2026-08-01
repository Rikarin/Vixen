// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Geometry;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
using Vixen.Rendering.Materials;

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
public readonly record struct ShapeBatch(SceneShape Shape, int First, int Count, bool Edges) {
    /// <summary>Which primitive, for a batch of them.</summary>
    /// <remarks>Meaningless for a batch of a mesh asset; ask <see cref="SceneShape.IsAsset" /> first.</remarks>
    public PrimitiveKind Kind => Shape.Kind;
}

/// <summary>What a run of entities draws: a built-in shape, or a mesh an artist authored.</summary>
/// <remarks>
///     <para>
///         <b>One key for two things, because a batch is a batch either way.</b> The viewport groups
///         entities that share geometry into one instanced draw, and whether that geometry came out of
///         <see cref="MeshPrimitives" /> or out of a bundle changes nothing about the grouping, the
///         instancing or the pipeline. What it changes is where the vertices come from, which is
///         <see cref="SceneMeshes.Shape" />'s business alone.
///     </para>
///     <para>
///         ⚠ <b>A <see cref="PrimitiveKind" /> of zero is <c>Cube</c>, so the discriminator has to be the
///         reference and not the kind.</b> Defaulting a key would otherwise mean "every entity is a
///         cube", which is a viewport full of cubes where the meshes should be — and the meshes are
///         exactly what nobody had yet, so it would have looked like the feature simply not working.
///     </para>
/// </remarks>
/// <param name="Kind">Which primitive, when this names one.</param>
/// <param name="Mesh">Which mesh asset, when it names one instead.</param>
/// <param name="Owner">Whose edited mesh, when it names one of those.</param>
/// <param name="Version">Which revision of that mesh — see <c>SceneDocument.MeshVersion</c>.</param>
/// <param name="Group">Which face group of it, or −1 for the whole mesh.</param>
public readonly record struct SceneShape(
    PrimitiveKind Kind,
    AssetReference Mesh,
    Entity Owner = default,
    int Version = 0,
    int Group = -1
) {
    /// <summary>Whether this names an authored mesh rather than a built-in shape.</summary>
    public bool IsAsset => !Mesh.IsNull;

    /// <summary>Whether this names one entity's own edited geometry.</summary>
    /// <remarks>
    ///     <b>Doc 24's B1 follow-up, and its own words for it: a block-out mesh is one shape per
    ///     <i>entity</i> rather than one per kind.</b> Everything else here is shared — a hundred cubes
    ///     are one upload — and an edited mesh cannot be, because no two of them are the same geometry.
    /// </remarks>
    public bool IsEdit => !Owner.IsNull;

    /// <summary>A key for a built-in shape.</summary>
    public static SceneShape Of(PrimitiveKind kind) => new(kind, AssetReference.Null);

    /// <summary>A key for an authored mesh.</summary>
    public static SceneShape Of(AssetReference mesh) => new(default, mesh);

    /// <summary>A key for one entity's edited mesh, at one revision of it.</summary>
    /// <param name="owner">Whose.</param>
    /// <param name="version">Which revision.</param>
    /// <param name="group">Which face group, or −1 for the whole mesh.</param>
    /// <returns>The key.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The version is part of the key rather than something checked beside it.</b> A device
    ///         holds geometry under whatever key registered it, so an edit that kept the key would be a
    ///         mesh drawn at the shape it had before the edit — and every consumer would need its own
    ///         staleness check. As a key, the change <i>is</i> the invalidation.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And so is the group, which is doc 24's P5 per-face material.</b> Two materials on
    ///         one mesh is two draws, because a material is per instance here — see the shader's own
    ///         README on why that is what keeps two entities of different materials one draw. So a
    ///         mesh with materials on three of its groups is uploaded as three pieces and drawn as
    ///         three instances of one transform, and a mesh with none is uploaded whole.
    ///     </para>
    /// </remarks>
    public static SceneShape Of(Entity owner, int version, int group = -1) =>
        new(default, AssetReference.Null, owner, version, group);
}

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
///         that was the thing <c>docs/plan/24-blockout-tools.md</c> § B1 called a blocker rather than a
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
    readonly Dictionary<SceneShape, List<MeshInstance>> solids = [];
    readonly Dictionary<SceneShape, List<MeshInstance>> wires = [];

    // What the source answered this frame, so a batch can be asked what its geometry is without the
    // source being asked twice for one mesh — and so `Shape` needs no source of its own.
    readonly Dictionary<AssetReference, MeshData> assets = [];

    // ⚠ Kept across frames and keyed by the shape — which carries the revision — so the drawing
    // geometry of an edited mesh is built on the frame the edit happened and not on every frame after
    // it. Trimmed to what the frame actually drew, because a key nobody names again is a mesh whose
    // entity has been deleted or whose next revision has replaced it.
    readonly Dictionary<SceneShape, MeshData> edits = [];
    readonly HashSet<SceneShape> living = [];

    // What one entity draws this frame, which is one piece unless doc 24's P5 has put a material on
    // some of its face groups. Reused rather than allocated per entity.
    readonly List<(SceneShape Shape, AssetReference Material)> pieces = [];

    // ⚠ Kept across frames rather than cleared with the rest, and the null entries are the reason: a
    // reference the source has no material for is asked about once and remembered as "none", so a
    // scene full of unmaterialled block-out does not walk an import cache once per entity per frame.
    // What makes that safe is `Invalidate`, which is already called when an import finishes.
    readonly Dictionary<AssetReference, MaterialSurface?> surfaces = [];

    /// <summary>The frame's entities, one instance each, grouped by <see cref="Batches" />.</summary>
    /// <remarks>
    ///     A span rather than an <see cref="IReadOnlyList{T}" />, which is what
    ///     <see cref="SceneLines" /> hands back: this is read by <c>MeshInstanceRenderer.Upload</c>,
    ///     which wants one.
    /// </remarks>
    public ReadOnlySpan<MeshInstance> Instances => CollectionsMarshal.AsSpan(instances);

    /// <summary>Which run of them is which shape.</summary>
    public IReadOnlyList<ShapeBatch> Batches => batches;

    /// <summary>Where the geometry a mesh reference names comes from. Null draws no referenced mesh.</summary>
    /// <remarks>
    ///     <b>The viewport's half of the join a game makes through <c>MeshExtractionSystem.Meshes</c>.</b>
    ///     The editor reads its meshes out of the import cache rather than out of a bundle, which is the
    ///     only difference and is exactly what the interface exists to absorb — see
    ///     <c>ProjectMeshSource</c>.
    /// </remarks>
    public IMeshSource? Meshes { get; set; }

    /// <summary>Where an entity's material comes from. Null shades everything neutrally.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><see cref="Meshes" />'s counterpart, and the reason the viewport is no longer one flat
    ///         grey.</b> A material reference resolves to the four numbers a preview can shade with —
    ///         see <see cref="MaterialSurface" />, which is what that reduction costs and what it keeps.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Null is the previous behaviour exactly, not a degraded one.</b> Every entity is
    ///         drawn with <see cref="MaterialSurface.Default" /> — a fully rough dielectric, which is
    ///         one directional term — so a host that sets no source draws the picture this collector
    ///         drew before it could read a material at all. The same is true of an entity naming no
    ///         material, and of one whose material has not been imported.
    ///     </para>
    /// </remarks>
    public ISurfaceSource? Surfaces { get; set; }

    /// <summary>How many entities are waiting for geometry that has not been read yet.</summary>
    /// <remarks>
    ///     ⚠ A number that stays up is a reference nothing can resolve — a mesh that failed to import, a
    ///     model deleted since the scene was saved — which otherwise looks exactly like a scene with a
    ///     hole in it.
    /// </remarks>
    public int Waiting { get; private set; }

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

    /// <summary>How big a block-out checker square is, in metres. Zero draws none.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 24's P5 blockout material, and it is the checker every editor makes you build by
    ///         hand.</b> Squares of a fixed size in <i>world</i> units, so a box scaled eight by three
    ///         has squares the same size as the floor it stands on and "how wide is that corridor" is
    ///         something you count rather than something you measure.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Meant to be kept in step with <c>WorkPlane.Step</c> by whoever owns both.</b> The
    ///         grid you can see, the grid you snap to and the squares on the surfaces being snapped are
    ///         one number in doc 24's D5, and they are two or three in more than one shipping editor —
    ///         which is a bug people never manage to describe.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Only on surfaces with no material.</b> An entity somebody has assigned brick to
    ///         should look like brick; the checker is what says "nobody has dressed this yet".
    ///     </para>
    /// </remarks>
    public float Checker { get; set; } = 1f;

    /// <summary>How strongly the checker is tinted by which axis a face points along, from zero to one.</summary>
    /// <remarks>
    ///     Small on purpose. It is there so that a wall and a floor read as different planes at a
    ///     glance; a strong one makes every screenshot of a block-out look like a debug view, which is
    ///     what makes people turn the whole thing off.
    /// </remarks>
    public float AxisTint { get; set; } = 1f;

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

        assets.Clear();

        Count = 0;
        Triangles = 0;
        Waiting = 0;

        var show = viewport?.Show ?? SceneShow.Default;
        var mode = viewport?.Modes.Current ?? ViewMode.Shaded;

        if ((show & SceneShow.Meshes) == 0) {
            return 0;
        }

        var surfaces = ViewShading.DrawsSurfaces(mode);
        var wire = ViewShading.DrawsEdges(mode);
        var normals = ViewShading.ColoursByNormal(mode);
        var rough = ViewShading.ColoursByRoughness(mode);
        var plain = ViewShading.IgnoresSelectionColour(mode);

        // ⚠ The two modes whose whole content is one channel of the surface are the two that must not
        // be *shaded* by it: a normal view lit by a metal's specular lobe, or a roughness view whose
        // greys are multiplied by the roughness they are a picture of, is the value and the thing it
        // decides multiplied together — which is exactly the picture the modes exist to take apart.
        var shaded = !normals && !rough;

        var outline = surfaces && (show & SceneShow.Outline) != 0;

        // The style lanes the shader reads, assembled once rather than per entity: everything that
        // varies per entity is the transform, the colour and the material.
        var surfaceStyle = new Vector4(0f, 0f, 0f, normals ? 1f : 0f);
        var outlineStyle = new Vector4(OutlineWidth, OutlineBias, 1f, 0f);
        var wireStyle = new Vector4(0f, 0f, 1f, 0f);

        // ⚠ The neutral surface, and the outline and the wires are drawn with it deliberately. Both
        // are lit flat, which the shader does by handing them a normal that faces the key — a trick
        // that survives a BRDF only because a fully rough dielectric's specular lobe is worth about
        // two per cent of its colour. A rim given a *metal* surface would be a selection outline with
        // a highlight sliding along it.
        var neutral = MeshInstance.Packed(MaterialSurface.Default);

        var world = document.World;

        living.Clear();

        foreach (var entity in document.Entities) {
            if (!world.Has<WorldTransform>(entity) || !Drawn(document, entity, pieces)) {
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

            foreach (var (kind, material) in pieces) {
                var surface = Surface(material);

                // ⚠ One matrix inverse per piece, shared by its surface, its outline and its wires.
                // The three instances a piece can produce differ only in colour, style and material,
                // so this is built once and copied — building each would be three inverses of one
                // transform.
                // ⚠ The checker only where there is no material, which is doc 24's P5 "blockout
                // material, default": what a wall nobody has dressed yet is drawn with, and what a
                // wall somebody has assigned brick to is not. It is also off in the modes whose whole
                // content is one channel of the surface, where a checker would be a picture of two
                // things multiplied together.
                var checkered = shaded && surface is null ? Checker : 0f;

                var placement = MeshInstance.Of(
                    transform,
                    ShapeColour,
                    surface: shaded ? surface : null,
                    checker: checkered,
                    tint: checkered > 0f ? AxisTint : 0f
                );

                if (surfaces) {
                    Add(
                        solids,
                        kind,
                        placement with { Colour = Tint(surface, selected && !plain, rough), Style = surfaceStyle }
                    );
                }

                if (outline && selected) {
                    Add(
                        solids,
                        kind,
                        placement with { Colour = OutlineColour, Style = outlineStyle, Surface = neutral, Emissive = default }
                    );
                }

                if (wire) {
                    Add(
                        wires,
                        kind,
                        placement with { Colour = WireColour, Style = wireStyle, Surface = neutral, Emissive = default }
                    );
                }
            }

            Count++;
        }

        // ⚠ The batch order does not matter and the grouping does. One batch per shape and topology is
        // what makes a shaded wireframe of a hundred cubes two draws; which of the two goes first is
        // settled by the depth buffer, because both pipelines test depth and an outline is biased away
        // from the eye rather than relying on being drawn second.
        Emit(solids, edges: false);
        Emit(wires, edges: true);

        // ⚠ After the emit, because a batch names a shape and `Shape` has to be able to answer for
        // every batch this frame produced. What goes is the revision an edit replaced, which is at
        // most one entry per mesh somebody is dragging.
        if (edits.Count > living.Count) {
            foreach (var shape in edits.Keys.Where(shape => !living.Contains(shape)).ToArray()) {
                edits.Remove(shape);
            }
        }

        return Count;
    }

    /// <summary>Forgets the geometry and the materials built so far.</summary>
    /// <remarks>
    ///     For a caller that changed <see cref="Segments" /> and wants it to take effect, which is the
    ///     only thing that invalidates the shapes — those never change otherwise.
    ///     <see cref="Revision" /> moves with it, because a shape already uploaded to a device is now
    ///     the wrong shape and nothing else would say so.
    ///     <para>
    ///         ⚠ <b>The material cache goes too, and it is the half that actually goes stale.</b> A
    ///         shape is a function of <see cref="Segments" /> and nothing else; a material is a file
    ///         somebody is editing in another tab, and one that has been re-imported is a different
    ///         chunk under the same reference — so nothing about the remembered surface would ever say
    ///         it is out of date. This is what an import finishing calls.
    ///     </para>
    /// </remarks>
    public void Invalidate() {
        shapes.Clear();
        surfaces.Clear();
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
    public MeshData? Shape(SceneShape shape) {
        if (shape.IsEdit) {
            return edits.GetValueOrDefault(shape);
        }

        if (shape.IsAsset) {
            // Read out of what the collect already resolved rather than asked for again: a batch names
            // only shapes some entity drew this frame, so a miss here is a caller asking about a batch
            // from a different frame.
            return assets.GetValueOrDefault(shape.Mesh);
        }

        if (!shapes.TryGetValue(shape.Kind, out var mesh)) {
            mesh = MeshPrimitives.Create(
                shape.Kind,
                Segments,
                Math.Max(MeshPrimitives.MinimumSegments, Segments / 2)
            );

            shapes[shape.Kind] = mesh;
        }

        return mesh;
    }

    /// <summary>The geometry of one built-in shape, built once and cached.</summary>
    /// <param name="kind">Which primitive.</param>
    /// <returns>Its vertices, normals and triangles, in the shape's own space.</returns>
    public MeshData Shape(PrimitiveKind kind) => Shape(SceneShape.Of(kind))!;

    /// <summary>What an entity draws, if this frame can draw it.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="shape">What it draws.</param>
    /// <returns>Whether it draws anything this frame.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>The mesh wins, exactly as it does in a game.</b> <c>MeshExtractionSystem</c> makes that
    ///         an archetype fact with <c>WithNone&lt;MeshRenderable&gt;</c>; this is the same rule written
    ///         as a branch, because the editor walks a document's entity list rather than a query. An
    ///         entity that looked different in the viewport from how it looks in the game is the one
    ///         defect a viewport must not have.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An unloaded mesh draws nothing rather than falling back to its shape</b> — for the
    ///         reason the extraction gives: an entity that changed appearance while its mesh loaded is a
    ///         scene that looks different depending on how fast the disk is. <see cref="Waiting" /> is
    ///         what says so, and it falls to zero once the imports have been read.
    ///     </para>
    /// </remarks>
    bool Drawn(SceneDocument document, Entity entity, List<(SceneShape Shape, AssetReference Material)> into) {
        into.Clear();

        var world = document.World;

        var own = PrimitiveShapes.TryGet(world, entity, out _)
            ? world.Read<PrimitiveShape>(entity).Material
            : AssetReference.Null;

        // ⚠ First, and above the mesh asset as well as above the primitive. An entity being edited is
        // an entity whose geometry is the `EditMesh` and nothing else: a moved vertex that did not
        // move on screen is doc 24's P1 exit clause coming due, and it comes due here.
        if (document.MeshOf(entity) is { } edited) {
            var version = document.MeshVersion(entity);
            var assigned = document.MaterialsOf(entity);

            // ⚠ One piece per group only when a group actually names a material, and the whole mesh
            // otherwise. A block-out is nearly all one material, and splitting every wall into six
            // pieces because a box has six groups would be six uploads and six draws for one picture.
            foreach (var group in Pieces(edited, assigned)) {
                var shape = SceneShape.Of(entity, version, group);

                if (!edits.ContainsKey(shape)) {
                    edits[shape] = edited.ToMeshData($"Edit {entity.Id}", group);
                }

                living.Add(shape);
                into.Add((shape, group >= 0 && assigned.TryGetValue(group, out var material) ? material : own));
            }

            return into.Count > 0;
        }

        if (MeshRenderables.TryGet(world, entity, out var renderable)) {
            if (Meshes is null || renderable.Mesh.IsNull || !Meshes.TryGet(renderable.Mesh, out var mesh)) {
                Waiting++;
                return false;
            }

            assets[renderable.Mesh] = mesh;
            into.Add((SceneShape.Of(renderable.Mesh), renderable.Material));

            return true;
        }

        if (!PrimitiveShapes.TryGet(world, entity, out var kind)) {
            return false;
        }

        // ⚠ A block-out primitive carries a material of its own, and it is read here rather than
        // being assumed absent. Giving a wall a brick material before anybody has modelled the wall
        // is most of what a block-out pass is for; a viewport that showed those walls grey would send
        // the author to the game to find out what they had made.
        into.Add((SceneShape.Of(kind), own));

        return true;
    }

    /// <summary>Which pieces an edited mesh is drawn as: one per materialled group, or one in all.</summary>
    static IEnumerable<int> Pieces(EditMesh mesh, IReadOnlyDictionary<int, AssetReference> assigned) {
        if (assigned.Count == 0) {
            return [-1];
        }

        SortedSet<int> groups = [];

        foreach (var face in mesh.Faces) {
            groups.Add(face.Group);
        }

        // Sorted, because the batch order follows the group order and a picture that changed which
        // half of a coplanar pair was drawn second between frames would flicker where they meet.
        return groups;
    }

    /// <summary>What a material reference is shaded as, remembered for the frame.</summary>
    /// <remarks>
    ///     ⚠ <b>Null is "no material", which is not the same value as
    ///     <see cref="MaterialSurface.Default" />.</b> The caller needs to tell them apart for one
    ///     reason: an entity with a material takes its base colour, and an entity without one takes
    ///     <see cref="ShapeColour" />. Collapsing the two would paint every unmaterialled block-out
    ///     white, which is the neutral surface's albedo and nobody's idea of a block-out.
    /// </remarks>
    MaterialSurface? Surface(AssetReference material) {
        if (Surfaces is null || material.IsNull) {
            return null;
        }

        if (surfaces.TryGetValue(material, out var cached)) {
            return cached;
        }

        var found = Surfaces.TryGet(material, out var surface) ? surface : (MaterialSurface?) null;

        surfaces[material] = found;

        return found;
    }

    /// <summary>What colour an instance's surface is drawn in.</summary>
    /// <remarks>
    ///     ⚠ <b>Selection wins over the material, and it is the one rule here worth arguing with.</b>
    ///     A selected object drawn in its own colours would be identified only by its rim, which is off
    ///     in some show-flag combinations and invisible against a background of the same hue — the case
    ///     <see cref="OutlineColour" />'s own remarks are about. So selection keeps the amber, and
    ///     looking at what a material does to an object means clicking somewhere else. Both Unity and
    ///     Unreal go the other way; this follows the outliner instead, where a selected row is a
    ///     coloured row.
    /// </remarks>
    Color4 Tint(MaterialSurface? surface, bool selected, bool rough) {
        if (rough) {
            var value = (surface ?? MaterialSurface.Default).Roughness;
            return new(value, value, value, 1f);
        }

        return selected ? SelectedColour : surface?.BaseColour ?? ShapeColour;
    }

    static void Add(Dictionary<SceneShape, List<MeshInstance>> buckets, SceneShape kind, MeshInstance instance) {
        if (!buckets.TryGetValue(kind, out var bucket)) {
            bucket = [];
            buckets[kind] = bucket;
        }

        bucket.Add(instance);
    }

    void Emit(Dictionary<SceneShape, List<MeshInstance>> buckets, bool edges) {
        foreach (var (kind, bucket) in buckets) {
            if (bucket.Count == 0) {
                continue;
            }

            batches.Add(new(kind, instances.Count, bucket.Count, edges));
            instances.AddRange(bucket);

            if (!edges) {
                Triangles += (Shape(kind)?.TriangleCount ?? 0) * bucket.Count;
            }
        }
    }
}
