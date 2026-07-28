// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Video.Codecs;

/// <summary>The decoder for video that was never encoded.</summary>
/// <remarks>
///     <para>
///         <b>What <c>PcmStreamDecoder</c> is to audio.</b> Every other codec is a package somebody
///         opts into; this one is here so that the video path can be exercised, tested and shipped
///         without any of them. A title sequence, a UI sting, a rendered cutscene at 320×240 — the
///         cases where a decoder would cost more in binary size than the file costs in disk — are
///         also genuinely served by it.
///     </para>
///     <para>
///         <b>The FourCC is the format.</b> Matroska stores it in <c>ColourSpace</c> on the track and
///         says nothing else about the layout, so a track with no FourCC is taken as I420 — which is
///         what every muxer that writes uncompressed video writes, and the only guess with a
///         defensible default.
///     </para>
///     <para>
///         <b>YV12 exists to be got wrong.</b> It is I420 with the two chroma planes the other way
///         round, and a decoder that ignores the difference produces a picture whose reds are blue.
///         Both are here because both are written, and the swap is one line that has to be in the
///         right place.
///     </para>
/// </remarks>
public sealed class UncompressedVideoCodec : IVideoCodec {
    readonly bool swapChroma;

    /// <summary>Creates a codec for a track.</summary>
    /// <param name="track">What the container said about it.</param>
    /// <exception cref="NotSupportedException">Its FourCC is not one of the layouts handled here.</exception>
    public UncompressedVideoCodec(in VideoTrackInfo track) {
        var layout = LayoutOf(track.FourCc);

        if (layout == VideoPixelLayout.Unknown) {
            throw new NotSupportedException(
                $"'{track.FourCc}' is not an uncompressed layout this codec knows. "
                + "I420, YV12, Y800 and BGRA are."
            );
        }

        swapChroma = Is(track.FourCc, "YV12");

        Format = new VideoFormat(
            track.Width,
            track.Height,
            layout,
            track.FrameRate,
            track.Range,
            track.Matrix
        );

        if (!Format.IsValid) {
            throw new NotSupportedException(
                $"An uncompressed track cannot be {track.Width}×{track.Height}."
            );
        }
    }

    /// <inheritdoc />
    public VideoFormat Format { get; }

    /// <inheritdoc />
    /// <remarks>
    ///     Every packet is a whole picture and every picture is a key frame, so this never returns
    ///     <see cref="VideoDecodeStatus.NeedMoreData" /> — which is exactly why it is the codec the
    ///     rest of the module is developed against.
    /// </remarks>
    public VideoDecodeStatus Decode(in VideoPacket packet, VideoFrame destination) {
        ArgumentNullException.ThrowIfNull(destination);

        var expected = Format.FrameSize;

        if (packet.Data.Length < expected) {
            throw new InvalidDataException(
                $"An uncompressed {Format.Width}×{Format.Height} {Format.Layout} frame is {expected} bytes; "
                + $"the packet has {packet.Data.Length}."
            );
        }

        destination.Reset(Format);
        destination.Timestamp = packet.Timestamp;
        destination.Duration = packet.Duration;
        destination.IsKeyFrame = true;

        if (!swapChroma) {
            packet.Data[..expected].CopyTo(destination.Writable);

            return VideoDecodeStatus.Decoded;
        }

        var plane = Format.PlaneWidth(1) * Format.PlaneHeight(1);
        var luma = Format.PlaneWidth(0) * Format.PlaneHeight(0);

        packet.Data[..luma].CopyTo(destination.Plane(0));
        packet.Data.Slice(luma, plane).CopyTo(destination.Plane(2));
        packet.Data.Slice(luma + plane, plane).CopyTo(destination.Plane(1));

        return VideoDecodeStatus.Decoded;
    }

    /// <inheritdoc />
    /// <remarks>There is nothing to drain: a frame goes in and comes straight back out.</remarks>
    public VideoDecodeStatus Drain(VideoFrame destination) {
        ArgumentNullException.ThrowIfNull(destination);

        return VideoDecodeStatus.EndOfStream;
    }

    /// <inheritdoc />
    /// <remarks>Nothing is held, so nothing is thrown away.</remarks>
    public void Reset() { }

    /// <inheritdoc />
    public void Dispose() { }

    /// <summary>Which layout a FourCC names.</summary>
    /// <param name="fourCc">The code, or empty for the default.</param>
    /// <returns>The layout, or <see cref="VideoPixelLayout.Unknown" /> if it names none of them.</returns>
    public static VideoPixelLayout LayoutOf(string fourCc) {
        if (string.IsNullOrEmpty(fourCc)) {
            return VideoPixelLayout.Yuv420Planar;
        }

        if (Is(fourCc, "I420") || Is(fourCc, "YV12")) {
            return VideoPixelLayout.Yuv420Planar;
        }

        if (Is(fourCc, "Y800") || Is(fourCc, "GREY")) {
            return VideoPixelLayout.Grey8;
        }

        if (Is(fourCc, "I444")) {
            return VideoPixelLayout.Yuv444Planar;
        }

        if (Is(fourCc, "I422")) {
            return VideoPixelLayout.Yuv422Planar;
        }

        return Is(fourCc, "BGRA") ? VideoPixelLayout.Bgra8 : VideoPixelLayout.Unknown;
    }

    static bool Is(string fourCc, string expected) =>
        fourCc.Equals(expected, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Makes <see cref="UncompressedVideoCodec" />s.</summary>
public sealed class UncompressedVideoCodecFactory : IVideoCodecFactory {
    /// <inheritdoc />
    public string Name => "uncompressed";

    /// <inheritdoc />
    public bool CanDecode(in VideoTrackInfo track) =>
        track.CodecId.Equals("V_UNCOMPRESSED", StringComparison.OrdinalIgnoreCase)
        && UncompressedVideoCodec.LayoutOf(track.FourCc) != VideoPixelLayout.Unknown;

    /// <inheritdoc />
    public IVideoCodec Create(in VideoTrackInfo track) => new UncompressedVideoCodec(in track);
}
