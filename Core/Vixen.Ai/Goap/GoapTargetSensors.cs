// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;

namespace Vixen.Ai;

/// <summary>Where each of a domain's actions happens, and where the agent asking is.</summary>
/// <remarks>
///     <para>
///         doc 37 § D12: an action declares a target <i>key</i> and a sensor resolves it — "the
///         nearest pear", not "pear number four". That is what keeps the graph a function of the
///         action set rather than of the world's contents.
///     </para>
///     <para>
///         ⚠ <b>Where the agent is comes through a delegate, and that is the layering.</b> A position
///         lives in <c>Vixen.Engine</c>, which this assembly may not reference — doc 37's whole
///         argument for putting the planners in <c>Core/</c>. A game or <c>Vixen.Ai.Nodes</c> hands
///         one over; a game with no transforms at all hands over nothing, every distance reads as
///         zero, and the search plans by action cost alone, which is a perfectly good way to plan.
///     </para>
/// </remarks>
public sealed class GoapTargetSensors {
    readonly Dictionary<Symbol, IGoapTargetSensor> sensors = [];

    /// <summary>How to find where the agent is.</summary>
    /// <param name="context">The agent.</param>
    /// <param name="position">Where to put it.</param>
    /// <returns>Whether it has a position.</returns>
    public delegate bool PositionLookup(in AgentContext context, out Vector3 position);

    /// <summary>How to find where the agent is, or null for the origin.</summary>
    public PositionLookup? AgentPosition { get; init; }

    /// <summary>How many sensors it holds.</summary>
    public int Count => sensors.Count;

    /// <summary>Registers a sensor.</summary>
    /// <param name="key">The target key an action names.</param>
    /// <param name="sensor">The sensor.</param>
    /// <returns>This table.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sensor" /> is null.</exception>
    public GoapTargetSensors Add(Symbol key, IGoapTargetSensor sensor) {
        ArgumentNullException.ThrowIfNull(sensor);

        sensors[key] = sensor;

        return this;
    }

    /// <summary>Registers a sensor written as a lambda.</summary>
    /// <param name="key">The target key.</param>
    /// <param name="sensor">What it does.</param>
    /// <returns>This table.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sensor" /> is null.</exception>
    public GoapTargetSensors Add(Symbol key, GoapTargetLookup sensor) => Add(key, new DelegateTargetSensor(sensor));

    /// <summary>Registers one of doc 37 § D13's target sensors under a GOAP target key.</summary>
    /// <param name="key">The target key an action names.</param>
    /// <param name="sensor">The sensor.</param>
    /// <returns>This table.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sensor" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>§ D13 says there is one implementation and two front ends, and this is the second
    ///     front end.</b> "The nearest apple to me" is one search whether a behaviour tree writes it to
    ///     a key, a utility consideration measures its distance, or a GOAP action goes there — so
    ///     <see cref="ITargetSensor" /> is what a project writes, and a domain names it here rather
    ///     than making them write it twice.
    /// </remarks>
    public GoapTargetSensors Add(Symbol key, ITargetSensor sensor) {
        ArgumentNullException.ThrowIfNull(sensor);

        return Add(key, new SensedTargetSensor(sensor));
    }

    /// <summary>Finds where an action would happen.</summary>
    /// <param name="key">Its target key.</param>
    /// <param name="context">The agent.</param>
    /// <param name="position">Where to put the place.</param>
    /// <param name="target">The entity, when the target is one.</param>
    /// <returns>Whether a sensor answered.</returns>
    public bool TryResolve(Symbol key, in AgentContext context, out Vector3 position, out Entity target) {
        position = Vector3.Zero;
        target = Entity.Null;

        return sensors.TryGetValue(key, out var sensor) && sensor.TryResolve(in context, out position, out target);
    }

    /// <summary>Where the agent is.</summary>
    /// <param name="context">The agent.</param>
    /// <returns>Its position, or the origin when nobody said how to find one.</returns>
    public Vector3 Where(in AgentContext context) =>
        AgentPosition is { } lookup && lookup(in context, out var position) ? position : Vector3.Zero;
}

/// <summary>A target sensor written as a lambda.</summary>
/// <param name="context">The agent.</param>
/// <param name="position">Where to put the place.</param>
/// <param name="target">The entity, when the target is one.</param>
/// <returns>Whether there is anywhere to go.</returns>
public delegate bool GoapTargetLookup(in AgentContext context, out Vector3 position, out Entity target);

sealed class DelegateTargetSensor(GoapTargetLookup lookup) : IGoapTargetSensor {
    readonly GoapTargetLookup lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));

    public bool TryResolve(in AgentContext context, out Vector3 position, out Entity target) =>
        lookup(in context, out position, out target);
}

/// <summary>A GOAP target that is one of § D13's target sensors, asked directly.</summary>
sealed class SensedTargetSensor(ITargetSensor sensor) : IGoapTargetSensor {
    readonly ITargetSensor sensor = sensor ?? throw new ArgumentNullException(nameof(sensor));

    public bool TryResolve(in AgentContext context, out Vector3 position, out Entity target) {
        var found = sensor.Sense(in context);

        position = found.Position;
        target = found.Entity;

        return found.Found;
    }
}
