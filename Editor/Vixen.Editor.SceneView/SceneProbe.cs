// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;

namespace Vixen.Editor.SceneView;

/// <summary>Everything a viewport asks about the geometry in front of it.</summary>
/// <remarks>
///     <para>
///         <b><see cref="ISurfaceProbe" /> plus the two questions a <i>drag</i> asks.</b> A drop needs
///         "what is under this ray" and nothing more, which is why the narrow interface exists and
///         stays. A drag needs the same question with the thing being dragged taken out of the answer,
///         and it needs "which vertex is nearest the pointer" — and both of those are useless without
///         the exclusion, because the pointer is over the object being moved for the whole of every
///         drag.
///     </para>
///     <para>
///         An interface rather than a type for the reason <see cref="IScenePicker" /> is one: the
///         viewport asks and something else knows the scene, so a test can answer with three stubbed
///         hits and no world.
///     </para>
/// </remarks>
public interface ISceneProbe : ISurfaceProbe {
    /// <summary>Casts a ray, ignoring some entities.</summary>
    /// <param name="ray">The ray.</param>
    /// <param name="ignore">What must not answer — normally what is being dragged.</param>
    /// <param name="hit">What it hit.</param>
    /// <returns>Whether it hit anything.</returns>
    bool Raycast(Ray ray, IReadOnlyList<Entity> ignore, out SurfaceHit hit);

    /// <summary>The vertex nearest the pointer, within a radius measured on screen.</summary>
    /// <param name="pointer">Where the pointer is, in render pixels from the pane's top-left.</param>
    /// <param name="camera">The camera the pane is looking through.</param>
    /// <param name="width">How wide the pane is, in render pixels.</param>
    /// <param name="height">How tall.</param>
    /// <param name="radius">How near the pointer a vertex has to be, in render pixels.</param>
    /// <param name="ignore">What must not answer.</param>
    /// <param name="position">Where it is, in world space.</param>
    /// <returns>Whether there was one.</returns>
    /// <remarks>
    ///     ⚠ <b>Nearest on screen, not nearest in the world.</b> The gesture is "put it on that
    ///     corner", and which corner is meant is decided by where the pointer is — a world-space
    ///     nearest would jump to a vertex behind the one being aimed at whenever the view is nearly
    ///     along the surface.
    /// </remarks>
    bool TryNearestVertex(
        Vector2 pointer,
        EditorCamera camera,
        int width,
        int height,
        float radius,
        IReadOnlyList<Entity> ignore,
        out Vector3 position
    );

    /// <summary>Where a snap lands, given everything a <see cref="SnapContext" /> says about it.</summary>
    /// <param name="ray">The ray under the pointer, for the surface element.</param>
    /// <param name="pointer">Where the pointer is, in render pixels from the pane's top-left.</param>
    /// <param name="camera">The camera the pane is looking through.</param>
    /// <param name="width">How wide the pane is, in render pixels.</param>
    /// <param name="height">How tall.</param>
    /// <param name="snap">Which elements may answer, and how the search is done.</param>
    /// <param name="origin">Where the snap base is, for a search that is not from the view.</param>
    /// <param name="ignore">What must not answer.</param>
    /// <param name="hit">Where it landed.</param>
    /// <returns>Whether anything answered.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>One question where there were two, and doc 24's D4 is why.</b> A caller asking "is
    ///         vertex snapping on, and if so which vertex, and otherwise is surface snapping on" is a
    ///         caller that has to be written again for every tool, and the second copy is the one that
    ///         behaves differently. The precedence — vertex, edge centre, edge, surface — lives here.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="SnapModifiers.IgnoreSelf" /> is <i>not</i> applied here.</b> What is
    ///         being dragged is the caller's own business — a gizmo knows its targets and a placement
    ///         has none — so the caller reads the modifier and passes the list or an empty one. A probe
    ///         that decided for itself would need to be told what "self" is, which is the parameter
    ///         that already exists.
    ///     </para>
    /// </remarks>
    bool TrySnap(
        Ray ray,
        Vector2 pointer,
        EditorCamera camera,
        int width,
        int height,
        SnapContext snap,
        Vector3 origin,
        IReadOnlyList<Entity> ignore,
        out SnapHit hit
    );
}

