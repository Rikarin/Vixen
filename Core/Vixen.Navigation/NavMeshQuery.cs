// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Collections;
using Vixen.Core.Mathematics;

namespace Vixen.Navigation;

/// <summary>How much of what was asked for a search actually found.</summary>
public enum NavPathStatus {
    /// <summary>Nothing usable. A bad start or end polygon, or no polygon the filter would cross.</summary>
    Failed,

    /// <summary>
    ///     A path to the polygon that got closest, because the destination could not be reached or
    ///     the caller's buffer ran out. Following it is the right behaviour: an agent that walks as
    ///     far as it can looks alive, and one that refuses to move because the door is shut does not.
    /// </summary>
    Partial,

    /// <summary>A path that ends on the polygon that was asked for.</summary>
    Complete
}

/// <summary>One point of a straight path, and the polygon it stands on.</summary>
/// <param name="Position">Where.</param>
/// <param name="Poly">The polygon it is on, which is what an agent resumes from.</param>
public readonly record struct NavPathPoint(Vector3 Position, NavPolyRef Poly);

/// <summary>What a walk across the surface ran into.</summary>
/// <param name="Hit">Whether a wall stopped it.</param>
/// <param name="Distance">How far along the segment it got, from 0 to 1.</param>
/// <param name="Position">Where it stopped.</param>
/// <param name="Normal">The wall's normal, facing back towards the walkable side, if it hit one.</param>
/// <param name="LastPoly">The last polygon it was on, which is where an agent now stands.</param>
public readonly record struct NavRaycastHit(bool Hit, float Distance, Vector3 Position, Vector3 Normal, NavPolyRef LastPoly);

/// <summary>
///     Asks a <see cref="NavMesh" /> questions: which polygon is under a point, how to get from one
///     to another, and what a straight line runs into on the way.
/// </summary>
/// <remarks>
///     <para>
///         <b>One per thread, reused.</b> Everything a search needs — the node pool, the open list,
///         the visited set — lives here and is cleared rather than reallocated, so a query costs no
///         allocation after the first. That is the whole reason this is an object and not a static
///         class over the mesh: sharing one between two threads is a data race, and making the state
///         local to a call would allocate it every frame.
///     </para>
///     <para>
///         <b>The mesh may change under it.</b> Nothing here caches a tile or a polygon between
///         calls, and every reference is resolved through the salt, so a tile unloaded between two
///         queries makes the second one fail rather than read freed data.
///     </para>
/// </remarks>
public sealed class NavMeshQuery {
    /// <summary>
    ///     Slightly less than one, so the heuristic never overestimates and A* stays admissible.
    ///     Recast uses the same number for the same reason: costs are distances scaled by area
    ///     multipliers that are at least one, so a heuristic of exactly the straight-line distance is
    ///     already a bound — and float error is what the last thousandth is for.
    /// </summary>
    const float HeuristicScale = 0.999f;

    readonly Dictionary<NavPolyRef, int> nodeIndices = [];
    readonly IndexedPriorityQueue<float> open = new();
    readonly List<Node> nodes = [];

    /// <summary>Creates a query over a mesh.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <exception cref="ArgumentNullException"><paramref name="mesh" /> is null.</exception>
    public NavMeshQuery(NavMesh mesh) {
        ArgumentNullException.ThrowIfNull(mesh);

        Mesh = mesh;
    }

    /// <summary>The mesh being queried.</summary>
    public NavMesh Mesh { get; }

    /// <summary>How many polygons the last search expanded. Diagnostic.</summary>
    public int LastSearchNodes { get; private set; }

    /// <summary>
    ///     How many corridor steps the last string-pull walked, restarts included. Diagnostic.
    /// </summary>
    /// <remarks>
    ///     Worth exposing because it is not the corridor length: the funnel restarts from each corner
    ///     it emits, so a corridor that turns often is walked more than once. A number far above the
    ///     corridor length is what a pathological funnel looks like from outside.
    /// </remarks>
    public int LastStraightPathSteps { get; private set; }

