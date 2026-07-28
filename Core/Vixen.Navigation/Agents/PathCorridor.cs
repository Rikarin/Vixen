// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Navigation.Agents;

/// <summary>
///     The polygons an agent is walking through, kept up to date as it moves.
/// </summary>
/// <remarks>
///     <para>
///         An agent does not follow a line, it follows a corridor. The expensive search happens once,
///         when the destination changes; after that every frame only trims the polygons the agent has
///         left off the front and pulls a fresh set of corners out of what remains. That is what makes
///         a hundred agents affordable — the per-frame cost is a string-pull over a handful of
///         polygons, not a search.
///     </para>
///     <para>
///         It is also what makes local avoidance safe. Avoidance pushes an agent sideways, off the
///         line it would have walked; because the corridor is a region rather than a line, the agent
///         is still inside its path afterwards and no replan is needed.
///     </para>
/// </remarks>
public sealed class PathCorridor {
    NavPolyRef[] path;
    NavPolyRef[] scratch;
    int count;

    /// <summary>Creates a corridor.</summary>
    /// <param name="capacity">The most polygons it will hold. A path longer than this is truncated.</param>
    public PathCorridor(int capacity = 256) {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        path = new NavPolyRef[capacity];
        scratch = new NavPolyRef[capacity];
    }

    /// <summary>Where the agent is.</summary>
    public Vector3 Position { get; private set; }

    /// <summary>Where it is going.</summary>
    public Vector3 Target { get; private set; }

    /// <summary>How many polygons the corridor holds.</summary>
    public int Count => count;

    /// <summary>The polygon the agent is on.</summary>
    public NavPolyRef FirstPoly => count > 0 ? path[0] : NavPolyRef.Null;

    /// <summary>The polygon the target is on.</summary>
    public NavPolyRef LastPoly => count > 0 ? path[count - 1] : NavPolyRef.Null;

    /// <summary>The corridor, for a caller that wants to look at it.</summary>
    public ReadOnlySpan<NavPolyRef> Path => path.AsSpan(0, count);

    /// <summary>Throws the corridor away and starts again from one polygon.</summary>
    /// <param name="poly">The polygon the agent is on.</param>
    /// <param name="position">Where it is.</param>
    public void Reset(NavPolyRef poly, Vector3 position) {
        path[0] = poly;
        count = poly.IsNull ? 0 : 1;
        Position = position;
        Target = position;
    }

    /// <summary>Gives the corridor a path to follow.</summary>
    /// <param name="target">Where it ends.</param>
    /// <param name="corridor">The polygons, from the one the agent is on to the one the target is on.</param>
    public void SetPath(Vector3 target, ReadOnlySpan<NavPolyRef> corridor) {
        if (corridor.Length > path.Length) {
            corridor = corridor[..path.Length];
        }

        corridor.CopyTo(path);
        count = corridor.Length;
        Target = target;
    }

    /// <summary>The corners the agent has to turn at, starting from where it is.</summary>
    /// <param name="query">The query to string-pull with.</param>
    /// <param name="corners">Where to write them.</param>
    /// <returns>How many were written.</returns>
    public int FindCorners(NavMeshQuery query, Span<NavPathPoint> corners) {
        ArgumentNullException.ThrowIfNull(query);

        return count == 0 ? 0 : query.FindStraightPath(Position, Target, path.AsSpan(0, count), corners);
    }

    /// <summary>
    ///     Moves the agent, keeping it on the mesh and trimming the polygons it has walked past.
    /// </summary>
    /// <param name="wanted">Where the agent is trying to be.</param>
    /// <param name="query">The query to move with.</param>
    /// <param name="filter">Which polygons it may cross.</param>
    /// <returns><see langword="false" /> if the corridor no longer describes where the agent is.</returns>
    /// <remarks>
    ///     A false return is not a failure to move — the agent has moved, and it is on the mesh. It
    ///     means the move ended somewhere the corridor does not pass through, which happens when
    ///     avoidance pushes an agent round the far side of an obstacle. The caller replans; the agent
    ///     keeps walking in the meantime, because <see cref="Position" /> is still valid.
    /// </remarks>
    public bool MovePosition(Vector3 wanted, NavMeshQuery query, NavQueryFilter filter) {
        ArgumentNullException.ThrowIfNull(query);

        if (count == 0 || !query.MoveAlongSurface(path[0], Position, wanted, filter, out var moved, out var poly)) {
            return false;
        }

        Position = moved;

        for (var index = 0; index < count; index++) {
            if (path[index] != poly) {
                continue;
            }

            if (index > 0) {
                Array.Copy(path, index, path, 0, count - index);
                count -= index;
            }

            return true;
        }

        // Off the corridor. Keep the polygon the agent actually ended up on, so that the next move
        // and the next query have somewhere to start from.
        path[0] = poly;
        count = 1;

        return false;
    }

