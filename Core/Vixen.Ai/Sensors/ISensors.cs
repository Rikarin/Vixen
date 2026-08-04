// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;

namespace Vixen.Ai;

/// <summary>Where something is, or what it is, or neither.</summary>
/// <param name="Found">Whether the sensor found anything at all.</param>
/// <param name="Position">Where it is.</param>
/// <param name="Entity">What it is, when it is a thing rather than a place.</param>
/// <remarks>
///     ⚠ <b>A position <i>and</i> an entity, and <see cref="Found" /> separate from both.</b> "The
///     nearest apple" is an entity that has a position; "the town square" is a position that is not an
///     entity; and "there is no apple" is neither — which a zero vector cannot say and
///     <see cref="Entity.Null" /> can only half say. A sensor that reported a bare
///     <see cref="Vector3" /> would make an agent walk confidently to the origin of the world.
/// </remarks>
public readonly record struct SensorTarget(bool Found, Vector3 Position, Entity Entity = default) {
    /// <summary>Nothing was found.</summary>
    public static SensorTarget None => default;

    /// <summary>A place.</summary>
    /// <param name="position">Where.</param>
    /// <returns>The target.</returns>
    public static SensorTarget At(Vector3 position) => new(true, position);

    /// <summary>A thing, and where it is.</summary>
    /// <param name="entity">What.</param>
    /// <param name="position">Where.</param>
    /// <returns>The target.</returns>
    public static SensorTarget Of(Entity entity, Vector3 position) => new(true, position, entity);
}

/// <summary>Reads one number about the world for one agent, once per agent.</summary>
/// <remarks>
///     doc 37 § D13's local world sensor — "how hungry am I", "how much ammo is left", "how far is the
///     leash". Two front ends reach it: <see cref="UpdateBlackboardService" /> runs it on a tree's
///     schedule, and <see cref="SensorSet" /> runs it on the agent's.
/// </remarks>
public interface ILocalWorldSensor : IWorldSensor;

/// <summary>Finds a place or a thing for one agent, once per agent.</summary>
/// <remarks>
///     doc 37 § D13's local target sensor — "the nearest apple <i>to me</i>". It writes a
///     <c>Vector3</c> key, an <c>Entity</c> key, or both, which is what makes one sensor serve
///     <c>MoveTo</c> (a place) and <c>RotateToward</c> (a thing).
/// </remarks>
public interface ITargetSensor {
    /// <summary>Looks.</summary>
    /// <param name="context">The agent.</param>
    /// <returns>What it found, or <see cref="SensorTarget.None" />.</returns>
    SensorTarget Sense(in AgentContext context);
}

/// <summary>Reads one number about the world for <i>everybody</i>, once a pass.</summary>
/// <remarks>
///     <para>
///         doc 37 § D13's global world sensor — "is it night", "how bad is the storm", "what is the
///         alert level". ⚠ <b>The difference between this and the local form is one query against a
///         thousand</b>, and it is the whole reason the taxonomy has four members rather than two.
///     </para>
///     <para>
///         ⚠ <b>It is handed a <see cref="World" /> and not an <see cref="AgentContext" />.</b> A
///         global sensor that took an agent would be one somebody could accidentally write per-agent
///         logic in — and it would then read whichever agent happened to be first, once, for all of
///         them.
///     </para>
/// </remarks>
public interface IGlobalWorldSensor {
    /// <summary>Reads the world.</summary>
    /// <param name="world">The world.</param>
    /// <param name="time">The clock.</param>
    /// <returns>The number every agent gets.</returns>
    float Sense(World world, GameTime time);
}

/// <summary>Finds a place or a thing for <i>everybody</i>, once a pass.</summary>
/// <remarks>
///     doc 37 § D13's global target sensor — "the town square", "where the fire is", "the objective".
///     Its result is computed once and written to every agent's key.
/// </remarks>
public interface IGlobalTargetSensor {
    /// <summary>Looks.</summary>
    /// <param name="world">The world.</param>
    /// <param name="time">The clock.</param>
    /// <returns>What it found, or <see cref="SensorTarget.None" />.</returns>
    SensorTarget Sense(World world, GameTime time);
}

/// <summary>A local world reading written as a lambda.</summary>
/// <param name="context">The agent.</param>
/// <returns>The number.</returns>
public delegate float WorldReading(in AgentContext context);

/// <summary>A local target search written as a lambda.</summary>
/// <param name="context">The agent.</param>
/// <returns>What it found.</returns>
public delegate SensorTarget TargetSearch(in AgentContext context);

/// <summary>The sensors that ship.</summary>
/// <remarks>
///     ⚠ <b>Delegates and constants, and deliberately nothing that reads a transform.</b> Every
///     interesting sensor is a game's own question — <c>Vixen.Ai</c> cannot see a position, a
///     collider or an inventory, and a library of half-guesses about what a game means by "hungry"
///     would be the behaviour library doc 28 says this must not become. What ships is the two shapes
///     every project needs on day one and the seam they hang on.
/// </remarks>
public static class Sensors {
    /// <summary>A local world sensor from a lambda.</summary>
    /// <param name="reading">What it reads.</param>
    /// <returns>The sensor.</returns>
    public static ILocalWorldSensor World(WorldReading reading) => new DelegateWorldSensor(reading);

