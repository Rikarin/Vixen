// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Transport;

/// <summary>
///     What a transport has counted about datagrams that did not arrive: two totals for each
///     direction, and nothing derived from them.
/// </summary>
/// <remarks>
///     <para>
///         <b>Four counters and no ratio, because the two directions are measured by different
///         evidence and only one of them is loss that was observed.</b> Inbound, a receiver knows
///         exactly what it did not get: the sender's sequence numbers are consecutive, so a gap that
///         falls out of the acknowledgement window is a datagram that was sent and never came —
///         <see cref="Missing" /> out of <see cref="Expected" /> is loss that happened, not loss
///         that was inferred. Outbound, a sender has no such evidence: the far end acknowledges what
///         it received and says nothing about what it did not, so all that can be counted here is
///         <see cref="Retransmitted" /> out of <see cref="Sent" /> — which is a <i>consequence</i> of
///         loss and reads high, for the three reasons under <see cref="Retransmitted" />.
///     </para>
///     <para>
///         <b>Totals, never rates, for the reason <c>NetworkMetrics</c> gives at length.</b> A
///         counter that is already a rate cannot be re-aggregated across three servers, and a
///         lifetime ratio of two counters is an average over the whole uptime — which is the number
///         that hides the thirty seconds somebody is asking about. Whoever wants a rate differences
///         two readings and divides by the time between them; the editor's network panel is one such
///         reader and its ring is what makes the subtraction possible.
///     </para>
///     <para>
///         <b>Absent rather than zero.</b> A transport that does not measure this reports
///         <see langword="null" /> from <see cref="ITransport.Loss" /> rather than a structure full
///         of zeroes. Zero loss and no measurement of loss are different answers, and a chart that
///         draws the second as the first is a chart claiming a clean link.
///     </para>
/// </remarks>
/// <param name="Sent">
///     Reliable datagrams handed to the socket for the first time — retransmissions of them are
///     <see cref="Retransmitted" /> and are not counted here again.
///     <para>
///         ⚠ <b>Datagrams rather than messages or bytes, and reliable ones rather than all of
///         them.</b> Datagrams because a datagram is what the network drops: a router discards a
///         whole packet, so a payload split into eight fragments is eight chances to lose something
///         and one message is not one trial. Not bytes, because loss is not proportional to size
///         over the range a datagram spans and a byte ratio would weight the answer by how large the
///         payloads happened to be. Reliable only, because that is the population
///         <see cref="Retransmitted" /> is drawn from — a denominator that also counted unreliable
///         traffic would move whenever a game changed how much of it it sent, while the link stayed
///         exactly as it was.
///     </para>
/// </param>
/// <param name="Retransmitted">
///     Datagrams sent again because no acknowledgement came for them in time.
///     <para>
///         ⚠ <b>Not a count of losses, and it reads high.</b> One lost datagram that takes three
///         attempts is three; a datagram whose <i>acknowledgement</i> was lost is retransmitted although
///         it arrived; and a round trip that lengthens faster than the estimator follows retransmits
///         datagrams that were merely late. It is the right number to watch — it is what a struggling
///         link produces, and it is what the resends cost — but a share of <see cref="Sent" /> is an
///         upper bound on outbound loss rather than a measurement of it.
///     </para>
/// </param>
/// <param name="Expected">
///     Inbound sequences that have passed out of the acknowledgement window, and so can no longer
///     arrive. Every one of them either came or did not, which is what makes this a denominator.
///     <para>
///         ⚠ <b>Counted when they leave the window rather than when the gap appears, which is what
///         keeps a late datagram from being mourned.</b> A sequence sits under judgement for the
///         thirty-two that follow it; reordering inside that window is invisible here, and anything
///         still in the window is in neither total yet.
///     </para>
/// </param>
/// <param name="Missing">
///     How many of <see cref="Expected" /> never arrived. Observed inbound loss: this over
///     <see cref="Expected" /> is the fraction of what the far end sent that this end did not get.
///     <para>
///         ⚠ <b>What it cannot see.</b> Datagrams lost before the first one arrived on a channel —
///         there is no gap without a sequence either side of it — a peer that sends nothing, and
///         anything the network dropped that was never a numbered datagram at all: a handshake, an
///         acknowledgement, a keep-alive.
///     </para>
/// </param>
public readonly record struct TransportLoss(long Sent, long Retransmitted, long Expected, long Missing);
