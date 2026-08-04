// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;

namespace Vixen.Ai;

/// <summary>
///     Every sensor a world's agents read through, and the pass that runs them: globals once, locals
///     per agent.
/// </summary>
/// <remarks>
///     <para>
///         <b>Doc 37 § D13's four kinds, and the pass is where the taxonomy pays for itself.</b> A
///         global sensor runs <i>once</i> and its answer is written to every agent's board; a local
///         one runs per agent. For "is it night" over a thousand villagers that is one query against
///         a thousand, which is the entire reason the split exists.
///     </para>
///     <para>
///         ⚠ <b>A global's answer is cached at <see cref="Begin" /> and never re-read.</b> An agent
///         late in the pass must see the same night as one early in it — a sensor asked per agent
///         would let the clock advance mid-pass and give two agents standing beside each other
///         different weather, which is the class of bug nobody looks for.
///     </para>
///     <para>
///         ⚠ <b>Sensors write keys; they do not decide anything.</b> A sensor that chose an action
///         would be a planner, and the whole arrangement of this library is that there are exactly
///         three of those. What a sensor is for is making the world <i>sayable</i> on a blackboard,
///         so that a tree's decorator, a utility set's consideration and a GOAP domain's world key
///         all read one number that was measured once.
///     </para>
/// </remarks>
public sealed class SensorSet {
    readonly List<Local> locals = [];
    readonly List<Global> globals = [];
    readonly List<SensorTarget> targets = [];
    readonly List<float> readings = [];

    /// <summary>How many sensors run per agent.</summary>
    public int LocalCount => locals.Count;

    /// <summary>How many run once a pass.</summary>
    public int GlobalCount => globals.Count;

    /// <summary>How many times <see cref="Begin" /> has run the globals.</summary>
    /// <remarks>What a test asserts the "one query against a thousand" claim with.</remarks>
    public int Passes { get; private set; }

    /// <summary>Adds a per-agent world sensor writing one numeric key.</summary>
    /// <param name="key">Where it writes.</param>
    /// <param name="sensor">What it reads.</param>
    /// <returns>This set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sensor" /> is null.</exception>
    public SensorSet Add(BlackboardKey key, IWorldSensor sensor) {
        ArgumentNullException.ThrowIfNull(sensor);

        locals.Add(new(key, BlackboardKey.Invalid, sensor, null));

        return this;
    }

    /// <summary>Adds a per-agent target sensor writing a place, a thing, or both.</summary>
    /// <param name="position">A <c>Vector3</c> key for where it is, or invalid for none.</param>
    /// <param name="entity">An <c>Entity</c> key for what it is, or invalid for none.</param>
    /// <param name="sensor">What it looks for.</param>
    /// <returns>This set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sensor" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Both keys, because one sensor answers two questions.</b> "The nearest apple" is a place
    ///     to walk to and a thing to pick up, and a project that had to run the search twice to get
    ///     both would run it twice.
    /// </remarks>
    public SensorSet AddTarget(BlackboardKey position, BlackboardKey entity, ITargetSensor sensor) {
        ArgumentNullException.ThrowIfNull(sensor);

        locals.Add(new(position, entity, null, sensor));

        return this;
    }

    /// <summary>Adds a once-a-pass world sensor writing one numeric key on every agent.</summary>
    /// <param name="key">Where it writes.</param>
    /// <param name="sensor">What it reads.</param>
    /// <returns>This set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sensor" /> is null.</exception>
    public SensorSet AddGlobal(BlackboardKey key, IGlobalWorldSensor sensor) {
        ArgumentNullException.ThrowIfNull(sensor);

        globals.Add(new(key, BlackboardKey.Invalid, sensor, null));
        readings.Add(0f);
        targets.Add(SensorTarget.None);

        return this;
    }

