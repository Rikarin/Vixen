// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Engine.Players;
using Vixen.Net.Messaging;
using Vixen.Net.Prediction;

namespace Vixen.Net.Engine.Players;

/// <summary>What a player is doing on one tick, on the wire.</summary>
/// <remarks>
///     <para>
///         <c>MoveIntent</c>, quantized. The two are deliberately separate types and not one:
///         <c>MoveIntent</c> is a component in <c>Vixen.Engine</c>, which may not reference
///         <c>Vixen.Net</c> at all, and a component that had to implement a networking interface
///         would drag the whole of it into every game that has a player and no server.
///     </para>
///     <para>
///         <b>Small is the whole point.</b> <see cref="InputLog{T}" /> sends the newest input and the
///         few before it, every tick, so this is the one payload that goes out more often than a
///         snapshot. Fifty-two bits: two axes at eight, two angles at ten, and the buttons whole.
///     </para>
///     <para>
///         <b>The quantization is not a loss the prediction has to tolerate — it is what makes the
///         prediction agree.</b> The client sends this, the server decodes it, and both then run the
///         same movement rules over the same <i>decoded</i> numbers. A client that predicted with its
///         full-precision intent and sent a rounded one would mispredict by the rounding on every
///         single tick, on a perfect connection, and it would look like jitter.
///         <see cref="Round" /> is what a client applies to its own intent to stay in step.
///     </para>
/// </remarks>
/// <remarks>
///     <b>Properties rather than the public fields a component would have.</b> The ECS's exemption
///     from CA1051 is earned by <c>world.Get&lt;T&gt;(e).Y = 9</c> writing through a <c>ref</c> into a
///     chunk, where a property returning a copy would drop the write. Nothing writes into this: it is
///     built once, encoded, and decoded into a new one. So it is a <c>readonly record struct</c>,
///     which also gives the input log a value equality it would otherwise have to be told.
/// </remarks>
public readonly record struct PlayerMoveInput : IPredictedInput<PlayerMoveInput> {
    /// <summary>The movement axes, each in <c>[-1, 1]</c>, at eight bits.</summary>
    /// <remarks>
    ///     Eight bits is a two-hundredth of full deflection, which is finer than a stick's own dead
    ///     zone and far finer than a player can hold. The same width <c>Samples/08-Multiplayer</c>
    ///     picked for the same axes by hand.
    /// </remarks>
    public static QuantizeRange Axis => new(-1f, 1f, 8);

    /// <summary>The look yaw, over a whole turn, at ten bits.</summary>
    /// <remarks>
    ///     A third of a degree. <c>ControlRotation.Turn</c> wraps the yaw into <c>(−π, π]</c>, which
    ///     is why a range this tight is enough — an unwrapped heading would need a width that grew
    ///     with the length of the session.
    /// </remarks>
    public static QuantizeRange Yaw => new(-MathUtil.Pi, MathUtil.Pi, 10);

    /// <summary>The look pitch, over the range a player may aim through, at ten bits.</summary>
    public static QuantizeRange Pitch => new(-MathUtil.PiOverTwo, MathUtil.PiOverTwo, 10);

    /// <summary>Sideways and forwards, each from -1 to 1.</summary>
    public Vector2 Move { get; init; }

    /// <summary>Which way the player is looking, in radians.</summary>
    /// <remarks>
    ///     <b>Absolute, not a delta.</b> Two machines integrating deltas drift apart, and a server
    ///     handed a delta has nothing it can refuse — <c>MoveIntent</c> makes the same choice for the
    ///     same reason, and this is what puts it on the wire unchanged.
    /// </remarks>
    public float LookYaw { get; init; }

    /// <summary>How far up they are looking, in radians.</summary>
    public float LookPitch { get; init; }

    /// <summary>What is held.</summary>
    public MoveButtons Buttons { get; init; }

    /// <summary>The input a player's intent encodes to.</summary>
    /// <param name="intent">The intent.</param>
    /// <returns>The input.</returns>
    public static PlayerMoveInput From(in MoveIntent intent) => new() {
        Move = intent.Move,
        LookYaw = intent.Yaw,
        LookPitch = intent.Pitch,
        Buttons = intent.Buttons
    };

    /// <summary>The intent this input decodes to.</summary>
    /// <returns>The intent.</returns>
    public readonly MoveIntent ToIntent() => new() {
        Move = Move,
        Yaw = LookYaw,
        Pitch = LookPitch,
        Buttons = Buttons
    };

    /// <summary>An intent put through the wire's precision without going near a wire.</summary>
    /// <param name="intent">What the player asked for.</param>
    /// <returns>What both ends will agree they asked for.</returns>
    /// <remarks>
    ///     <b>What a client applies to its own intent before predicting with it.</b> Prediction
    ///     compares the client's guess against the server's answer, and the server's answer was
    ///     computed from the decoded input — so a client predicting with full precision disagrees by
    ///     the rounding on every tick and pays a rollback for it. Rounding first costs a third of a
    ///     degree of aim and removes the whole class.
    /// </remarks>
    public static MoveIntent Round(in MoveIntent intent) => From(intent).Roundtrip().ToIntent();

    /// <summary>This input as it would arrive at the far end.</summary>
    /// <returns>The decoded input.</returns>
    /// <remarks>
    ///     Encoding and decoding for real rather than reimplementing the arithmetic, so this cannot
    ///     drift from <see cref="Write" /> — the failure a hand-written "quantize" helper beside a
    ///     codec always eventually has.
    /// </remarks>
    public readonly PlayerMoveInput Roundtrip() {
        Span<byte> buffer = stackalloc byte[16];
        var writer = new BitWriter(buffer);

        Write(ref writer);

        if (!writer.TryFinish(out var payload)) {
            return this;
        }

        var reader = new BitReader(payload);

        return TryRead(ref reader, out var value) ? value : this;
    }

    /// <inheritdoc />
    public readonly void Write(ref BitWriter writer) {
        writer.WriteQuantized(Move.X, Axis);
        writer.WriteQuantized(Move.Y, Axis);
        writer.WriteQuantized(LookYaw, Yaw);
        writer.WriteQuantized(LookPitch, Pitch);
        writer.Write((uint)Buttons, 16);
    }

    /// <inheritdoc />
    public static bool TryRead(ref BitReader reader, out PlayerMoveInput value) {
        value = default;

        if (!reader.TryReadQuantized(Axis, out var x)
            || !reader.TryReadQuantized(Axis, out var y)
            || !reader.TryReadQuantized(Yaw, out var yaw)
            || !reader.TryReadQuantized(Pitch, out var pitch)
            || !reader.TryRead(16, out var buttons)) {
            return false;
        }

        value = new() {
            Move = new(x, y),
            LookYaw = yaw,
            LookPitch = pitch,
            Buttons = (MoveButtons)buttons
        };

        return true;
    }
}
