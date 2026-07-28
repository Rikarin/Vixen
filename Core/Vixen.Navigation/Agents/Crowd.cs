// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Navigation.Agents;

/// <summary>What an agent is and how it moves.</summary>
/// <remarks>
///     Properties with initialisers rather than positional parameters with defaults: on a positional
///     record struct, <c>new CrowdAgentParams()</c> resolves to the struct's parameterless
///     constructor and zeroes everything, which would give an agent no radius and no speed while
///     looking exactly like a request for the defaults.
/// </remarks>
public readonly record struct CrowdAgentParams {
    /// <summary>A human-sized walker.</summary>
    public CrowdAgentParams() { }

    /// <summary>How wide it is. Should match what the mesh was baked for.</summary>
    public float Radius { get; init; } = 0.6f;

    /// <summary>How tall it is. Used when looking for the polygon it is standing on.</summary>
    public float Height { get; init; } = 2f;

    /// <summary>The fastest it walks.</summary>
    public float MaxSpeed { get; init; } = 3.5f;

    /// <summary>How quickly it can change velocity.</summary>
    public float MaxAcceleration { get; init; } = 8f;

    /// <summary>How close to the destination counts as being there.</summary>
    public float ArrivalRadius { get; init; } = 0.25f;

    /// <summary>How far out it starts slowing down for the destination.</summary>
    public float SlowdownRadius { get; init; } = 1.5f;

    /// <summary>
    ///     How hard it pushes out of an overlap with another agent. Avoidance is what stops overlaps
    ///     happening; this is what recovers from the ones that happen anyway, in a doorway or a corner
    ///     where there was no admissible velocity.
    /// </summary>
    public float SeparationWeight { get; init; } = 2f;

    /// <summary>Whether it steers around other agents at all.</summary>
    public bool AvoidanceEnabled { get; init; } = true;
}

/// <summary>An agent in a <see cref="Crowd" />.</summary>
/// <param name="Index">Its slot.</param>
/// <param name="Generation">Which occupant of that slot it is.</param>
/// <remarks>
///     Generation-checked for the same reason <see cref="NavPolyRef" /> is salted: an agent's slot is
///     reused, and a stale handle has to fail rather than steer somebody else's agent.
/// </remarks>
public readonly record struct CrowdAgentHandle(int Index, uint Generation) {
    /// <summary>The handle that names no agent.</summary>
    public static CrowdAgentHandle Null => new(-1, 0);

    /// <summary>Whether this names no agent.</summary>
    public bool IsNull => Index < 0;
}

/// <summary>How an agent is getting on with where it was told to go.</summary>
public enum CrowdTargetState {
    /// <summary>It has not been told to go anywhere.</summary>
    None,

    /// <summary>It has, and the path has not been worked out yet.</summary>
    Requested,

    /// <summary>It is walking.</summary>
    Following,

    /// <summary>It is there.</summary>
    Arrived,

    /// <summary>There is no way there, or the agent is not on the mesh.</summary>
    Failed
}

/// <summary>Everything about an agent that a caller reads back.</summary>
/// <param name="Position">Where it is, on the mesh.</param>
/// <param name="Velocity">How it is moving.</param>
/// <param name="DesiredVelocity">How it wanted to move, before avoidance had its say.</param>
/// <param name="Poly">The polygon it is standing on.</param>
/// <param name="Target">Where it was told to go.</param>
/// <param name="State">How that is going.</param>
/// <param name="OffMesh">
///     What it is in the middle of, if it is crossing an off-mesh connection. This is what a game
///     watches to play a ladder or a jump: it appears the frame the agent leaves the ground and stops
///     the frame it lands.
/// </param>
public readonly record struct CrowdAgentState(
    Vector3 Position,
    Vector3 Velocity,
    Vector3 DesiredVelocity,
    NavPolyRef Poly,
    Vector3 Target,
    CrowdTargetState State,
    CrowdOffMeshTraversal? OffMesh = null
);

/// <summary>An agent part-way across an off-mesh connection.</summary>
/// <param name="UserId">Whatever the connection was authored with — which animation to play.</param>
/// <param name="Start">Where it left the surface.</param>
/// <param name="End">Where it will land.</param>
/// <param name="Progress">How far along it is, from 0 to 1.</param>
public readonly record struct CrowdOffMeshTraversal(uint UserId, Vector3 Start, Vector3 End, float Progress);

