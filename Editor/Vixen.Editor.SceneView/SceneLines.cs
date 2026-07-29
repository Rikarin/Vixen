// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Rendering;

namespace Vixen.Editor.SceneView;

/// <summary>Everything a viewport draws over the scene, collected once a frame.</summary>
/// <remarks>
///     <para>
///         <b>Three sources: the grid, the entities, the gizmo.</b> They are drawn together because
///         they are nearly the same kind of thing — world-space geometry with no material — and
///         collecting them here rather than in three places is what makes each of the passes below one
///         buffer write and one draw call.
///     </para>
///     <para>
///         ⚠ <b>Three lists, and none of the splits is cosmetic.</b> The grid and the markers are
///         depth-tested so an entity behind a wall is behind it; the gizmo is not, because a handle
///         you cannot reach through the thing it moves is a handle you cannot use. That much is two
///         <c>LineRenderer</c>s. The third is the gizmo's <i>solid</i> parts — the head on the end of
///         each arm — which are triangles rather than segments and want <c>MeshRenderer</c>, so they
///         cannot share a buffer with either of the other two however alike they look on screen.
///     </para>
///     <para>
///         <b>An entity with no mesh is drawn as a marker.</b> Until there is a mesh path in the
///         editor that is <i>every</i> entity, and even after there is one it stays true for the
///         lights, the cameras and the empties — which is what doc 11 means by visualisation gizmos.
///     </para>
/// </remarks>
public sealed class SceneLines {
    readonly List<LineVertex> world = [];
    readonly List<LineVertex> overlay = [];
    readonly List<MeshVertex> handles = [];
    readonly List<uint> handleIndices = [];

    /// <summary>The segments drawn with the depth test on.</summary>
    public IReadOnlyList<LineVertex> World => world;

    /// <summary>The segments drawn over everything.</summary>
    public IReadOnlyList<LineVertex> Overlay => overlay;

    /// <summary>The gizmo's solid parts, drawn over everything.</summary>
    /// <remarks>
    ///     A span rather than an <see cref="IReadOnlyList{T}" />, which is what the two segment lists
    ///     hand back: this is read by <c>MeshRenderer.Upload</c>, which wants one. The same choice
    ///     <c>SceneMeshes</c> makes, and for the same reason.
    /// </remarks>
    public ReadOnlySpan<MeshVertex> Handles => CollectionsMarshal.AsSpan(handles);

    /// <summary>Three indices per triangle, into <see cref="Handles" />.</summary>
    public ReadOnlySpan<uint> HandleIndices => CollectionsMarshal.AsSpan(handleIndices);

    /// <summary>How big an entity's marker is, in world units.</summary>
    public float MarkerSize { get; set; } = 0.25f;

    /// <summary>The colour of an entity that is not selected.</summary>
    public Color4 MarkerColour { get; set; } = new(0.55f, 0.58f, 0.62f, 0.9f);

    /// <summary>The colour of a selected one.</summary>
    /// <remarks>
    ///     ⚠ <b>The selection is shown by <i>colour</i> and not only by the gizmo sitting on it.</b>
    ///     With several things selected the gizmo is at one place and the other nineteen have nothing
    ///     saying they are going to move — which is the state in which somebody drags and is
    ///     surprised.
    /// </remarks>
    public Color4 SelectedColour { get; set; } = new(1f, 0.62f, 0.15f, 1f);

    /// <summary>Collects a frame's lines.</summary>
    /// <param name="document">The scene being drawn.</param>
    /// <param name="viewport">The pane drawing it.</param>
    /// <param name="height">How tall the pane is, in render pixels.</param>
    public void Build(SceneDocument document, SceneViewport viewport, int height) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(viewport);

        world.Clear();
        overlay.Clear();
        handles.Clear();
        handleIndices.Clear();

        Grid(viewport, height);
        Markers(document);
        LightShapes(document);

