// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Ai;

/// <summary>How a decorator tests a key.</summary>
public enum BlackboardTest : byte {
    /// <summary>The key has been written and not since cleared.</summary>
    IsSet,

    /// <summary>It has not.</summary>
    IsNotSet,

    /// <summary>It equals the value.</summary>
    Equal,

    /// <summary>It does not.</summary>
    NotEqual,

    /// <summary>It is below the value. Numeric keys only.</summary>
    Less,

    /// <summary>It is at or below it.</summary>
    LessOrEqual,

    /// <summary>It is above it.</summary>
    Greater,

    /// <summary>It is at or above it.</summary>
    GreaterOrEqual
}

/// <summary>A key is set, or not set, or compares to a constant.</summary>
/// <remarks>
///     The commonest decorator there is, in every reference implementation. Everything it compares is
///     resolved at construction — the key to an index, the constant to bytes — so a test is a span
///     read and a comparison, and never a name lookup.
/// </remarks>
public sealed class BlackboardDecorator : BehaviorDecorator {
    readonly BlackboardKey[] keys;
    readonly BlackboardTest test;
    readonly float number;
    readonly Symbol symbol;
    readonly Entity entity;

    BlackboardDecorator(
        BlackboardKey key,
        BlackboardTest test,
        ObserverAborts aborts,
        float number = 0f,
        Symbol symbol = default,
        Entity entity = default
    ) {
        keys = [key];
        this.test = test;
        this.number = number;
        this.symbol = symbol;
        this.entity = entity;
        Aborts = aborts;
    }

    /// <inheritdoc />
    public override ObserverAborts Aborts { get; }

    /// <inheritdoc />
    public override ReadOnlySpan<BlackboardKey> ObservedKeys => keys;

    /// <summary>Tests whether a key holds anything at all.</summary>
    /// <param name="key">The key.</param>
    /// <param name="set">Whether it should be set, or should not.</param>
    /// <param name="aborts">What it may interrupt.</param>
    /// <returns>The decorator.</returns>
    public static BlackboardDecorator Set(
        BlackboardKey key,
        bool set = true,
        ObserverAborts aborts = ObserverAborts.None
    ) => new(key, set ? BlackboardTest.IsSet : BlackboardTest.IsNotSet, aborts);

    /// <summary>Tests a boolean key.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">What it should be.</param>
    /// <param name="aborts">What it may interrupt.</param>
    /// <returns>The decorator.</returns>
    public static BlackboardDecorator Bool(
        BlackboardKey key,
        bool value = true,
        ObserverAborts aborts = ObserverAborts.None
    ) => new(key, BlackboardTest.Equal, aborts, value ? 1f : 0f);

    /// <summary>Compares a numeric key against a constant.</summary>
    /// <param name="key">The key.</param>
    /// <param name="test">How to compare.</param>
    /// <param name="value">What to compare against.</param>
    /// <param name="aborts">What it may interrupt.</param>
    /// <returns>The decorator.</returns>
    /// <remarks>
    ///     Whether the key is an <c>Int</c> or a <c>Float</c> is the layout's business, not the
    ///     caller's: both are read as a float and compared as one, so a decorator authored against an
    ///     int key that is later retyped keeps meaning what it said.
    /// </remarks>
    public static BlackboardDecorator Number(
        BlackboardKey key,
        BlackboardTest test,
        float value,
        ObserverAborts aborts = ObserverAborts.None
    ) => new(key, test, aborts, value);

    /// <summary>Compares a symbol key against a constant.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The word it should hold.</param>
    /// <param name="equal">Whether it should match, or should not.</param>
    /// <param name="aborts">What it may interrupt.</param>
    /// <returns>The decorator.</returns>
    public static BlackboardDecorator Word(
        BlackboardKey key,
        Symbol value,
        bool equal = true,
        ObserverAborts aborts = ObserverAborts.None
    ) => new(
        key,
        equal ? BlackboardTest.Equal : BlackboardTest.NotEqual,
        aborts,
        symbol: value
    );

