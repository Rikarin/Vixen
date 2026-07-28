// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Messaging;

namespace Vixen.Net.Prediction;

/// <summary>What a client is doing on one tick, as the game defines it.</summary>
/// <remarks>
///     <para>
///         <b>A game defines one of these and the engine never looks inside it.</b> Movement axes, a
///         look direction, which buttons are down — it is a struct with a codec, exactly as
///         <c>IBroadcast</c> is, and for the same reason: <c>static abstract</c> members give both
///         ends the same encoding at compile time, so nothing here reflects over anything at run time
///         and the whole path survives NativeAOT.
///     </para>
///     <para>
///         <b>Small is the whole point.</b> An input goes out every tick, several times over — see
///         <see cref="InputLog{T}.Redundancy" /> — so it is the one payload that is sent more often
///         than a snapshot. Quantize the axes, pack the buttons into bits, and do not put anything in
///         here the server can work out.
///     </para>
///     <para>
///         <b>One input type per session.</b> A game whose players and vehicles want different inputs
///         puts a discriminator in the struct rather than having two — because the tick a payload
///         belongs to is what makes it useful, and two streams of ticks that have to be interleaved is
///         a second ordering problem for no benefit.
///     </para>
/// </remarks>
/// <typeparam name="TSelf">The implementing type.</typeparam>
public interface IPredictedInput<TSelf> where TSelf : struct, IPredictedInput<TSelf> {
    /// <summary>Writes this input.</summary>
    /// <param name="writer">Where it goes.</param>
    void Write(ref BitWriter writer);

    /// <summary>Reads one.</summary>
    /// <param name="reader">Where it comes from.</param>
    /// <param name="value">The input, if it decoded.</param>
    /// <returns>Whether it did.</returns>
    static abstract bool TryRead(ref BitReader reader, out TSelf value);
}

/// <summary>What a client has done recently, and what it has not been told arrived.</summary>
/// <remarks>
///     <para>
///         <b>Two jobs, and they are the same log.</b> It is what goes on the wire, and it is what a
///         rollback replays: reconciling means restoring the server's state for tick T and simulating
///         T+1 to now again, which needs the inputs that were used the first time. A design with a
///         send buffer and a replay buffer would be two copies of one truth.
///     </para>
///     <para>
///         <b>Every packet carries the last several ticks, not just the newest.</b> A lost input is
///         not a lost update — it is a tick the server simulates differently from the client that
///         predicted it, and the divergence is permanent rather than corrected by the next packet. So
///         inputs are sent redundantly, which costs a few bytes and removes the failure entirely for
///         any loss shorter than the redundancy. It is the oldest trick in netcode and it is worth
///         spelling out, because the obvious implementation — send this tick's input, it is
///         unreliable, the next one will fix it — is wrong in a way that only shows up on a bad
///         connection.
///     </para>
///     <para>
///         <b>Trimmed by acknowledgement, not by age.</b> The server says which tick it has everything
///         up to; anything at or below that has been consumed and cannot be needed again, by either
///         job. Trimming by age instead would throw away the inputs a slow acknowledgement still needs
///         for the replay.
///     </para>
/// </remarks>
/// <typeparam name="T">The game's input type.</typeparam>
public sealed class InputLog<T> where T : struct, IPredictedInput<T> {
    readonly Dictionary<uint, T> entries = [];
    readonly List<uint> expired = [];

    Tick newest;
    bool any;

    /// <summary>How many past ticks ride along with the newest one.</summary>
    /// <remarks>
    ///     Four covers a burst of four lost packets, which is far past what a connection anybody can
    ///     play on does. Each one costs whatever the game's input encodes to — for a movement vector
    ///     and eight buttons, a couple of bytes — so this is cheap in a way that reliability would not
    ///     be: a reliable channel would make every input wait behind the one before it, and an input
    ///     that arrives late is worth less than one that does not arrive.
    /// </remarks>
    public int Redundancy { get; init; } = 4;

    /// <summary>The most inputs the log will hold before it starts forgetting the oldest.</summary>
    /// <remarks>
    ///     A bound rather than a policy: the log is trimmed by acknowledgement, and this is what stops
    ///     a client whose acknowledgements have stopped arriving from growing it for ever. Sixty-four
    ///     ticks is a second, which is longer than any round trip a game is playable over.
    /// </remarks>
    public int Capacity { get; init; } = 64;

    /// <summary>The newest tick recorded.</summary>
    public Tick Newest => newest;

    /// <summary>Whether anything has been recorded.</summary>
    public bool HasAny => any;

    /// <summary>How many inputs are held.</summary>
    public int Count => entries.Count;

    /// <summary>The newest tick the server has said it has.</summary>
    public Tick Acknowledged { get; private set; }

