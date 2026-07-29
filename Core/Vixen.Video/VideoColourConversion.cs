// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Video;

/// <summary>The six numbers that take YUV to RGB, for whoever is doing the arithmetic.</summary>
/// <param name="LumaOffset">Subtracted from luma before anything else — 16 for limited range, 0 for full.</param>
/// <param name="LumaScale">What the offset luma is multiplied by.</param>
/// <param name="RedV">V's contribution to red.</param>
/// <param name="GreenU">U's contribution to green. Negative.</param>
/// <param name="GreenV">V's contribution to green. Negative.</param>
/// <param name="BlueU">U's contribution to blue.</param>
/// <remarks>
///     <para>
///         A record rather than a branch in a loop, because the same six numbers are wanted in two
///         places that cannot share code: <see cref="VideoColourConversion" /> uses them on the CPU,
///         and a material that samples the three planes uses them in a shader constant block. Deriving
///         them twice is how the two paths end up disagreeing by a hair and only on limited-range
///         BT.601.
///     </para>
///     <para>
///         Chroma is taken as <c>sample − 128</c> in every case, so the offset that varies is luma's
///         alone. The scales already carry the 255/219 and 255/224 that limited range needs.
///     </para>
/// </remarks>
public readonly record struct VideoColourCoefficients(
    float LumaOffset,
    float LumaScale,
    float RedV,
    float GreenU,
    float GreenV,
    float BlueU
) {
    /// <summary>Works out the coefficients for a matrix and a range.</summary>
    /// <param name="matrix">Which primaries.</param>
    /// <param name="range">Which range the samples use.</param>
    /// <returns>The coefficients.</returns>
    public static VideoColourCoefficients For(VideoColourMatrix matrix, VideoColourRange range) {
        // The two constants the whole ITU derivation hangs off. Green's is whatever is left.
        var (kr, kb) = matrix switch {
            VideoColourMatrix.Bt601 => (0.299f, 0.114f),
            _ => (0.2126f, 0.0722f)
        };

        var kg = 1f - kr - kb;

        // Limited range packs 0–1 into 16–235 for luma and 16–240 for chroma; undoing it is the
        // whole difference between the two ranges, and it is why a full-range clip decoded as
        // limited looks washed out rather than broken.
        var full = range == VideoColourRange.Full;
        var lumaScale = full ? 1f : 255f / 219f;
        var chromaScale = full ? 1f : 255f / 224f;

        return new VideoColourCoefficients(
            full ? 0f : 16f,
            lumaScale,
            2f * (1f - kr) * chromaScale,
            -2f * kb * (1f - kb) / kg * chromaScale,
            -2f * kr * (1f - kr) / kg * chromaScale,
            2f * (1f - kb) * chromaScale
        );
    }
}

/// <summary>Turns a decoded frame into packed BGRA, on the CPU.</summary>
/// <remarks>
///     <para>
///         <b>This is not the playback path.</b> Playback uploads the planes as they are and converts
///         in the sampler, because at 1080p60 this loop is most of a core and the GPU does it for
///         nothing. What this is for is everything else: a thumbnail, a test that wants to assert on
///         a pixel, a tool that writes a PNG, and a platform whose material stack cannot sample three
///         textures at once.
///     </para>
///     <para>
///         <b>Fixed point, not floating.</b> Sixteen fractional bits are exact enough that no eight-bit
///         output differs from the float result, and the loop stays integer end to end — which
///         matters here because the alternative is four converts per pixel and two million pixels a
///         frame.
///     </para>
/// </remarks>
public static class VideoColourConversion {
    const int FractionBits = 16;
    const int Half = 1 << (FractionBits - 1);

    /// <summary>How many bytes <see cref="ToBgra" /> writes for a format.</summary>
    /// <param name="format">The source format.</param>
    /// <returns>The byte count.</returns>
    public static int BgraSize(in VideoFormat format) => format.Width * format.Height * 4;