    /// <summary>A local world sensor that always answers the same number.</summary>
    /// <param name="value">The number.</param>
    /// <returns>The sensor.</returns>
    /// <remarks>
    ///     Not a placeholder: "this game has no weather, so the storm key is zero" is a real answer
    ///     and one an agent's set can be tuned against before the weather exists.
    /// </remarks>
    public static ILocalWorldSensor Constant(float value) => new ConstantWorldSensor(value);

    /// <summary>A local target sensor from a lambda.</summary>
    /// <param name="search">What it looks for.</param>
    /// <returns>The sensor.</returns>
    public static ITargetSensor Target(TargetSearch search) => new DelegateAgentTargetSensor(search);

    /// <summary>A local target sensor that always answers the same place.</summary>
    /// <param name="position">Where.</param>
    /// <returns>The sensor.</returns>
    public static ITargetSensor Place(Vector3 position) => new ConstantTargetSensor(SensorTarget.At(position));

    /// <summary>A global world sensor from a lambda.</summary>
    /// <param name="reading">What it reads.</param>
    /// <returns>The sensor.</returns>
    public static IGlobalWorldSensor GlobalWorld(Func<World, GameTime, float> reading) =>
        new DelegateGlobalWorldSensor(reading);

    /// <summary>A global world sensor over the clock: the fraction of a day that has passed.</summary>
    /// <param name="dayLength">How long a day is, in seconds.</param>
    /// <returns>The sensor.</returns>
    /// <remarks>
    ///     The one global reading every project has and nobody's game logic owns, and the example
    ///     § D13 uses by name. It is normalised, so it is a utility consideration's input unchanged.
    /// </remarks>
    public static IGlobalWorldSensor TimeOfDay(float dayLength = 1200f) => new TimeOfDaySensor(dayLength);

    /// <summary>A global target sensor from a lambda.</summary>
    /// <param name="search">What it looks for.</param>
    /// <returns>The sensor.</returns>
    public static IGlobalTargetSensor GlobalTarget(Func<World, GameTime, SensorTarget> search) =>
        new DelegateGlobalTargetSensor(search);

    /// <summary>A global target sensor that always answers the same place.</summary>
    /// <param name="position">Where.</param>
    /// <returns>The sensor.</returns>
    public static IGlobalTargetSensor Landmark(Vector3 position) => new LandmarkSensor(SensorTarget.At(position));
}

sealed class DelegateWorldSensor(WorldReading reading) : ILocalWorldSensor {
    readonly WorldReading reading = reading ?? throw new ArgumentNullException(nameof(reading));

    public void Sense(in AgentContext context, Blackboard blackboard, BlackboardKey key) =>
        blackboard.SetFloat(key, reading(in context));
}

sealed class ConstantWorldSensor(float value) : ILocalWorldSensor {
    public void Sense(in AgentContext context, Blackboard blackboard, BlackboardKey key) =>
        blackboard.SetFloat(key, value);
}

sealed class DelegateAgentTargetSensor(TargetSearch search) : ITargetSensor {
    readonly TargetSearch search = search ?? throw new ArgumentNullException(nameof(search));

    public SensorTarget Sense(in AgentContext context) => search(in context);
}

sealed class ConstantTargetSensor(SensorTarget target) : ITargetSensor {
    public SensorTarget Sense(in AgentContext context) => target;
}

sealed class DelegateGlobalWorldSensor(Func<World, GameTime, float> reading) : IGlobalWorldSensor {
    readonly Func<World, GameTime, float> reading = reading ?? throw new ArgumentNullException(nameof(reading));

    public float Sense(World world, GameTime time) => reading(world, time);
}

/// <summary>The fraction of a day that has passed, in <c>[0,1)</c>.</summary>
sealed class TimeOfDaySensor(float dayLength) : IGlobalWorldSensor {
    readonly float dayLength = MathF.Max(1f, dayLength);

    public float Sense(World world, GameTime time) {
        var fraction = (float)(time.TotalSeconds % dayLength) / dayLength;

        // A negative clock is not a thing this engine produces, but a wrapped one is what a modulo
        // gives on one, and a sensor that answered −0.3 would put every curve outside its domain.
        return fraction < 0f ? fraction + 1f : fraction;
    }
}

sealed class DelegateGlobalTargetSensor(Func<World, GameTime, SensorTarget> search) : IGlobalTargetSensor {
    readonly Func<World, GameTime, SensorTarget> search = search ?? throw new ArgumentNullException(nameof(search));

    public SensorTarget Sense(World world, GameTime time) => search(world, time);
}

sealed class LandmarkSensor(SensorTarget target) : IGlobalTargetSensor {
    public SensorTarget Sense(World world, GameTime time) => target;
}