    /// <summary>Finds the polygon nearest a point.</summary>
    /// <param name="center">The point.</param>
    /// <param name="halfExtents">How far to look in each direction.</param>
    /// <param name="filter">Which polygons may be considered.</param>
    /// <param name="poly">The polygon found.</param>
    /// <param name="point">The nearest point on it.</param>
    /// <returns><see langword="false" /> if there is no acceptable polygon in the box.</returns>
    /// <remarks>
    ///     The extents are a box rather than a radius, and the vertical one usually wants to be the
    ///     larger: an agent's position is at its feet and the mesh sits at the surface it walks on, so
    ///     the horizontal search is about how far off the mesh a spawn point may be, while the
    ///     vertical one is about how far above or below it the caller's idea of "here" may sit.
    /// </remarks>
    public bool FindNearestPoly(Vector3 center, Vector3 halfExtents, NavQueryFilter filter, out NavPolyRef poly, out Vector3 point) {
        ArgumentNullException.ThrowIfNull(filter);

        poly = NavPolyRef.Null;
        point = center;

        var box = new BoundingBox(center - halfExtents, center + halfExtents);
        var (minX, minZ) = Mesh.TileCoordinates(box.Minimum);
        var (maxX, maxZ) = Mesh.TileCoordinates(box.Maximum);
        var best = float.MaxValue;

        Span<Vector3> vertices = stackalloc Vector3[NavMesh.MaxVerticesPerPoly];

        for (var z = minZ; z <= maxZ; z++) {
            for (var x = minX; x <= maxX; x++) {
                if (Mesh.TileAt(x, z) is not { } tile || !tile.Data.Bounds.Intersects(box)) {
                    continue;
                }

                // Surface only: an agent stands on ground, never on a connection. A ladder whose
                // endpoint happened to be the nearest thing to a spawn point would put the agent on a
                // polygon with no interior, where every question about where it is standing is
                // meaningless.
                for (var index = 0; index < tile.SurfacePolyCount; index++) {
                    var candidate = NavMesh.Reference(tile, index);

                    if (!tile.PolyBounds[index].Intersects(box)) {
                        continue;
                    }

                    if (!Mesh.TryGetPolyAttributes(candidate, out _, out var flags) || !filter.Passes(flags)) {
                        continue;
                    }

                    var count = Mesh.GetPolyVertices(candidate, vertices);
                    var closest = ClosestPointOnPoly(vertices[..count], center);
                    var distance = Vector3.DistanceSquared(center, closest);

                    if (distance < best) {
                        best = distance;
                        poly = candidate;
                        point = closest;
                    }
                }
            }
        }

        return !poly.IsNull;
    }

    /// <summary>The point of a polygon closest to a position.</summary>
    /// <param name="reference">The polygon.</param>
    /// <param name="position">The position.</param>
    /// <param name="closest">The closest point, with the polygon's height at it.</param>
    /// <param name="isOverPoly">Whether the position was inside the polygon to begin with.</param>
    /// <returns><see langword="false" /> if the reference does not resolve.</returns>
    public bool ClosestPointOnPoly(NavPolyRef reference, Vector3 position, out Vector3 closest, out bool isOverPoly) {
        Span<Vector3> vertices = stackalloc Vector3[NavMesh.MaxVerticesPerPoly];
        var count = Mesh.GetPolyVertices(reference, vertices);

        if (count == 0) {
            closest = position;
            isOverPoly = false;

            return false;
        }

        var poly = vertices[..count];
        isOverPoly = NavGeometry.ContainsPoint2D(position, poly);
        closest = ClosestPointOnPoly(poly, position);

        return true;
    }