/// <summary>What a ray and a pointer meet in a scene, worked out on the processor.</summary>
/// <remarks>
///     <para>
///         <b><see cref="ScenePicker" />'s twin, and it shares that type's whole argument.</b> The
///         geometry the editor draws is <see cref="MeshPrimitives" />' own, built from a kind and a
///         matrix, so testing a ray against it exactly costs one matrix inversion per entity and is
///         the same arithmetic whether or not there is a GPU. What it is not is the readback doc 20
///         asks for: a shader that moved its vertices, a skinned mesh or an instanced forest is
///         geometry this cannot see, and the day the viewport draws one of those this becomes the
///         fallback rather than the answer.
///     </para>
///     <para>
///         ⚠ <b>The ray goes into local space and the vertices do not come out of it — except for the
///         vertex query, which has to.</b> A ray test survives the transform; "which vertex is nearest
///         the pointer <i>on screen</i>" does not, because screen space is on the other side of the
///         projection. So the surface half is one inversion per entity and the vertex half is one
///         projection per vertex, and the second is why it is bounded by a screen-space rejection
///         against the entity's box first.
///     </para>
/// </remarks>
public sealed class SceneProbe : ISceneProbe {
    readonly SceneDocument document;
    readonly Dictionary<PrimitiveKind, MeshData> shapes = [];

    /// <summary>The drawing geometry of each edited mesh, with the revision it is of.</summary>
    /// <inheritdoc cref="Geometry" select="remarks" />
    readonly Dictionary<Entity, (int Version, MeshData Mesh)> edits = [];

    /// <summary>The welded elements per shape kind, which is what a snap actually lands on.</summary>
    readonly Dictionary<PrimitiveKind, MeshElements> elements = [];

    /// <summary>Builds a probe over a scene.</summary>
    /// <param name="document">The scene.</param>
    public SceneProbe(SceneDocument document) {
        ArgumentNullException.ThrowIfNull(document);
        this.document = document;
    }

    /// <summary>How many divisions a curved shape is tested with.</summary>
    /// <remarks>
    ///     <see cref="SceneMeshes.Segments" />', so that what is snapped to is what is drawn. A sphere
    ///     tested finer than it is drawn has vertices that are not on screen anywhere.
    /// </remarks>
    public int Segments { get; set; } = 24;

    /// <inheritdoc />
    public bool Raycast(Ray ray, out SurfaceHit hit) => Raycast(ray, [], out hit);

    /// <inheritdoc />
    public bool Raycast(Ray ray, IReadOnlyList<Entity> ignore, out SurfaceHit hit) {
        ArgumentNullException.ThrowIfNull(ignore);

        hit = default;

        var world = document.World;
        var nearest = float.MaxValue;
        var found = false;

        foreach (var entity in document.Entities) {
            if (!Eligible(entity, ignore)) {
                continue;
            }

            // ⚠ The entity's own mesh first. Everything doc 24's P4 makes is an `EditMesh`, so a probe
            // that only knew about `PrimitiveShape` could not see a single wall in a block-out — which
            // is why "Work Plane to Face" did nothing, and why a drop and a surface snap fell through
            // to the ground plane on geometry they were pointing straight at.
            var geometry = Geometry(entity);

            if (geometry is null) {
                continue;
            }

            var transform = world.Read<WorldTransform>(entity).Value;

            if (Surface(ray, geometry, transform, out var candidate) && candidate.Distance < nearest) {
                nearest = candidate.Distance;
                hit = candidate;
                found = true;
            }
        }

        return found;
    }

