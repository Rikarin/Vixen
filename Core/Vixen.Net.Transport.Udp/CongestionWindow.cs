// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Transport.Udp;

/// <summary>How many reliable datagrams one connection may have on the wire at once.</summary>
/// <remarks>
///     <para>
///         <b>A window rather than a cap, which is the whole difference.</b>
///         <see cref="UdpTransportOptions.MaxUnacknowledged" /> is a bound on memory: it does not
///         move, so a link that has started dropping is offered exactly as much traffic as one that
///         has not. This moves — additively up on acknowledgement, multiplicatively down on a
///         retransmission — so the sender's offered load follows the path's capacity instead of
///         arguing with it.
///     </para>
///     <para>
///         <b>Additive-increase/multiplicative-decrease, and no slow start.</b> Slow start exists to
///         find the capacity of a path a bulk transfer is about to saturate for minutes. A game
///         connection sends a tick's worth and stops, so starting at one datagram would rate-limit
///         the first second of every match to find a number the initial window already states. What
///         is kept from TCP is the part that matters here: the decrease is multiplicative and the
///         increase is not, which is what makes several senders sharing a bottleneck converge on a
///         fair share rather than oscillate.
///     </para>
///     <para>
///         ⚠ <b>The decrease is once per loss <i>event</i>, not once per lost datagram.</b> A
///         retransmission pass routinely finds a whole window's worth due at the same moment — they
///         were sent together and the same outage covered all of them. Halving once for each would
///         take the window to its floor on a single hiccup and keep it there, which is a worse answer
///         than not responding to loss at all. One pass that found anything is one event.
///     </para>
///     <para>
///         The unit is datagrams and not bytes, because that is the unit the thing being limited is
///         counted in — <c>ChannelSender</c> remembers whole datagrams — and because
///         <see cref="UdpProtocol.MaxDatagramBytes" /> means a datagram's size varies by less than
///         the window does.
///     </para>
/// </remarks>
sealed class CongestionWindow {
    readonly double minimum;
    readonly double maximum;

    double window;

    /// <summary>How many datagrams may be in flight now.</summary>
    public int Limit => (int)Math.Max(minimum, Math.Min(maximum, window));

    /// <summary>How many times the window has been halved.</summary>
    /// <remarks>
    ///     A counter rather than a rate, so a test can assert that loss reached the controller
    ///     without asserting how long it took to. One per loss event, so it is also the number of
    ///     events.
    /// </remarks>
    public long ShrinkCount { get; private set; }

    /// <summary>How many times the window has grown by a whole datagram.</summary>
    public long GrowthCount { get; private set; }

    /// <summary>Creates a window.</summary>
    /// <param name="initial">Where it starts.</param>
    /// <param name="minimum">The floor. Never zero, or a connection could not recover.</param>
    /// <param name="maximum">The ceiling, which is the memory cap.</param>
    public CongestionWindow(int initial, int minimum, int maximum) {
        this.minimum = Math.Max(1, minimum);
        this.maximum = Math.Max(this.minimum, maximum);
        window = Math.Clamp(initial, this.minimum, this.maximum);
    }

    /// <summary>Takes an acknowledgement, and opens the window by a fraction of a datagram.</summary>
    /// <param name="count">How many datagrams the acknowledgement retired.</param>
    /// <remarks>
    ///     A window's worth of acknowledgements adds one datagram, which is what makes the increase
    ///     additive per round trip rather than per packet — the round trip is not measured, it is
    ///     what a window's worth of acknowledgements takes.
    /// </remarks>
    public void Acknowledged(int count) {
        if (count <= 0) {
            return;
        }

        var before = Limit;

        window = Math.Min(maximum, window + (count / window));

        if (Limit > before) {
            GrowthCount++;
        }
    }

    /// <summary>Takes a loss event, and halves the window.</summary>
    public void Lost() {
        window = Math.Max(minimum, window / 2);
        ShrinkCount++;
    }
}