    /// <summary>The height of the surface at a point on a polygon.</summary>
    /// <param name="reference">The polygon.</param>
    /// <param name="position">The point, whose own height is ignored.</param>
    /// <param name="height">The surface height.</param>
    /// <returns><see langword="false" /> if the point is not over the polygon.</returns>
    public bool GetPolyHeight(NavPolyRef reference, Vector3 position, out float height) {
        Span<Vector3> vertices = stackalloc Vector3[NavMesh.MaxVerticesPerPoly];
        var count = Mesh.GetPolyVertices(reference, vertices);

        if (count == 0) {
            height = 0f;

            return false;
        }

        return NavGeometry.TryGetHeight(position, vertices[..count], out height);
    }

    /// <summary>Finds a path from one polygon to another.</summary>
    /// <param name="start">The polygon to start on.</param>
    /// <param name="end">The polygon to reach.</param>
    /// <param name="startPosition">Where on the start polygon.</param>
    /// <param name="endPosition">Where on the end polygon.</param>
    /// <param name="filter">Which polygons may be crossed, and what crossing them costs.</param>
    /// <param name="path">Where to write the polygons, start first.</param>
    /// <param name="count">How many were written.</param>
    /// <returns>How much of the path was found.</returns>
    /// <remarks>
    ///     <para>
    ///         The result is a corridor of polygons, not a line. Turning it into something an agent
    ///         can steer along is <see cref="FindStraightPath" />, and the two are separate because a
    ///         corridor stays valid while the agent moves inside it — the expensive search happens
    ///         when the destination changes, and the cheap string-pull happens every time the agent
    ///         needs a new corner.
    ///     </para>
    ///     <para>
    ///         A search that cannot reach the destination returns <see cref="NavPathStatus.Partial" />
    ///         and the path to whichever polygon ended up closest, which is also what happens when
    ///         <paramref name="path" /> is too small to hold the answer.
    ///     </para>
    /// </remarks>
    public NavPathStatus FindPath(
        NavPolyRef start,
        NavPolyRef end,
        Vector3 startPosition,
        Vector3 endPosition,
        NavQueryFilter filter,
        Span<NavPolyRef> path,
        out int count
    ) {
        ArgumentNullException.ThrowIfNull(filter);

        count = 0;
        LastSearchNodes = 0;

        if (!Mesh.IsValid(start) || !Mesh.IsValid(end) || path.IsEmpty) {
            return NavPathStatus.Failed;
        }

        if (start == end) {
            path[0] = start;
            count = 1;

            return NavPathStatus.Complete;
        }

        ResetSearch();

        var startNode = CreateNode(start, startPosition, -1);

        // Visited from the outset, and not because it has been relaxed: it is what makes the start
        // polygon unreachable as somebody's neighbour. Without it a cycle back to the start would be
        // accepted — every fresh node is — and would give the start a parent, which is a loop the
        // path reconstruction walks for ever.
        nodes[startNode] = nodes[startNode] with {
            Cost = 0f,
            Total = NavGeometry.Distance2D(startPosition, endPosition) * HeuristicScale,
            Visited = true
        };

        open.Enqueue(startNode, nodes[startNode].Total);

        var lastBest = startNode;
        var lastBestHeuristic = nodes[startNode].Total;
        var reached = false;

        while (open.TryDequeue(out var current, out _)) {
            var node = nodes[current];
            LastSearchNodes++;

            if (node.Poly == end) {
                lastBest = current;
                reached = true;

                break;
            }

            var parentPoly = node.Parent >= 0 ? nodes[node.Parent].Poly : NavPolyRef.Null;

            foreach (var neighbour in Mesh.Neighbours(node.Poly)) {
                if (neighbour.Reference == parentPoly) {
                    continue;
                }

                if (!Mesh.TryGetPolyAttributes(neighbour.Reference, out var area, out var flags) || !filter.Passes(flags)) {
                    continue;
                }

                var index = GetOrCreateNode(neighbour.Reference, node.Poly, current);
                var neighbourNode = nodes[index];

                float cost;
                float heuristic;

                if (neighbour.Reference == end) {
                    // The last step is measured to the actual destination rather than to the far
                    // edge of the last polygon, or a path that ends in a large polygon would be
                    // chosen by where its edge is rather than by where the caller is going.
                    cost = node.Cost
                        + (NavGeometry.Distance2D(node.Position, neighbourNode.Position) * filter.GetAreaCost(area))
                        + (NavGeometry.Distance2D(neighbourNode.Position, endPosition) * filter.GetAreaCost(area));

                    heuristic = 0f;
                } else {
                    cost = node.Cost + (NavGeometry.Distance2D(node.Position, neighbourNode.Position) * filter.GetAreaCost(area));
                    heuristic = NavGeometry.Distance2D(neighbourNode.Position, endPosition) * HeuristicScale;
                }

                var total = cost + heuristic;

                if (neighbourNode.Visited && total >= neighbourNode.Total) {
                    continue;
                }

                nodes[index] = neighbourNode with {
                    Cost = cost,
                    Total = total,
                    Parent = current,
                    Visited = true
                };

                open.SetPriority(index, total);

                if (heuristic < lastBestHeuristic) {
                    lastBestHeuristic = heuristic;
                    lastBest = index;
                }
            }
        }

        var length = 0;

        for (var walk = lastBest; walk >= 0; walk = nodes[walk].Parent) {
            length++;
        }

        var truncated = length > path.Length;
        count = Math.Min(length, path.Length);

        // Written back to front, because the parents run from the end of the path to its start. When
        // the caller's buffer is too small it is the *start* that has to survive: an agent walks the
        // first polygons now and asks again later.
        var write = count - 1;
        var skip = length - count;

        for (var walk = lastBest; walk >= 0; walk = nodes[walk].Parent) {
            if (skip-- > 0) {
                continue;
            }

            path[write--] = nodes[walk].Poly;
        }

        if (truncated) {
            return NavPathStatus.Partial;
        }

        return reached && nodes[lastBest].Poly == end ? NavPathStatus.Complete : NavPathStatus.Partial;
    }

