// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Physics;

/// <summary>How a <see cref="PhysicsWorld" /> is built and how hard it works.</summary>
/// <remarks>
///     <para>
///         The capacities are hard ceilings, not hints: Jolt allocates its body array, its body-pair
///         cache and its contact-constraint buffer once at construction and never grows them, which
///         is what lets a step allocate nothing at all. Running out is reported through
///         <see cref="PhysicsStepResult" /> rather than by growing.
///     </para>
///     <para>
///         The defaults suit a level of a few thousand bodies. A stress test that wants a hundred
///         thousand says so here, at the cost of the memory to hold them.
///     </para>
/// </remarks>
public sealed record PhysicsWorldSettings {
    /// <summary>The layer table. What collides with what.</summary>
    public PhysicsLayers Layers { get; init; } = PhysicsLayers.Default;

    /// <summary>Gravity, in metres a second squared. Earth by default.</summary>
    public Vector3 Gravity { get; init; } = new(0f, -9.81f, 0f);

    /// <summary>The most bodies that can exist at once.</summary>
    public int MaxBodies { get; init; } = 10_240;

    /// <summary>The most overlapping pairs the broad phase may report in one step.</summary>
    public int MaxBodyPairs { get; init; } = 65_536;

    /// <summary>The most contact constraints the solver may hold in one step.</summary>
    public int MaxContactConstraints { get; init; } = 20_480;

    /// <summary>
    ///     How many mutexes guard the body array, or zero for Jolt's default.
    /// </summary>
    /// <remarks>
    ///     Only matters when bodies are touched from several threads. A world driven from one thread
    ///     — which the ECS bridge is — wants the default, and a value of 1 makes every body access
    ///     contend on one lock.
    /// </remarks>
    public int BodyMutexCount { get; init; }

    /// <summary>
    ///     How many worker threads Jolt may use, or zero for one per hardware thread bar one.
    /// </summary>
    /// <remarks>
    ///     <b>Determinism depends on this.</b> Jolt's simulation is deterministic for a given thread
    ///     count and not across different ones, so a replay, a rollback or a lockstep peer has to
    ///     agree on the number. Setting it to 1 is the strongest guarantee and the one the
    ///     determinism tests use.
    /// </remarks>
    public int ThreadCount { get; init; }

    /// <summary>
    ///     How many collision sub-steps each call to <see cref="PhysicsWorld.Step" /> runs.
    /// </summary>
    /// <remarks>
    ///     More than one is the answer to a fixed step long enough that fast bodies tunnel, when
    ///     turning on continuous detection per body is not enough. It multiplies the cost of a step
    ///     almost exactly.
    /// </remarks>
    public int CollisionStepsPerUpdate { get; init; } = 1;

    /// <summary>How many velocity iterations the solver runs.</summary>
    public int VelocityIterations { get; init; } = 10;

    /// <summary>How many position iterations the solver runs.</summary>
    public int PositionIterations { get; init; } = 2;

    /// <summary>
    ///     Whether to hold Jolt to bit-identical results for identical input.
    /// </summary>
    /// <remarks>
    ///     Costs a few per cent, mostly by forcing the island splitter to a fixed order. On, because
    ///     the alternative is that a determinism bug is discovered by a multiplayer desync months
    ///     later rather than by a test today, and because it is one of two settings — with
    ///     <see cref="ThreadCount" /> — that the whole of Phase 9's lag compensation stands on.
    /// </remarks>
    public bool Deterministic { get; init; } = true;

    /// <summary>Whether bodies may fall asleep at all. Per-body sleeping is on top of this.</summary>
    public bool AllowSleeping { get; init; } = true;
}

/// <summary>What a step ran into, if anything.</summary>
/// <remarks>
///     Every value other than <see cref="Ok" /> means a capacity in
///     <see cref="PhysicsWorldSettings" /> was reached and contacts were silently dropped — bodies
///     will fall through one another, and the only clue without this is that the scene misbehaves
///     under load and behaves when it is quiet.
/// </remarks>
public enum PhysicsStepResult {
    /// <summary>The step ran to completion.</summary>
    Ok,

    /// <summary>The manifold cache filled. Raise <see cref="PhysicsWorldSettings.MaxBodyPairs" />.</summary>
    ManifoldCacheFull,

    /// <summary>The body-pair cache filled. Raise <see cref="PhysicsWorldSettings.MaxBodyPairs" />.</summary>
    BodyPairCacheFull,

    /// <summary>
    ///     The contact-constraint buffer filled. Raise
    ///     <see cref="PhysicsWorldSettings.MaxContactConstraints" />.
    /// </summary>
    ContactConstraintsFull
}
