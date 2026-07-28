// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Transforms;
using Vixen.Navigation.Agents;

namespace Vixen.Navigation.Ecs;

/// <summary>
///     Joins the entities that have a <see cref="NavigationAgent" /> to a <see cref="Crowd" />, runs
///     it, and puts the results back on the entities.
/// </summary>
/// <remarks>
///     <para>
///         The crowd is the simulation and this is the seam. Everything interesting — paths, steering,
///         avoidance — happens in <see cref="Agents.Crowd" />, which knows nothing about entities and
///         can be used without a world at all; what this adds is that the set of agents is a query
///         rather than a list, so an entity that is spawned joins the crowd and an entity that is
///         destroyed leaves it, without anybody calling anything.
///     </para>
///     <para>
///         <b>It writes <see cref="LocalTransform" />, so an agent should be a root entity.</b> A
///         parented one would have its world-space position written into a local-space field, which
///         the transform pass would then compose with its parent's — the agent would be somewhere
///         else entirely. Characters that walk are roots in practice; the alternative is a full
///         inverse-parent solve for a case that does not arise.
///     </para>
///     <para>
///         Runs in <see cref="SystemPhase.Update" />, so the transforms it writes are resolved by the
///         transform pass in <see cref="SystemPhase.PreRender" /> in the same frame.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.Update)]
public sealed class NavigationSystem : SystemBase, IDeclaredAccess {
    readonly QueryDescription agents = new QueryDescription().WithAll<NavigationAgent, LocalTransform>();
    readonly QueryDescription destinations = new QueryDescription().WithAll<NavigationAgent, NavigationDestination>();
    readonly QueryDescription states = new QueryDescription().WithAll<NavigationAgent, NavigationState>();

    readonly Dictionary<int, Entity> owners = [];
    readonly HashSet<int> seen = [];
    readonly List<int> departed = [];

    /// <summary>Creates the system over a crowd.</summary>
    /// <param name="crowd">The crowd its agents live in.</param>
    /// <exception cref="ArgumentNullException"><paramref name="crowd" /> is null.</exception>
    public NavigationSystem(Crowd crowd) {
        ArgumentNullException.ThrowIfNull(crowd);

        Crowd = crowd;
    }

    /// <summary>The crowd being driven.</summary>
    public Crowd Crowd { get; }

    /// <inheritdoc />
    /// <remarks>
    ///     Declared rather than attributed, for the reason <c>TransformSystem</c> gives: naming a
    ///     component in a generic call is what assigns it an id, and an attribute can only look one
    ///     up.
    /// </remarks>
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<NavigationDestination>()
        .Write<NavigationAgent>()
        .Write<NavigationState>()
        .Write<LocalTransform>()
        .Build();

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Step(context.World, context.Time.DeltaSeconds);

