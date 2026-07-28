// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Video;

/// <summary>How a decoded frame's samples are arranged in memory.</summary>
/// <remarks>
///     <para>
///         Planar and subsampled, because that is what every video codec in existence produces. A
///         decoder that handed back packed BGRA would be doing a colour conversion the GPU does for
///         free in the sampler, on the CPU, per frame — and at 1080p60 that conversion alone is a
///         core.
///     </para>
///     <para>
///         The layouts here are the ones a container can actually name. Anything more exotic — 10-bit,
///         4:2:2 interleaved, tiled — is a codec's internal business and is converted to one of these
///         before it becomes a <see cref="VideoFrame" />.
///     </para>
/// </remarks>
public enum VideoPixelLayout : byte {
    /// <summary>Not a layout. What a default-constructed format has.</summary>
    Unknown = 0,

    /// <summary>One 8-bit luma plane, no chroma. What a depth or alpha video is.</summary>
    Grey8 = 1,

    /// <summary>
    ///     8-bit luma plus two 8-bit chroma planes at half width and half height — I420, the layout
    ///     of essentially all delivered video.
    /// </summary>
    Yuv420Planar = 2,

    /// <summary>8-bit luma plus two chroma planes at half width and full height.</summary>
    Yuv422Planar = 3,

    /// <summary>8-bit luma plus two chroma planes at full resolution. No subsampling.</summary>
    Yuv444Planar = 4,

    /// <summary>
    ///     One plane of packed BGRA bytes. Not what a codec produces; what an uncompressed capture or
    ///     a CPU conversion does.
    /// </summary>
    Bgra8 = 5
}

/// <summary>Whether the samples use the whole 0–255 range or television's 16–235.</summary>
/// <remarks>
///     Getting this wrong is the single most common video bug there is, and it does not look like a
///     bug: the picture is simply a little washed out or a little crushed, which everybody attributes
///     to the source. It is carried on the format rather than assumed because a container states it
///     and a converter needs it.
/// </remarks>
public enum VideoColourRange : byte {
    /// <summary>16–235 for luma, 16–240 for chroma. What broadcast and almost all delivered video is.</summary>
    Limited = 0,

    /// <summary>0–255 for everything. What a screen capture or a still image is.</summary>
    Full = 1
}

/// <summary>Which set of coefficients takes YUV back to RGB.</summary>
/// <remarks>
///     Two, not the full ITU register: BT.601 is what standard-definition content and most webcams
///     use, BT.709 is what everything HD and above uses. BT.2020 belongs with 10-bit support and
///     neither exists here yet, so it is deliberately absent rather than present and wrong.
/// </remarks>
public enum VideoColourMatrix : byte {
    /// <summary>ITU-R BT.601. Standard definition.</summary>
    Bt601 = 0,

    /// <summary>ITU-R BT.709. High definition, and the default for anything 720p or larger.</summary>
    Bt709 = 1
}

/// <summary>An exact frame rate, as the ratio it actually is.</summary>
/// <param name="Numerator">Frames.</param>
/// <param name="Denominator">Per this many seconds' worth of ticks.</param>
/// <remarks>
///     <para>
///         A rational rather than a <see langword="float" />, because the rates that matter are not
///         representable: NTSC is 30000/1001, which is 29.97002997… and never 29.97. A player that
///         rounds it drifts by a frame every thirty-three seconds, which over a two-hour film is two
///         hundred frames — audio and video visibly apart.
///     </para>
///     <para>
///         Zero denominator means "the container did not say", which is ordinary: WebM stores a
///         per-block timestamp and is under no obligation to state a rate at all. Timing comes from
///         the timestamps; this is for the things that want a nominal rate, such as a UI that shows
///         one.
///     </para>
/// </remarks>
public readonly record struct VideoRational(int Numerator, int Denominator) {
    /// <summary>Film.</summary>
    public static VideoRational Cinema24 => new(24, 1);

    /// <summary>NTSC, exactly. Not 29.97.</summary>
    public static VideoRational Ntsc30 => new(30_000, 1_001);

    /// <summary>PAL, and what most game capture is.</summary>
    public static VideoRational Pal50 => new(50, 1);

    /// <summary>The rate nothing stated.</summary>
    public static VideoRational Unknown => default;

    /// <summary>Whether it describes a rate at all.</summary>
    public bool IsKnown => Numerator > 0 && Denominator > 0;

    /// <summary>The rate as a number, for display and for budgets. Zero if unknown.</summary>
    public double Hz => IsKnown ? (double)Numerator / Denominator : 0d;

    /// <summary>How long one frame lasts, or zero if the rate is unknown.</summary>
    public TimeSpan FrameDuration =>
        IsKnown ? TimeSpan.FromSeconds((double)Denominator / Numerator) : TimeSpan.Zero;
}

