// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Rendering;

namespace Vixen.Editor.SceneView;

/// <summary>Everything a viewport draws as lines, collected once a frame.</summary>
/// <remarks>
///     <para>
///         <b>One list, three sources: the grid, the entities, the gizmo.</b> They are drawn together
///         because they are the same kind of thing — world-space segments with no material — and
///         collecting them here rather than in three places is what makes the whole overlay one
///         buffer write and one draw call.
///     </para>
///     <para>
///         ⚠ <b>Two lists, actually, and the split is not cosmetic.</b> The grid and the markers are
///         depth-tested so an entity behind a wall is behind it; the gizmo is not, because a handle
///         you cannot reach through the thing it moves is a handle you cannot use. Two lists is what
///         lets one <c>LineRenderer</c> draw them with its two pipelines.
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

    /// <summary>The segments drawn with the depth test on.</summary>
    public IReadOnlyList<LineVertex> World => world;

    /// <summary>The segments drawn over everything.</summary>
    public IReadOnlyList<LineVertex> Overlay => overlay;

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

        Grid(viewport, height);
        Markers(document);

        GizmoGeometry.Build(viewport.Gizmo, viewport.Camera, height, overlay);
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
}