    /// <summary>Whether the server has acknowledged anything.</summary>
    public bool HasAcknowledged { get; private set; }

    /// <summary>Inputs dropped because the log was full, which is a connection that has gone quiet.</summary>
    public long OverflowCount { get; private set; }

    /// <summary>Records what the local player did on a tick.</summary>
    /// <param name="tick">The tick.</param>
    /// <param name="input">What they did.</param>
    public void Record(Tick tick, in T input) {
        entries[tick.Value] = input;

        if (!any || tick.IsAfter(newest)) {
            newest = tick;
            any = true;
        }

        Trim();
    }

    /// <summary>Finds what was done on a tick, for replaying it.</summary>
    /// <param name="tick">The tick.</param>
    /// <param name="input">What was done, if it is still held.</param>
    /// <returns>Whether it is.</returns>
    public bool TryGet(Tick tick, out T input) => entries.TryGetValue(tick.Value, out input);

    /// <summary>Writes the newest input and the few before it.</summary>
    /// <param name="buffer">Where to write.</param>
    /// <param name="payload">The payload, if there was anything to send and it fit.</param>
    /// <returns>Whether there was.</returns>
    /// <remarks>
    ///     <para>
    ///         The run is contiguous and starts at the oldest tick sent, so the receiver needs one
    ///         tick number rather than one per input — which for four inputs of two bytes each is a
    ///         third of the packet saved by knowing they are consecutive.
    ///     </para>
    ///     <para>
    ///         <b>The window is walked back from the newest rather than computed</b>, and stops at the
    ///         first tick that is not held. Computing it as <c>newest − redundancy</c> reaches past
    ///         the beginning of the log at the start of a session and sends ticks that never existed —
    ///         which is not merely wasteful: the server counts them as <i>late</i>, and lateness is
    ///         the signal the client steers its lead by, so it would answer by running further ahead
    ///         and paying input latency for it, for ever. Walking makes the run correct by
    ///         construction.
    ///     </para>
    /// </remarks>
    public bool TryWrite(Span<byte> buffer, out ReadOnlySpan<byte> payload) {
        payload = default;

        if (!any || !entries.ContainsKey(newest.Value)) {
            return false;
        }

        var oldest = newest;

        for (var back = 1; back < Redundancy; back++) {
            var candidate = newest.Subtract(back);

            // Never at or below what has been acknowledged: those are inputs the server has already
            // taken, and re-sending them is bandwidth spent on a settled question.
            if (HasAcknowledged && !candidate.IsAfter(Acknowledged)) {
                break;
            }

            if (!entries.ContainsKey(candidate.Value)) {
                break;
            }

            oldest = candidate;
        }

        var count = newest.Subtract(oldest) + 1;
        var writer = new BitWriter(buffer);
        writer.WriteUInt32(oldest.Value);
        writer.Write((uint)count, 8);

        for (var offset = 0; offset < count; offset++) {
            entries[oldest.Add(offset).Value].Write(ref writer);
        }

        return writer.TryFinish(out payload);
    }

    /// <summary>Takes the server's word for what it has.</summary>
    /// <param name="tick">The newest tick the server has an input for.</param>
    public void Acknowledge(Tick tick) {
        if (HasAcknowledged && !tick.IsAfter(Acknowledged)) {
            return;
        }

        Acknowledged = tick;
        HasAcknowledged = true;
        Trim();
    }

    /// <summary>Forgets everything, for a client that is reconnecting.</summary>
    public void Clear() {
        entries.Clear();
        any = false;
        HasAcknowledged = false;
        Acknowledged = default;
    }

    void Trim() {
        // Everything at or below the acknowledgement has been consumed by the server and can no
        // longer be needed by a replay either — a replay starts from a state the server sent, and the
        // server sends states it has already applied the inputs to. Below that, the capacity is the
        // backstop for a client whose acknowledgements have stopped arriving.
        // Subtract(Capacity), not Capacity − 1: everything at or below the floor goes, so a floor of
        // newest − Capacity leaves exactly Capacity entries. Off by one the other way and the log
        // silently holds one fewer than it was told to.
        var floor = newest.Subtract(Capacity);

        if (HasAcknowledged && Acknowledged.IsAfter(floor)) {
            floor = Acknowledged;
        }

        expired.Clear();

        foreach (var key in entries.Keys) {
            if (!new Tick(key).IsAfter(floor)) {
                expired.Add(key);
            }
        }

        foreach (var key in expired) {
            entries.Remove(key);

            // Only what the capacity took counts as overflow. What the acknowledgement took is the
            // mechanism working, and a counter that conflated the two would read as a problem on
            // every healthy connection.
            if (!HasAcknowledged || new Tick(key).IsAfter(Acknowledged)) {
                OverflowCount++;
            }
        }
    }
}