    /// <summary>Pulls a corridor of polygons straight, into the corners an agent turns at.</summary>
    /// <param name="startPosition">Where the agent is.</param>
    /// <param name="endPosition">Where it is going.</param>
    /// <param name="path">The corridor, as <see cref="FindPath" /> produced it.</param>
    /// <param name="points">Where to write the corners, including both ends.</param>
    /// <returns>How many corners were written.</returns>
    /// <remarks>
    ///     <para>
    ///         The funnel algorithm. Walking the corridor, the two sides of each shared edge narrow a
    ///         wedge from the current corner; when one side would cross the other, the vertex it
    ///         crossed is a corner the agent has to turn at, and the wedge restarts from there. The
    ///         result is the shortest path inside the corridor, which — because the corridor came out
    ///         of A* over polygon centres — is not quite the shortest path across the mesh, and is
    ///         the trade every navmesh makes.
    ///     </para>
    ///     <para>
    ///         Left and right come from the polygon winding, through
    ///         <see cref="NavMesh.GetPortalPoints" />.
    ///     </para>
    /// </remarks>
    public int FindStraightPath(Vector3 startPosition, Vector3 endPosition, ReadOnlySpan<NavPolyRef> path, Span<NavPathPoint> points) {
        if (path.IsEmpty || points.IsEmpty) {
            return 0;
        }

        var count = 0;
        LastStraightPathSteps = 0;
        points[count++] = new(startPosition, path[0]);

        if (path.Length == 1) {
            if (count < points.Length) {
                points[count++] = new(endPosition, path[0]);
            }

            return count;
        }

        var apex = startPosition;
        var left = startPosition;
        var right = startPosition;
        var apexIndex = 0;
        var leftIndex = 0;
        var rightIndex = 0;

        for (var index = 0; index <= path.Length && count < points.Length; index++) {
            LastStraightPathSteps++;

            Vector3 candidateLeft;
            Vector3 candidateRight;

            if (index < path.Length - 1) {
                if (!Mesh.GetPortalPoints(path[index], path[index + 1], out candidateLeft, out candidateRight)) {
                    // The corridor is stale — a tile went away under it. Stop where it stops.
                    break;
                }
            } else if (index == path.Length - 1) {
                candidateLeft = endPosition;
                candidateRight = endPosition;
            } else {
                break;
            }

            // Right side.
            if (NavGeometry.Side2D(apex, right, candidateRight) >= 0f) {
                if (Same(apex, right) || NavGeometry.Side2D(apex, left, candidateRight) < 0f) {
                    right = candidateRight;
                    rightIndex = index;
                } else {
                    points[count++] = new(left, path[Math.Min(leftIndex + 1, path.Length - 1)]);

                    apex = left;
                    apexIndex = leftIndex;
                    right = apex;
                    left = apex;
                    rightIndex = apexIndex;
                    index = apexIndex;

                    continue;
                }
            }

            // Left side, the mirror of the above.
            if (NavGeometry.Side2D(apex, left, candidateLeft) <= 0f) {
                if (Same(apex, left) || NavGeometry.Side2D(apex, right, candidateLeft) > 0f) {
                    left = candidateLeft;
                    leftIndex = index;
                } else {
                    points[count++] = new(right, path[Math.Min(rightIndex + 1, path.Length - 1)]);

                    apex = right;
                    apexIndex = rightIndex;
                    right = apex;
                    left = apex;
                    leftIndex = apexIndex;
                    index = apexIndex;
                }
            }
        }

        if (count < points.Length) {
            points[count++] = new(endPosition, path[^1]);
        }

        return count;
    }