    /// <summary>Compares an entity key against one.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The entity it should name.</param>
    /// <param name="equal">Whether it should match, or should not.</param>
    /// <param name="aborts">What it may interrupt.</param>
    /// <returns>The decorator.</returns>
    public static BlackboardDecorator Is(
        BlackboardKey key,
        Entity value,
        bool equal = true,
        ObserverAborts aborts = ObserverAborts.None
    ) => new(
        key,
        equal ? BlackboardTest.Equal : BlackboardTest.NotEqual,
        aborts,
        entity: value
    );

    /// <inheritdoc />
    public override bool Evaluate(in BehaviorContext context, ReadOnlySpan<byte> state) {
        var board = context.Blackboard;
        var key = keys[0];

        switch (test) {
            case BlackboardTest.IsSet:
                return board.IsSet(key);

            case BlackboardTest.IsNotSet:
                return !board.IsSet(key);
        }

        // ⚠ An unset key fails every comparison rather than comparing as zero. `Entity.Null`, `0`
        // and the zero vector are values somebody means, so "unset" cannot be allowed to look like
        // any of them — which is the whole reason set-ness is a separate bit.
        if (!board.IsSet(key)) {
            return false;
        }

        return board.Layout[key].Type switch {
            BlackboardValueType.Symbol => Match(board.GetSymbol(key) == symbol),
            BlackboardValueType.Entity => Match(board.GetEntity(key) == entity),
            BlackboardValueType.Bool => Match(board.GetBool(key) == (number != 0f)),
            BlackboardValueType.Int => Compare(board.GetInt(key)),
            BlackboardValueType.Float => Compare(board.GetFloat(key)),
            _ => false
        };
    }

    bool Match(bool equal) => test == BlackboardTest.NotEqual ? !equal : equal;

    bool Compare(float value) => test switch {
        BlackboardTest.Equal => value == number,
        BlackboardTest.NotEqual => value != number,
        BlackboardTest.Less => value < number,
        BlackboardTest.LessOrEqual => value <= number,
        BlackboardTest.Greater => value > number,
        BlackboardTest.GreaterOrEqual => value >= number,
        _ => false
    };
}

/// <summary>Two keys against each other.</summary>
/// <remarks>
///     Its own decorator rather than a special case of <see cref="BlackboardDecorator" />, because
///     "is the target closer than the leash" is a different authoring gesture from "is the target
///     closer than four metres" and the editor shows two key pickers rather than one and a number.
/// </remarks>
public sealed class CompareEntriesDecorator : BehaviorDecorator {
    readonly BlackboardKey[] keys;
    readonly BlackboardTest test;

    /// <summary>Compares two keys of the same type.</summary>
    /// <param name="left">The key on the left of the comparison.</param>
    /// <param name="right">The one on the right.</param>
    /// <param name="test">How to compare them.</param>
    /// <param name="aborts">What it may interrupt.</param>
    public CompareEntriesDecorator(
        BlackboardKey left,
        BlackboardKey right,
        BlackboardTest test = BlackboardTest.Equal,
        ObserverAborts aborts = ObserverAborts.None
    ) {
        keys = [left, right];
        this.test = test;
        Aborts = aborts;
    }

    /// <inheritdoc />
    public override ObserverAborts Aborts { get; }

    /// <inheritdoc />
    public override ReadOnlySpan<BlackboardKey> ObservedKeys => keys;

    /// <inheritdoc />
    public override bool Evaluate(in BehaviorContext context, ReadOnlySpan<byte> state) {
        var board = context.Blackboard;

        if (!board.IsSet(keys[0]) || !board.IsSet(keys[1])) {
            return false;
        }

        var type = board.Layout[keys[0]].Type;

        if (type != board.Layout[keys[1]].Type) {
            return false;
        }

        return type switch {
            BlackboardValueType.Symbol => Match(board.GetSymbol(keys[0]) == board.GetSymbol(keys[1])),
            BlackboardValueType.Entity => Match(board.GetEntity(keys[0]) == board.GetEntity(keys[1])),
            BlackboardValueType.Bool => Match(board.GetBool(keys[0]) == board.GetBool(keys[1])),
            BlackboardValueType.Vector3 => Match(board.GetVector3(keys[0]) == board.GetVector3(keys[1])),
            BlackboardValueType.Int => Compare(board.GetInt(keys[0]), board.GetInt(keys[1])),
            BlackboardValueType.Float => Compare(board.GetFloat(keys[0]), board.GetFloat(keys[1])),
            _ => false
        };
    }