    /// <summary>
    ///     Steps onto the off-mesh connection the corridor is about to use, if the agent has arrived at
    ///     its near end.
    /// </summary>
    /// <param name="mesh">The mesh the corridor is on.</param>
    /// <param name="reach">How close to the entry point counts as being there.</param>
    /// <param name="connection">The connection being used.</param>
    /// <param name="entry">Where it starts.</param>
    /// <param name="exit">Where it ends.</param>
    /// <returns><see langword="false" /> if the next step is not a connection, or the agent is not there yet.</returns>
    /// <remarks>
    ///     <para>
    ///         The corridor is trimmed past the connection immediately, so what the caller holds
    ///         afterwards is a corridor that starts at the far end. Where the <i>agent</i> is in the
    ///         meantime is the caller's business — <see cref="Crowd" /> walks it across over time, and
    ///         a game that wants a ladder animation is what that time is for.
    ///     </para>
    ///     <para>
    ///         The position is set to the exit rather than left at the entry, because everything else
    ///         here — the string pull, the next move — is relative to a position that is on the
    ///         corridor's first polygon, and the entry is not on it any more.
    ///     </para>
    /// </remarks>
    public bool TryUseOffMeshConnection(NavMesh mesh, float reach, out NavPolyRef connection, out Vector3 entry, out Vector3 exit) {
        ArgumentNullException.ThrowIfNull(mesh);

        connection = NavPolyRef.Null;
        entry = default;
        exit = default;

        if (count < 3 || !mesh.IsOffMeshConnection(path[1])) {
            return false;
        }

        if (!mesh.GetPortalPoints(path[0], path[1], out var start, out _) ||
            !mesh.GetPortalPoints(path[1], path[2], out var end, out _)) {
            return false;
        }

        if (NavGeometry.Distance2D(Position, start) > reach) {
            return false;
        }

        connection = path[1];
        entry = start;
        exit = end;

        Array.Copy(path, 2, path, 0, count - 2);
        count -= 2;
        Position = end;

        return true;
    }

    /// <summary>Cuts the corridor short wherever the agent can already see past it.</summary>
    /// <param name="query">The query to cast with.</param>
    /// <param name="filter">Which polygons may be crossed.</param>
    /// <param name="distance">How far ahead to look.</param>
    /// <remarks>
    ///     A corridor comes out of a search over polygon edges and can wander — around the outside of
    ///     a polygon the straight line crosses, most often. Casting a ray at a point a little way along
    ///     it and, if nothing is in the way, replacing the polygons up to there with the ones the ray
    ///     crossed straightens it out for the cost of one raycast per agent per frame.
    /// </remarks>
    public void Optimize(NavMeshQuery query, NavQueryFilter filter, float distance = 6f) {
        ArgumentNullException.ThrowIfNull(query);

        if (count < 3) {
            return;
        }

        Span<NavPathPoint> corners = stackalloc NavPathPoint[2];

        if (FindCorners(query, corners) < 2) {
            return;
        }

        var direction = corners[1].Position - Position;
        var length = direction.Length();

        if (length < 0.01f) {
            return;
        }

        var probe = Position + (direction / length * MathF.Min(length, distance));

        Span<NavPolyRef> visited = stackalloc NavPolyRef[16];

        if (!query.Raycast(path[0], Position, probe, filter, out var hit, visited, out var visitedCount) || hit.Hit || visitedCount == 0) {
            return;
        }

        Merge(visited[..visitedCount]);
    }

    /// <summary>Replaces the front of the corridor with the polygons a ray actually crossed.</summary>
    void Merge(ReadOnlySpan<NavPolyRef> visited) {
        // Where the two agree last is where the corridor's own polygons resume. Anything before it in
        // the corridor has been bypassed; anything after it is untouched.
        var join = -1;
        var joinVisited = -1;

        for (var index = visited.Length - 1; index >= 0 && join < 0; index--) {
            for (var other = Math.Min(count, 32) - 1; other >= 0; other--) {
                if (visited[index] == path[other]) {
                    join = other;
                    joinVisited = index;

                    break;
                }
            }
        }

        if (join < 0) {
            return;
        }

        // The ray's polygons up to and including the shared one, then the corridor's own from just
        // after it.
        var tail = count - join - 1;
        var total = joinVisited + 1 + tail;

        if (total > path.Length) {
            return;
        }

        visited[..(joinVisited + 1)].CopyTo(scratch);
        Array.Copy(path, join + 1, scratch, joinVisited + 1, tail);
        Array.Copy(scratch, path, total);
        count = total;
    }

    /// <summary>Makes room for a longer path.</summary>
    /// <param name="capacity">The new capacity. Ignored if it is not larger.</param>
    public void EnsureCapacity(int capacity) {
        if (capacity > path.Length) {
            Array.Resize(ref path, capacity);
            Array.Resize(ref scratch, capacity);
        }
    }
}
