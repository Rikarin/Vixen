// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;

namespace Vixen.Editor.SceneView;

/// <summary>Something that can say which entity is under a ray.</summary>
/// <remarks>
///     An interface rather than a type, for the reason <see cref="ISurfaceProbe" /> is one: the
///     viewport asks the question and something else knows the scene. It is also what lets a test
///     drive selection with three stub answers and no world.
/// </remarks>
public interface IScenePicker {
    /// <summary>Which entity a ray hits first.</summary>
    /// <param name="ray">The ray, in world space.</param>
    /// <param name="camera">The camera it came from, for the handles that are a size on screen.</param>
    /// <param name="width">How wide the viewport is, in render pixels.</param>
    /// <param name="height">How tall.</param>
    /// <returns>The entity, or <see cref="Entity.Null" /> for nothing.</returns>
    Entity Under(Ray ray, EditorCamera camera, int width, int height);

    /// <summary>Which entities a rubber-band rectangle takes.</summary>
    /// <param name="marquee">The band, in render pixels from the pane's top-left.</param>
    /// <param name="camera">The camera it was dragged in.</param>
    /// <param name="width">How wide the viewport is, in render pixels.</param>
    /// <param name="height">How tall.</param>
    /// <param name="into">Where to put them. Not cleared.</param>
    /// <remarks>
    ///     ⚠ <b>A list the caller owns rather than a returned one.</b> This runs once per drag, not
    ///     once per frame, so the allocation is not the point — what is, is that the caller is the
    ///     thing that knows whether the answer replaces a selection or extends one, and handing back a
    ///     fresh list would make the additive case a second copy.
    /// </remarks>
    void Within(Marquee marquee, EditorCamera camera, int width, int height, List<Entity> into);
}

/// <summary>Something that can say which face, edge or vertex of an entity is under the pointer.</summary>
/// <remarks>
///     <para>
///         <b>Deliberately not on <see cref="IScenePicker" />, and doc 24's B4 is why the two are
///         different questions.</b> "Which entity is under this ray" is asked of a scene and answers
///         with something the whole editor understands; "which face of <i>this</i> mesh" is asked of
///         one entity, answers with an index into a table only the caller and the mesh agree about,
///         and needs a tolerance in pixels because a vertex has no area. A stub that answered the
///         first cannot sensibly answer the second, and every test in this assembly that has one
///         would have had to.
///     </para>
///     <para>
///         An interface for the reason the other two are: the viewport asks and something else knows
///         the scene.
///     </para>
/// </remarks>
public interface ISubObjectPicker {
    /// <summary>Which element of an entity's mesh is under a point in the viewport.</summary>
    /// <param name="entity">The entity being edited.</param>
    /// <param name="pointer">Where the pointer is, in render pixels from the pane's top-left.</param>
    /// <param name="camera">The camera looking at it.</param>
    /// <param name="width">How wide the viewport is, in render pixels.</param>
    /// <param name="height">How tall.</param>
    /// <param name="filter">Which kinds may answer.</param>
    /// <param name="tolerance">How near counts, in render pixels.</param>
    /// <returns>The element, or <see cref="SubObject.None" />.</returns>
    SubObject Under(
        Entity entity,
        Vector2 pointer,
        EditorCamera camera,
        int width,
        int height,
        SubObjectFilter filter = SubObjectFilter.All,
        float tolerance = SubObjectPicker.DefaultTolerance
    );

    /// <summary>The elements of an entity's mesh, or <see langword="null" /> if it has none.</summary>
    /// <remarks>
    ///     What a highlight is drawn from: an index on its own names nothing without the table it
    ///     indexes. Doc 24's P2 is what draws it; this is what P2 will read.
    /// </remarks>
    MeshElements? ElementsOf(Entity entity);
}

