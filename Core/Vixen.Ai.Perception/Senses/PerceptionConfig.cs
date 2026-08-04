// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Ai.Perception;

/// <summary>What being able to see means for one kind of agent.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><see cref="LoseSightRadius" /> is a separate, larger radius, and leaving it out makes
///         targets flicker.</b> With one radius, a target walking the boundary is perceived, lost,
///         perceived, lost — several times a second — and every decorator observing the target key
///         aborts a branch each time. It is the first thing every hand-rolled implementation gets
///         wrong, it looks like a bug in the behaviour tree rather than in the sense, and it costs
///         one field. doc 37 § D15.
///     </para>
///     <para>
///         The three tests run in a fixed order and the order is the cost model:
///         <b>radius first, cone second, occlusion last</b> — the trace is a physics raycast and it
///         only ever runs for what survived two comparisons.
///     </para>
/// </remarks>
public sealed record SightSettings {
    /// <summary>How far it can notice something, in metres.</summary>
    public float Radius { get; init; } = 20f;

    /// <summary>
    ///     How far something already seen can get before it is lost. Clamped up to
    ///     <see cref="Radius" />, because a smaller one is the flicker with extra steps.
    /// </summary>
    public float LoseSightRadius { get; init; } = 25f;

    /// <summary>The full angle of the cone, in degrees. 360 is all round.</summary>
    public float ConeDegrees { get; init; } = 90f;

    /// <summary>Whether something solid in the way stops it.</summary>
    /// <remarks>
    ///     Off is a real configuration and not a shortcut: a sense modelling a minimap ping, a
    ///     scripted "the boss always knows", or a top-down game with no vertical geometry wants the
    ///     radius and the cone without paying for a trace per candidate.
    /// </remarks>
    public bool Occlusion { get; init; } = true;

    /// <summary>Where the eye is above the entity's own position, in metres.</summary>
    /// <remarks>
    ///     ⚠ Both ends need one. A trace from the floor to the floor is blocked by every kerb, doorstep
    ///     and ramp in the level, and the resulting "he cannot see me if I stand on a slope" is the
    ///     second-most-reported perception bug after the flicker.
    /// </remarks>
    public float EyeHeight { get; init; } = 1.7f;

    /// <summary>The effective radius for something that is already perceived.</summary>
    public float RadiusFor(bool perceived) => perceived ? MathF.Max(Radius, LoseSightRadius) : Radius;

    /// <summary>The cosine of the half-angle, which is what a dot product is compared against.</summary>
    public float ConeCosine => ConeDegrees >= 360f ? -1f : MathF.Cos(float.DegreesToRadians(ConeDegrees) * 0.5f);
}

/// <summary>What being able to hear means.</summary>
/// <remarks>
///     A noise is reported rather than sampled — <c>PerceptionSystem.ReportNoise</c> — because there
///     is nothing continuous to test against: an entity is not audible, an <i>event</i> is. That is
///     also why there is no occlusion here. Sound goes round corners, and a hearing sense that
///     raycasts is a sense that cannot hear a shout from the next room, which is the one thing
///     players expect it to do.
/// </remarks>
public sealed record HearingSettings {
    /// <summary>How far a noise of loudness 1 carries, in metres.</summary>
    /// <remarks>Scaled by the noise's own loudness, so a gunshot at 3 carries three times as far.</remarks>
    public float Range { get; init; } = 30f;
}

/// <summary>What counts as touching.</summary>
public sealed record TouchSettings {
    /// <summary>How close counts, in metres.</summary>
    public float Radius { get; init; } = 1.2f;
}

/// <summary>What being hurt tells an agent.</summary>
/// <remarks>
///     There is no radius: damage is reported by whatever applied it, and it is the one sense that
///     works with the source behind you, out of range and behind a wall — which is the entire point
///     of having it.
/// </remarks>
public sealed record DamageSettings {
    /// <summary>How much damage registers at all.</summary>
    public float Threshold { get; init; }
}

/// <summary>How far an ally's report travels.</summary>
/// <remarks>
///     Unreal's team sense: an ally that perceives something tells the agents near it, so a squad
///     reacts together without anybody writing squad code. It is applied after the other four, over
///     what they produced this pass — which is why a relayed report is never relayed twice.
/// </remarks>
public sealed record TeamSettings {
    /// <summary>How far a report carries, in metres.</summary>
    public float Range { get; init; } = 25f;
}

/// <summary>Everything one kind of agent perceives with, and how often.</summary>
/// <remarks>
///     <para>
///         Shared by every agent of that kind, exactly the way a <c>BehaviorTreeTemplate</c> is: an
///         <see cref="Ecs.AiPerception" /> names one by index and carries no settings of its own. The
///         thousand guards in a level have one of these between them.
///     </para>
///     <para>
///         ⚠ <b>A record class rather than a record struct, and that is not a style choice.</b> A
///         record struct's <c>new()</c> is its <i>zero</i> value — the primary-constructor defaults
///         only apply when somebody names the constructor — so a config built with an object
///         initialiser would silently get a sight radius of zero. The same trap cost P2 a working
///         layout; here a class's field initialisers run whatever the caller writes.
///     </para>
/// </remarks>
public sealed record PerceptionConfig {
    /// <summary>What it can be called.</summary>
    public Symbol Name { get; init; }