    /// <summary>Walks a straight line across the surface until it leaves it.</summary>
    /// <param name="start">The polygon to start on.</param>
    /// <param name="startPosition">Where on it.</param>
    /// <param name="endPosition">Where the line goes.</param>
    /// <param name="filter">Which polygons may be crossed.</param>
    /// <param name="hit">Where it stopped, and on what.</param>
    /// <returns><see langword="false" /> if the start polygon does not resolve.</returns>
    /// <remarks>
    ///     Two jobs in one, as in Detour. It is a visibility test — a corridor can be shortened
    ///     wherever a raycast reaches the later polygon — and it is the movement primitive: an agent
    ///     that wants to step somewhere asks what is in the way rather than asking whether the
    ///     destination is on the mesh, which is the same question one polygon at a time.
    /// </remarks>
    public bool Raycast(NavPolyRef start, Vector3 startPosition, Vector3 endPosition, NavQueryFilter filter, out NavRaycastHit hit) =>
        Raycast(start, startPosition, endPosition, filter, out hit, [], out _);

    /// <summary>Walks a straight line across the surface, recording the polygons it crossed.</summary>
    /// <param name="start">The polygon to start on.</param>
    /// <param name="startPosition">Where on it.</param>
    /// <param name="endPosition">Where the line goes.</param>
    /// <param name="filter">Which polygons may be crossed.</param>
    /// <param name="hit">Where it stopped, and on what.</param>
    /// <param name="visited">Where to write the polygons crossed, start first. May be empty.</param>
    /// <param name="visitedCount">How many were written.</param>
    /// <returns><see langword="false" /> if the start polygon does not resolve.</returns>
    public bool Raycast(
        NavPolyRef start,
        Vector3 startPosition,
        Vector3 endPosition,
        NavQueryFilter filter,
        out NavRaycastHit hit,
        Span<NavPolyRef> visited,
        out int visitedCount
    ) {
        ArgumentNullException.ThrowIfNull(filter);

        hit = default;
        visitedCount = 0;

        if (!Mesh.IsValid(start)) {
            return false;
        }

        Span<Vector3> vertices = stackalloc Vector3[NavMesh.MaxVerticesPerPoly];

        var current = start;
        var travelled = 0f;
        var direction = endPosition - startPosition;

        // Every polygon crossed advances the parameter, and a polygon is never entered twice, so the
        // bound is the corridor length rather than a guess. It is still a bound: a mesh with a
        // degenerate polygon in it would otherwise be an infinite loop in the movement path.
        for (var step = 0; step < 128; step++) {
            if (visitedCount < visited.Length) {
                visited[visitedCount++] = current;
            }

            var count = Mesh.GetPolyVertices(current, vertices);

            // A connection has two vertices and no interior, so there is nothing to clip a segment
            // against and nothing a line of sight can cross. A raycast that reached one stops there,
            // which is the honest answer: you cannot see across a ladder.
            if (count < 3) {
                hit = new(true, travelled, startPosition + (direction * travelled), Vector3.Zero, current);

                return true;
            }

            var poly = vertices[..count];

            if (!NavGeometry.ClipSegment2D(startPosition, endPosition, poly, out _, out var exit, out _, out var exitEdge)) {
                break;
            }

            if (exitEdge < 0) {
                // The line ends inside this polygon: nothing was hit.
                hit = new(false, 1f, endPosition, Vector3.Zero, current);

                return true;
            }

            travelled = MathF.Max(travelled, exit);

            var exitPoint = startPosition + (direction * exit);
            var next = NavPolyRef.Null;

            foreach (var neighbour in Mesh.Neighbours(current)) {
                // Off-mesh links are not crossings: an agent uses one deliberately, and a raycast
                // walking into one would report a line of sight that goes up a ladder.
                if (neighbour.IsOffMesh || neighbour.Edge != exitEdge) {
                    continue;
                }

                if (!Mesh.TryGetPolyAttributes(neighbour.Reference, out _, out var flags) || !filter.Passes(flags)) {
                    continue;
                }

                var (edgeStart, edgeEnd) = (poly[exitEdge], poly[(exitEdge + 1) % count]);
                NavGeometry.ClosestPointOnSegment2D(exitPoint, edgeStart, edgeEnd, out var t);

                // An edge may be shared with more than one polygon of a neighbouring tile, so the
                // one to step into is the one whose part of the edge the line actually leaves through.
                if (t < neighbour.Min - 1e-4f || t > neighbour.Max + 1e-4f) {
                    continue;
                }

                next = neighbour.Reference;

                break;
            }

            if (next.IsNull) {
                var (edgeStart, edgeEnd) = (poly[exitEdge], poly[(exitEdge + 1) % count]);

                // Negated: the wall's normal faces the walkable side, which is the side the caller is
                // on. That is what makes it the right vector to project a movement out of.
                hit = new(true, travelled, exitPoint, -NavGeometry.OutwardNormal2D(edgeStart, edgeEnd), current);

                return true;
            }

            current = next;
        }

        hit = new(true, travelled, startPosition + (direction * travelled), Vector3.Zero, current);

        return true;
    }

