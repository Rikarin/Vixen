// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Threading;

namespace Vixen.Ecs.Systems;

/// <summary>What a system is given every time it runs.</summary>
/// <param name="World">The world it operates on.</param>
/// <param name="Time">The clock. For a <see cref="SystemPhase.FixedUpdate" /> system this is the fixed step.</param>
/// <param name="Jobs">The scheduler to hand work to, or <see langword="null" /> to run inline.</param>
/// <param name="Commands">
///     Where structural change goes. Played back at the end of the phase, when nothing is iterating.
/// </param>
/// <param name="Phase">Which phase is running, so the context describes itself in a diagnostic.</param>
public readonly record struct SystemContext(
    World World,
    GameTime Time,
    JobScheduler? Jobs,
    CommandBuffer Commands,
    SystemPhase Phase = SystemPhase.Update
);

/// <summary>
///     One unit of per-frame work over a world.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="Update" /> returns the handle for whatever it scheduled rather than waiting for
///         it. That is the whole point: the runner has already worked out which systems conflict, so
///         a system that returns promptly with a handle lets every non-conflicting system after it
///         start immediately, and the phase costs its critical path rather than its sum. A system
///         that does its work inline returns <c>dependency</c> — correct, and serialising.
///     </para>
///     <para>
///         What a system reads and writes is declared with <see cref="ReadsAttribute" /> and
///         <see cref="WritesAttribute" />, or by implementing <see cref="IDeclaredAccess" /> when the
///         set is not known until construction. Getting it wrong in the direction of declaring too
///         little is a data race the runner cannot see; too much only costs parallelism.
///     </para>
/// </remarks>
public interface ISystem : IDisposable {
    /// <summary>Called once, before the first update.</summary>
    /// <param name="context">The world and its services.</param>
    void Initialize(in SystemContext context);

    /// <summary>Runs the system.</summary>
    /// <param name="context">The world, the clock and the scheduler.</param>
    /// <param name="dependency">What must finish before this system's work may touch the world.</param>
    /// <returns>A handle for the work scheduled, or <paramref name="dependency" /> if it ran inline.</returns>
    JobHandle Update(in SystemContext context, JobHandle dependency);
}

/// <summary>
///     Declares a system's access at construction time, for systems whose component set is not a
///     compile-time constant.
/// </summary>
/// <remarks>
///     Overrides the attributes rather than adding to them, so a system that computes its access has
///     one place that says what it is.
/// </remarks>
public interface IDeclaredAccess {
    /// <summary>What this system reads and writes.</summary>
    SystemAccess Access { get; }
}

/// <summary>A convenient base: no initialisation, no disposal, work done inline.</summary>
/// <remarks>
///     Deliberately not required. <see cref="ISystem" /> is an interface so a system can inherit from
///     something the game already has, and so a struct system is possible later.
/// </remarks>
public abstract class SystemBase : ISystem {
    /// <inheritdoc />
    public virtual void Initialize(in SystemContext context) { }

    /// <inheritdoc />
    public abstract JobHandle Update(in SystemContext context, JobHandle dependency);

    /// <inheritdoc />
    public virtual void Dispose() => GC.SuppressFinalize(this);
}
