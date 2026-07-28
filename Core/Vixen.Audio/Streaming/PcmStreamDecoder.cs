// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.Buffers.Binary;

namespace Vixen.Audio.Streaming;

/// <summary>Reads interleaved PCM out of a stream, without a codec.</summary>
/// <remarks>
///     <para>
///         What a clip the content build decided was too big to hold in memory looks like: the same
///         bytes <see cref="AudioClip.Samples" /> would have held, left in the bundle and read as
///         they are needed. No compression, so no decoder — which makes this the implementation that
///         proves the streaming path works before any codec exists, and the one a project with
///         plenty of disk and no patience for Opus can ship on.
///     </para>
///     <para>
///         Little-endian, like everything the serializer writes, and the same two sample formats
///         <see cref="AudioClip" /> allows, for the same reason.
///     </para>
/// </remarks>
public sealed class PcmStreamDecoder : IAudioStreamDecoder {
    readonly Stream stream;
    readonly bool ownsStream;
    readonly long dataOffset;
    readonly long dataLength;
    readonly int bytesPerSample;
    readonly int bytesPerFrame;
    readonly AudioSampleFormat sampleFormat;
    long position;

    /// <summary>A decoder over a region of a stream.</summary>
    /// <param name="stream">Where the bytes are. Must be readable and seekable.</param>
    /// <param name="format">The rate and channel count the bytes are in.</param>
    /// <param name="sampleFormat">How one sample is stored.</param>
    /// <param name="dataOffset">Where the samples start.</param>
    /// <param name="dataLength">How many bytes of samples there are, or <c>-1</c> for the rest.</param>
    /// <param name="ownsStream">Whether disposing this should dispose the stream.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream" /> is null.</exception>
    /// <exception cref="ArgumentException">The stream cannot be read and seeked, or the format is not valid.</exception>
    public PcmStreamDecoder(
        Stream stream,
        AudioFormat format,
        AudioSampleFormat sampleFormat = AudioSampleFormat.Int16,
        long dataOffset = 0,
        long dataLength = -1,
        bool ownsStream = true
    ) {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanRead || !stream.CanSeek) {
            throw new ArgumentException(
                "A PCM stream has to be readable and seekable: the pump reads it in blocks and a "
                + "loop seeks it back to the start.",
                nameof(stream)
            );
        }

        if (!format.IsValid) {
            throw new ArgumentException($"{format} is not a format anything can be decoded into.", nameof(format));
        }

        this.stream = stream;
        this.ownsStream = ownsStream;
        this.sampleFormat = sampleFormat;
        this.dataOffset = dataOffset;
        this.dataLength = dataLength >= 0 ? dataLength : stream.Length - dataOffset;

        Format = format;
        bytesPerSample = sampleFormat is AudioSampleFormat.Float32 ? 4 : 2;
        bytesPerFrame = bytesPerSample * format.Channels;
        FrameCount = this.dataLength / bytesPerFrame;
    }

    /// <inheritdoc />
    public AudioFormat Format { get; }

    /// <inheritdoc />
    public long FrameCount { get; }

    /// <inheritdoc />
    public long Position => position;

    /// <summary>Always. A region of a seekable stream is seekable.</summary>
    public bool CanSeek => true;

    /// <inheritdoc />
    public int Decode(Span<float> destination, int frameCount) {
        var wanted = (int)Math.Min(frameCount, FrameCount - position);

        if (wanted <= 0) {
            return 0;
        }

        var bytes = wanted * bytesPerFrame;
        var rented = ArrayPool<byte>.Shared.Rent(bytes);

        try {
            stream.Position = dataOffset + (position * bytesPerFrame);
            var read = stream.ReadAtLeast(rented.AsSpan(0, bytes), bytes, throwOnEndOfStream: false);
            var frames = read / bytesPerFrame;

            if (frames <= 0) {
                return 0;
            }

            Widen(rented.AsSpan(0, frames * bytesPerFrame), destination, frames * Format.Channels);
            position += frames;
            return frames;
        } finally {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <inheritdoc />
    public void Seek(long frame) => position = Math.Clamp(frame, 0, FrameCount);

    /// <inheritdoc />
    public void Dispose() {
        if (ownsStream) {
            stream.Dispose();
        }
    }

    void Widen(ReadOnlySpan<byte> source, Span<float> destination, int samples) {
        if (sampleFormat is AudioSampleFormat.Float32) {
            for (var i = 0; i < samples; i++) {
                destination[i] = BitConverter.Int32BitsToSingle(
                    BinaryPrimitives.ReadInt32LittleEndian(source[(i * 4)..])
                );
            }

            return;
        }

        for (var i = 0; i < samples; i++) {
            destination[i] = BinaryPrimitives.ReadInt16LittleEndian(source[(i * 2)..]) / 32_768f;
        }
    }
}
