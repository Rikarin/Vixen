// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Engine.Coroutines;

namespace Vixen.Engine.Behaviors;

/// <content>
///     The coroutine half of the behaviour API — what
///     [04](../../../docs/plan/04-ecs-and-scripting.md) § Layer 3 calls for in place of Unity's
///     <c>IEnumerator</c> coroutines.
/// </content>
/// <remarks>
///     <para>
///         <code>
///     protected override void Start() =&gt; Run(Patrol());
///
///     async Coroutine Patrol() {
///         while (true) {
///             await MoveTo(LeftPost);
///             await Seconds(2f);
///             await MoveTo(RightPost);
///             await Seconds(2f);
///         }
///     }
///     </code>
///         The infinite loop is not a leak: the behaviour's destruction cancels it at its next resume
///         point, and the <c>while</c> unwinds through whatever <c>finally</c> blocks are in the way.
///     </para>
///     <para>
///         <b>Why one base class rather than Stride's two.</b> Stride splits <c>SyncScript</c>, which
///         has <c>Update</c>, from <c>AsyncScript</c>, whose whole life is one <c>Execute</c> loop. A
///         behaviour usually wants both — a per-frame <c>Update</c> that reads input, and a coroutine
///         that plays out a door opening — and the split forces the choice at the moment a class is
///         declared, which is the moment least is known. Here every behaviour can do both, and a
///         coroutine is something you start rather than something you are.
///     </para>
/// </remarks>
public abstract partial class Behavior {
    /// <summary>
    ///     Bumped by <see cref="StopCoroutines" />. Everything suspended under an older value cancels.
    /// </summary>
    [DataMemberIgnore]
    [EditorVisible(false)]
    public int CoroutineGeneration { get; private set; }

    /// <summary>The scheduler this behaviour's coroutines run on.</summary>
    /// <exception cref="InvalidOperationException">The behaviour is not attached to anything.</exception>
    [DataMemberIgnore]
    [EditorVisible(false)]
    public CoroutineScheduler Coroutines =>
        Store?.Coroutines
        ?? throw new InvalidOperationException(
            "A behaviour that has not been attached to an entity has no scheduler to run coroutines on."
        );

    /// <summary>Starts a coroutine owned by this behaviour.</summary>
    /// <param name="coroutine">The coroutine, as returned by an <c>async Coroutine</c> method.</param>
    /// <returns>A handle that reports whether it is still going.</returns>
    /// <exception cref="InvalidOperationException">The behaviour is not attached to anything.</exception>
    public CoroutineHandle Run(Coroutine coroutine) => Coroutines.Run(coroutine);

    /// <summary>Cancels every coroutine of this behaviour at its next resume point.</summary>
    /// <remarks>
    ///     <para>
    ///         Reaches coroutines this behaviour did not start directly. Everything suspended when
    ///         this is called expressed its wait before the call, so everything suspended carries an
    ///         older generation and cancels — including a coroutine three <c>await</c>s deep inside
    ///         another one. That is the whole reason the counter exists rather than a per-coroutine
    ///         flag.
    ///     </para>
    ///     <para>
    ///         What it does not reach is a coroutine suspended on something that is not one of these
    ///         waits — a <see cref="System.Threading.Tasks.Task" />, a file read. That one cancels
    ///         when it next comes back through <c>ResumeOnLoop</c> or any other wait, and not before.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>"At its next resume point" is a promise about time, and this makes no promise
    ///         about memory.</b> Until that drain the scheduler still holds every one of these
    ///         continuations. A caller that needs the scheduler to have <i>let go</i> — because it is
    ///         about to unload the assembly the coroutine's state machine is a type in — wants
    ///         <see cref="CoroutineScheduler.Cancel" />, which unwinds them before it returns.
    ///         Detaching a behaviour does both, so an author who only ever attaches and detaches
    ///         never has to know the difference.
    ///     </para>
    /// </remarks>
    public void StopCoroutines() => CoroutineGeneration++;

    /// <summary>Waits for the next occurrence of a resume point.</summary>
    /// <param name="point">Where to come back, or <see langword="null" /> for wherever this is running.</param>
    /// <returns>The wait.</returns>
    /// <exception cref="InvalidOperationException">The behaviour is not attached to anything.</exception>
    public CoroutineAwaitable NextFrame(ResumePoint? point = null) => Coroutines.NextFrame(point, this);

    /// <summary>Waits an amount of scaled game time. A paused game does not advance it.</summary>
    /// <param name="seconds">How long.</param>
    /// <param name="point">Where to come back, or <see langword="null" /> for wherever this is running.</param>
    /// <returns>The wait.</returns>
    /// <exception cref="InvalidOperationException">The behaviour is not attached to anything.</exception>
    public CoroutineAwaitable Seconds(float seconds, ResumePoint? point = null) =>
        Coroutines.Seconds(seconds, point, this);

    /// <summary>Waits an amount of unscaled time, which a pause does not stop.</summary>
    /// <param name="seconds">How long.</param>
    /// <param name="point">Where to come back, or <see langword="null" /> for wherever this is running.</param>
    /// <returns>The wait.</returns>
    /// <exception cref="InvalidOperationException">The behaviour is not attached to anything.</exception>
    public CoroutineAwaitable UnscaledSeconds(float seconds, ResumePoint? point = null) =>
        Coroutines.UnscaledSeconds(seconds, point, this);

    /// <summary>Waits until a predicate is true, testing it once per occurrence of the resume point.</summary>
    /// <param name="predicate">The test.</param>
    /// <param name="point">Where to come back, or <see langword="null" /> for wherever this is running.</param>
    /// <returns>The wait.</returns>
    /// <exception cref="InvalidOperationException">The behaviour is not attached to anything.</exception>
    public CoroutineAwaitable Until(Func<bool> predicate, ResumePoint? point = null) =>
        Coroutines.Until(predicate, point, this);

    /// <summary>Waits while a predicate is true.</summary>
    /// <param name="predicate">The test.</param>
    /// <param name="point">Where to come back, or <see langword="null" /> for wherever this is running.</param>
    /// <returns>The wait.</returns>
    /// <exception cref="InvalidOperationException">The behaviour is not attached to anything.</exception>
    public CoroutineAwaitable While(Func<bool> predicate, ResumePoint? point = null) =>
        Coroutines.While(predicate, point, this);

    /// <summary>Comes back to the loop thread after awaiting something that left it.</summary>
    /// <param name="point">Where to come back.</param>
    /// <returns>The wait.</returns>
    /// <exception cref="InvalidOperationException">The behaviour is not attached to anything.</exception>
    /// <remarks>
    ///     Everything before this may be on a thread pool thread and must not touch the world;
    ///     everything after it is back on the loop thread and may.
    /// </remarks>
    public CoroutineAwaitable ResumeOnLoop(ResumePoint point = ResumePoint.Update) =>
        Coroutines.ResumeOnLoop(point, this);
}
