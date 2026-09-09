// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Transport;

/// <summary>
///     What a peer says it did not receive of what this end sent it: the far end's inbound counters,
///     carried back over the wire.
/// </summary>
/// <remarks>
///     <para>
///         <b>This is the fifth measurement, and it is why it is not a field on
///         <see cref="TransportLoss" />.</b> That struct carries four totals taken from two kinds of
///         evidence — what this end sent and resent, and what this end expected and missed — and its
///         own remarks refuse to conflate them. "What the peer says it missed of what I sent" is
///         taken by neither: it is a measurement made by a different machine, arriving a round trip
///         late, and absent entirely until the peer chooses to speak. Folding it in beside
///         <see cref="TransportLoss.Retransmitted" /> would hide all three of those properties.
///     </para>
///     <para>
///         <b>It is the only honest outbound loss there is.</b> A sender acknowledges nothing about
///         what it did not get, so the sending end can only count
///         <see cref="TransportLoss.Retransmitted" /> — a consequence of loss that reads high, for
///         the three reasons that field lists. The receiving end knows exactly: its sequence numbers
///         are consecutive, so a gap that falls out of the acknowledgement window is a datagram that
///         was sent and never came. Carrying that back is the whole of the protocol change.
///     </para>
///     <para>
///         <b>Totals, never rates, and cumulative for the life of the link</b> — the rule
///         <c>NetworkMetrics</c> gives at length. Whoever wants a rate differences two readings and
///         divides by the time between them.
///     </para>
///     <para>
///         ⚠ <b>Absent rather than zero, twice over.</b> A peer whose transport does not count
///         losses sends no report at all, so the property holding one of these stays
///         <see langword="null" /> — and a peer that is simply new has not sent its first yet.
///         Neither of those is a clean link, and a chart that drew them as one would be claiming a
///         measurement nobody took.
///     </para>
/// </remarks>
/// <param name="Expected">
///     Inbound sequences the peer judged: every one of them either came or did not, which is what
///     makes this the denominator. The peer's <see cref="TransportLoss.Expected" />, verbatim.
/// </param>
/// <param name="Missing">
///     How many of <paramref name="Expected" /> never arrived. Divided by it, this is the fraction
///     of what this end sent that the far end did not get — observed outbound loss, as opposed to
///     the upper bound a share of <see cref="TransportLoss.Sent" /> gives.
/// </param>
public readonly record struct LinkReport(long Expected, long Missing);
