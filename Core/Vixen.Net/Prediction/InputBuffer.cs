// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Messaging;

namespace Vixen.Net.Prediction;

/// <summary>What one client's inputs are doing, which is what tells them to run further ahead.</summary>
/// <param name="Depth">How many ticks of input are waiting. The number the client steers by.</param>
/// <param name="Starved">Ticks simulated with no input for them.</param>
/// <param name="Late">Inputs that arrived for a tick already simulated.</param>
/// <param name="Duplicate">Inputs already held when they arrived, which is redundancy working.</param>
public readonly record struct InputHealth(int Depth, long Starved, long Late, long Duplicate);

/// <summary>Holds one client's inputs until the tick they belong to comes round. Server-side.</summary>
/// <remarks>
///     <para>
///         <b>A jitter buffer, and the reason a predicting client runs ahead of the server.</b> An
///         input has to be in the server's hands <i>before</i> the tick it is for, or the server
///         simulates that tick without it — so the client stamps its inputs with a tick a little in
///         the future and the server holds them until it gets there. <see cref="TargetDepth" /> is
///         how much slack that is, and the whole of prediction's latency budget is in it: too little
///         and the buffer starves on every jitter spike, too much and the player's own actions take
///         longer to reach the world than they need to.
///     </para>
///     <para>
///         <b>Starvation repeats the last input rather than zeroing.</b> The tick has to be simulated
///         with something, and zero is the worst available choice: a player holding forward would
///         stop dead for one tick on the server while their own client predicted them still moving,
///         which turns a dropped packet into a guaranteed correction. Repeating is usually exactly
///         what the client predicted, so most starvation costs nothing at all — and
///         <see cref="StarvedCount" /> is how anybody finds out it is happening.
///     </para>
///     <para>
///         <b>The counters are a control signal, not diagnostics.</b>
///         <see cref="InputHealth.Depth" /> against <see cref="TargetDepth" /> is what the server
///         sends back so the client can adjust its lead — a buffer that keeps starving means "run
///         further ahead", and one that keeps growing means "you are further ahead than you need to
///         be, and paying for it in input latency".
///     </para>
/// </remarks>
/// <typeparam name="T">The game's input type.</typeparam>
public sealed class InputBuffer<T> where T : struct, IPredictedInput<T> {
    readonly Dictionary<uint, T> entries = [];
    readonly List<uint> expired = [];

    T last;
    bool hasLast;
    Tick newest;
    bool any;

    /// <summary>How many ticks of input the server would like to be holding.</summary>
    /// <remarks>
    ///     Two is a tick of jitter plus one. It is deliberately small: every tick of depth is a tick
    ///     between a player pressing something and the world responding, and that is the cost
    ///     prediction exists to avoid paying twice.
    /// </remarks>
    public int TargetDepth { get; init; } = 2;

    /// <summary>The most ticks of input to hold before refusing the ones furthest ahead.</summary>
    /// <remarks>
    ///     A client that runs far ahead — because its clock is wrong, or because it is trying to — is
    ///     asking the server to remember a second of its future. The bound is what makes that a
    ///     refusal rather than unbounded memory per connection, which is a thing a client can choose
    ///     to do to a server.
    /// </remarks>
    public int Capacity { get; init; } = 32;

    /// <summary>The newest tick an input is held for.</summary>
    public Tick Newest => newest;

    /// <summary>Whether anything is held.</summary>
    public bool HasAny => any;

    /// <summary>How many ticks of input are waiting to be used.</summary>
    public int Depth => entries.Count;

    /// <summary>Ticks simulated with no input, which repeated the last one.</summary>
    public long StarvedCount { get; private set; }

    /// <summary>Inputs that arrived for a tick already simulated, and could not be used.</summary>
    public long LateCount { get; private set; }

    /// <summary>Inputs already held when they arrived. Redundancy working, not a problem.</summary>
    public long DuplicateCount { get; private set; }

    /// <summary>Inputs refused because the client was running further ahead than the buffer holds.</summary>
    public long RefusedCount { get; private set; }

    /// <summary>Payloads that did not decode.</summary>
    public long MalformedCount { get; private set; }