    /// <summary>Moves a point across the surface, sliding along whatever it runs into.</summary>
    /// <param name="start">The polygon the point is on.</param>
    /// <param name="startPosition">Where it is.</param>
    /// <param name="endPosition">Where it is trying to get to.</param>
    /// <param name="filter">Which polygons it may cross.</param>
    /// <param name="position">Where it ended up, on the mesh.</param>
    /// <param name="poly">The polygon it ended up on.</param>
    /// <returns><see langword="false" /> if the start polygon does not resolve.</returns>
    /// <remarks>
    ///     <para>
    ///         This is what actually moves an agent, and the reason an agent cannot walk off the mesh
    ///         however hard avoidance pushes it: the requested position is a wish, and what comes back
    ///         is the part of it the surface allowed.
    ///     </para>
    ///     <para>
    ///         Detour does this with a local flood search around the segment; this is a raycast that
    ///         slides along the wall it hits and casts again, up to a small number of times. The
    ///         difference shows up when a point is pushed into a corner, where the flood search finds
    ///         its way around an obstacle that the slide gives up on — an agent that stops at a corner
    ///         for a frame, rather than one that ends up off the mesh.
    ///     </para>
    /// </remarks>
    public bool MoveAlongSurface(
        NavPolyRef start,
        Vector3 startPosition,
        Vector3 endPosition,
        NavQueryFilter filter,
        out Vector3 position,
        out NavPolyRef poly
    ) {
        ArgumentNullException.ThrowIfNull(filter);

        position = startPosition;
        poly = start;

        if (!Mesh.IsValid(start)) {
            return false;
        }

        var target = endPosition;

        for (var slide = 0; slide < 4; slide++) {
            var travel = target - position;

            if (NavGeometry.DistanceSquared2D(position, target) < 1e-10f) {
                break;
            }

            if (!Raycast(poly, position, target, filter, out var hit)) {
                return false;
            }

            poly = hit.LastPoly;

            if (!hit.Hit) {
                position = hit.Position;

                break;
            }

            // Stop just short of the wall, so the next cast starts inside a polygon rather than
            // exactly on its edge, where containment is a coin toss.
            var remaining = target - hit.Position;
            position = hit.Position - (Vector3.Normalize(travel) * 0.01f);

            if (hit.Normal == Vector3.Zero) {
                break;
            }

            target = position + (remaining - (hit.Normal * Vector3.Dot(remaining, hit.Normal)));

            if (NavGeometry.DistanceSquared2D(position, target) < 1e-8f) {
                break;
            }
        }

        if (GetPolyHeight(poly, position, out var height)) {
            position = new(position.X, height, position.Z);
        }

        return true;
    }