    bool Match(bool equal) => test == BlackboardTest.NotEqual ? !equal : equal;

    bool Compare(float left, float right) => test switch {
        BlackboardTest.Equal => left == right,
        BlackboardTest.NotEqual => left != right,
        BlackboardTest.Less => left < right,
        BlackboardTest.LessOrEqual => left <= right,
        BlackboardTest.Greater => left > right,
        BlackboardTest.GreaterOrEqual => left >= right,
        _ => false
    };
}

/// <summary>How a <see cref="CompositeDecorator" /> joins the decorators under it.</summary>
public enum DecoratorLogic : byte {
    /// <summary>Every one of them must pass.</summary>
    And,

    /// <summary>At least one must.</summary>
    Or,

    /// <summary>None of them may.</summary>
    Not
}

/// <summary>AND, OR and NOT over other decorators, so a condition is not a branch.</summary>
/// <remarks>
///     ⚠ <b>This matters more than it looks.</b> Without it, "attack if he is visible <b>and</b> I
///     have ammo <b>and</b> I am not fleeing" is either three stacked decorators — whose failure
///     semantics compose but whose <i>abort</i> semantics do not, because each one aborts on its own
///     — or a branch per combination. Unreal added its equivalent late and its own documentation
///     warns that it costs more than the C++ version; here it is an expression over key indices and
///     the inner decorators never see a step of their own.
/// </remarks>
public sealed class CompositeDecorator : BehaviorDecorator {
    readonly BehaviorDecorator[] operands;
    readonly BlackboardKey[] keys;
    readonly DecoratorLogic logic;

    /// <summary>Joins some decorators.</summary>
    /// <param name="logic">How to join them.</param>
    /// <param name="aborts">What the whole expression may interrupt.</param>
    /// <param name="operands">The decorators.</param>
    /// <exception cref="ArgumentException">There are none.</exception>
    public CompositeDecorator(DecoratorLogic logic, ObserverAborts aborts, params BehaviorDecorator[] operands) {
        ArgumentNullException.ThrowIfNull(operands);

        if (operands.Length == 0) {
            throw new ArgumentException("A composite decorator needs something to join.", nameof(operands));
        }

        this.logic = logic;
        this.operands = operands;
        Aborts = aborts;

        // The union of what the operands read, so that a change to any of them wakes the whole
        // expression. Deduplicated, because two operands on one key would otherwise register twice
        // and the tree would test the expression twice for one write.
        keys = [.. operands.SelectMany(operand => operand.ObservedKeys.ToArray()).Distinct()];
    }

    /// <inheritdoc />
    public override ObserverAborts Aborts { get; }

    /// <inheritdoc />
    public override ReadOnlySpan<BlackboardKey> ObservedKeys => keys;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ The operands are handed an empty span, so an operand that wanted state of its own would
    ///     silently share none. Every decorator that composes is stateless — that is what makes it
    ///     composable — and the compiler has no way to see inside one of these to allocate for it.
    /// </remarks>
    public override bool Evaluate(in BehaviorContext context, ReadOnlySpan<byte> state) {
        switch (logic) {
            case DecoratorLogic.Or:
                foreach (var operand in operands) {
                    if (operand.Evaluate(in context, default)) {
                        return true;
                    }
                }

                return false;

            case DecoratorLogic.Not:
                foreach (var operand in operands) {
                    if (operand.Evaluate(in context, default)) {
                        return false;
                    }
                }

                return true;

            default:
                foreach (var operand in operands) {
                    if (!operand.Evaluate(in context, default)) {
                        return false;
                    }
                }

                return true;
        }
    }
}

/// <summary>A key's position is within an acceptance radius of another, in 2D or in 3D.</summary>
public sealed class IsAtLocationDecorator : BehaviorDecorator {
    readonly BlackboardKey[] keys;
    readonly float radius;
    readonly bool flat;

