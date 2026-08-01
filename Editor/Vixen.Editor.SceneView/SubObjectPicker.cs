// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Editor.SceneView;

/// <summary>What one element of a mesh is.</summary>
public enum SubObjectKind : byte {
    /// <summary>Nothing was under the pointer.</summary>
    None,

    /// <summary>A face, indexed by triangle.</summary>
    Face,

    /// <summary>An edge, indexed into <see cref="MeshElements.Edges" />.</summary>
    Edge,

    /// <summary>A shared position, indexed into <see cref="MeshElements.Positions" />.</summary>
    Vertex
}

/// <summary>Which kinds of element a query may answer with.</summary>
/// <remarks>
///     ⚠ <b>A set rather than the one kind, because the answer is not always the mode.</b> The
///     element modes of doc 24's inventory ask for one kind at a time, and a tool that highlights
///     "whatever is under the pointer" asks for all three — which is the case
///     <see cref="SubObjectPicker" />'s innermost-wins rule exists for.
/// </remarks>
[Flags]
public enum SubObjectFilter : byte {
    /// <summary>Nothing, which answers nothing.</summary>
    None = 0,

    /// <summary>Shared positions.</summary>
    Vertex = 1,

    /// <summary>Edges.</summary>
    Edge = 2,

    /// <summary>Faces.</summary>
    Face = 4,

    /// <summary>All three.</summary>
    All = Vertex | Edge | Face
}

/// <summary>One element of one mesh.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Index">Which one, in the table <see cref="Kind" /> names.</param>
public readonly record struct SubObject(SubObjectKind Kind, int Index) {
    /// <summary>Nothing.</summary>
    public static SubObject None => default;

    /// <summary>Whether anything was hit.</summary>
    public bool IsHit => Kind != SubObjectKind.None;
}

/// <summary>Which face, edge or vertex of one mesh is under the pointer.</summary>
/// <remarks>
///     <para>
///         <b>The third question, and doc 24's B4 is the argument for it.</b> <see cref="ScenePicker" />
///         answers "which entity" and <see cref="PickingBuffer" /> answers it again on the GPU; half
///         of a blockout toolset asks something else — which face of <i>this</i> mesh, within a
///         tolerance measured in pixels, with the innermost element winning. That is the ray test with
///         a different payload rather than a new subsystem, and it is a test against one mesh rather
///         than against a scene, which is why it runs on the processor.
///     </para>
///     <para>
///         ⚠ <b>A face is answered by a ray and a vertex by a projection, and the split is not an
///         inconsistency.</b> A face has area, so the exact question is which triangle the ray through
///         the pointer meets first, and the answer needs no tolerance at all. A vertex has no area and
///         an edge has no width: the only thing that can be asked about them is how near the pointer
///         came <i>on screen</i>, which is on the other side of the projection. So the mesh's
///         positions are projected once per query and the tolerance is in pixels — the same bargain
///         <c>SceneProbe.TryNearestVertex</c> already makes, and for the same reason.
///     </para>
///     <para>
///         ⚠ <b>Innermost wins: a vertex beats an edge and an edge beats a face.</b> Every element
///         within the tolerance is a candidate and the smallest one takes it, because the corner of a
///         face is also on two edges and inside the face, and a rule that took the largest would make
///         a vertex unclickable. It is what every modelling tool does and it is why the tolerance can
///         be generous without making the finer elements unreachable.
///     </para>
///     <para>
///         ⚠ <b>Nothing is occluded, and that is a stated limit rather than an oversight.</b> The
///         vertex on the far side of a cube is as selectable as the one facing you. Fixing it properly
///         means asking what the <i>picture</i> has at a pixel, which is
///         <see cref="PickingRenderer" />'s id buffer with an element id in it instead of an entity
///         id — the move B4 says this defers — and every cheap approximation in between is a depth
///         bias that is wrong at a silhouette. A block-out mesh is a few hundred elements and its far
///         side rarely projects within ten pixels of its near side; when it does, the nearer of the
///         two wins, which is the case the depth tie-break below is for.
///     </para>
///     <para>
///         ⚠ <b>An instance rather than a static, because this runs on every pointer move.</b> Hover
///         feedback is the thing B4 names as the bar, and it means one query per mouse move for as
///         long as the pointer is over the pane. The projected positions are kept in buffers that
///         grow to the largest mesh asked about and are then reused, so a query allocates nothing.
///     </para>
/// </remarks>
public sealed class SubObjectPicker {
    /// <summary>How near the pointer has to come to a vertex or an edge, in render pixels.</summary>
    /// <remarks>
    ///     ⚠ <b>Bigger than the thing being aimed at, exactly as <c>ScenePicker.MarkerRadius</c> and
    ///     <c>TransformGizmo.GrabRadius</c> are.</b> A vertex is drawn as a handful of pixels and an
    ///     edge as one line; a tolerance equal to what is drawn is one that misses far more often than
    ///     it hits.
    /// </remarks>
    public const float DefaultTolerance = 10f;