    static Vector3 ClosestPointOnPoly(ReadOnlySpan<Vector3> poly, Vector3 position) {
        // A connection is a segment, and the closest point on it is the closest point on that segment
        // — the containment test below would answer nonsense for a polygon with no interior.
        if (poly.Length == 2) {
            return NavGeometry.ClosestPointOnSegment2D(position, poly[0], poly[1], out _);
        }

        if (NavGeometry.ContainsPoint2D(position, poly) && NavGeometry.TryGetHeight(position, poly, out var height)) {
            return new(position.X, height, position.Z);
        }

        return NavGeometry.ClosestPointOnBoundary2D(position, poly, out _);
    }

    static bool Same(Vector3 left, Vector3 right) => NavGeometry.DistanceSquared2D(left, right) < 1e-12f;

    void ResetSearch() {
        nodeIndices.Clear();
        nodes.Clear();
        open.Clear();
    }

    int CreateNode(NavPolyRef poly, Vector3 position, int parent) {
        nodes.Add(new() { Poly = poly, Position = position, Parent = parent });
        nodeIndices[poly] = nodes.Count - 1;

        return nodes.Count - 1;
    }

    int GetOrCreateNode(NavPolyRef poly, NavPolyRef from, int parent) {
        if (nodeIndices.TryGetValue(poly, out var index)) {
            return index;
        }

        // A polygon's position, for the purpose of measuring the path, is the middle of the edge it
        // was reached through rather than its centre. Centres make a path that zigzags between the
        // middles of large polygons cost less than the straight line an agent will actually walk.
        var position = Mesh.GetEdgeMidpoint(from, poly, out var midpoint) ? midpoint : Vector3.Zero;

        return CreateNode(poly, position, parent);
    }

    /// <summary>One polygon, as the search sees it.</summary>
    /// <remarks>
    ///     <see cref="Visited" /> rather than a null check, because a node is created when it is first
    ///     reached but only gets a cost when it is relaxed, and the two are not the same moment: the
    ///     first neighbour to reach a polygon creates the node, and any of them may be the one whose
    ///     cost survives.
    /// </remarks>
    readonly record struct Node {
        public NavPolyRef Poly { get; init; }

        public Vector3 Position { get; init; }

        public float Cost { get; init; }

        public float Total { get; init; }

        public int Parent { get; init; }

        public bool Visited { get; init; }
    }
}