    /// <summary>Compares two position keys.</summary>
    /// <param name="here">Where the agent is.</param>
    /// <param name="there">Where it is trying to be.</param>
    /// <param name="acceptanceRadius">How close counts as arrived.</param>
    /// <param name="ignoreHeight">Whether to compare on the ground plane only.</param>
    /// <param name="aborts">What it may interrupt.</param>
    public IsAtLocationDecorator(
        BlackboardKey here,
        BlackboardKey there,
        float acceptanceRadius,
        bool ignoreHeight = true,
        ObserverAborts aborts = ObserverAborts.None
    ) {
        keys = [here, there];
        radius = acceptanceRadius;
        flat = ignoreHeight;
        Aborts = aborts;
    }

    /// <inheritdoc />
    public override ObserverAborts Aborts { get; }

    /// <inheritdoc />
    public override ReadOnlySpan<BlackboardKey> ObservedKeys => keys;

    /// <inheritdoc />
    public override bool Evaluate(in BehaviorContext context, ReadOnlySpan<byte> state) {
        var board = context.Blackboard;

        if (!board.IsSet(keys[0]) || !board.IsSet(keys[1])) {
            return false;
        }

        var delta = board.GetVector3(keys[1]) - board.GetVector3(keys[0]);

        if (flat) {
            delta = new(delta.X, 0f, delta.Z);
        }

        return delta.LengthSquared() <= radius * radius;
    }
}

/// <summary>A position is inside a cone from another position, pointing along a direction.</summary>
/// <remarks>
///     The two forms in doc 37 § Part 3 are one class and a flag: <c>Cone</c> gates entry and
///     <c>KeepInCone</c> keeps testing, which is <see cref="BehaviorDecorator.Continuous" />.
/// </remarks>
public sealed class ConeDecorator : BehaviorDecorator {
    readonly BlackboardKey[] keys;
    readonly float cosine;

    /// <summary>Tests a cone.</summary>
    /// <param name="origin">Where the cone starts.</param>
    /// <param name="direction">Which way it points. Normalised on read.</param>
    /// <param name="target">The position to test.</param>
    /// <param name="halfAngleDegrees">Half the cone's opening angle.</param>
    /// <param name="keepTesting">Whether it keeps testing while the branch runs.</param>
    /// <param name="aborts">What it may interrupt.</param>
    public ConeDecorator(
        BlackboardKey origin,
        BlackboardKey direction,
        BlackboardKey target,
        float halfAngleDegrees,
        bool keepTesting = false,
        ObserverAborts aborts = ObserverAborts.None
    ) {
        keys = [origin, direction, target];
        cosine = MathF.Cos(Math.Clamp(halfAngleDegrees, 0f, 180f) * (MathF.PI / 180f));
        Continuous = keepTesting;
        Aborts = aborts;
    }

    /// <inheritdoc />
    public override ObserverAborts Aborts { get; }

    /// <inheritdoc />
    public override bool Continuous { get; }

    /// <inheritdoc />
    public override ReadOnlySpan<BlackboardKey> ObservedKeys => keys;

    /// <inheritdoc />
    public override bool Evaluate(in BehaviorContext context, ReadOnlySpan<byte> state) {
        var board = context.Blackboard;

        foreach (var key in keys) {
            if (!board.IsSet(key)) {
                return false;
            }
        }

        var facing = board.GetVector3(keys[1]);
        var toTarget = board.GetVector3(keys[2]) - board.GetVector3(keys[0]);
        var lengths = facing.Length() * toTarget.Length();

        // A zero-length facing or a target standing exactly on the origin: inside, because a cone
        // with no direction cannot exclude anything and a target at the apex is not outside it.
        return lengths <= 0f || Vector3.Dot(facing, toTarget) / lengths >= cosine;
    }
}

/// <summary>Writes a value the tree computed into a key.</summary>
/// <remarks>Beside the decorators because it reads as one, but it is a task: it does something.</remarks>
public sealed class SetBlackboardValueTask : IAgentAction {
    readonly BlackboardKey key;
    readonly BlackboardKey? copyFrom;
    readonly float number;
    readonly Symbol symbol;
    readonly Entity entity;
    readonly Vector3 vector;

    SetBlackboardValueTask(
        BlackboardKey key,
        BlackboardKey? copyFrom = null,
        float number = 0f,
        Symbol symbol = default,
        Entity entity = default,
        Vector3 vector = default
    ) {
        this.key = key;
        this.copyFrom = copyFrom;
        this.number = number;
        this.symbol = symbol;
        this.entity = entity;
        this.vector = vector;
    }