    /// <inheritdoc />
    public bool TryNearestVertex(
        Vector2 pointer,
        EditorCamera camera,
        int width,
        int height,
        float radius,
        IReadOnlyList<Entity> ignore,
        out Vector3 position
    ) {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(ignore);

        position = default;

        if (width <= 0 || height <= 0 || radius <= 0f) {
            return false;
        }

        var world = document.World;
        var nearest = radius * radius;
        var found = false;

        foreach (var entity in document.Entities) {
            if (!Eligible(entity, ignore) || Geometry(entity) is not { } mesh) {
                continue;
            }

            var transform = world.Read<WorldTransform>(entity).Value;

            // ⚠ The box first, in screen space, widened by the radius. Without it this projects every
            // vertex of every shape in the scene on every frame of every drag — which for a scene of
            // spheres is hundreds of thousands of matrix multiplies a second to answer a question
            // about the handful of vertices near the pointer.
            if (!NearBox(mesh.Bounds, transform, camera, width, height, pointer, radius)) {
                continue;
            }

            foreach (var local in mesh.Positions) {
                var point = Matrix4x4.TransformPosition(local, transform);

                if (!camera.TryProject(point, width, height, out var screen)) {
                    continue;
                }

                var distance = Vector2.DistanceSquared(screen, pointer);

                if (distance < nearest) {
                    nearest = distance;
                    position = point;
                    found = true;
                }
            }
        }

        return found;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The elements of a mesh rather than its drawing vertices, which is what unblocked
    ///     this.</b> Doc 24's B5 said vertex snapping was waiting for "the mesh under the pointer with
    ///     an indexed vertex list", and <see cref="MeshElements" /> is one: a cube's eight corners
    ///     rather than the twenty-four entries <c>MeshData</c> splits them into, and the twelve edges
    ///     that are not in the drawing structure at all. Without the welding, snapping to a cube's
    ///     corner would have three answers at the same place and edge snapping would have none.
    /// </remarks>
    public bool TrySnap(
        Ray ray,
        Vector2 pointer,
        EditorCamera camera,
        int width,
        int height,
        SnapContext snap,
        Vector3 origin,
        IReadOnlyList<Entity> ignore,
        out SnapHit hit
    ) {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(snap);
        ArgumentNullException.ThrowIfNull(ignore);

        hit = default;

        if (width <= 0 || height <= 0) {
            return false;
        }

        // ⚠ Smallest element first, and each pass walks the scene rather than one pass ranking
        // everything. A vertex within reach must beat an edge through it even when the edge is nearer
        // in pixels, which a single accumulator over one metric cannot express — and it is the same
        // innermost-wins rule `SubObjectPicker` applies for the same reason.
        foreach (var element in Precedence) {
            if (!snap.Has(element)) {
                continue;
            }

            if (element == SnapElements.Face) {
                if (Raycast(ray, ignore, out var surface)) {
                    hit = new SnapHit(surface.Point, surface.Normal, SnapElements.Face);
                    return true;
                }

                continue;
            }

            if (TryElement(element, pointer, camera, width, height, snap, origin, ignore, out var point)) {
                // ⚠ No normal. A vertex is a point and an edge is a line; neither says which way
                // anything faces, so `AlignToTarget` has nothing to align to and the drag is a move.
                hit = new SnapHit(point, null, element);
                return true;
            }
        }

        return false;
    }

    /// <summary>The order the elements are tried in: smallest first.</summary>
    static readonly SnapElements[] Precedence = [
        SnapElements.Vertex, SnapElements.EdgeCentre, SnapElements.Edge, SnapElements.Face
    ];

    /// <summary>The nearest vertex, edge centre or point on an edge, by whichever metric is in force.</summary>
    bool TryElement(
        SnapElements element,
        Vector2 pointer,
        EditorCamera camera,
        int width,
        int height,
        SnapContext snap,
        Vector3 origin,
        IReadOnlyList<Entity> ignore,
        out Vector3 position
    ) {
        position = default;

        if (snap.VertexRadius <= 0f) {
            return false;
        }

        var projected = snap.Is(SnapModifiers.ProjectFromView);

        // ⚠ The same reach either way. Turning the modifier off changes *where* the search happens
        // and not how far it goes, so a user who tries both does not also have to re-tune a radius.
        var reach = projected
            ? snap.VertexRadius
            : camera.WorldPerPixel(origin, height) * snap.VertexRadius;

        var nearest = reach * reach;
        var world = document.World;
        var found = false;

        // ⚠ A local rather than the out parameter, because C# will not let a local function capture
        // one — and the three metrics below are local functions so that the two searches share a
        // single "is this the best so far" test rather than repeating it four times.
        var chosen = Vector3.Zero;

        foreach (var entity in document.Entities) {
            if (!Eligible(entity, ignore) || !PrimitiveShapes.TryGet(world, entity, out var kind)) {
                continue;
            }

            var transform = world.Read<WorldTransform>(entity).Value;

            // The screen-space box rejection only helps the screen-space search; a world-space one is
            // bounded by `reach` in metres, which is the comparison the inner loop already makes.
            if (projected && !NearBox(Shape(kind).Bounds, transform, camera, width, height, pointer, reach)) {
                continue;
            }

            var elements = Elements(kind);

            if (element == SnapElements.Edge) {
                Edges(elements, transform);
            } else {
                Points(elements, transform, element == SnapElements.EdgeCentre);
            }
        }

        position = chosen;
        return found;

        // Vertices, or the midpoints of edges — both are points and are measured the same way.
        void Points(MeshElements elements, in Matrix4x4 transform, bool centres) {
            var positions = elements.Positions;
            var edges = elements.Edges;
            var count = centres ? edges.Length : positions.Length;

            for (var index = 0; index < count; index++) {
                var local = centres
                    ? (positions[edges[index].A] + positions[edges[index].B]) * 0.5f
                    : positions[index];

                Consider(Matrix4x4.TransformPosition(local, transform));
            }
        }

        void Edges(MeshElements elements, in Matrix4x4 transform) {
            var positions = elements.Positions;

            foreach (var edge in elements.Edges) {
                var a = Matrix4x4.TransformPosition(positions[edge.A], transform);
                var b = Matrix4x4.TransformPosition(positions[edge.B], transform);

                if (!projected) {
                    Consider(Nearest(a, b, origin));
                    continue;
                }

                // ⚠ Both ends, not either. An edge with one end behind the eye has no screen segment:
                // the far end's projection is mirrored through the middle of the pane, so what would
                // be measured is a line nothing drew, lying across the viewport.
                if (!camera.TryProject(a, width, height, out var from)
                    || !camera.TryProject(b, width, height, out var to)) {
                    continue;
                }

                var along = Along(from, to, pointer);
                var distance = Vector2.DistanceSquared(Vector2.Lerp(from, to, along), pointer);

                if (distance < nearest) {
                    nearest = distance;
                    chosen = Vector3.Lerp(a, b, along);
                    found = true;
                }
            }
        }

        void Consider(Vector3 point) {
            float distance;

            if (projected) {
                if (!camera.TryProject(point, width, height, out var screen)) {
                    return;
                }

                distance = Vector2.DistanceSquared(screen, pointer);
            } else {
                distance = Vector3.DistanceSquared(point, origin);
            }

            if (distance < nearest) {
                nearest = distance;
                chosen = point;
                found = true;
            }
        }
    }

    /// <summary>The point on a segment nearest another point.</summary>
    static Vector3 Nearest(Vector3 from, Vector3 to, Vector3 point) {
        var span = to - from;
        var length = span.LengthSquared();

        return length <= MathUtil.ZeroTolerance
            ? from
            : from + (span * Math.Clamp(Vector3.Dot(point - from, span) / length, 0f, 1f));
    }

    /// <summary>How far along a screen segment the point nearest the pointer is, clamped to it.</summary>
    static float Along(Vector2 from, Vector2 to, Vector2 pointer) {
        var span = to - from;
        var length = span.LengthSquared();

        return length <= float.Epsilon ? 0f : Math.Clamp(Vector2.Dot(pointer - from, span) / length, 0f, 1f);
    }

    /// <summary>Forgets the shapes built so far, for a caller that changed <see cref="Segments" />.</summary>
    /// <remarks>
    ///     ⚠ <b>The element tables go with them.</b> They are derived from the shapes, so one kept
    ///     across a change of <see cref="Segments" /> would offer corners the drawn mesh has not got.
    /// </remarks>
    public void Invalidate() {
        shapes.Clear();
        elements.Clear();
    }

    /// <summary>The elements of a shape kind, welded once and shared by every entity of it.</summary>
    MeshElements Elements(PrimitiveKind kind) {
        if (!elements.TryGetValue(kind, out var mesh)) {
            elements[kind] = mesh = MeshElements.From(Shape(kind));
        }

        return mesh;
    }

    /// <summary>Whether an entity may answer at all.</summary>
    /// <remarks>
    ///     Hidden and locked are skipped for <see cref="ScenePicker" />'s reason, and the ignore list
    ///     on top of it: an object snapping to its own surface is a drag that never moves, and one
    ///     snapping to its own vertices is a drag that jumps between its own corners.
    /// </remarks>
    bool Eligible(Entity entity, IReadOnlyList<Entity> ignore) {
        var world = document.World;

        if (!world.IsAlive(entity) || !world.Has<WorldTransform>(entity)) {
            return false;
        }

        if (document.IsHidden(entity) || document.IsLocked(entity)) {
            return false;
        }

        for (var index = 0; index < ignore.Count; index++) {
            if (ignore[index] == entity) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether a shape's box comes within a radius of a point on screen.</summary>
    static bool NearBox(
        BoundingBox bounds,
        in Matrix4x4 transform,
        EditorCamera camera,
        int width,
        int height,
        Vector2 pointer,
        float radius
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

        return any
            && pointer.X >= left - radius
            && pointer.X <= right + radius
            && pointer.Y >= top - radius
            && pointer.Y <= bottom + radius;
    }

    /// <summary>Where a ray first meets a shape, and which way that surface faces.</summary>
    /// <remarks>
    ///     ⚠ <b>The normal comes back through the inverse transpose, not through the matrix.</b> A
    ///     surface snap turns the normal into a rotation, so a crate dropped on a non-uniformly scaled
    ///     ramp would be stood up along a direction that is not perpendicular to the ramp — which
    ///     reads as the snap being approximate rather than as a matrix being the wrong one.
    /// </remarks>
    /// <summary>What geometry an entity presents to a ray: its own mesh, or the shape it is drawn as.</summary>
    /// <remarks>
    ///     ⚠ <b>Cached per entity and per revision, because a probe answers per pointer move.</b>
    ///     Converting an edited mesh to drawing geometry is one pass over its corners; doing it per
    ///     move for every entity in the scene is the whole cost of a drag. The revision is what makes
    ///     a moved vertex re-derive it and every other frame free.
    /// </remarks>
    MeshData? Geometry(Entity entity) {
        if (document.MeshOf(entity) is { } edited) {
            var version = document.MeshVersion(entity);

            if (!edits.TryGetValue(entity, out var cached) || cached.Version != version) {
                edits[entity] = cached = (version, edited.ToMeshData());
            }

            return cached.Mesh;
        }

        return PrimitiveShapes.TryGet(document.World, entity, out var kind) ? Shape(kind) : null;
    }

    static bool Surface(Ray ray, MeshData mesh, in Matrix4x4 transform, out SurfaceHit hit) {
        hit = default;

        if (!Matrix4x4.Invert(transform, out var inverse)) {
            return false;
        }

        var local = new Ray(
            Matrix4x4.TransformPosition(ray.Origin, inverse),
            Matrix4x4.TransformDirection(ray.Direction, inverse)
        );

        var nearest = float.MaxValue;
        var normal = Vector3.UnitY;

        for (var index = 0; index + 2 < mesh.Indices.Length; index += 3) {
            var a = mesh.Positions[mesh.Indices[index]];
            var b = mesh.Positions[mesh.Indices[index + 1]];
            var c = mesh.Positions[mesh.Indices[index + 2]];

            if (!local.Intersects(a, b, c, out var distance) || distance < 0f || distance >= nearest) {
                continue;
            }

            nearest = distance;
            normal = Vector3.Cross(b - a, c - a);
        }

        if (nearest == float.MaxValue) {
            return false;
        }

        // ⚠ The hit is brought back out of local space rather than measured along the world ray, and
        // that is not a stylistic choice. `Ray`'s constructor normalises, so the local ray's
        // direction is a unit vector in *local* units — the parameter it hands back is therefore in
        // local units too, and a shape scaled fourfold answers with a quarter of the distance. Taken
        // through the matrix, the point is exact whatever the scale, and the world distance measured
        // from it is what makes two differently-scaled entities comparable.
        var point = Matrix4x4.TransformPosition(local.GetPoint(nearest), transform);
        var reach = (point - ray.Origin).Length();

        var normals = Matrix4x4.Transpose(inverse);
        var facing = Matrix4x4.TransformDirection(normal, normals);

        // ⚠ Turned towards the eye. The geometry is two-sided in the picture and a snap is not: a
        // normal pointing away puts the dropped object underneath the surface it was dropped on.
        if (Vector3.Dot(facing, ray.Direction) > 0f) {
            facing = -facing;
        }

        hit = new SurfaceHit(
            point,
            facing.LengthSquared() > MathUtil.ZeroTolerance ? Vector3.Normalize(facing) : Vector3.UnitY,
            reach
        );

        return true;
    }

    MeshData Shape(PrimitiveKind kind) {
        if (!shapes.TryGetValue(kind, out var mesh)) {
            mesh = MeshPrimitives.Create(kind, Segments, Math.Max(MeshPrimitives.MinimumSegments, Segments / 2));
            shapes[kind] = mesh;
        }

        return mesh;
    }
}