        return dependency;
    }

    /// <summary>Runs one step of the crowd against a world.</summary>
    /// <param name="world">The world.</param>
    /// <param name="deltaTime">How much time has passed.</param>
    /// <remarks>Public so a test or a tool can step the crowd without standing up a runner.</remarks>
    public void Step(World world, float deltaTime) {
        ArgumentNullException.ThrowIfNull(world);

        Join(world);
        Retarget(world);

        Crowd.Update(deltaTime);

        Publish(world);
        Reap(world);
    }

    /// <summary>Gives every entity that does not have an agent one.</summary>
    void Join(World world) {
        seen.Clear();

        foreach (var chunk in world.Chunks(agents)) {
            var values = chunk.Values<NavigationAgent>();
            var transforms = chunk.ReadValues<LocalTransform>();
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                ref var agent = ref values[index];

                // Rejoined rather than assumed live: the crowd may have been reset, or the handle may
                // have come in on a prefab or a save file, where it names an agent that never existed
                // in this run.
                if (!agent.Handle.IsNull && Crowd.TryGetState(agent.Handle, out _)) {
                    seen.Add(agent.Handle.Index);

                    continue;
                }

                var handle = Crowd.AddAgent(transforms[index].Position, Parameters(in agent));

                agent.Handle = handle;

                if (!handle.IsNull) {
                    owners[handle.Index] = entities[index];
                    seen.Add(handle.Index);
                }
            }
        }
    }

    /// <summary>Passes on the destinations that have changed since the crowd was last told.</summary>
    /// <remarks>
    ///     <para>
    ///         Compared against what the crowd already holds rather than filtered on the ECS change
    ///         version. The change filter is the cheaper mechanism and the wrong one here: it answers
    ///         "written since version V", which is only the same question as "different from what the
    ///         crowd was told" when every writer runs in a different phase from this system. A tool, a
    ///         test or an editor stepping this by hand writes in the same version epoch it steps in,
    ///         and the filter would silently answer "nothing changed".
    ///     </para>
    ///     <para>
    ///         The comparison costs one vector compare per agent per frame against a crowd update that
    ///         is already doing a path-following pass over the same agents, so what it buys — a
    ///         destination component that means what it says wherever it is written from — is free.
    ///     </para>
    /// </remarks>
    void Retarget(World world) {
        foreach (var chunk in world.Chunks(destinations)) {
            var values = chunk.Values<NavigationAgent>();
            var targets = chunk.ReadValues<NavigationDestination>();

            for (var index = 0; index < chunk.Count; index++) {
                ref var agent = ref values[index];

                if (!Crowd.TryGetState(agent.Handle, out var state)) {
                    continue;
                }

                // A version bump is a replan with the destination where it was — what a caller does
                // when the world changed rather than the plan.
                if (state.State != CrowdTargetState.None &&
                    state.Target == targets[index].Value &&
                    agent.AppliedDestination == targets[index].Version) {
                    continue;
                }

                Crowd.SetTarget(agent.Handle, targets[index].Value);
                agent.AppliedDestination = targets[index].Version;
            }
        }
    }

    /// <summary>Writes where everybody ended up back onto the entities.</summary>
    void Publish(World world) {
        foreach (var chunk in world.Chunks(agents)) {
            var values = chunk.ReadValues<NavigationAgent>();
            var transforms = chunk.Values<LocalTransform>();

            for (var index = 0; index < chunk.Count; index++) {
                if (Crowd.TryGetState(values[index].Handle, out var state)) {
                    transforms[index].Position = state.Position;
                }
            }
        }

        foreach (var chunk in world.Chunks(states)) {
            var values = chunk.ReadValues<NavigationAgent>();
            var published = chunk.Values<NavigationState>();

            for (var index = 0; index < chunk.Count; index++) {
                if (!Crowd.TryGetState(values[index].Handle, out var state)) {
                    continue;
                }

                published[index].Velocity = state.Velocity;
                published[index].Position = state.Position;
                published[index].Target = state.State;
            }
        }
    }

    /// <summary>Removes the agents whose entities are gone.</summary>
    /// <remarks>
    ///     By absence rather than by notification. An entity can leave the query by being destroyed,
    ///     by having its component removed, or by being moved to another world, and none of those is
    ///     something the ECS calls anybody about — so what is checked is which agents were <i>not</i>
    ///     visited this step.
    /// </remarks>
    void Reap(World world) {
        departed.Clear();

        foreach (var (index, entity) in owners) {
            if (!seen.Contains(index) || !world.IsAlive(entity)) {
                departed.Add(index);
            }
        }

        foreach (var index in departed) {
            if (Crowd.TryGetHandle(index, out var handle)) {
                Crowd.RemoveAgent(handle);
            }

            owners.Remove(index);
        }
    }

    static CrowdAgentParams Parameters(ref readonly NavigationAgent agent) => new() {
        Radius = agent.Radius > 0 ? agent.Radius : 0.6f,
        Height = agent.Height > 0 ? agent.Height : 2f,
        MaxSpeed = agent.MaxSpeed > 0 ? agent.MaxSpeed : 3.5f,
        MaxAcceleration = agent.MaxAcceleration > 0 ? agent.MaxAcceleration : 8f,
        AvoidanceEnabled = agent.AvoidanceEnabled
    };
}