/// <summary>
///     Agents that follow paths across a <see cref="NavMesh" /> and stay out of each other's way.
/// </summary>
/// <remarks>
///     <para>
///         Four things happen to an agent every update, in this order: it is given a path if it asked
///         for one, it works out which way it would like to go, it is talked out of that by whoever is
///         in the way, and it is moved — across the surface, so that whatever the previous three
///         steps decided, it ends the frame standing somewhere it could have walked to.
///     </para>
///     <para>
///         That last step is the invariant worth stating on its own: <b>an agent cannot leave the
///         mesh.</b> Steering and avoidance produce a wish, and <see cref="NavMeshQuery.MoveAlongSurface" />
///         is what turns the wish into a position. A bug in avoidance makes an agent walk oddly; it
///         cannot make one walk through a wall.
///     </para>
///     <para>
///         Pathfinding is the expensive part and is done only when something changed — a new
///         destination, or a move that ended outside the corridor. Steering is a string-pull over the
///         corridor and costs a few microseconds; that is what runs every frame for every agent.
///     </para>
/// </remarks>
public sealed class Crowd {
    const int MaxNeighbours = 8;
    const int MaxCorners = 4;
    const int SeparationIterations = 4;

    readonly List<Agent> agents = [];
    readonly List<int> freeSlots = [];
    readonly List<int> active = [];
    readonly ProximityGrid grid;
    readonly LocalAvoidance avoidance;
    readonly NavPolyRef[] pathBuffer;

    uint nextGeneration = 1;

    /// <summary>Creates a crowd over a mesh.</summary>
    /// <param name="mesh">The mesh its agents walk on.</param>
    /// <param name="maxPathLength">The longest corridor an agent may hold.</param>
    /// <param name="avoidanceSettings">How the avoidance sampler is weighted.</param>
    /// <exception cref="ArgumentNullException"><paramref name="mesh" /> is null.</exception>
    public Crowd(NavMesh mesh, int maxPathLength = 256, LocalAvoidanceSettings avoidanceSettings = default) {
        ArgumentNullException.ThrowIfNull(mesh);

        Mesh = mesh;
        Query = new(mesh);
        Filter = NavQueryFilter.Default;
        MaxPathLength = maxPathLength;

        avoidance = new(avoidanceSettings);
        grid = new(4f);
        pathBuffer = new NavPolyRef[maxPathLength];
        Paths = new(mesh, maximumPathLength: maxPathLength);
    }

    /// <summary>The mesh the agents walk on.</summary>
    public NavMesh Mesh { get; }

    /// <summary>The query the crowd plans and moves with.</summary>
    public NavMeshQuery Query { get; }

    /// <summary>Which polygons the agents may use.</summary>
    public NavQueryFilter Filter { get; set; }

    /// <summary>The longest corridor an agent may hold.</summary>
    public int MaxPathLength { get; }

    /// <summary>Where the searches happen, a slice at a time.</summary>
    /// <remarks>
    ///     Exposed so a game can watch it — <see cref="NavPathQueue.PendingCount" /> is how many agents
    ///     are waiting to be told where to go, which is a useful thing to put on a debug overlay when
    ///     a crowd looks hesitant.
    /// </remarks>
    public NavPathQueue Paths { get; }

    /// <summary>
    ///     How many polygon expansions the queue may do per update, across every search in flight.
    /// </summary>
    /// <remarks>
    ///     The frame budget for pathfinding, in the only unit that means anything: a polygon expansion
    ///     is about a tenth of a microsecond, so the default is a few tens of microseconds a frame
    ///     however many agents are asking. Raise it for a crowd that must react instantly; lower it
    ///     for one that can afford to think.
    /// </remarks>
    public int PathIterationsPerUpdate { get; set; } = 256;

    /// <summary>How many agents there are.</summary>
    public int AgentCount => active.Count;

    /// <summary>How far off the mesh a position may be and still be found on it.</summary>
    /// <remarks>
    ///     Wider vertically than horizontally, because a caller's idea of where an agent is comes from
    ///     a transform whose origin is at its feet and a mesh that sits a voxel above the floor.
    /// </remarks>
    public Vector3 SearchExtents { get; set; } = new(2f, 4f, 2f);

