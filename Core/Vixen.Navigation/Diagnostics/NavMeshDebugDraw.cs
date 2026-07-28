// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Engine.Diagnostics;
using Vixen.Navigation.Agents;

namespace Vixen.Navigation.Diagnostics;

/// <summary>How a navmesh is coloured when it is drawn.</summary>
/// <param name="Interior">Edges between two polygons of the same tile.</param>
/// <param name="Border">Edges with nothing on the other side — the walls, as the mesh understands them.</param>
/// <param name="Portal">Edges linked across a tile border.</param>
/// <param name="Disabled">Polygons a default filter would refuse.</param>
/// <param name="Lift">How far above the surface to draw, so the lines are not inside the floor.</param>
public readonly record struct NavMeshDrawStyle(
    Color4 Interior,
    Color4 Border,
    Color4 Portal,
    Color4 Disabled,
    float Lift = 0.05f
) {
    /// <summary>Readable over most level geometry: dim interior, bright walls, blue portals.</summary>
    public static NavMeshDrawStyle Default => new(
        new(0.2f, 0.5f, 0.7f, 0.5f),
        new(0.1f, 0.8f, 1f, 1f),
        new(1f, 0.9f, 0.2f, 1f),
        new(0.8f, 0.2f, 0.2f, 1f)
    );
}

/// <summary>
///     Draws a navmesh, a path or a crowd into <see cref="DebugDraw" />.
/// </summary>
/// <remarks>
///     <para>
///         A navmesh is a derived thing that nothing renders, so without this the only way to find out
///         that a bake went wrong is that a path did — and a path failing tells you almost nothing
///         about <i>why</i>. Seeing the polygons is the difference between "the agent will not go
///         through the doorway" and "the doorway is a centimetre narrower than the agent radius".
///     </para>
///     <para>
///         Lines rather than filled triangles, because that is what <see cref="DebugDraw" /> has and
///         because a wireframe over the level is more readable than a translucent surface anyway: the
///         thing being checked is usually where an edge is, not what colour a face is.
///     </para>
///     <para>
///         <b>Every interior edge is drawn once</b>, by drawing it only from the polygon with the
///         lower reference. Drawing both sides doubles the line count for no visible difference and
///         makes the shared edges brighter than the walls, which is the wrong way round.
///     </para>
/// </remarks>
public static class NavMeshDebugDraw {
    /// <summary>Draws every polygon of a mesh.</summary>
    /// <param name="draw">Where the lines go.</param>
    /// <param name="mesh">The mesh.</param>
    /// <param name="style">How to colour it.</param>
    /// <param name="filter">Which polygons count as enabled, or null for the default filter.</param>
    /// <param name="seconds">How long the lines last. Zero is one frame.</param>
    /// <exception cref="ArgumentNullException"><paramref name="draw" /> or <paramref name="mesh" /> is null.</exception>
    public static void DrawMesh(
        DebugDraw draw,
        NavMesh mesh,
        NavMeshDrawStyle style = default,
        NavQueryFilter? filter = null,
        float seconds = 0f
    ) {
        ArgumentNullException.ThrowIfNull(draw);
        ArgumentNullException.ThrowIfNull(mesh);

        if (!draw.Enabled) {
            return;
        }

        var colours = style == default ? NavMeshDrawStyle.Default : style;
        var rules = filter ?? NavQueryFilter.Default;
        var lift = new Vector3(0f, colours.Lift, 0f);

        Span<Vector3> vertices = stackalloc Vector3[NavMesh.MaxVerticesPerPoly];

        foreach (var tile in mesh.Tiles) {
            // The connections, drawn as what they are: a line between two points, with a mark at each
            // end so a link whose endpoint failed to attach is visible as a line going nowhere.
            foreach (var connection in tile.Data.OffMeshConnections) {
                var colour = new Color4(1f, 0.4f, 0.9f, 1f);

                draw.Line(connection.Start + lift, connection.End + lift, colour, seconds);
                draw.Line(connection.Start, connection.Start + new Vector3(0f, 0.5f, 0f), colour, seconds);
                draw.Line(connection.End, connection.End + new Vector3(0f, 0.5f, 0f), colour, seconds);
            }

            for (var index = 0; index < tile.SurfacePolyCount; index++) {
                var reference = NavMesh.ReferenceOf(tile, index);
                var count = mesh.GetPolyVertices(reference, vertices);

                if (count == 0) {
                    continue;
                }

                mesh.TryGetPolyAttributes(reference, out _, out var flags);
                var enabled = rules.Passes(flags);

                for (var edge = 0; edge < count; edge++) {
                    var from = vertices[edge] + lift;
                    var to = vertices[(edge + 1) % count] + lift;

                    var neighbour = NavPolyRef.Null;
                    var crossesTile = false;

                    foreach (var candidate in mesh.Neighbours(reference)) {
                        if (candidate.Edge == edge) {
                            neighbour = candidate.Reference;
                            crossesTile = candidate.Reference.Tile != reference.Tile;

                            break;
                        }
                    }

                    // Shared edges belong to the polygon with the lower reference, so each is drawn
                    // once and the walls stay the brightest thing on screen.
                    if (!neighbour.IsNull && !crossesTile && neighbour.ToUInt64() < reference.ToUInt64()) {
                        continue;
                    }

                    if (crossesTile && neighbour.Tile < reference.Tile) {
                        continue;
                    }

                    var colour = !enabled ? colours.Disabled
                        : crossesTile ? colours.Portal
                        : neighbour.IsNull ? colours.Border
                        : colours.Interior;

                    draw.Line(from, to, colour, seconds);
                }
            }
        }
    }

