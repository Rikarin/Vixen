// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vixen.Core;

namespace Vixen.Ai;

/// <summary>A decorator that takes part in a tree-wide named cooldown.</summary>
/// <remarks>
///     What the compiler looks for to size an agent's cooldown table. An interface rather than a
///     hard-coded pair of types, so a project's own "shout cooldown" decorator joins the same table.
/// </remarks>
public interface ITagCooldown {
    /// <summary>The name of the cooldown it reads or starts.</summary>
    Symbol Tag { get; }
}

/// <summary>Inverts what the node it is attached to returns.</summary>
/// <remarks>
///     Gates nothing: a node under an inverter is always entered, and it is the <i>result</i> that
///     turns over. That is the difference between this and a negated condition, and it is why
///     <c>CompositeDecorator</c> with <c>Not</c> exists as well.
/// </remarks>
public sealed class InverterDecorator : BehaviorDecorator {
    /// <inheritdoc />
    public override bool Evaluate(in BehaviorContext context, ReadOnlySpan<byte> state) => true;

    /// <inheritdoc />
    public override ActionStatus Finish(in BehaviorContext context, Span<byte> state, ActionStatus result) =>
        result switch {
            ActionStatus.Succeeded => ActionStatus.Failed,
            ActionStatus.Failed => ActionStatus.Succeeded,
            _ => result
        };
}

/// <summary>Makes the node it is attached to report success whatever it did.</summary>
public sealed class ForceSuccessDecorator : BehaviorDecorator {
    /// <inheritdoc />
    public override bool Evaluate(in BehaviorContext context, ReadOnlySpan<byte> state) => true;

    /// <inheritdoc />
    public override ActionStatus Finish(in BehaviorContext context, Span<byte> state, ActionStatus result) =>
        result == ActionStatus.Running ? result : ActionStatus.Succeeded;
}

/// <summary>Makes the node it is attached to report failure whatever it did.</summary>
public sealed class ForceFailureDecorator : BehaviorDecorator {
    /// <inheritdoc />
    public override bool Evaluate(in BehaviorContext context, ReadOnlySpan<byte> state) => true;

    /// <inheritdoc />
    public override ActionStatus Finish(in BehaviorContext context, Span<byte> state, ActionStatus result) =>
        result == ActionStatus.Running ? result : ActionStatus.Failed;
}

/// <summary>Passes with a fixed probability, from the agent's own stream.</summary>
/// <remarks>
///     Drawn from <see cref="AgentRandom" /> salted by the node, so it is the same answer on every
///     machine for the same agent on the same node — and a <i>different</i> answer for the agent
///     beside it, which is the point. A shared generator here would be a desync per NPC per second.
/// </remarks>
public sealed class RandomChanceDecorator(float probability) : BehaviorDecorator {
    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ The draw is keyed on the node and the number of times the branch has been entered, which
    ///     is what makes it a coin flip rather than a constant: without the count, an agent that
    ///     failed the roll once would fail it for ever.
    /// </remarks>
    public override int StateSize => Unsafe.SizeOf<TimerState>();

    /// <inheritdoc />
    public override bool Evaluate(in BehaviorContext context, ReadOnlySpan<byte> state) {
        var count = state.Length >= Unsafe.SizeOf<TimerState>() ? MemoryMarshal.Read<TimerState>(state).Count : 0;
        var salt = (uint)((context.Node << 12) ^ count);

        return context.Agent.Random(salt) < probability;
    }

    /// <inheritdoc />
    public override void Enter(in BehaviorContext context, Span<byte> state) {
        ref var timer = ref MemoryMarshal.AsRef<TimerState>(state);

        timer.Count++;
    }
}

/// <summary>Refuses entry until a number of seconds has passed since the branch last ended.</summary>
public sealed class CooldownDecorator(float seconds) : BehaviorDecorator {
    /// <inheritdoc />
    public override int StateSize => Unsafe.SizeOf<TimerState>();

    /// <inheritdoc />
    public override bool Evaluate(in BehaviorContext context, ReadOnlySpan<byte> state) {
        var timer = MemoryMarshal.Read<TimerState>(state);

        // Count is what distinguishes "never run" from "ran at time zero", which matters because a
        // tree started at the first frame of a level would otherwise be on cooldown at birth.
        return timer.Count == 0 || context.Now - timer.Stamp >= seconds;
    }

    /// <inheritdoc />
    public override ActionStatus Finish(in BehaviorContext context, Span<byte> state, ActionStatus result) {
        ref var timer = ref MemoryMarshal.AsRef<TimerState>(state);

        timer.Stamp = context.Now;
        timer.Count++;

        return result;
    }
}

/// <summary>A cooldown shared by name across the whole tree.</summary>
/// <remarks>
///     Unreal's pair, and the pair is the point: <see cref="SetTagCooldownDecorator" /> starts the
///     clock wherever the thing actually happened, and any number of these refuse entry until it has
///     run out. "Do not shout again for eight seconds" belongs on the branch that shouts; "do not
///     enter anything that shouts" belongs on four other branches.
/// </remarks>
public sealed class TagCooldownDecorator(Symbol tag, float seconds) : BehaviorDecorator, ITagCooldown {
    /// <inheritdoc />
    /// <remarks>Re-tested every step, so a branch becomes available the moment the clock runs out.</remarks>
    public override bool Continuous => true;

    /// <inheritdoc />
    public override bool Evaluate(in BehaviorContext context, ReadOnlySpan<byte> state) =>
        context.Tree.TagCooldownRemaining(tag, context.Now) <= 0f;

    /// <summary>The tag this waits on.</summary>
    public Symbol Tag => tag;

