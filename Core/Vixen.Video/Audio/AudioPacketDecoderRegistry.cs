// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio;

namespace Vixen.Video.Audio;

/// <summary>Everything a packet decoder needs to know before it sees a packet.</summary>
/// <param name="CodecId">
///     What the container called it — <c>A_OPUS</c>, <c>A_PCM/INT/LIT</c>. Matroska's spelling, for
///     the reason <see cref="Codecs.VideoTrackInfo" /> gives: a registry has to agree on one.
/// </param>
/// <param name="SampleRate">What the container said the rate is.</param>
/// <param name="Channels">How many channels.</param>
/// <param name="BitDepth">How many bits a sample takes, for the uncompressed codecs. Zero otherwise.</param>
/// <param name="CodecPrivate">The codec's own header — Opus's <c>OpusHead</c> — or empty.</param>
/// <param name="CodecDelay">
///     How much of the decoder's output at the start of the stream is priming rather than sound.
///     Zero for a codec that has none.
/// </param>
/// <remarks>
///     <b><see cref="CodecDelay" /> is not optional for Opus.</b> Every Opus stream begins with
///     samples the encoder needed and the listener must not hear, and a decoder that plays them
///     starts every track with a few milliseconds of artefact. Matroska states it twice — here, and
///     inside the <c>OpusHead</c> — and this is the one that wins when they disagree, because it is
///     the one the muxer wrote knowing what it had put in the clusters.
/// </remarks>
public readonly record struct AudioTrackInfo(
    string CodecId,
    int SampleRate,
    int Channels,
    int BitDepth = 0,
    ReadOnlyMemory<byte> CodecPrivate = default,
    TimeSpan CodecDelay = default
);

/// <summary>Makes packet decoders for the codec ids it recognises.</summary>
public interface IAudioPacketDecoderFactory {
    /// <summary>A name for logs.</summary>
    string Name { get; }

    /// <summary>Whether it could decode a track.</summary>
    /// <param name="track">What the container said about it.</param>
    /// <returns>Whether <see cref="Create" /> would succeed.</returns>
    bool CanDecode(in AudioTrackInfo track);

    /// <summary>Creates a decoder for a track.</summary>
    /// <param name="track">What the container said about it.</param>
    /// <returns>The decoder.</returns>
    /// <exception cref="NotSupportedException">It cannot, in fact, decode this track.</exception>
    IAudioPacketDecoder Create(in AudioTrackInfo track);
}

/// <summary>Which audio codecs this process can pull out of a container.</summary>
/// <remarks>
///     <para>
///         The same arrangement <see cref="Codecs.VideoCodecRegistry" /> has, deliberately: a video's
///         picture and its sound are two codecs in one file, and having one of them be a registry and
///         the other a hard-coded switch would be an asymmetry with no reason behind it.
///     </para>
///     <para>
///         <b>Only the uncompressed cases are registered by default.</b> That is the whole promise of
///         this module — a game that plays one uncompressed sting links no codec — and it is why
///         Opus lives in <c>Vixen.Video.Codecs</c>, which pulls in Concentus, rather than here.
///     </para>
/// </remarks>
public static class AudioPacketDecoderRegistry {
    static readonly Lock Gate = new();
    static readonly List<IAudioPacketDecoderFactory> Factories = [new PcmPacketDecoderFactory()];

    /// <summary>Adds a codec, ahead of everything already registered.</summary>
    /// <param name="factory">What makes it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="factory" /> is null.</exception>
    /// <remarks>
    ///     Ahead, so an application registering a decoder for a codec the engine also handles gets
    ///     its own. Last-registered-wins is the only rule that lets a default be overridden without
    ///     an unregister.
    /// </remarks>
    public static void Register(IAudioPacketDecoderFactory factory) {
        ArgumentNullException.ThrowIfNull(factory);

        lock (Gate) {
            Factories.Insert(0, factory);
        }
    }

    /// <summary>Finds a decoder for a track.</summary>
    /// <param name="track">What the container said about it.</param>
    /// <param name="decoder">The decoder, if one was found.</param>
    /// <returns>Whether one was.</returns>
    public static bool TryCreate(in AudioTrackInfo track, out IAudioPacketDecoder? decoder) {
        IAudioPacketDecoderFactory[] candidates;

        lock (Gate) {
            candidates = [.. Factories];
        }

        foreach (var factory in candidates) {
            if (factory.CanDecode(in track)) {
                decoder = factory.Create(in track);

                return true;
            }
        }

        decoder = null;

        return false;
    }

    /// <summary>The names of every registered codec, for a log line that explains a failure.</summary>
    /// <returns>The names, in the order they are tried.</returns>
    public static IReadOnlyList<string> RegisteredNames() {
        lock (Gate) {
            var names = new string[Factories.Count];

            for (var index = 0; index < Factories.Count; index++) {
                names[index] = Factories[index].Name;
            }

            return names;
        }
    }
}

/// <summary>Makes <see cref="PcmPacketDecoder" />s for the two uncompressed Matroska codecs.</summary>
public sealed class PcmPacketDecoderFactory : IAudioPacketDecoderFactory {
    /// <inheritdoc />
    public string Name => "pcm";

    /// <inheritdoc />
    public bool CanDecode(in AudioTrackInfo track) =>
        track.SampleRate > 0 && track.Channels > 0 && (IsFloat(in track) || IsInteger(in track));

    /// <inheritdoc />
    public IAudioPacketDecoder Create(in AudioTrackInfo track) {
        var isFloat = IsFloat(in track);
        var depth = track.BitDepth > 0 ? track.BitDepth : isFloat ? 32 : 16;

        return new PcmPacketDecoder(new AudioFormat(track.SampleRate, track.Channels), depth, isFloat);
    }

    static bool IsFloat(in AudioTrackInfo track) =>
        track.CodecId.Equals("A_PCM/FLOAT/IEEE", StringComparison.OrdinalIgnoreCase);

    static bool IsInteger(in AudioTrackInfo track) =>
        track.CodecId.Equals("A_PCM/INT/LIT", StringComparison.OrdinalIgnoreCase);
}