    /// <summary>Which senses it runs. A sense not in here costs nothing at all.</summary>
    public SenseMask Senses { get; init; } = SenseMask.Sight | SenseMask.Hearing;

    /// <summary>Its sight.</summary>
    public SightSettings Sight { get; init; } = new();

    /// <summary>Its hearing.</summary>
    public HearingSettings Hearing { get; init; } = new();

    /// <summary>Its sense of touch.</summary>
    public TouchSettings Touch { get; init; } = new();

    /// <summary>What being hurt tells it.</summary>
    public DamageSettings Damage { get; init; } = new();

    /// <summary>How far it relays to allies.</summary>
    public TeamSettings Team { get; init; } = new();

    /// <summary>Seconds between passes for one listener, before distance LOD.</summary>
    /// <remarks>
    ///     Ten hertz, which is roughly the rate at which a human notices something. Under
    ///     <see cref="DistanceLodGovernor" />'s shipped bands the far one lands on 4 Hz, which is doc
    ///     37 § D15's figure for an agent behind the player.
    /// </remarks>
    public float Interval { get; init; } = 0.1f;

    /// <summary>
    ///     How much to jitter that interval, in seconds. ⚠ Zero puts every agent spawned in the same
    ///     frame on the same tick for ever, which is a frame that costs the whole population.
    /// </summary>
    public float RandomDeviation { get; init; } = 0.05f;

    /// <summary>
    ///     Seconds a target stays in the perceived list after it stops being perceived. This is what
    ///     "search where he was" is made of.
    /// </summary>
    public float Memory { get; init; } = 5f;

    /// <summary>The most targets one listener keeps. The oldest goes when it is full.</summary>
    public int MaxPerceived { get; init; } = 16;

    /// <summary>Who it is allowed to perceive.</summary>
    public IPerceptionFilter Filter { get; init; } = PerceptionFilters.Everyone;

    /// <summary>How its perceived list reaches its blackboard, or null to write nothing.</summary>
    public IBlackboardBinding? Binding { get; init; }

    /// <summary>The furthest any of its senses reaches, which is what the broad phase is asked for.</summary>
    /// <remarks>
    ///     ⚠ The lose-sight radius counts, and forgetting it here is a subtle version of the flicker:
    ///     the target is inside the sense's own radius and outside what the broad phase returned, so
    ///     it is dropped by a bound that was supposed to be conservative.
    /// </remarks>
    public float MaxRadius {
        get {
            var radius = 0f;

            if (Senses.Has(AiSense.Sight)) {
                radius = MathF.Max(radius, Sight.RadiusFor(true));
            }

            if (Senses.Has(AiSense.Touch)) {
                radius = MathF.Max(radius, Touch.Radius);
            }

            if (Senses.Has(AiSense.Team)) {
                radius = MathF.Max(radius, Team.Range);
            }

            return radius;
        }
    }
}

/// <summary>The configurations a world's listeners may name, by index.</summary>
/// <remarks>
///     The same arrangement as <c>BehaviorTreeLibrary</c>, and for its reason: an
///     <see cref="Ecs.AiPerception" /> is a component, a component is a handle and a few numbers, and a
///     reference is not a number.
/// </remarks>
public sealed class PerceptionLibrary {
    readonly Dictionary<Symbol, PerceptionConfig> byName = [];
    readonly List<PerceptionConfig> ordered = [];

    /// <summary>How many it holds.</summary>
    public int Count => ordered.Count;

    /// <summary>The configuration at an index, which is what an <see cref="Ecs.AiPerception" /> names.</summary>
    /// <param name="index">Its index.</param>
    public PerceptionConfig this[int index] => ordered[index];

    /// <summary>Adds one.</summary>
    /// <param name="config">The configuration.</param>
    /// <returns>Its index.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="config" /> is null.</exception>
    /// <exception cref="InvalidOperationException">One of that name is already in it.</exception>
    public int Add(PerceptionConfig config) {
        ArgumentNullException.ThrowIfNull(config);

        if (config.Name != Symbol.None && !byName.TryAdd(config.Name, config)) {
            throw new InvalidOperationException($"'{config.Name}' is already in this library.");
        }

        ordered.Add(config);

        return ordered.Count - 1;
    }

    /// <summary>Looks one up by name.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="config">Where to put it.</param>
    /// <returns>Whether the library has it.</returns>
    public bool TryGet(Symbol name, out PerceptionConfig? config) => byName.TryGetValue(name, out config);

    /// <summary>Looks an index up by name.</summary>
    /// <param name="name">Its name.</param>
    /// <returns>Its index, or <c>-1</c>.</returns>
    public int IndexOf(Symbol name) => byName.TryGetValue(name, out var config) ? ordered.IndexOf(config) : -1;
}
