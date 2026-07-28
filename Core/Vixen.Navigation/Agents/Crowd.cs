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
public readonly record struct CrowdAgentState(
    Vector3 Position,
    Vector3 Velocity,
    Vector3 DesiredVelocity,
    NavPolyRef Poly,
    Vector3 Target,
    CrowdTargetState State
);

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
    }

    /// <summary>The mesh the agents walk on.</summary>
    public NavMesh Mesh { get; }

    /// <summary>The query the crowd plans and moves with.</summary>
    public NavMeshQuery Query { get; }

    /// <summary>Which polygons the agents may use.</summary>
    public NavQueryFilter Filter { get; set; }

    /// <summary>The longest corridor an agent may hold.</summary>
    public int MaxPathLength { get; }

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
        agent.Active = true;
        agent.Params = parameters;
        agent.Position = point;
        agent.Velocity = Vector3.Zero;
        agent.DesiredVelocity = Vector3.Zero;
        agent.Poly = poly;
        agent.Target = point;
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

        agent.Active = false;
        active.Remove(handle.Index);
        freeSlots.Add(handle.Index);

        return true;
    }

    /// <summary>Tells an agent where to go.</summary>
    /// <param name="handle">The agent.</param>
    /// <param name="target">Where. Snapped to the nearest point on the mesh when the path is planned.</param>
    /// <returns><see langword="false" /> if the handle names no live agent.</returns>
    public bool SetTarget(CrowdAgentHandle handle, Vector3 target) {
        if (!TryGet(handle, out var agent)) {
            return false;
        }

        agent.Target = target;
        agent.State = CrowdTargetState.Requested;

        return true;
    }

    /// <summary>Tells an agent to stop.</summary>
    /// <param name="handle">The agent.</param>
    /// <returns><see langword="false" /> if the handle names no live agent.</returns>
    public bool ClearTarget(CrowdAgentHandle handle) {
        if (!TryGet(handle, out var agent)) {
            return false;
        }

        agent.State = CrowdTargetState.None;
        agent.Target = agent.Position;
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

    /// <summary>Reads an agent back.</summary>
    /// <param name="handle">The agent.</param>
    /// <param name="state">Where it is and what it is doing.</param>
    /// <returns><see langword="false" /> if the handle names no live agent.</returns>
    public bool TryGetState(CrowdAgentHandle handle, out CrowdAgentState state) {
        if (!TryGet(handle, out var agent)) {
            state = default;

            return false;
        }

        state = new(agent.Position, agent.Velocity, agent.DesiredVelocity, agent.Poly, agent.Target, agent.State);

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

        Plan();
        Populate();
        Steer(deltaTime);
        Move(deltaTime);
    }

    /// <summary>Gives a path to everybody who asked for one.</summary>
    void Plan() {
        foreach (var slot in active) {
            var agent = agents[slot];

            if (agent.State != CrowdTargetState.Requested) {
                continue;
            }

            if (!Query.FindNearestPoly(agent.Position, SearchExtents, Filter, out var start, out var startPoint) ||
                !Query.FindNearestPoly(agent.Target, SearchExtents, Filter, out var end, out var endPoint)) {
                agent.State = CrowdTargetState.Failed;

                continue;
            }

            var status = Query.FindPath(start, end, startPoint, endPoint, Filter, pathBuffer, out var count);

            if (status == NavPathStatus.Failed || count == 0) {
                agent.State = CrowdTargetState.Failed;

                continue;
            }

            agent.Position = startPoint;
            agent.Poly = start;
            agent.Corridor.Reset(start, startPoint);
            agent.Corridor.SetPath(endPoint, pathBuffer.AsSpan(0, count));
            agent.State = CrowdTargetState.Following;
        }
    }

    /// <summary>Rebuilds the neighbour grid.</summary>
    void Populate() {
        grid.Clear();

        foreach (var slot in active) {
            grid.Add(slot, agents[slot].Position);
        }
    }

    /// <summary>Works out what velocity each agent would like, and what it is allowed.</summary>
    void Steer(float deltaTime) {
        Span<NavPathPoint> corners = stackalloc NavPathPoint[MaxCorners];
        Span<int> nearby = stackalloc int[MaxNeighbours * 4];
        Span<AvoidanceNeighbour> neighbours = stackalloc AvoidanceNeighbour[MaxNeighbours];

        foreach (var slot in active) {
            var agent = agents[slot];

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

        // Overlap recovery, iterated a few times so that an agent squeezed between two others is
        // pushed by both rather than by whichever was considered last.
        for (var iteration = 0; iteration < SeparationIterations; iteration++) {
            foreach (var slot in active) {
                var agent = agents[slot];

                if (agent.Params.SeparationWeight <= 0f) {
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

        public CrowdTargetState State { get; set; }
    }
}