    /// <summary>Adds an agent.</summary>
    /// <param name="position">Where to put it. Snapped to the nearest point on the mesh.</param>
    /// <param name="parameters">What it is and how it moves.</param>
    /// <returns>Its handle, or <see cref="CrowdAgentHandle.Null" /> if there is no mesh under it.</returns>
    public CrowdAgentHandle AddAgent(Vector3 position, CrowdAgentParams parameters) {
        if (!Query.FindNearestPoly(position, SearchExtents, Filter, out var poly, out var point)) {
            return CrowdAgentHandle.Null;
        }

        int slot;

        if (freeSlots.Count > 0) {
            slot = freeSlots[^1];
            freeSlots.RemoveAt(freeSlots.Count - 1);
        } else {
            slot = agents.Count;
            agents.Add(new(MaxPathLength));
        }

        var agent = agents[slot];
        agent.Generation = nextGeneration++;
        agent.Request = NavPathRequest.Null;
        agent.Active = true;
        agent.Params = parameters;
        agent.Position = point;
        agent.Velocity = Vector3.Zero;
        agent.DesiredVelocity = Vector3.Zero;
        agent.Poly = poly;
        agent.Target = point;

        // Where it stands is where it is going, until it is told otherwise. Cleared rather than left,
        // because a slot is reused and the previous occupant's destination is still in it.
        agent.TargetPoly = poly;
        agent.TargetPoint = point;
        agent.State = CrowdTargetState.None;
        agent.Corridor.Reset(poly, point);

        active.Add(slot);

        return new(slot, agent.Generation);
    }

    /// <summary>Removes an agent.</summary>
    /// <param name="handle">Its handle.</param>
    /// <returns><see langword="false" /> if the handle names no live agent.</returns>
    public bool RemoveAgent(CrowdAgentHandle handle) {
        if (!TryGet(handle, out var agent)) {
            return false;
        }

        Paths.Cancel(agent.Request);
        agent.Request = NavPathRequest.Null;
        agent.Active = false;
        active.Remove(handle.Index);
        freeSlots.Add(handle.Index);

        return true;
    }

    /// <summary>Tells an agent where to go.</summary>
    /// <param name="handle">The agent.</param>
    /// <param name="target">Where. Snapped to the nearest point on the mesh.</param>
    /// <returns><see langword="false" /> if the handle names no live agent.</returns>
    /// <remarks>
    ///     The destination's polygon is resolved here rather than when the search is submitted, because
    ///     a destination is set once and planned for however many times it takes — a refused request, a
    ///     corridor gone bad — and the lookup is the same answer every time. A caller that already has
    ///     the polygon should use <see cref="SetTarget(CrowdAgentHandle, NavPolyRef, Vector3)" /> and
    ///     skip it entirely.
    /// </remarks>
    public bool SetTarget(CrowdAgentHandle handle, Vector3 target) {
        if (!TryGet(handle, out var agent)) {
            return false;
        }

        var found = Query.FindNearestPoly(target, SearchExtents, Filter, out var poly, out var point);

        Retarget(agent, found ? poly : NavPolyRef.Null, point, target);

        return true;
    }

    /// <summary>Tells an agent to go to a polygon it has already been given.</summary>
    /// <param name="handle">The agent.</param>
    /// <param name="poly">The polygon the destination is on.</param>
    /// <param name="point">The point on it.</param>
    /// <returns><see langword="false" /> if the handle names no live agent.</returns>
    /// <remarks>
    ///     <para>
    ///         For the caller that knows. A patrol point resolved once when the level loaded, a
    ///         destination taken from another agent's <see cref="CrowdAgentState.Poly" />, a waypoint
    ///         the game already snapped — all of these have been through a nearest-polygon search
    ///         already, and doing it again per agent per retarget is the single largest cost left in a
    ///         crowd that changes its mind all at once.
    ///     </para>
    ///     <para>
    ///         The reference is checked when the path is planned, not here, so a polygon whose tile is
    ///         later unloaded or rebuilt falls back to a search rather than searching from nothing.
    ///     </para>
    /// </remarks>
    public bool SetTarget(CrowdAgentHandle handle, NavPolyRef poly, Vector3 point) {
        if (!TryGet(handle, out var agent)) {
            return false;
        }

        Retarget(agent, poly, point, point);

        return true;
    }

    /// <summary>Points an agent at a new destination, whichever way it was named.</summary>
    void Retarget(Agent agent, NavPolyRef poly, Vector3 point, Vector3 target) {
        // Whatever was being searched for is now the wrong question.
        Paths.Cancel(agent.Request);
        agent.Request = NavPathRequest.Null;

        agent.Target = target;
        agent.TargetPoly = poly;
        agent.TargetPoint = point;
        agent.State = CrowdTargetState.Requested;
    }