        GizmoGeometry.Build(viewport.Gizmo, viewport.Camera, height, overlay);
        GizmoGeometry.BuildSolid(viewport.Gizmo, viewport.Camera, height, handles, handleIndices);
    }

    void Grid(SceneViewport viewport, int height) {
        foreach (var line in viewport.Grid.Build(viewport.Camera, height)) {
            // ⚠ A colour per end, not one for the line. The grid fades its lines out towards their
            // far ends so that a level runs out into nothing instead of stopping at a rectangle, and
            // that fade only exists if both ends are carried through — writing `line.Colour` twice
            // draws the rectangle back.
            world.Add(new(line.From, line.Colour));
            world.Add(new(line.To, line.ToColour));
        }
    }

    /// <summary>A three-axis cross at every entity, in the selection's colour when it is in it.</summary>
    /// <remarks>
    ///     A cross rather than a box, because a box implies a size the entity does not have and a
    ///     cross says only "something is here", which is the truth about an entity with no mesh. The
    ///     arms are along the entity's <i>own</i> axes, so a rotated empty looks rotated.
    /// </remarks>
    void Markers(SceneDocument document) {
        foreach (var entity in document.Entities) {
            if (!document.World.IsAlive(entity) || !document.World.Has<WorldTransform>(entity)) {
                continue;
            }

            var transform = new Transform(document.World, entity);
            var origin = transform.Position;
            var selected = document.Selection.Contains(entity);
            var colour = selected ? SelectedColour : MarkerColour;
            var size = MarkerSize * (selected ? 1.6f : 1f);

            Cross(origin, transform.Right * size, colour);
            Cross(origin, transform.Up * size, colour);
            Cross(origin, transform.Forward * size, colour);

            // A line to the parent, so a hierarchy is visible in the viewport rather than only in the
            // panel. Faded, because it is a relationship and not a thing.
            if (transform.Parent is { IsNull: false } parent && document.World.Has<WorldTransform>(parent)) {
                var faded = new Color4(colour.R, colour.G, colour.B, 0.25f);

                world.Add(new(origin, faded));
                world.Add(new(new Transform(document.World, parent).Position, faded));
            }
        }
    }

    void Cross(Vector3 origin, Vector3 arm, Color4 colour) {
        world.Add(new(origin - arm, colour));
        world.Add(new(origin + arm, colour));
    }

    /// <summary>How far a directional light's rays are drawn, in world units.</summary>
    /// <remarks>
    ///     A fixed length, because a directional light has no range to take one from — the sun does
    ///     not fall off, which is the whole difference between it and the other four.
    /// </remarks>
    const float SunLength = 1.5f;

    /// <summary>How many segments a gizmo's circles are drawn with.</summary>
    const float Segments = 24;

    /// <summary>What each light in the scene reaches, drawn in its own colour.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A light is invisible, so without this it is a name in the hierarchy and a marker
    ///         cross in the viewport.</b> Which way a spot points and how far a point light carries
    ///         are the two things somebody placing lights is actually adjusting, and neither is
    ///         legible from a transform gizmo — a spot aimed at the ceiling and one aimed at the floor
    ///         look identical until the cone is drawn.
    ///     </para>
    ///     <para>
    ///         <b>In the light's own colour rather than a fixed one</b>, dimmed towards the marker
    ///         colour so a scene full of white lights does not read as a scene full of selections.
    ///         Selected still wins, for the reason <see cref="SelectedColour" /> gives.
    ///     </para>
    /// </remarks>
    void LightShapes(SceneDocument document) {
        foreach (var entity in document.Entities) {
            if (!Lights.TryGet(document.World, entity, out var light) || !document.World.Has<WorldTransform>(entity)) {
                continue;
            }

            var transform = new Transform(document.World, entity);
            var selected = document.Selection.Contains(entity);
            var colour = selected ? SelectedColour : Tint(light.Colour);

            var origin = transform.Position;
            var forward = transform.Forward;
            var right = transform.Right;
            var up = transform.Up;

            switch (light.Kind) {
                case LightKind.Directional: {
                    // Parallel rays, which is what makes it read as a sun rather than as a spot: four
                    // of them from a disc, all pointing the same way and none of them spreading.
                    var reach = origin + (forward * SunLength);

                    Ring(origin, right, up, MarkerSize, colour);
                    Segment(origin, reach, colour);

                    for (var i = 0; i < 4; i++) {
                        var offset = Around(right, up, i / 4f) * MarkerSize;
                        Segment(origin + offset, reach + offset, colour);
                    }

                    break;
                }

                case LightKind.Point:
                    // Three rings at the range, which is the sphere a wireframe can afford: a full
                    // one is hundreds of segments per light and says nothing the three do not.
                    Ring(origin, right, up, light.Range, colour);
                    Ring(origin, up, forward, light.Range, colour);
                    Ring(origin, forward, right, light.Range, colour);

                    break;

                case LightKind.Spot: {
                    // The outer cone and not the inner: the outer is where the light stops, and two
                    // cones drawn together are a shape nobody can read at a glance.
                    var apex = origin;
                    var centre = origin + (forward * light.Range);
                    var radius = light.Range * MathF.Tan(light.OuterAngle);

                    Ring(centre, right, up, radius, colour);

                    for (var i = 0; i < 4; i++) {
                        Segment(apex, centre + (Around(right, up, i / 4f) * radius), colour);
                    }

                    break;
                }

                case LightKind.Rect: {
                    // The rectangle it emits from and the way it faces. One-sided, so the normal is
                    // the difference between a softbox lighting the room and lighting the wall.
                    var across = right * light.HalfLength;
                    var down = up * light.Radius;

                    Segment(origin - across - down, origin + across - down, colour);
                    Segment(origin + across - down, origin + across + down, colour);
                    Segment(origin + across + down, origin - across + down, colour);
                    Segment(origin - across + down, origin - across - down, colour);

                    Segment(origin, origin + (forward * MarkerSize * 2f), colour);
                    break;
                }

                case LightKind.Tube: {
                    var end = right * light.HalfLength;

                    Segment(origin - end, origin + end, colour);
                    Ring(origin - end, up, forward, light.Radius, colour);
                    Ring(origin + end, up, forward, light.Radius, colour);

                    break;
                }

                default:
                    break;
            }
        }
    }

    /// <summary>A light's colour as a line colour: never black, never a full-strength selection orange.</summary>
    /// <remarks>
    ///     ⚠ <b>Lifted off the floor, because a light may legitimately be almost black</b> — a dim
    ///     blue fill at 0.02 is a real thing to author — and a gizmo drawn in it is a gizmo that is
    ///     not there. The hue survives; only the floor is imposed.
    /// </remarks>
    static Color4 Tint(Color3 colour) =>
        new(
            MathF.Max(colour.R, 0.35f),
            MathF.Max(colour.G, 0.35f),
            MathF.Max(colour.B, 0.35f),
            0.7f
        );

    /// <summary>A point on the unit circle spanned by two axes.</summary>
    /// <param name="first">One axis.</param>
    /// <param name="second">The other.</param>
    /// <param name="turn">How far round, from 0 to 1.</param>
    static Vector3 Around(Vector3 first, Vector3 second, float turn) {
        var angle = turn * MathF.Tau;
        return (first * MathF.Cos(angle)) + (second * MathF.Sin(angle));
    }

    void Segment(Vector3 from, Vector3 to, Color4 colour) {
        world.Add(new(from, colour));
        world.Add(new(to, colour));
    }

    /// <summary>A circle in the plane two axes span. Nothing is drawn for a radius of nothing.</summary>
    void Ring(Vector3 centre, Vector3 first, Vector3 second, float radius, Color4 colour) {
        if (radius <= 0f) {
            return;
        }

        var previous = centre + (first * radius);

        for (var i = 1; i <= Segments; i++) {
            var next = centre + (Around(first, second, i / Segments) * radius);

            Segment(previous, next, colour);
            previous = next;
        }
    }
}