/// <summary>Which entity is under a click, worked out on the processor.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is not what <see cref="PickingBuffer" /> is, and it is not meant to replace
///         it.</b> Drawing object ids with the same vertex path as the picture is the only way to be
///         right for a skinned mesh, an instanced forest or anything whose shader moved its vertices,
///         and that stage is written and tested. What it needs is a host that owns a render target for
///         it, and until there is one <c>SceneViewport.Picking</c> is null and every click in the
///         viewport selects nothing at all — which is what this is for. A scene of primitives and
///         markers is a scene a ray can be tested against exactly, and the arithmetic is the same
///         arithmetic whether or not a GPU is involved.
///     </para>
///     <para>
///         ⚠ <b>The ray goes into each shape's local space rather than the vertices coming out of
///         it.</b> A cube is twenty-four vertices and a torus six hundred; transforming them per
///         entity per click is the whole cost of the test, and inverting one matrix is not. The
///         parameter along the ray survives the transform unchanged so long as the direction is
///         <i>not</i> renormalised on the way in — which is what makes distances from differently
///         scaled entities comparable, and what makes a click on the near cube not select the far one
///         behind it.
///     </para>
///     <para>
///         <b>An entity with no shape is a marker, and a marker is a cross.</b> A cross has no area to
///         hit, so what is tested is a small sphere about the origin — sized in render pixels rather
///         than world units, because a light two hundred metres away is a handful of pixels and has to
///         stay clickable. That is the same reason a gizmo is a constant size on screen.
///     </para>
/// </remarks>
public sealed class ScenePicker : IScenePicker, ISubObjectPicker {
    readonly SceneDocument document;
    readonly Dictionary<PrimitiveKind, MeshData> shapes = [];

    /// <summary>The derived element tables, one per shape kind and shared by every entity of it.</summary>
    /// <remarks>
    ///     ⚠ <b>Cached because hover is a query per pointer move, which is doc 24's B4's own bar.</b>
    ///     Welding a torus' positions and finding its unique edges is a few thousand operations; doing
    ///     it per mouse move is the difference between a highlight that follows the pointer and one
    ///     that lags it. Keyed by kind rather than by entity for the same reason
    ///     <see cref="shapes" /> is — a hundred cubes are one table.
    /// </remarks>
    readonly Dictionary<PrimitiveKind, MeshElements> elements = [];

    readonly SubObjectPicker subObjects = new();

    /// <summary>Builds a picker over a scene.</summary>
    /// <param name="document">The scene.</param>
    public ScenePicker(SceneDocument document) {
        ArgumentNullException.ThrowIfNull(document);
        this.document = document;
    }

    /// <summary>How near a click has to be to a shapeless entity to pick it, in render pixels.</summary>
    /// <remarks>
    ///     ⚠ <b>Bigger than it looks like it should be.</b> The thing being aimed at is three
    ///     one-pixel lines crossing, and a tolerance equal to what is drawn is one that misses far
    ///     more often than it hits — the same reason <c>TransformGizmo.GrabRadius</c> is fourteen
    ///     pixels for an arm five across.
    /// </remarks>
    public float MarkerRadius { get; set; } = 14f;

    /// <summary>How many divisions a curved shape is tested with.</summary>
    /// <remarks>
    ///     <see cref="SceneMeshes.Segments" />', so that what is tested is what is drawn. A sphere
    ///     tested at a higher division than it is drawn at answers clicks on pixels that are not
    ///     there, and one tested lower has a rim of drawn pixels that does not answer — which is the
    ///     gizmo's own rule about tolerance and thickness, in a second place.
    /// </remarks>
    public int Segments { get; set; } = 24;