    /// <summary>The newest tick this buffer has an input for, for telling the client.</summary>
    /// <remarks>
    ///     What the client trims its log by. Deliberately the newest <i>held or consumed</i> rather
    ///     than the newest consumed: an input the server is holding will be used, so the client need
    ///     not keep sending it.
    /// </remarks>
    public Tick Acknowledged { get; private set; }

    /// <summary>Whether anything has been received at all.</summary>
    public bool HasAcknowledged { get; private set; }

    /// <summary>Everything worth reporting back, in one value.</summary>
    public InputHealth Health => new(Depth, StarvedCount, LateCount, DuplicateCount);

    /// <summary>Takes a payload of inputs.</summary>
    /// <param name="payload">The bytes as they arrived.</param>
    /// <param name="simulated">The newest tick already simulated, so lateness can be told from jitter.</param>
    /// <returns>Whether it decoded. A false is a malformed packet and nothing was filed.</returns>
    /// <remarks>
    ///     <b>Partial application is deliberate.</b> A payload's later inputs are filed even if an
    ///     earlier one was late or a duplicate, because a run of four inputs where the first two are
    ///     already held is the normal case rather than an error — that is what redundancy looks like
    ///     from this end.
    /// </remarks>
    public bool TryReceive(ReadOnlySpan<byte> payload, Tick simulated) {
        var reader = new BitReader(payload);

        if (!reader.TryReadUInt32(out var first) || !reader.TryRead(8, out var count)) {
            MalformedCount++;

            return false;
        }

        var oldest = new Tick(first);

        for (var offset = 0; offset < count; offset++) {
            if (!T.TryRead(ref reader, out var input)) {
                MalformedCount++;

                return false;
            }

            File(oldest.Add(offset), input, simulated);
        }

        return true;
    }

    /// <summary>Files one input directly, for a server that is also the client.</summary>
    /// <param name="tick">The tick it is for.</param>
    /// <param name="input">The input.</param>
    /// <param name="simulated">The newest tick already simulated.</param>
    /// <remarks>
    ///     A listen server's own player has no packet to decode, and giving it a second path through
    ///     the buffer would mean the host's input being handled by code the tests never run. This is
    ///     the same path minus the reader.
    /// </remarks>
    public void Offer(Tick tick, in T input, Tick simulated) => File(tick, input, simulated);

    /// <summary>Takes the input for a tick, so the tick can be simulated.</summary>
    /// <param name="tick">The tick.</param>
    /// <param name="input">What to simulate with. The last input again if none arrived.</param>
    /// <returns>Whether it was a real input rather than a repeat.</returns>
    /// <remarks>
    ///     Consuming: an input is used once, because the tick it belongs to happens once. Everything
    ///     older is dropped with it, since a tick that has passed cannot be simulated again.
    /// </remarks>
    public bool TryTake(Tick tick, out T input) {
        DropThrough(tick);

        if (entries.Remove(tick.Value, out input)) {
            last = input;
            hasLast = true;

            return true;
        }

        StarvedCount++;
        input = hasLast ? last : default;

        return false;
    }

    /// <summary>Forgets everything, for a player who has gone.</summary>
    public void Clear() {
        entries.Clear();
        expired.Clear();
        any = false;
        hasLast = false;
        HasAcknowledged = false;
        Acknowledged = default;
    }

    void File(Tick tick, in T input, Tick simulated) {
        if (!tick.IsAfter(simulated)) {
            // Its tick has been and gone. Nothing can be done with it, and it is the number that says
            // the client is not running far enough ahead.
            LateCount++;

            return;
        }

        if (entries.ContainsKey(tick.Value)) {
            DuplicateCount++;

            return;
        }

        if (entries.Count >= Capacity) {
            RefusedCount++;

            return;
        }

        entries[tick.Value] = input;

        if (!any || tick.IsAfter(newest)) {
            newest = tick;
            any = true;
        }

        if (!HasAcknowledged || tick.IsAfter(Acknowledged)) {
            Acknowledged = tick;
            HasAcknowledged = true;
        }
    }

    void DropThrough(Tick tick) {
        expired.Clear();

        foreach (var key in entries.Keys) {
            if (new Tick(key).IsBefore(tick)) {
                expired.Add(key);
            }
        }

        foreach (var key in expired) {
            entries.Remove(key);
        }
    }
}
