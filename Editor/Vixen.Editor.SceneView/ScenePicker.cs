// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Rendering;

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
public sealed class ScenePicker : IScenePicker {
    readonly SceneDocument document;
    readonly Dictionary<PrimitiveKind, MeshData> shapes = [];

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

            var hit = MeshShapes.TryGet(world, entity, out var kind)
                ? Shaped(ray, Shape(kind), transform)
                : Marker(ray, transform.Translation, camera, height);

            if (hit is { } distance && distance < nearest) {
                nearest = distance;
                found = entity;
            }
        }

        return found;
    }

    /// <summary>Forgets the shapes built so far, for a caller that changed <see cref="Segments" />.</summary>
    public void Invalidate() => shapes.Clear();

    /// <summary>How far along a ray it first meets a shape, or null.</summary>
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

        return nearest;
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
    float Radius(Vector3 position, EditorCamera camera, int height) {
        if (height <= 0) {
            return MarkerRadius;
        }

        if (camera.IsOrthographic) {
            return camera.OrthographicHeight / height * MarkerRadius;
        }

        var depth = MathF.Max(Vector3.Dot(position - camera.Position, camera.Forward), camera.NearPlane);

        return 2f * depth * MathF.Tan(camera.FieldOfView * 0.5f) / height * MarkerRadius;
    }

    MeshData Shape(PrimitiveKind kind) {
        if (!shapes.TryGetValue(kind, out var mesh)) {
            mesh = MeshPrimitives.Create(kind, Segments, Math.Max(MeshPrimitives.MinimumSegments, Segments / 2));
            shapes[kind] = mesh;
        }

        return mesh;
    }
}