    /// <summary>How much nearer in screen space one candidate must be to beat another outright.</summary>
    /// <remarks>
    ///     In squared pixels, and it is what makes the depth tie-break reachable: two corners of a
    ///     cube seen edge-on project to the same pixel, and "whichever the loop met first" is an
    ///     answer that changes when the mesh is rebuilt.
    /// </remarks>
    const float TieBreak = 0.25f;

    Vector2[] screen = [];
    float[] depths = [];
    bool[] onScreen = [];

    /// <summary>Which element of a mesh is under a point in the viewport.</summary>
    /// <param name="elements">The mesh's elements.</param>
    /// <param name="transform">Where the mesh is, in world space.</param>
    /// <param name="camera">The camera looking at it.</param>
    /// <param name="width">How wide the viewport is, in render pixels.</param>
    /// <param name="height">How tall.</param>
    /// <param name="pointer">Where the pointer is, in render pixels from the top-left.</param>
    /// <param name="filter">Which kinds may answer.</param>
    /// <param name="tolerance">How near counts, in render pixels.</param>
    /// <returns>The element, or <see cref="SubObject.None" />.</returns>
    public SubObject Under(
        MeshElements elements,
        in Matrix4x4 transform,
        EditorCamera camera,
        int width,
        int height,
        Vector2 pointer,
        SubObjectFilter filter = SubObjectFilter.All,
        float tolerance = DefaultTolerance
    ) {
        ArgumentNullException.ThrowIfNull(elements);
        ArgumentNullException.ThrowIfNull(camera);

        if (width <= 0 || height <= 0 || filter == SubObjectFilter.None) {
            return SubObject.None;
        }

        // ⚠ Only when a vertex or an edge may answer. A face-mode query is a ray test and nothing
        // else, and projecting a torus' worth of positions to answer it would be the whole cost of
        // the query spent on a table nothing reads.
        if ((filter & (SubObjectFilter.Vertex | SubObjectFilter.Edge)) != 0) {
            Project(elements, transform, camera, width, height);

            if ((filter & SubObjectFilter.Vertex) != 0 && Vertex(elements, pointer, tolerance) is { } vertex) {
                return new SubObject(SubObjectKind.Vertex, vertex);
            }

            if ((filter & SubObjectFilter.Edge) != 0 && Edge(elements, pointer, tolerance) is { } edge) {
                return new SubObject(SubObjectKind.Edge, edge);
            }
        }

        return (filter & SubObjectFilter.Face) != 0 && Face(elements, transform, camera, pointer, width, height) is { } face
            ? new SubObject(SubObjectKind.Face, face)
            : SubObject.None;
    }

    /// <summary>Puts every shared position on screen, into the buffers this instance keeps.</summary>
    void Project(MeshElements elements, in Matrix4x4 transform, EditorCamera camera, int width, int height) {
        var count = elements.PositionCount;

        if (screen.Length < count) {
            screen = new Vector2[count];
            depths = new float[count];
            onScreen = new bool[count];
        }

        var positions = elements.Positions;
        var eye = camera.Position;
        var forward = camera.Forward;

        for (var index = 0; index < count; index++) {
            var world = Matrix4x4.TransformPosition(positions[index], transform);

            onScreen[index] = camera.TryProject(world, width, height, out screen[index]);

            // Along the view direction rather than the distance to the eye, because what a tie-break
            // wants is which of two things is in front of the other from here — and two points at the
            // same distance from the camera are not at the same depth unless they are on its axis.
            depths[index] = Vector3.Dot(world - eye, forward);
        }
    }

