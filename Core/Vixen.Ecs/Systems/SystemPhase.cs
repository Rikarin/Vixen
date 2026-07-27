// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ecs.Systems;

/// <summary>
///     The ordered stages of a frame. Within a phase, order is the dependency graph; between
///     phases, a hard sync point.
/// </summary>
/// <remarks>
///     <para>
///         Named stages rather than one flat graph, because some ordering is not derivable from what
///         systems touch. Input must be read before anything reacts to it and the camera must be
///         final before culling starts, whether or not those systems share a component — and a user
///         who has to express that as an <see cref="UpdateAfterAttribute" /> chain across a hundred
///         systems will get it wrong.
///     </para>
///     <para>
///         The hard sync between phases is what makes a phase boundary a place where structural
///         change can safely happen: command buffers are played back there, and the world's change
///         version moves on, so a system asking "what changed since I last ran" gets an answer whose
///         granularity is the phase.
///     </para>
/// </remarks>
public enum SystemPhase {
    /// <summary>Before anything else. Frame bookkeeping and time.</summary>
    EarlyUpdate,

    /// <summary>Devices are polled and input state is published.</summary>
    Input,

    /// <summary>
    ///     Simulation at a fixed timestep. Runs zero or more times per frame from an accumulator, so
    ///     anything here must be a function of the fixed delta and never of the frame delta.
    /// </summary>
    FixedUpdate,

    /// <summary>Ordinary per-frame game logic.</summary>
    Update,

    /// <summary>Animation sampling and skinning, after logic has decided what is playing.</summary>
    Animation,

    /// <summary>After everything has moved. Cameras that follow, constraints that correct.</summary>
    LateUpdate,

    /// <summary>Transforms are resolved, culling runs, draw lists are built.</summary>
    PreRender,

    /// <summary>The render graph is recorded and submitted.</summary>
    Render,

    /// <summary>After submission. Presentation bookkeeping, readbacks, statistics.</summary>
    PostRender
}