    /// <summary>Adds a once-a-pass target sensor writing a place, a thing, or both, on every agent.</summary>
    /// <param name="position">A <c>Vector3</c> key, or invalid for none.</param>
    /// <param name="entity">An <c>Entity</c> key, or invalid for none.</param>
    /// <param name="sensor">What it looks for.</param>
    /// <returns>This set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sensor" /> is null.</exception>
    public SensorSet AddGlobalTarget(BlackboardKey position, BlackboardKey entity, IGlobalTargetSensor sensor) {
        ArgumentNullException.ThrowIfNull(sensor);

        globals.Add(new(position, entity, null, sensor));
        readings.Add(0f);
        targets.Add(SensorTarget.None);

        return this;
    }

    /// <summary>Runs every global sensor once and remembers what they said.</summary>
    /// <param name="world">The world.</param>
    /// <param name="time">The clock.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    public void Begin(World world, GameTime time) {
        ArgumentNullException.ThrowIfNull(world);

        for (var index = 0; index < globals.Count; index++) {
            var sensor = globals[index];

            if (sensor.World is { } reading) {
                readings[index] = reading.Sense(world, time);
            } else {
                targets[index] = sensor.Target!.Sense(world, time);
            }
        }

        Passes++;
    }

    /// <summary>Writes what the globals said and runs every local sensor, on one agent.</summary>
    /// <param name="context">The agent.</param>
    /// <returns>How many keys were written.</returns>
    /// <remarks>
    ///     ⚠ <b>Globals first, so a local sensor may read one.</b> "How far am I from the fire" needs
    ///     the fire, which is a global target — and an order that ran the locals first would make that
    ///     sensor read last pass's answer, once, for ever.
    /// </remarks>
    public int Apply(in AgentContext context) {
        var written = 0;
        var blackboard = context.Blackboard;

        for (var index = 0; index < globals.Count; index++) {
            var sensor = globals[index];

            if (sensor.World is not null) {
                if (sensor.Position.IsValid) {
                    blackboard.SetFloat(sensor.Position, readings[index]);
                    written++;
                }

                continue;
            }

            written += Write(blackboard, sensor.Position, sensor.Entity, targets[index]);
        }

        foreach (var sensor in locals) {
            if (sensor.World is { } world) {
                if (sensor.Position.IsValid) {
                    world.Sense(in context, blackboard, sensor.Position);
                    written++;
                }

                continue;
            }

            written += Write(blackboard, sensor.Position, sensor.Entity, sensor.Target!.Sense(in context));
        }

        return written;
    }

    /// <summary>What a global world sensor last said.</summary>
    /// <param name="index">Its index among the globals.</param>
    /// <returns>The reading.</returns>
    public float ReadingOf(int index) => (uint)index < (uint)readings.Count ? readings[index] : 0f;

    /// <summary>What a global target sensor last found.</summary>
    /// <param name="index">Its index among the globals.</param>
    /// <returns>The target.</returns>
    public SensorTarget TargetOf(int index) =>
        (uint)index < (uint)targets.Count ? targets[index] : SensorTarget.None;

    /// <summary>
    ///     Writes a target's place and thing, or clears both when nothing was found.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Cleared and not left alone.</b> A key still holding the apple that was eaten is an
    ///     agent walking confidently to where an apple used to be — and "unset" is a state the
    ///     blackboard has precisely so that "there is none" is sayable.
    /// </remarks>
    static int Write(Blackboard blackboard, BlackboardKey position, BlackboardKey entity, in SensorTarget target) {
        var written = 0;

        if (position.IsValid) {
            if (target.Found) {
                blackboard.SetVector3(position, target.Position);
            } else {
                blackboard.Clear(position);
            }

            written++;
        }

        if (!entity.IsValid) {
            return written;
        }

        if (target is { Found: true, Entity.IsNull: false }) {
            blackboard.SetEntity(entity, target.Entity);
        } else {
            blackboard.Clear(entity);
        }

        return written + 1;
    }

    /// <summary>One per-agent sensor: a world reading or a target search, and where it writes.</summary>
    readonly record struct Local(
        BlackboardKey Position,
        BlackboardKey Entity,
        IWorldSensor? World,
        ITargetSensor? Target
    );

    /// <summary>One once-a-pass sensor, ditto.</summary>
    readonly record struct Global(
        BlackboardKey Position,
        BlackboardKey Entity,
        IGlobalWorldSensor? World,
        IGlobalTargetSensor? Target
    );
}
