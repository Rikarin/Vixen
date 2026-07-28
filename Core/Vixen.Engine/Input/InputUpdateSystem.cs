// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Threading;
using Vixen.Ecs.Systems;
using Vixen.Input;

namespace Vixen.Engine.Input;

/// <summary>Reads the frame's input into every registered action asset.</summary>
/// <param name="input">The service that owns the devices and the assets.</param>
/// <remarks>
///     <para>
///         <b>Runs in <see cref="SystemPhase.Input" />, which exists for this.</b>
///         <c>SystemPhase</c>'s own documentation gives the reason the phase is named rather than
///         derived: "input must be read before anything reacts to it", and that ordering is not
///         something a dependency graph over component types can see. So this system is the phase's
///         only occupant in a default engine, and everything that reads an action runs in a later
///         one.
///     </para>
///     <para>
///         <b>It reads no components and writes none.</b> An action asset is not world state — it is
///         one object per player, read by whatever wants it — so there is nothing here for the
///         runner's conflict analysis to serialise on, and a game's own systems parallelise freely
///         around it.
///     </para>
///     <para>
///         <b>What it does <em>not</em> do is pump the platform.</b> The host drains the OS event
///         queue and submits what it found to the <see cref="InputService" /> before the frame's
///         phases run; this turns the state that arrived into actions. Splitting it there is what
///         lets a determinism test replay a recorded input log through the same system with no
///         platform underneath it at all.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.Input)]
public sealed class InputUpdateSystem(InputService input) : SystemBase {
    readonly InputService input = input ?? throw new ArgumentNullException(nameof(input));

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        input.Update(context.Time);
        return dependency;
    }
}