    /// <summary>Tells an agent to stop.</summary>
    /// <param name="handle">The agent.</param>
    /// <returns><see langword="false" /> if the handle names no live agent.</returns>
    public bool ClearTarget(CrowdAgentHandle handle) {
        if (!TryGet(handle, out var agent)) {
            return false;
        }

        Paths.Cancel(agent.Request);
        agent.Request = NavPathRequest.Null;
        agent.State = CrowdTargetState.None;
        agent.Target = agent.Position;
        agent.TargetPoly = agent.Poly;
        agent.TargetPoint = agent.Position;
        agent.OffMeshTotal = 0f;
        agent.Corridor.Reset(agent.Poly, agent.Position);

        return true;
    }

    /// <summary>The handle for a slot, if it holds a live agent.</summary>
    /// <param name="index">The slot.</param>
    /// <param name="handle">Its handle.</param>
    /// <returns><see langword="false" /> if the slot is empty.</returns>
    /// <remarks>
    ///     For a caller that has been holding slots rather than handles — the ECS bridge, which keys
    ///     its own bookkeeping by slot and needs the current generation to remove an agent.
    /// </remarks>
    public bool TryGetHandle(int index, out CrowdAgentHandle handle) {
        if ((uint)index >= (uint)agents.Count || !agents[index].Active) {
            handle = CrowdAgentHandle.Null;

            return false;
        }

        handle = new(index, agents[index].Generation);

        return true;
    }

    /// <summary>Every live agent, for a caller that wants to walk them — a debug view, a save.</summary>
    /// <remarks>
    ///     A struct enumerator rather than an iterator method, so that walking the crowd does not
    ///     allocate. The order is the order agents were added, with removed slots skipped.
    /// </remarks>
    public AgentEnumerator Agents => new(this);

    /// <summary>Reads an agent's parameters back.</summary>
    /// <param name="handle">The agent.</param>
    /// <param name="parameters">What it is and how it moves.</param>
    /// <returns><see langword="false" /> if the handle names no live agent.</returns>
    public bool TryGetParams(CrowdAgentHandle handle, out CrowdAgentParams parameters) {
        if (!TryGet(handle, out var agent)) {
            parameters = default;

            return false;
        }

        parameters = agent.Params;

        return true;
    }

    /// <summary>Changes an agent's parameters.</summary>
    /// <param name="handle">The agent.</param>
    /// <param name="parameters">What it is and how it moves.</param>
    /// <returns><see langword="false" /> if the handle names no live agent.</returns>
    /// <remarks>
    ///     The radius is not rebaked into anything, so widening an agent past what the mesh was baked
    ///     for does not make it collide with walls — it makes it clip through corners, because the
    ///     mesh's promise is about the radius the bake was given.
    /// </remarks>
    public bool SetParams(CrowdAgentHandle handle, CrowdAgentParams parameters) {
        if (!TryGet(handle, out var agent)) {
            return false;
        }

        agent.Params = parameters;

        return true;
    }

    /// <summary>Reads an agent back.</summary>
    /// <param name="handle">The agent.</param>
    /// <param name="state">Where it is and what it is doing.</param>
    /// <returns><see langword="false" /> if the handle names no live agent.</returns>
    public bool TryGetState(CrowdAgentHandle handle, out CrowdAgentState state) {
        if (!TryGet(handle, out var agent)) {
            state = default;

            return false;
        }

        state = new(
            agent.Position,
            agent.Velocity,
            agent.DesiredVelocity,
            agent.Poly,
            agent.Target,
            agent.State,
            agent.OffMeshTotal > 0f
                ? new CrowdOffMeshTraversal(
                    agent.OffMeshUserId,
                    agent.OffMeshStart,
                    agent.OffMeshEnd,
                    1f - (agent.OffMeshRemaining / agent.OffMeshTotal)
                )
                : null
        );

        return true;
    }

    /// <summary>Moves an agent somewhere without walking it there.</summary>
    /// <param name="handle">The agent.</param>
    /// <param name="position">Where to put it. Snapped to the mesh.</param>
    /// <returns><see langword="false" /> if the handle names no live agent, or there is no mesh there.</returns>
    public bool Teleport(CrowdAgentHandle handle, Vector3 position) {
        if (!TryGet(handle, out var agent)) {
            return false;
        }

        if (!Query.FindNearestPoly(position, SearchExtents, Filter, out var poly, out var point)) {
            return false;
        }

        agent.Position = point;
        agent.Poly = poly;
        agent.Velocity = Vector3.Zero;
        agent.OffMeshTotal = 0f;
        agent.Corridor.Reset(poly, point);

        if (agent.State is CrowdTargetState.Following or CrowdTargetState.Arrived) {
            agent.State = CrowdTargetState.Requested;
        }

        return true;
    }