/// <summary>What a decoder produces: a size, a layout, and how to read the samples.</summary>
/// <param name="Width">The picture's width in luma samples.</param>
/// <param name="Height">Its height in luma samples.</param>
/// <param name="Layout">How the planes are arranged.</param>
/// <param name="FrameRate">The nominal rate, or <see cref="VideoRational.Unknown" />.</param>
/// <param name="Range">Whether the samples are full-range or limited-range.</param>
/// <param name="Matrix">Which coefficients convert them.</param>
/// <remarks>
///     The layout and the two colour fields travel together because a plane of bytes means nothing
///     without them, and every place that has ever separated them has eventually shown somebody a
///     green picture.
/// </remarks>
public readonly record struct VideoFormat(
    int Width,
    int Height,
    VideoPixelLayout Layout,
    VideoRational FrameRate = default,
    VideoColourRange Range = VideoColourRange.Limited,
    VideoColourMatrix Matrix = VideoColourMatrix.Bt709
) {
    /// <summary>Whether this describes something that could actually be decoded into.</summary>
    public bool IsValid => Width > 0 && Height > 0 && Layout != VideoPixelLayout.Unknown;

    /// <summary>How many planes a frame in this format has.</summary>
    public int PlaneCount => Layout switch {
        VideoPixelLayout.Grey8 or VideoPixelLayout.Bgra8 => 1,
        VideoPixelLayout.Yuv420Planar or VideoPixelLayout.Yuv422Planar or VideoPixelLayout.Yuv444Planar => 3,
        _ => 0
    };

    /// <summary>How wide a plane is, in samples.</summary>
    /// <param name="plane">Which plane. <c>0</c> is luma or the packed plane.</param>
    /// <returns>The width, or <c>0</c> if the plane does not exist.</returns>
    /// <remarks>
    ///     Rounded up, not down. An odd-width 4:2:0 picture has <c>(width + 1) / 2</c> chroma
    ///     samples, because the last column still needs one — rounding down loses the right-hand edge
    ///     of every odd-sized frame, which is exactly the kind of defect that survives review because
    ///     the test video was 1920 wide.
    /// </remarks>
    public int PlaneWidth(int plane) {
        if (plane < 0 || plane >= PlaneCount) {
            return 0;
        }

        if (plane == 0) {
            return Layout == VideoPixelLayout.Bgra8 ? Width * 4 : Width;
        }

        return Layout switch {
            VideoPixelLayout.Yuv420Planar or VideoPixelLayout.Yuv422Planar => (Width + 1) / 2,
            VideoPixelLayout.Yuv444Planar => Width,
            _ => 0
        };
    }

    /// <summary>How tall a plane is, in rows.</summary>
    /// <param name="plane">Which plane.</param>
    /// <returns>The height, or <c>0</c> if the plane does not exist.</returns>
    public int PlaneHeight(int plane) {
        if (plane < 0 || plane >= PlaneCount) {
            return 0;
        }

        if (plane == 0) {
            return Height;
        }

        return Layout switch {
            VideoPixelLayout.Yuv420Planar => (Height + 1) / 2,
            VideoPixelLayout.Yuv422Planar or VideoPixelLayout.Yuv444Planar => Height,
            _ => 0
        };
    }

    /// <summary>How many bytes a frame in this format occupies, tightly packed.</summary>
    public int FrameSize {
        get {
            var total = 0;

            for (var plane = 0; plane < PlaneCount; plane++) {
                total += PlaneWidth(plane) * PlaneHeight(plane);
            }

            return total;
        }
    }

    /// <summary>Whether two formats describe the same bytes, ignoring the nominal frame rate.</summary>
    /// <param name="other">The other format.</param>
    /// <returns>Whether a frame allocated for one can hold a frame of the other.</returns>
    /// <remarks>
    ///     The rate is excluded deliberately. A stream that changes resolution mid-play needs new
    ///     buffers; one whose container states a rate the decoder disagrees with does not, and a
    ///     frame pool that reallocated over that would churn for nothing.
    /// </remarks>
    public bool IsCompatibleWith(in VideoFormat other) =>
        Width == other.Width && Height == other.Height && Layout == other.Layout;
}
