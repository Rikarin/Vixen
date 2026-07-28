// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Concentus;

namespace Vixen.Audio.Codecs;

/// <summary>Turns Opus packets back into audio, and invents the ones that never arrived.</summary>
/// <remarks>
///     <para>
///         <b>The interesting half is what it does with nothing.</b> Decoding a packet is a library
///         call. A voice decoder's job is what happens when the packet is not there — and doing
///         nothing is the worst of the options, because a hole in a waveform is a click, and a click
///         is more noticeable than the syllable that went missing.
///     </para>
///     <para>
///         <b>Two ways to not have a packet.</b> Its successor may carry a coarse copy of it, which
///         the encoder put there on purpose and which is very nearly the real audio; failing that,
///         the decoder extrapolates its own state — pitch and spectrum carried forward and faded.
///         Both live in <see cref="Conceal" />, because Opus will not say which one it did.
///     </para>
/// </remarks>
public sealed class OpusPacketDecoder : IDisposable {
    /// <summary>The rate Opus decodes at, whatever it was encoded from.</summary>
    public const int Rate = 48_000;

    readonly IOpusDecoder decoder;

    /// <summary>A decoder for one talker.</summary>
    /// <param name="channels">1 or 2, matching what the far end encodes.</param>
    /// <param name="frameMilliseconds">What the far end packetises at, so a concealed frame is the right length.</param>
    /// <exception cref="ArgumentOutOfRangeException">The channel count or frame length is not one Opus has.</exception>
    public OpusPacketDecoder(int channels = 1, int frameMilliseconds = 20) {
        if (channels is < 1 or > 2) {
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "Opus decodes 1 or 2 channels.");
        }

        FrameSize = OpusPacketEncoder.FramesPer(frameMilliseconds);
        Format = new AudioFormat(Rate, channels);

        OpusRuntime.Ensure();
        decoder = OpusCodecFactory.CreateDecoder(Rate, channels);
    }

    /// <summary>How many frames come out of one packet.</summary>
    public int FrameSize { get; }

    /// <summary>What comes out.</summary>
    public AudioFormat Format { get; }

    /// <summary>How many frames this decoder has had to produce without the packet for them.</summary>
    public long Concealed { get; private set; }

    /// <summary>Decodes a packet that arrived.</summary>
    /// <param name="packet">It.</param>
    /// <param name="pcm">Interleaved output, at least <see cref="FrameSize" /> frames of it.</param>
    /// <returns>Frames written.</returns>
    /// <remarks>
    ///     A packet the decoder refuses is concealed rather than thrown: a corrupt packet and a
    ///     missing one are the same thing as far as the ear is concerned, and a damaged stream should
    ///     degrade rather than stop the game.
    /// </remarks>
    public int Decode(ReadOnlySpan<byte> packet, Span<float> pcm) {
        if (packet.IsEmpty) {
            return Conceal(pcm);
        }

        try {
            var frames = decoder.Decode(packet, pcm, FrameSize, false);

            if (frames > 0) {
                return frames;
            }
        } catch (OpusException) {
            // Fall through.
        }

        return Conceal(pcm);
    }

    /// <summary>Produces a frame that never arrived, out of its successor if that helps.</summary>
    /// <param name="pcm">Interleaved output, at least <see cref="FrameSize" /> frames of it.</param>
    /// <param name="next">
    ///     The packet <em>after</em> the missing one, if it is already in hand. When the far end was
    ///     encoding with <see cref="OpusPacketEncoder.ExpectedPacketLoss" /> set, that packet carries
    ///     a coarse copy of the missing frame, and this reconstructs from it instead of extrapolating.
    /// </param>
    /// <returns>Frames written.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>One method, because Opus does not let these be two.</b> Asking it to decode the
    ///         redundancy out of a packet that has none does not fail — it silently returns
    ///         concealment instead. So there is no way, from outside the codec, to find out which of
    ///         the two a given frame was, and an API with a separate <c>Recover</c> would be inviting
    ///         a caller to count something it cannot actually observe.
    ///     </para>
    ///     <para>
    ///         Passing <paramref name="next" /> is therefore always at least as good as not passing
    ///         it, and never worse.
    ///     </para>
    ///     <para>
    ///         <b>It degrades on purpose.</b> Extrapolating one frame sounds like the talker;
    ///         extrapolating ten sounds like a robot holding a vowel. Opus fades toward silence across
    ///         consecutive concealments, which is why a bad connection sounds choppy rather than
    ///         demonic.
    ///     </para>
    /// </remarks>
    public int Conceal(Span<float> pcm, ReadOnlySpan<byte> next = default) {
        Concealed++;

        try {
            var frames = next.IsEmpty
                ? decoder.Decode(ReadOnlySpan<byte>.Empty, pcm, FrameSize, false)
                : decoder.Decode(next, pcm, FrameSize, true);

            if (frames > 0) {
                return frames;
            }
        } catch (OpusException) {
            // Fall through to silence.
        }

        // A decoder with no state to extrapolate — the very first packet was the one that went
        // missing — has nothing to say, and silence is the honest answer.
        pcm[..(FrameSize * Format.Channels)].Clear();
        return FrameSize;
    }

    /// <summary>Forgets the signal, for a new talker in the same slot.</summary>
    public void Reset() {
        decoder.ResetState();
        Concealed = 0;
    }

    /// <inheritdoc />
    public void Dispose() => decoder.Dispose();
}