    /// <summary>Steers and moves every agent.</summary>
    /// <param name="deltaTime">How much time has passed. Zero or less does nothing.</param>
    public void Update(float deltaTime) {
        if (deltaTime <= 0f || active.Count == 0) {
            return;
        }

        Paths.Update(PathIterationsPerUpdate);

        Plan();
        Populate();
        Traverse(deltaTime);
        Steer(deltaTime);
        Move(deltaTime);
    }

    /// <summary>Carries the agents that are on a connection across it, and starts the ones that arrive.</summary>
    /// <remarks>
    ///     <para>
    ///         A connection is crossed over time rather than stepped over instantly, and the time is
    ///         the whole point: it is what a ladder animation plays during, and an agent that vanished
    ///         from the bottom of a ladder and appeared at the top would need the game to fake it back.
    ///     </para>
    ///     <para>
    ///         An agent in transit is skipped by steering, avoidance and separation. It is not on the
    ///         surface — nothing can be in its way, and there is nothing for it to be talked out of.
    ///     </para>
    /// </remarks>
    void Traverse(float deltaTime) {
        foreach (var slot in active) {
            var agent = agents[slot];

            if (agent.OffMeshTotal > 0f) {
                agent.OffMeshRemaining -= deltaTime;

                if (agent.OffMeshRemaining > 0f) {
                    var progress = 1f - (agent.OffMeshRemaining / agent.OffMeshTotal);
                    var moved = Vector3.Lerp(agent.OffMeshStart, agent.OffMeshEnd, progress);

                    agent.Velocity = (moved - agent.Position) / deltaTime;
                    agent.Position = moved;

                    continue;
                }

                agent.Position = agent.OffMeshEnd;
                agent.Poly = agent.Corridor.FirstPoly;
                agent.Velocity = Vector3.Zero;
                agent.OffMeshTotal = 0f;
                agent.OffMeshRemaining = 0f;

                continue;
            }

            if (agent.State != CrowdTargetState.Following) {
                continue;
            }

            var reach = MathF.Max(agent.Params.Radius, 0.25f);

            if (!agent.Corridor.TryUseOffMeshConnection(Mesh, reach, out var connection, out var entry, out var exit)) {
                continue;
            }

            Mesh.TryGetOffMeshConnection(connection, out var authored);

            agent.OffMeshStart = entry;
            agent.OffMeshEnd = exit;
            agent.OffMeshUserId = authored.UserId;

            // Long enough to walk it at the agent's own speed, so a two-metre drop is quick and a
            // twenty-metre zip line is not. Never zero: a connection with coincident ends would
            // otherwise divide by it.
            agent.OffMeshTotal = MathF.Max(Vector3.Distance(entry, exit) / MathF.Max(agent.Params.MaxSpeed, 0.01f), 0.05f);
            agent.OffMeshRemaining = agent.OffMeshTotal;
            agent.Position = entry;
        }
    }