    /// <summary>Converts a frame to packed BGRA.</summary>
    /// <param name="frame">The frame.</param>
    /// <param name="destination">
    ///     Where to write, at least <see cref="BgraSize" /> bytes. Rows are tightly packed, top row
    ///     first, and alpha is always <c>255</c>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="frame" /> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination" /> is too small.</exception>
    public static void ToBgra(VideoFrame frame, Span<byte> destination) {
        ArgumentNullException.ThrowIfNull(frame);

        var format = frame.Format;

        if (destination.Length < BgraSize(in format)) {
            throw new ArgumentException(
                $"A {format.Width}×{format.Height} picture needs {BgraSize(in format)} bytes; "
                + $"{destination.Length} were given.",
                nameof(destination)
            );
        }

        switch (format.Layout) {
            case VideoPixelLayout.Bgra8:
                frame.Pixels.CopyTo(destination);

                break;

            case VideoPixelLayout.Grey8:
                Grey(frame, destination);

                break;

            case VideoPixelLayout.Yuv420Planar:
            case VideoPixelLayout.Yuv422Planar:
            case VideoPixelLayout.Yuv444Planar:
                Planar(frame, destination);

                break;

            default:
                throw new ArgumentException($"There is no conversion for {format.Layout}.", nameof(frame));
        }
    }

    static void Grey(VideoFrame frame, Span<byte> destination) {
        var format = frame.Format;
        var coefficients = VideoColourCoefficients.For(format.Matrix, format.Range);
        var scale = Fixed(coefficients.LumaScale);
        var offset = (int)coefficients.LumaOffset;

        for (var y = 0; y < format.Height; y++) {
            var source = frame.Row(0, y);
            var row = destination[(y * format.Width * 4)..];

            for (var x = 0; x < format.Width; x++) {
                var luma = Clamp(((source[x] - offset) * scale) + Half);

                row[x * 4] = luma;
                row[(x * 4) + 1] = luma;
                row[(x * 4) + 2] = luma;
                row[(x * 4) + 3] = 255;
            }
        }
    }

    static void Planar(VideoFrame frame, Span<byte> destination) {
        var format = frame.Format;
        var coefficients = VideoColourCoefficients.For(format.Matrix, format.Range);

        var lumaScale = Fixed(coefficients.LumaScale);
        var lumaOffset = (int)coefficients.LumaOffset;
        var redV = Fixed(coefficients.RedV);
        var greenU = Fixed(coefficients.GreenU);
        var greenV = Fixed(coefficients.GreenV);
        var blueU = Fixed(coefficients.BlueU);

        // How many luma samples share one chroma sample, in each direction. Shifts rather than
        // divisions because the answer is only ever one or two.
        var horizontal = format.Layout == VideoPixelLayout.Yuv444Planar ? 0 : 1;
        var vertical = format.Layout == VideoPixelLayout.Yuv420Planar ? 1 : 0;

        for (var y = 0; y < format.Height; y++) {
            var luma = frame.Row(0, y);
            var chromaRow = y >> vertical;
            var blue = frame.Row(1, chromaRow);
            var red = frame.Row(2, chromaRow);
            var row = destination[(y * format.Width * 4)..];

            for (var x = 0; x < format.Width; x++) {
                var chromaColumn = x >> horizontal;
                var common = (luma[x] - lumaOffset) * lumaScale;
                var u = blue[chromaColumn] - 128;
                var v = red[chromaColumn] - 128;

                row[x * 4] = Clamp(common + (u * blueU) + Half);
                row[(x * 4) + 1] = Clamp(common + (u * greenU) + (v * greenV) + Half);
                row[(x * 4) + 2] = Clamp(common + (v * redV) + Half);
                row[(x * 4) + 3] = 255;
            }
        }
    }

    static int Fixed(float value) => (int)MathF.Round(value * (1 << FractionBits));

    static byte Clamp(int value) {
        var scaled = value >> FractionBits;

        return scaled < 0 ? (byte)0 : scaled > 255 ? (byte)255 : (byte)scaled;
    }
}
