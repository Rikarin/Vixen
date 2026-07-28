// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Codecs;

/// <summary>What a voice packet carries besides its bytes.</summary>
/// <param name="Sequence">Which transmitted packet this is. Increments by one every time something is sent.</param>
/// <param name="Timestamp">Which frame of the talker's timeline it starts at. Advances by a frame length per <em>elapsed</em> frame.</param>
/// <remarks>
///     <para>
///         <b>Two counters, because one cannot answer the question that matters.</b> A receiver
///         needs to distinguish a packet that was lost from a packet that was deliberately not sent —
///         the sender's gate was shut and nobody was talking. Concealing a loss is right; concealing
///         a silence invents speech into a pause, and sounds like the talker stuttering.
///     </para>
///     <para>
///         <see cref="Sequence" /> counts what left the sender, so a gap in it is loss.
///         <see cref="Timestamp" /> counts the talker's clock, so a gap in it with no gap in sequence
///         is silence. This is what RTP does, for exactly this reason, and no simpler scheme
///         distinguishes the two cases.
///     </para>
/// </remarks>
public readonly record struct VoicePacketHeader(ushort Sequence, uint Timestamp);