    /// <summary>Which shared position the pointer is nearest, within the tolerance.</summary>
    int? Vertex(MeshElements elements, Vector2 pointer, float tolerance) {
        var best = tolerance * tolerance;
        var depth = float.MaxValue;
        int? found = null;

        for (var index = 0; index < elements.PositionCount; index++) {
            if (!onScreen[index]) {
                continue;
            }

            var distance = Vector2.DistanceSquared(screen[index], pointer);

            if (distance > best + TieBreak) {
                continue;
            }

            if (found is null || distance < best - TieBreak || depths[index] < depth) {
                best = MathF.Min(best, distance);
                depth = depths[index];
                found = index;
            }
        }

        return found;
    }

    /// <summary>Which edge the pointer is nearest, within the tolerance.</summary>
    int? Edge(MeshElements elements, Vector2 pointer, float tolerance) {
        var best = tolerance * tolerance;
        var depth = float.MaxValue;
        int? found = null;

        var edges = elements.Edges;

        for (var index = 0; index < edges.Length; index++) {
            var (a, b) = edges[index];

            // ⚠ Both ends, not either. An edge with one end behind the eye has no screen segment —
            // the projection of the far end is mirrored through the middle of the pane, so the
            // segment tested would be a line nothing drew, lying across the viewport.
            if (!onScreen[a] || !onScreen[b]) {
                continue;
            }

            var along = Along(screen[a], screen[b], pointer);
            var closest = Vector2.Lerp(screen[a], screen[b], along);
            var distance = Vector2.DistanceSquared(closest, pointer);

            if (distance > best + TieBreak) {
                continue;
            }

            var at = float.Lerp(depths[a], depths[b], along);

            if (found is null || distance < best - TieBreak || at < depth) {
                best = MathF.Min(best, distance);
                depth = at;
                found = index;
            }
        }

        return found;
    }

    /// <summary>Which triangle the ray through the pointer meets first.</summary>
    /// <remarks>
    ///     ⚠ <b>The ray goes into the mesh's local space rather than the triangles coming out of
    ///     it.</b> <see cref="ScenePicker" />'s own remarks say why: transforming a torus' six hundred
    ///     vertices per query is the whole cost of the test, and inverting one matrix is not. Nothing
    ///     here compares distances across meshes, so the parameter is left in local units.
    /// </remarks>
    static int? Face(
        MeshElements elements,
        in Matrix4x4 transform,
        EditorCamera camera,
        Vector2 pointer,
        int width,
        int height
    ) {
        if (!Matrix4x4.Invert(transform, out var inverse)) {
            // A zero scale, which has no surface to hit. An entity can be scaled to nothing and
            // scaled back, and a picker that threw would take the editor with it.
            return null;
        }

        var ray = camera.PickingRay(pointer, width, height);

        var local = new Ray(
            Matrix4x4.TransformPosition(ray.Origin, inverse),
            Matrix4x4.TransformDirection(ray.Direction, inverse)
        );

        var positions = elements.Positions;
        var triangles = elements.Triangles;

        var nearest = float.MaxValue;
        int? found = null;

        for (var index = 0; index + 2 < triangles.Length; index += 3) {
            var a = positions[triangles[index]];
            var b = positions[triangles[index + 1]];
            var c = positions[triangles[index + 2]];

            if (local.Intersects(a, b, c, out var distance) && distance >= 0f && distance < nearest) {
                nearest = distance;
                found = index / 3;
            }
        }

        return found;
    }

    /// <summary>How far along a screen segment the point nearest the pointer is, clamped to it.</summary>
    static float Along(Vector2 from, Vector2 to, Vector2 pointer) {
        var span = to - from;
        var length = span.LengthSquared();

        // A segment that projects to a point — an edge seen exactly end-on. Both ends are the same
        // pixel, so any parameter gives the same answer and zero is the one that does not divide.
        return length <= float.Epsilon ? 0f : Math.Clamp(Vector2.Dot(pointer - from, span) / length, 0f, 1f);
    }
}
