// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using NVorbis;
using Vixen.Audio.Streaming;

namespace Vixen.Audio.Codecs;

/// <summary>Ogg Vorbis, decoded as it plays.</summary>
/// <remarks>
///     <para>
///         <b>What this is for is not saving disk, it is saving memory.</b> A five-minute track as
///         PCM is fifty megabytes resident; as Vorbis it is five on disk and a few kilobytes at a
///         time in flight. That is the difference between music being a thing a game has and a thing
///         it budgets for.
///     </para>
///     <para>
///         <b>Pure managed, deliberately.</b> A native libvorbis decodes faster and would mean
///         shipping a binary per RID plus a resolver like <c>OpenALLoader</c>'s — and the browser
///         target has no answer for that at all. NVorbis publishes under NativeAOT and runs in
///         WebAssembly, and the decode is a fraction of a per-cent of a core for a stereo stream, on
///         the pump's own thread where it is allowed to take as long as it likes.
///     </para>
///     <para>
///         <b>The container is Ogg and NVorbis owns it.</b> Unlike Opus — where the codec and the
///         container are separate packages and the demuxer is ours — a Vorbis stream only ever comes
///         in an Ogg, and the library that decodes one reads the other.
///     </para>
/// </remarks>
public sealed class VorbisStreamDecoder : IAudioStreamDecoder {
    readonly VorbisReader reader;
    readonly Stream? owned;

    /// <summary>Opens a track from a file.</summary>
    /// <param name="path">Where it is.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path" /> is null.</exception>
    /// <exception cref="InvalidDataException">It is not an Ogg Vorbis stream.</exception>
    public VorbisStreamDecoder(string path)
        : this(OpenFile(path), leaveOpen: false) { }

    /// <summary>Opens a track from a stream.</summary>
    /// <param name="stream">The bytes. Must be seekable for <see cref="Seek" /> to work.</param>
    /// <param name="leaveOpen">Whether the stream outlives this decoder.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream" /> is null.</exception>
    /// <exception cref="InvalidDataException">It is not an Ogg Vorbis stream.</exception>
    public VorbisStreamDecoder(Stream stream, bool leaveOpen = false) {
        ArgumentNullException.ThrowIfNull(stream);

        try {
            reader = new VorbisReader(stream, closeOnDispose: false);
        } catch (Exception exception) when (exception is not OutOfMemoryException) {
            if (!leaveOpen) {
                stream.Dispose();
            }

            throw new InvalidDataException("The stream is not Ogg Vorbis, or its headers are damaged.", exception);
        }

        owned = leaveOpen ? null : stream;
        Format = new AudioFormat(reader.SampleRate, reader.Channels);
        FrameCount = reader.TotalSamples;
        CanSeek = stream.CanSeek;
    }

    /// <inheritdoc />
    public AudioFormat Format { get; }

    /// <inheritdoc />
    public long FrameCount { get; }

    /// <inheritdoc />
    public long Position => reader.SamplePosition;

    /// <inheritdoc />
    public bool CanSeek { get; }

    /// <inheritdoc />
    /// <remarks>
    ///     NVorbis reads interleaved floats, which is what the mixer wants — so this is a bounds check
    ///     and a call, and no conversion happens anywhere.
    /// </remarks>
    public int Decode(Span<float> destination, int frameCount) {
        var channels = Format.Channels;
        var wanted = Math.Min(frameCount, destination.Length / channels) * channels;
        return wanted <= 0 ? 0 : reader.ReadSamples(destination[..wanted]) / channels;
    }

    /// <inheritdoc />
    public void Seek(long frame) {
        if (!CanSeek) {
            throw new NotSupportedException("This Vorbis stream is not seekable.");
        }

        reader.SeekTo(Math.Clamp(frame, 0, FrameCount > 0 ? FrameCount : long.MaxValue));
    }

    /// <inheritdoc />
    public void Dispose() {
        reader.Dispose();
        owned?.Dispose();
    }

    static FileStream OpenFile(string path) {
        ArgumentNullException.ThrowIfNull(path);

        // Sequential, because that is what a stream is — and asynchronous, because the pump thread is
        // allowed to block but should not be spinning a core while it does.
        return new(path, new FileStreamOptions {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.SequentialScan
        });
    }
}
