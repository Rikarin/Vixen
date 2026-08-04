// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Navigation;

namespace Vixen.Ai.Nodes;

/// <summary>The sensors that need to know where things are.</summary>
/// <remarks>
///     <para>
///         doc 37 § D13's four kinds, given the two implementations apiece that need a transform or a
///         navmesh — which is why they are here and not in <c>Vixen.Ai</c>. The shipped ones there are
///         delegates and constants; these are the shapes a project reaches for first and would
///         otherwise write itself.
///     </para>
///     <para>
///         ⚠ <b>A global sensor gets one <c>foreach</c> over the world and a local one gets a
///         <c>foreach</c> per agent, and that is the whole of § D13.</b> "Where is the town square" is
///         one query for a thousand villagers; "which apple is nearest <i>me</i>" is a thousand. Both
///         are here so the difference is a thing an author picks rather than a thing they discover.
///     </para>
/// </remarks>
public static class WorldSensors {
    /// <summary>The nearest entity carrying a component, to this agent.</summary>
    /// <typeparam name="T">The component.</typeparam>
    /// <param name="range">How far to look, or zero for the whole world.</param>
    /// <returns>The sensor.</returns>
    public static ITargetSensor Nearest<T>(float range = 0f) => new NearestSensor<T>(range);

    /// <summary>How far the agent is from the nearest entity carrying a component, in metres.</summary>
    /// <typeparam name="T">The component.</typeparam>
    /// <param name="far">What to write when there is none. Defaults to a very long way.</param>
    /// <returns>The sensor.</returns>
    /// <remarks>
    ///     ⚠ <b>A distance with nothing to measure to is written as <paramref name="far" /> rather
    ///     than left alone or written as zero.</b> Zero means "right here", which for "how far is the
    ///     nearest wolf" is the opposite of the truth, and leaving the key alone means acting on a
    ///     measurement that stopped being taken.
    /// </remarks>
    public static ILocalWorldSensor DistanceToNearest<T>(float far = 1000f) => new DistanceSensor<T>(far);

    /// <summary>The centre of every entity carrying a component — a crowd's middle, a fire's front.</summary>
    /// <typeparam name="T">The component.</typeparam>
    /// <returns>The sensor.</returns>
    /// <remarks>
    ///     One <c>foreach</c> for every agent in the world, which is § D13's global form doing exactly
    ///     what it is for.
    /// </remarks>
    public static IGlobalTargetSensor CentreOf<T>() => new CentreSensor<T>();

    /// <summary>How many entities carry a component.</summary>
    /// <typeparam name="T">The component.</typeparam>
    /// <returns>The sensor.</returns>
    public static IGlobalWorldSensor CountOf<T>() => new CountSensor<T>();

    /// <summary>The nearest point on the navmesh to where the agent is standing.</summary>
    /// <param name="query">The mesh to ask.</param>
    /// <returns>The sensor.</returns>
    /// <remarks>
    ///     ⚠ <b>"Am I on the mesh at all" is a question worth asking as a sensor</b>, because an agent
    ///     knocked into a hole is one whose every <c>MoveTo</c> fails for a reason nothing on its
    ///     blackboard explains. This writes the nearest legal spot, so a recovery branch has somewhere
    ///     to send it.
    /// </remarks>
    public static ITargetSensor NearestOnNavMesh(NavMeshQuery query) => new NavMeshSensor(query);
}

/// <summary>The nearest entity carrying a component.</summary>
sealed class NearestSensor<T> : ITargetSensor {
    readonly QueryDescription description = new QueryDescription().WithAll<T, LocalTransform>();
    readonly float range;

    public NearestSensor(float range) => this.range = MathF.Max(0f, range);

    public SensorTarget Sense(in AgentContext context) {
        if (!context.World.Has<LocalTransform>(context.Entity)) {
            return SensorTarget.None;
        }

        var here = context.World.Read<LocalTransform>(context.Entity).Position;
        var limit = range > 0f ? range * range : float.MaxValue;
        var best = float.MaxValue;
        var found = SensorTarget.None;

        foreach (var chunk in context.World.Chunks(description)) {
            var transforms = chunk.ReadValues<LocalTransform>();
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                // ⚠ The agent itself is skipped. A sensor for "the nearest villager" run on a villager
                // otherwise answers "me", every time, and the branch that uses it never fires.
                if (entities[index] == context.Entity) {
                    continue;
                }

                var distance = (transforms[index].Position - here).LengthSquared();

                if (distance >= best || distance > limit) {
                    continue;
                }

                best = distance;
                found = SensorTarget.Of(entities[index], transforms[index].Position);
            }
        }

        return found;
    }
}

/// <summary>How far the nearest entity carrying a component is.</summary>
sealed class DistanceSensor<T> : ILocalWorldSensor {
    readonly NearestSensor<T> nearest = new(0f);
    readonly float far;

    public DistanceSensor(float far) => this.far = far;

    public void Sense(in AgentContext context, Blackboard blackboard, BlackboardKey key) {
        var target = nearest.Sense(in context);

        if (!target.Found || !context.World.Has<LocalTransform>(context.Entity)) {
            blackboard.SetFloat(key, far);

            return;
        }

        var here = context.World.Read<LocalTransform>(context.Entity).Position;

        blackboard.SetFloat(key, (target.Position - here).Length());
    }
}

/// <summary>The centre of every entity carrying a component.</summary>
sealed class CentreSensor<T> : IGlobalTargetSensor {
    readonly QueryDescription description = new QueryDescription().WithAll<T, LocalTransform>();

    public SensorTarget Sense(World world, GameTime time) {
        ArgumentNullException.ThrowIfNull(world);

        var total = Vector3.Zero;
        var count = 0;

        foreach (var chunk in world.Chunks(description)) {
            var transforms = chunk.ReadValues<LocalTransform>();

            for (var index = 0; index < chunk.Count; index++) {
                total += transforms[index].Position;
                count++;
            }
        }

        return count == 0 ? SensorTarget.None : SensorTarget.At(total / count);
    }
}

/// <summary>How many entities carry a component.</summary>
sealed class CountSensor<T> : IGlobalWorldSensor {
    readonly QueryDescription description = new QueryDescription().WithAll<T>();

    public float Sense(World world, GameTime time) {
        ArgumentNullException.ThrowIfNull(world);

        var count = 0;

        foreach (var chunk in world.Chunks(description)) {
            count += chunk.Count;
        }

        return count;
    }
}

/// <summary>The nearest point on the navmesh to where the agent is.</summary>
sealed class NavMeshSensor(NavMeshQuery query) : ITargetSensor {
    static readonly Vector3 Extents = new(4f, 4f, 4f);

    readonly NavMeshQuery query = query ?? throw new ArgumentNullException(nameof(query));

    public SensorTarget Sense(in AgentContext context) {
        if (!context.World.Has<LocalTransform>(context.Entity)) {
            return SensorTarget.None;
        }

        var here = context.World.Read<LocalTransform>(context.Entity).Position;

        return query.FindNearestPoly(here, Extents, NavQueryFilter.Default, out _, out var on)
            ? SensorTarget.At(on)
            : SensorTarget.None;
    }
}