    /// <summary>Writes a number.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">What to write.</param>
    /// <returns>The task.</returns>
    public static SetBlackboardValueTask Number(BlackboardKey key, float value) => new(key, number: value);

    /// <summary>Writes a word.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">What to write.</param>
    /// <returns>The task.</returns>
    public static SetBlackboardValueTask Word(BlackboardKey key, Symbol value) => new(key, symbol: value);

    /// <summary>Writes an entity.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">What to write.</param>
    /// <returns>The task.</returns>
    public static SetBlackboardValueTask Is(BlackboardKey key, Entity value) => new(key, entity: value);

    /// <summary>Writes a position.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">What to write.</param>
    /// <returns>The task.</returns>
    public static SetBlackboardValueTask At(BlackboardKey key, Vector3 value) => new(key, vector: value);

    /// <summary>Copies one key into another of the same type.</summary>
    /// <param name="target">Where to write.</param>
    /// <param name="source">Where to read.</param>
    /// <returns>The task.</returns>
    public static SetBlackboardValueTask Copy(BlackboardKey target, BlackboardKey source) => new(target, source);

    /// <inheritdoc />
    public void Start(in AgentContext context, Span<byte> state) { }

    /// <inheritdoc />
    public ActionStatus Tick(in AgentContext context, Span<byte> state, float delta) {
        var board = context.Blackboard;
        var source = copyFrom ?? key;

        if (copyFrom is not null && !board.IsSet(source)) {
            return ActionStatus.Failed;
        }

        switch (board.Layout[key].Type) {
            case BlackboardValueType.Bool:
                board.SetBool(key, copyFrom is null ? number != 0f : board.GetBool(source));


                break;

            case BlackboardValueType.Int:
                board.SetInt(key, copyFrom is null ? (int)number : board.GetInt(source));

                break;

            case BlackboardValueType.Float:
                board.SetFloat(key, copyFrom is null ? number : board.GetFloat(source));

                break;

            case BlackboardValueType.Vector3:
                board.SetVector3(key, copyFrom is null ? vector : board.GetVector3(source));

                break;

            case BlackboardValueType.Entity:
                board.SetEntity(key, copyFrom is null ? entity : board.GetEntity(source));

                break;

            default:
                board.SetSymbol(key, copyFrom is null ? symbol : board.GetSymbol(source));

                break;
        }

        return ActionStatus.Succeeded;
    }

    /// <inheritdoc />
    public void Abort(in AgentContext context, Span<byte> state) { }
}

/// <summary>Unsets a key.</summary>
public sealed class ClearBlackboardValueTask(BlackboardKey key) : IAgentAction {
    /// <inheritdoc />
    public void Start(in AgentContext context, Span<byte> state) { }

    /// <inheritdoc />
    public ActionStatus Tick(in AgentContext context, Span<byte> state, float delta) {
        context.Blackboard.Clear(key);

        return ActionStatus.Succeeded;
    }

    /// <inheritdoc />
    public void Abort(in AgentContext context, Span<byte> state) { }
}

/// <summary>Runs an <see cref="IWorldSensor" /> on an interval and writes what it says into a key.</summary>
/// <remarks>
///     ⚠ <b>A service and a sensor are the same thing with two front ends.</b> doc 37 § D13 splits
///     sensors into local and global, and a behaviour tree's service <i>is</i> a local sensor with a
///     schedule — so this is the whole of the tree half, and P6's GOAP reads the same
///     <see cref="IWorldSensor" /> implementations without a tree anywhere.
/// </remarks>
public sealed class UpdateBlackboardService(IWorldSensor sensor, BlackboardKey key) : BehaviorService {
    /// <inheritdoc />
    public override void Tick(in BehaviorContext context, Span<byte> state, float delta) =>
        sensor.Sense(context.Agent, context.Blackboard, key);
}

/// <summary>What a <see cref="MemoryMarshal" />-shaped decorator keeps between steps.</summary>
/// <remarks>Shared by the timed decorators, so that one struct is laid out once and read three ways.</remarks>
[StructLayout(LayoutKind.Sequential)]
struct TimerState {
    public float Stamp;
    public int Count;
}