    /// <summary>Draws the polygons of a corridor, as their outlines.</summary>
    /// <param name="draw">Where the lines go.</param>
    /// <param name="mesh">The mesh the corridor is on.</param>
    /// <param name="corridor">The polygons.</param>
    /// <param name="colour">What colour to draw them.</param>
    /// <param name="lift">How far above the surface to draw.</param>
    /// <param name="seconds">How long the lines last.</param>
    /// <exception cref="ArgumentNullException"><paramref name="draw" /> or <paramref name="mesh" /> is null.</exception>
    public static void DrawCorridor(
        DebugDraw draw,
        NavMesh mesh,
        ReadOnlySpan<NavPolyRef> corridor,
        Color4 colour,
        float lift = 0.06f,
        float seconds = 0f
    ) {
        ArgumentNullException.ThrowIfNull(draw);
        ArgumentNullException.ThrowIfNull(mesh);

        if (!draw.Enabled) {
            return;
        }

        Span<Vector3> vertices = stackalloc Vector3[NavMesh.MaxVerticesPerPoly];
        var offset = new Vector3(0f, lift, 0f);

        foreach (var reference in corridor) {
            var count = mesh.GetPolyVertices(reference, vertices);

            for (var edge = 0; edge < count; edge++) {
                draw.Line(vertices[edge] + offset, vertices[(edge + 1) % count] + offset, colour, seconds);
            }
        }
    }

    /// <summary>Draws a straight path as a polyline, with a tick at every corner.</summary>
    /// <param name="draw">Where the lines go.</param>
    /// <param name="path">The corners, as <see cref="NavMeshQuery.FindStraightPath" /> produced them.</param>
    /// <param name="colour">What colour to draw it.</param>
    /// <param name="lift">How far above the surface to draw.</param>
    /// <param name="seconds">How long the lines last.</param>
    /// <exception cref="ArgumentNullException"><paramref name="draw" /> is null.</exception>
    public static void DrawPath(DebugDraw draw, ReadOnlySpan<NavPathPoint> path, Color4 colour, float lift = 0.1f, float seconds = 0f) {
        ArgumentNullException.ThrowIfNull(draw);

        if (!draw.Enabled || path.IsEmpty) {
            return;
        }

        var offset = new Vector3(0f, lift, 0f);

        for (var index = 1; index < path.Length; index++) {
            draw.Line(path[index - 1].Position + offset, path[index].Position + offset, colour, seconds);
        }

        // A tick at each corner, because a path that doubles back on itself is otherwise one line and
        // the corner count is the thing worth seeing.
        foreach (var point in path) {
            draw.Line(point.Position, point.Position + new Vector3(0f, lift * 3f, 0f), colour, seconds);
        }
    }

    /// <summary>Draws every agent of a crowd: its circle, where it is going, and how fast.</summary>
    /// <param name="draw">Where the lines go.</param>
    /// <param name="crowd">The crowd.</param>
    /// <param name="seconds">How long the lines last.</param>
    /// <exception cref="ArgumentNullException"><paramref name="draw" /> or <paramref name="crowd" /> is null.</exception>
    /// <remarks>
    ///     The desired velocity is drawn beside the achieved one deliberately. The gap between them is
    ///     avoidance doing its job, and it is the only way to see from outside whether an agent that
    ///     is not moving has been talked out of it or has simply not been told where to go.
    /// </remarks>
    public static void DrawCrowd(DebugDraw draw, Crowd crowd, float seconds = 0f) {
        ArgumentNullException.ThrowIfNull(draw);
        ArgumentNullException.ThrowIfNull(crowd);

        if (!draw.Enabled) {
            return;
        }

        foreach (var handle in crowd.Agents) {
            if (!crowd.TryGetState(handle, out var state)) {
                continue;
            }

            var colour = state.State switch {
                CrowdTargetState.Following => new Color4(0.2f, 1f, 0.4f, 1f),
                CrowdTargetState.Arrived => new Color4(0.4f, 0.6f, 1f, 1f),
                CrowdTargetState.Failed => new Color4(1f, 0.3f, 0.2f, 1f),
                _ => Color4.White
            };

            var radius = crowd.TryGetParams(handle, out var parameters) ? parameters.Radius : 0.5f;

            Circle(draw, state.Position, radius, colour, seconds);

            draw.Line(state.Position, state.Position + state.Velocity, colour, seconds);
            draw.Line(state.Position, state.Position + state.DesiredVelocity, new(1f, 1f, 1f, 0.4f), seconds);
        }
    }

    static void Circle(DebugDraw draw, Vector3 centre, float radius, Color4 colour, float seconds) {
        const int Segments = 12;

        var previous = centre + new Vector3(radius, 0f, 0f);

        for (var step = 1; step <= Segments; step++) {
            var angle = step * MathF.Tau / Segments;
            var next = centre + new Vector3(MathF.Cos(angle) * radius, 0f, MathF.Sin(angle) * radius);

            draw.Line(previous, next, colour, seconds);
            previous = next;
        }
    }
}