    /// <inheritdoc />
    public Entity Under(Ray ray, EditorCamera camera, int width, int height) {
        ArgumentNullException.ThrowIfNull(camera);

        var found = Entity.Null;
        var nearest = float.MaxValue;
        var world = document.World;

        foreach (var entity in document.Entities) {
            if (!world.IsAlive(entity) || !world.Has<WorldTransform>(entity)) {
                continue;
            }

            // ⚠ Hidden as well as locked, and the first is the one people are surprised by. Something
            // you cannot see and can still click is worse than either — you drag what you cannot look
            // at — and it is the whole reason an outliner has an eye rather than a delete key.
            if (document.IsHidden(entity) || document.IsLocked(entity)) {
                continue;
            }

            var transform = world.Read<WorldTransform>(entity).Value;

            var hit = PrimitiveShapes.TryGet(world, entity, out var kind)
                ? Shaped(ray, Shape(kind), transform)
                : Marker(ray, transform.Translation, camera, height);

            if (hit is { } distance && distance < nearest) {
                nearest = distance;
                found = entity;
            }
        }

        return found;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         <b>The shape's own oriented box, projected, and then the screen rectangle round
    ///         that.</b> Testing every triangle against the band would be exact and would cost a
    ///         scene's worth of projections per drag; testing the world-aligned bounds would be a box
    ///         bigger than a rotated crate on every axis. Eight corners through the entity's matrix is
    ///         the middle answer, and it is the one both reference editors give.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Corners behind the eye are dropped rather than projected.</b>
    ///         <see cref="EditorCamera.TryProject" />'s own remarks say why a perspective divide
    ///         answers for them at all: the point comes back mirrored through the middle of the pane,
    ///         which here would stretch the rectangle across the whole viewport and put the object in
    ///         every band anybody drags. An entity with no corner in front of the eye is behind the
    ///         camera and is skipped.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Hidden and locked entities are skipped, exactly as <see cref="Under" /> skips
    ///         them.</b> A band is the gesture that most easily takes something the user cannot see,
    ///         and a marquee and a click disagreeing about what is selectable is worse than either
    ///         rule on its own.
    ///     </para>
    /// </remarks>
    public void Within(Marquee marquee, EditorCamera camera, int width, int height, List<Entity> into) {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(into);

        if (width <= 0 || height <= 0) {
            return;
        }

        var world = document.World;

        foreach (var entity in document.Entities) {
            if (!world.IsAlive(entity) || !world.Has<WorldTransform>(entity)) {
                continue;
            }

            if (document.IsHidden(entity) || document.IsLocked(entity)) {
                continue;
            }

            var transform = world.Read<WorldTransform>(entity).Value;

            var taken = PrimitiveShapes.TryGet(world, entity, out var kind)
                ? Boxed(marquee, Shape(kind).Bounds, transform, camera, width, height)
                : camera.TryProject(transform.Translation, width, height, out var point) && marquee.Contains(point);

            if (taken) {
                into.Add(entity);
            }
        }
    }

    /// <summary>Whether a shape's oriented box touches the band once it is on screen.</summary>
    static bool Boxed(
        Marquee marquee,
        BoundingBox bounds,
        in Matrix4x4 transform,
        EditorCamera camera,
        int width,
        int height
    ) {
        var centre = (bounds.Minimum + bounds.Maximum) * 0.5f;
        var extent = (bounds.Maximum - bounds.Minimum) * 0.5f;

        var left = float.MaxValue;
        var top = float.MaxValue;
        var right = float.MinValue;
        var bottom = float.MinValue;
        var any = false;

        for (var index = 0; index < 8; index++) {
            var local = centre + new Vector3(
                (index & 1) == 0 ? -extent.X : extent.X,
                (index & 2) == 0 ? -extent.Y : extent.Y,
                (index & 4) == 0 ? -extent.Z : extent.Z
            );

            if (!camera.TryProject(Matrix4x4.TransformPosition(local, transform), width, height, out var point)) {
                continue;
            }

            left = MathF.Min(left, point.X);
            top = MathF.Min(top, point.Y);
            right = MathF.Max(right, point.X);
            bottom = MathF.Max(bottom, point.Y);
            any = true;
        }

        return any && marquee.Touches(left, top, right, bottom);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Hidden and locked entities answer nothing, exactly as <see cref="Under(Ray,
    ///     EditorCamera, int, int)" /> skips them.</b> Something you cannot see and can still edit is
    ///     worse than either, and a rule that held for clicking an object and not for clicking its
    ///     face would be a rule nobody could describe.
    /// </remarks>
    public SubObject Under(
        Entity entity,
        Vector2 pointer,
        EditorCamera camera,
        int width,
        int height,
        SubObjectFilter filter = SubObjectFilter.All,
        float tolerance = SubObjectPicker.DefaultTolerance
    ) {
        ArgumentNullException.ThrowIfNull(camera);

        if (ElementsOf(entity) is not { } mesh) {
            return SubObject.None;
        }

        var transform = document.World.Read<WorldTransform>(entity).Value;

        return subObjects.Under(mesh, transform, camera, width, height, pointer, filter, tolerance);
    }

    /// <inheritdoc />
    public MeshElements? ElementsOf(Entity entity) {
        var world = document.World;

        if (!world.IsAlive(entity)
            || !world.Has<WorldTransform>(entity)
            || document.IsHidden(entity)
            || document.IsLocked(entity)
            || !PrimitiveShapes.TryGet(world, entity, out var kind)) {
            return null;
        }

        if (!elements.TryGetValue(kind, out var mesh)) {
            elements[kind] = mesh = MeshElements.From(Shape(kind));
        }

        return mesh;
    }

    /// <summary>Forgets the shapes built so far, for a caller that changed <see cref="Segments" />.</summary>
    /// <remarks>
    ///     ⚠ <b>The element tables go with them.</b> They are derived from the shapes, so a table kept
    ///     across a change of <see cref="Segments" /> would name triangles of a mesh that no longer
    ///     exists — and the symptom is a highlight drawn round a face nobody can see.
    /// </remarks>
    public void Invalidate() {
        shapes.Clear();
        elements.Clear();
    }

    /// <summary>How far along a ray it first meets a shape, in world units, or null.</summary>
    /// <remarks>
    ///     ⚠ <b>The nearest hit is brought back out of local space rather than measured along the
    ///     world ray.</b> <c>Ray</c>'s constructor normalises, so the local ray's direction is a unit
    ///     vector in <i>local</i> units and the parameter it hands back is in local units too — a
    ///     shape scaled fourfold answers with a quarter of the distance, and a shape scaled to a
    ///     tenth answers with ten times it. That made <see cref="Under" />'s comparison meaningless
    ///     between two entities of different scale, and meaningless between a shape and a marker,
    ///     whose distance is already the world one. Taking the point through the matrix costs one
    ///     transform per entity and is exact.
    /// </remarks>
    static float? Shaped(Ray ray, MeshData mesh, in Matrix4x4 transform) {
        if (!Matrix4x4.Invert(transform, out var inverse)) {
            // A zero scale, which has no surface to hit. Not an error: an entity can be scaled to
            // nothing and scaled back, and a picker that threw would take the editor with it.
            return null;
        }

        var local = new Ray(
            Matrix4x4.TransformPosition(ray.Origin, inverse),
            Matrix4x4.TransformDirection(ray.Direction, inverse)
        );

        float? nearest = null;

        for (var index = 0; index + 2 < mesh.Indices.Length; index += 3) {
            var a = mesh.Positions[mesh.Indices[index]];
            var b = mesh.Positions[mesh.Indices[index + 1]];
            var c = mesh.Positions[mesh.Indices[index + 2]];

            if (local.Intersects(a, b, c, out var distance) && distance >= 0f && distance < (nearest ?? float.MaxValue)) {
                nearest = distance;
            }
        }

        return nearest is { } hit
            ? (Matrix4x4.TransformPosition(local.GetPoint(hit), transform) - ray.Origin).Length()
            : null;
    }

    /// <summary>How far along a ray it passes the sphere standing in for a marker, or null.</summary>
    float? Marker(Ray ray, Vector3 position, EditorCamera camera, int height) {
        var radius = Radius(position, camera, height);

        return ray.Intersects(new BoundingSphere(position, radius), out var distance) && distance >= 0f
            ? distance
            : null;
    }

    /// <summary>How big a marker's sphere is in world units, from how big it should look.</summary>
    /// <remarks>
    ///     ⚠ <b>Measured at the marker rather than at the camera's own pivot.</b> They differ the
    ///     moment the thing being clicked is not what the camera is orbiting, which is most of the
    ///     time — and a radius taken from the pivot makes everything nearer than it too easy to hit
    ///     and everything further too hard.
    /// </remarks>
    float Radius(Vector3 position, EditorCamera camera, int height) =>
        height <= 0 ? MarkerRadius : camera.WorldPerPixel(position, height) * MarkerRadius;

    MeshData Shape(PrimitiveKind kind) {
        if (!shapes.TryGetValue(kind, out var mesh)) {
            mesh = MeshPrimitives.Create(kind, Segments, Math.Max(MeshPrimitives.MinimumSegments, Segments / 2));
            shapes[kind] = mesh;
        }

        return mesh;
    }
}