    /// <summary>How long it waits.</summary>
    public float Seconds => seconds;
}

/// <summary>Starts a named cooldown when the branch it is on finishes.</summary>
public sealed class SetTagCooldownDecorator(Symbol tag, float seconds) : BehaviorDecorator, ITagCooldown {
    /// <inheritdoc />
    public override bool Evaluate(in BehaviorContext context, ReadOnlySpan<byte> state) => true;

    /// <inheritdoc />
    public override ActionStatus Finish(in BehaviorContext context, Span<byte> state, ActionStatus result) {
        context.Tree.StartTagCooldown(tag, context.Now + seconds);

        return result;
    }

    /// <summary>The tag this starts.</summary>
    public Symbol Tag => tag;
}

/// <summary>Fails the branch once it has been running for too long.</summary>
public sealed class TimeLimitDecorator(float seconds) : BehaviorDecorator {
    /// <inheritdoc />
    public override int StateSize => Unsafe.SizeOf<TimerState>();

    /// <inheritdoc />
    /// <remarks>
    ///     Continuous, because nothing writes a key when time passes. Without it the limit would only
    ///     be noticed when the branch happened to finish, which is the one case it does not care
    ///     about.
    /// </remarks>
    public override bool Continuous => true;

    /// <inheritdoc />
    public override bool Evaluate(in BehaviorContext context, ReadOnlySpan<byte> state) {
        var timer = MemoryMarshal.Read<TimerState>(state);

        return timer.Count == 0 || context.Now - timer.Stamp < seconds;
    }

    /// <inheritdoc />
    public override void Enter(in BehaviorContext context, Span<byte> state) {
        ref var timer = ref MemoryMarshal.AsRef<TimerState>(state);

        timer.Stamp = context.Now;
        timer.Count = 1;
    }

    /// <inheritdoc />
    public override ActionStatus Finish(in BehaviorContext context, Span<byte> state, ActionStatus result) {
        ref var timer = ref MemoryMarshal.AsRef<TimerState>(state);

        timer.Count = 0;

        return result;
    }
}

/// <summary>Runs the node again: a fixed number of times, until it fails, or until a timeout.</summary>
public sealed class LoopDecorator : BehaviorDecorator {
    readonly int times;
    readonly float timeout;
    readonly bool untilFailure;

    /// <summary>Loops a fixed number of times.</summary>
    /// <param name="times">How many, counting the first run.</param>
    public LoopDecorator(int times) {
        ArgumentOutOfRangeException.ThrowIfLessThan(times, 1);

        this.times = times;
        timeout = 0f;
    }

    LoopDecorator(float seconds, bool failure) {
        times = 0;
        timeout = seconds;
        untilFailure = failure;
    }

    /// <summary>Loops until the node fails.</summary>
    /// <param name="timeoutSeconds">A bound, or zero for none.</param>
    /// <returns>The decorator.</returns>
    public static LoopDecorator UntilFailure(float timeoutSeconds = 0f) => new(timeoutSeconds, failure: true);

    /// <summary>
    ///     Loops for ever, with a timeout.
    /// </summary>
    /// <param name="timeoutSeconds">How long. Must be positive.</param>
    /// <returns>The decorator.</returns>
    /// <remarks>
    ///     ⚠ <b>A timeout is required, and that is not an over-cautious API.</b> A forever-loop over a
    ///     node that finishes instantly is the one authoring mistake that turns a frame into a
    ///     hang — <see cref="BehaviorTreeInstance.MaximumTransitionsPerStep" /> catches it, but a
    ///     caught runaway is still an agent that does nothing.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeoutSeconds" /> is not positive.</exception>
    public static LoopDecorator Forever(float timeoutSeconds) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutSeconds);

        return new(timeoutSeconds, failure: false);
    }

    /// <inheritdoc />
    public override int StateSize => Unsafe.SizeOf<TimerState>();

    /// <inheritdoc />
    public override bool Evaluate(in BehaviorContext context, ReadOnlySpan<byte> state) => true;

    /// <inheritdoc />
    public override void Enter(in BehaviorContext context, Span<byte> state) {
        ref var timer = ref MemoryMarshal.AsRef<TimerState>(state);

        if (timer.Count == 0) {
            timer.Stamp = context.Now;
        }
    }

    /// <inheritdoc />
    public override bool ShouldRepeat(in BehaviorContext context, Span<byte> state, ActionStatus result) {
        ref var timer = ref MemoryMarshal.AsRef<TimerState>(state);

        timer.Count++;

        if (timeout > 0f && context.Now - timer.Stamp >= timeout) {
            timer.Count = 0;

            return false;
        }

        if (times > 0) {
            if (timer.Count < times) {
                return true;
            }

            timer.Count = 0;

            return false;
        }

        if (untilFailure && result == ActionStatus.Failed) {
            timer.Count = 0;

            return false;
        }

        return true;
    }
}

/// <summary>Runs the node again for as long as a key condition holds.</summary>
public sealed class ConditionalLoopDecorator(BehaviorDecorator condition) : BehaviorDecorator {
    /// <inheritdoc />
    public override ReadOnlySpan<BlackboardKey> ObservedKeys => condition.ObservedKeys;

    /// <inheritdoc />
    /// <remarks>Gates nothing on the way in: the condition is what decides whether to go round again.</remarks>
    public override bool Evaluate(in BehaviorContext context, ReadOnlySpan<byte> state) => true;

    /// <inheritdoc />
    public override bool ShouldRepeat(in BehaviorContext context, Span<byte> state, ActionStatus result) =>
        condition.Evaluate(in context, default);
}