    /// <summary>Submits the searches that are wanted, and collects the ones that are done.</summary>
    /// <remarks>
    ///     <para>
    ///         An agent that has asked for a path does not get one this update, and that is the point:
    ///         the search goes into <see cref="Paths" /> and comes back a few updates later, so a
    ///         crowd that is all given a new destination at once costs the frame a budget rather than
    ///         a search per agent. Two hundred and fifty-six agents replanning together is three and a
    ///         half milliseconds if it is done inline, which is more than everything else the crowd
    ///         does put together.
    ///     </para>
    ///     <para>
    ///         In the meantime the agent keeps walking the corridor it already had. That is the right
    ///         behaviour and not a compromise: it was going somewhere sensible a moment ago, and
    ///         stopping dead while it thinks is what makes a crowd look like a computer program.
    ///     </para>
    /// </remarks>
    void Plan() {
        foreach (var slot in active) {
            var agent = agents[slot];

            if (!agent.Request.IsNull) {
                Collect(agent);

                continue;
            }

            // An agent on a connection has a corridor that already starts at the far end. Replanning
            // from a position half-way up a ladder would search from the polygon it is about to land
            // on, which is right — but the plan would then be thrown away when it lands anyway.
            if (agent.State != CrowdTargetState.Requested || agent.OffMeshTotal > 0f) {
                continue;
            }

            // The agent is standing on the mesh and knows which polygon: that is what a corridor is,
            // and it has been kept current by every move since. Searching for it again was the whole
            // cost of a retarget storm — two lookups an agent, at about a microsecond each, which on
            // an eighty-metre level came to more than the searches they were preparing for.
            var start = agent.Poly;
            var startPoint = agent.Position;

            if (!IsUsable(start) && !Query.FindNearestPoly(startPoint, SearchExtents, Filter, out start, out startPoint)) {
                agent.State = CrowdTargetState.Failed;

                continue;
            }

            var end = agent.TargetPoly;
            var endPoint = agent.TargetPoint;

            // The remembered destination, unless its tile has been unloaded or replaced under it —
            // which a rebuilt tile does deliberately, by changing the salt every reference to it
            // carries. The fallback is the old behaviour, so a destination set before its tile
            // loaded still resolves the moment it can.
            if (!IsUsable(end) && !Query.FindNearestPoly(agent.Target, SearchExtents, Filter, out end, out endPoint)) {
                agent.State = CrowdTargetState.Failed;

                continue;
            }

            agent.TargetPoly = end;
            agent.TargetPoint = endPoint;

            var request = Paths.Submit(start, end, startPoint, endPoint, Filter);

            if (request.IsNull) {
                // The queue is full. The agent keeps whatever corridor it has and asks again next
                // update, which is what a refusal is for.
                continue;
            }

            agent.Request = request;
            agent.RequestStart = start;
            agent.RequestStartPosition = startPoint;
            agent.RequestEndPosition = endPoint;
        }
    }

    /// <summary>Whether a remembered polygon can still be searched from or to.</summary>
    /// <remarks>
    ///     Both halves matter. The reference has to still name a polygon — a tile that was unloaded or
    ///     rebuilt takes its polygons' salt with it, so a stale reference fails here rather than
    ///     resolving to whatever now occupies the slot. And the filter has to still accept it, because
    ///     a door that was closed with <see cref="NavMesh.SetPolyFlags" /> is a polygon that exists and
    ///     may not be used.
    /// </remarks>
    bool IsUsable(NavPolyRef poly) => Mesh.TryGetPolyAttributes(poly, out _, out var flags) && Filter.Passes(flags);

    /// <summary>Takes a finished search and turns it into the agent's corridor.</summary>
    void Collect(Agent agent) {
        if (Paths.GetState(agent.Request) != NavPathRequestState.Ready) {
            return;
        }

        if (!Paths.TryTakeResult(agent.Request, pathBuffer, out var count, out var status)) {
            agent.Request = NavPathRequest.Null;

            return;
        }

        agent.Request = NavPathRequest.Null;

        if (status == NavPathStatus.Failed || count == 0) {
            agent.State = CrowdTargetState.Failed;

            return;
        }

        // The corridor starts where the search did, which is where the agent was when it asked — and
        // it has been walking since. So the corridor is walked up to where the agent actually is
        // rather than the agent being put back where the corridor starts, which would yank it
        // backwards by however far it got while the queue was busy.
        agent.Corridor.Reset(agent.RequestStart, agent.RequestStartPosition);
        agent.Corridor.SetPath(agent.RequestEndPosition, pathBuffer.AsSpan(0, count));
        agent.Corridor.MovePosition(agent.Position, Query, Filter);

        agent.Position = agent.Corridor.Position;
        agent.Poly = agent.Corridor.FirstPoly;
        agent.State = CrowdTargetState.Following;
    }

    /// <summary>Rebuilds the neighbour grid.</summary>
    void Populate() {
        grid.Clear();

        foreach (var slot in active) {
            // Agents crossing a connection are not on the surface, so they are not in anybody's way
            // and nobody has to steer around them.
            if (agents[slot].OffMeshTotal <= 0f) {
                grid.Add(slot, agents[slot].Position);
            }
        }
    }

    /// <summary>Works out what velocity each agent would like, and what it is allowed.</summary>
    void Steer(float deltaTime) {
        Span<NavPathPoint> corners = stackalloc NavPathPoint[MaxCorners];
        Span<int> nearby = stackalloc int[MaxNeighbours * 4];
        Span<AvoidanceNeighbour> neighbours = stackalloc AvoidanceNeighbour[MaxNeighbours];

        foreach (var slot in active) {
            var agent = agents[slot];

            // Mid-connection: Traverse owns this agent's position this frame.
            if (agent.OffMeshTotal > 0f) {
                continue;
            }

            if (agent.State is not CrowdTargetState.Following) {
                agent.DesiredVelocity = Vector3.Zero;
                agent.Velocity = Approach(agent.Velocity, Vector3.Zero, agent.Params.MaxAcceleration * deltaTime);

                continue;
            }

            agent.Corridor.Optimize(Query, Filter);

            var cornerCount = agent.Corridor.FindCorners(Query, corners);
            var toTarget = NavGeometry.Distance2D(agent.Position, agent.Corridor.Target);

            if (cornerCount == 0 || toTarget <= agent.Params.ArrivalRadius) {
                agent.State = toTarget <= MathF.Max(agent.Params.ArrivalRadius, 0.01f)
                    ? CrowdTargetState.Arrived
                    : CrowdTargetState.Requested;

                agent.DesiredVelocity = Vector3.Zero;
                agent.Velocity = Vector3.Zero;

                continue;
            }

            // The first corner is where the agent is standing; the one after it is where it is going.
            var steerTarget = cornerCount > 1 ? corners[1].Position : corners[0].Position;
            var direction = steerTarget - agent.Position;
            direction = new(direction.X, 0f, direction.Z);

            var length = direction.Length();
            var speed = agent.Params.MaxSpeed;

            if (agent.Params.SlowdownRadius > 0f && toTarget < agent.Params.SlowdownRadius) {
                speed *= toTarget / agent.Params.SlowdownRadius;
            }

            agent.DesiredVelocity = length > 1e-4f ? direction / length * speed : Vector3.Zero;

            var wanted = agent.DesiredVelocity;

            if (agent.Params.AvoidanceEnabled) {
                var found = Neighbours(slot, agent, nearby, neighbours);

                if (found > 0) {
                    wanted = avoidance.Sample(
                        agent.Position,
                        agent.Params.Radius,
                        agent.Velocity,
                        agent.DesiredVelocity,
                        agent.Params.MaxSpeed,
                        neighbours[..found]
                    );
                }
            }

            agent.Velocity = Approach(agent.Velocity, wanted, agent.Params.MaxAcceleration * deltaTime);
        }
    }

    /// <summary>Integrates, pushes overlapping agents apart, and puts everybody back on the mesh.</summary>
    void Move(float deltaTime) {
        Span<int> nearby = stackalloc int[MaxNeighbours * 4];

        foreach (var slot in active) {
            var agent = agents[slot];
            agent.Wanted = agent.Position + (agent.Velocity * deltaTime);
        }

        // Everything below is about the surface, and an agent on a connection is not on it.

        // Overlap recovery, iterated a few times so that an agent squeezed between two others is
        // pushed by both rather than by whichever was considered last.
        for (var iteration = 0; iteration < SeparationIterations; iteration++) {
            foreach (var slot in active) {
                var agent = agents[slot];

                if (agent.Params.SeparationWeight <= 0f || agent.OffMeshTotal > 0f) {
                    continue;
                }

                var count = grid.Query(agent.Wanted, agent.Params.Radius * 4f, nearby);
                var push = Vector3.Zero;

                for (var index = 0; index < count; index++) {
                    if (nearby[index] == slot) {
                        continue;
                    }

                    var other = agents[nearby[index]];
                    var offset = agent.Wanted - other.Wanted;
                    offset = new(offset.X, 0f, offset.Z);

                    var distance = offset.Length();
                    var wanted = agent.Params.Radius + other.Params.Radius;

                    if (distance >= wanted) {
                        continue;
                    }

                    // Two agents exactly on top of each other have no direction to be pushed along,
                    // so one is invented from their slots — deterministic, and different for the two
                    // of them, which is all that is needed to break the tie.
                    var direction = distance > 1e-4f
                        ? offset / distance
                        : new Vector3(slot < nearby[index] ? 1f : -1f, 0f, 0f);

                    push += direction * ((wanted - distance) * 0.5f * agent.Params.SeparationWeight);
                }

                agent.Wanted += push * 0.5f;
            }
        }

        foreach (var slot in active) {
            var agent = agents[slot];

            if (agent.OffMeshTotal > 0f) {
                continue;
            }

            if (!agent.Corridor.MovePosition(agent.Wanted, Query, Filter) && agent.State == CrowdTargetState.Following) {
                // The agent is somewhere its corridor does not go through. It has still moved, and it
                // is still on the mesh; what it needs is a new path, next update.
                agent.State = CrowdTargetState.Requested;
            }

            var moved = agent.Corridor.Position;

            // The velocity the agent actually achieved, which is what avoidance should reason from
            // next frame — not the one it was given, which a wall may have eaten.
            agent.Velocity = new((moved.X - agent.Position.X) / deltaTime, 0f, (moved.Z - agent.Position.Z) / deltaTime);
            agent.Position = moved;
            agent.Poly = agent.Corridor.FirstPoly;
        }
    }

    int Neighbours(int slot, Agent agent, Span<int> scratch, Span<AvoidanceNeighbour> neighbours) {
        var range = MathF.Max(agent.Params.Radius * 6f, 2f);
        var found = grid.Query(agent.Position, range, scratch);
        var count = 0;

        for (var index = 0; index < found && count < neighbours.Length; index++) {
            if (scratch[index] == slot) {
                continue;
            }

            var other = agents[scratch[index]];

            if (NavGeometry.DistanceSquared2D(agent.Position, other.Position) > range * range) {
                continue;
            }

            neighbours[count++] = new(other.Position, other.Velocity, other.Params.Radius);
        }

        return count;
    }

    static Vector3 Approach(Vector3 from, Vector3 to, float maximumChange) {
        var delta = to - from;
        var length = delta.Length();

        return length <= maximumChange || length < 1e-6f ? to : from + (delta / length * maximumChange);
    }

    bool TryGet(CrowdAgentHandle handle, out Agent agent) {
        agent = null!;

        if ((uint)handle.Index >= (uint)agents.Count) {
            return false;
        }

        var candidate = agents[handle.Index];

        if (!candidate.Active || candidate.Generation != handle.Generation) {
            return false;
        }

        agent = candidate;

        return true;
    }

    /// <summary>Walks a crowd's live agents without allocating.</summary>
    public struct AgentEnumerator {
        readonly Crowd crowd;
        int position;

        internal AgentEnumerator(Crowd crowd) {
            this.crowd = crowd;
            position = -1;
            Current = CrowdAgentHandle.Null;
        }

        /// <summary>The agent the enumerator is on.</summary>
        public CrowdAgentHandle Current { get; private set; }

        /// <summary>So that this can be used in a <c>foreach</c>.</summary>
        /// <returns>Itself.</returns>
        public AgentEnumerator GetEnumerator() => this;

        /// <summary>Moves to the next agent.</summary>
        /// <returns><see langword="false" /> when there are none left.</returns>
        public bool MoveNext() {
            while (++position < crowd.active.Count) {
                if (crowd.TryGetHandle(crowd.active[position], out var handle)) {
                    Current = handle;

                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>One agent's state. A class, because the crowd hands the same instance around.</summary>
    sealed class Agent(int maxPathLength) {
        public PathCorridor Corridor { get; } = new(maxPathLength);

        public bool Active { get; set; }

        public uint Generation { get; set; }

        public CrowdAgentParams Params { get; set; }

        public Vector3 Position { get; set; }

        public Vector3 Velocity { get; set; }

        public Vector3 DesiredVelocity { get; set; }

        public Vector3 Target { get; set; }

        public Vector3 Wanted { get; set; }

        public NavPolyRef Poly { get; set; }

        /// <summary>The polygon <see cref="Target" /> is on, resolved when it was set.</summary>
        /// <remarks>
        ///     Remembered rather than looked up per plan, because a destination is set once and
        ///     planned for repeatedly — every refused request, every replan after a corridor goes
        ///     bad. Held with the point on it, because that is the other half of the same answer.
        /// </remarks>
        public NavPolyRef TargetPoly { get; set; }

        public Vector3 TargetPoint { get; set; }

        public CrowdTargetState State { get; set; }

        public float OffMeshRemaining { get; set; }

        public float OffMeshTotal { get; set; }

        public Vector3 OffMeshStart { get; set; }

        public Vector3 OffMeshEnd { get; set; }

        public uint OffMeshUserId { get; set; }

        public NavPathRequest Request { get; set; } = NavPathRequest.Null;

        public NavPolyRef RequestStart { get; set; }

        public Vector3 RequestStartPosition { get; set; }

        public Vector3 RequestEndPosition { get; set; }
    }
}
