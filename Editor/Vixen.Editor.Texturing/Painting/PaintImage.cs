// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Editor.Texturing.Painting;

/// <summary>A rectangle of texels, half-open on the high side.</summary>
/// <param name="X">The low column.</param>
/// <param name="Y">The low row.</param>
/// <param name="Width">How many columns.</param>
/// <param name="Height">How many rows.</param>
readonly record struct PaintRect(int X, int Y, int Width, int Height) {
    /// <summary>The rectangle that covers nothing.</summary>
    public static PaintRect Empty => new(0, 0, 0, 0);

    /// <summary>Whether it covers nothing.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>One past the last column.</summary>
    public int EndX => X + Width;

    /// <summary>One past the last row.</summary>
    public int EndY => Y + Height;

    /// <summary>How many texels it covers.</summary>
    public int Area => IsEmpty ? 0 : Width * Height;

    /// <summary>The whole-texel rectangle covering a float region.</summary>
    /// <param name="minimum">The low corner, in texels.</param>
    /// <param name="maximum">The high corner, in texels.</param>
    /// <returns>The rectangle, floored and ceiled outward.</returns>
    public static PaintRect Covering(Vector2 minimum, Vector2 maximum) {
        var x = (int)MathF.Floor(minimum.X);
        var y = (int)MathF.Floor(minimum.Y);
        var endX = (int)MathF.Ceiling(maximum.X);
        var endY = (int)MathF.Ceiling(maximum.Y);

        return new(x, y, Math.Max(0, endX - x), Math.Max(0, endY - y));
    }

    /// <summary>The rectangle clipped to an image.</summary>
    /// <param name="width">The image's width.</param>
    /// <param name="height">Its height.</param>
    /// <returns>The clipped rectangle, possibly empty.</returns>
    public PaintRect Clip(int width, int height) {
        var x = Math.Max(X, 0);
        var y = Math.Max(Y, 0);
        var endX = Math.Min(EndX, width);
        var endY = Math.Min(EndY, height);

        return new(x, y, Math.Max(0, endX - x), Math.Max(0, endY - y));
    }

    /// <summary>The rectangle grown by a margin on every side.</summary>
    /// <param name="margin">How many texels.</param>
    /// <returns>The grown rectangle.</returns>
    public PaintRect Grow(int margin) =>
        IsEmpty ? this : new(X - margin, Y - margin, Width + (2 * margin), Height + (2 * margin));

    /// <summary>The smallest rectangle containing both.</summary>
    /// <param name="other">The other.</param>
    /// <returns>The union. An empty operand returns the other.</returns>
    public PaintRect Union(PaintRect other) {
        if (IsEmpty) {
            return other;
        }

        if (other.IsEmpty) {
            return this;
        }

        var x = Math.Min(X, other.X);
        var y = Math.Min(Y, other.Y);

        return new(x, y, Math.Max(EndX, other.EndX) - x, Math.Max(EndY, other.EndY) - y);
    }

    /// <summary>Whether a texel is inside.</summary>
    /// <param name="x">Its column.</param>
    /// <param name="y">Its row.</param>
    /// <returns>Whether it is inside.</returns>
    public bool Contains(int x, int y) => x >= X && x < EndX && y >= Y && y < EndY;
}

/// <summary>
///     A paint layer's pixels: RGBA, eight bits a channel, straight alpha.
/// </summary>
/// <remarks>
///     <para>
///         <b>Doc 48 § D10: a paint layer stores pixels and not strokes.</b> Storing strokes would
///         make every brush, every falloff and every blend mode a format compatibility surface —
///         change the falloff curve and every existing project repaints differently. This is the
///         other half of that decision: the file beside a <c>.vxlayers</c> holds texels, and
///         <c>PaintStroke</c>'s list of stamps is a session's undo record that is discarded on save.
///     </para>
///     <para>
///         ⚠ <b>Eight bits, and the reason is the size rather than the fidelity.</b> A 4K set is
///         16.8 million texels; at four floats a texel one paint layer is 268 MB and a stack with
///         twelve of them is not openable. Painter and InstaMAT both store eight or sixteen bits for
///         the same arithmetic. The accumulation that would actually suffer from eight bits — a
///         low-flow build-up over hundreds of overlapping stamps — does not happen here at all,
///         because <c>PaintStroke</c> accumulates coverage in <see langword="float" /> and writes
///         each texel <em>once per stamp from the value it had before the stroke</em>. Quantisation
///         therefore happens once, not once per stamp, which is the difference between a rounding
///         error and a drift.
///     </para>
/// </remarks>
sealed class PaintImage {
    /// <summary>How many bytes one texel occupies.</summary>
    public const int BytesPerTexel = 4;

    /// <summary>An image of a size, filled with one colour.</summary>
    /// <param name="width">Its width in texels.</param>
    /// <param name="height">Its height in texels.</param>
    /// <param name="fill">What every texel starts as.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
    public PaintImage(int width, int height, uint fill = 0u) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        Width = width;
        Height = height;
        Texels = new byte[(long)width * height * BytesPerTexel];

        if (fill != 0u) {
            Fill(fill);
        }
    }

    /// <summary>Its width in texels.</summary>
    public int Width { get; }

    /// <summary>Its height in texels.</summary>
    public int Height { get; }

    /// <summary>The texels, RGBA, row-major from the top.</summary>
    public byte[] Texels { get; }

    /// <summary>The whole image, as a rectangle.</summary>
    public PaintRect Bounds => new(0, 0, Width, Height);

    /// <summary>Reads a texel.</summary>
    /// <param name="index">Its index, row-major.</param>
    /// <returns>The texel, packed <c>0xAABBGGRR</c>.</returns>
    public uint this[int index] {
        get {
            var at = index * BytesPerTexel;

            return Texels[at]
                | ((uint)Texels[at + 1] << 8)
                | ((uint)Texels[at + 2] << 16)
                | ((uint)Texels[at + 3] << 24);
        }

        set {
            var at = index * BytesPerTexel;

            Texels[at] = (byte)value;
            Texels[at + 1] = (byte)(value >> 8);
            Texels[at + 2] = (byte)(value >> 16);
            Texels[at + 3] = (byte)(value >> 24);
        }
    }

    /// <summary>Reads a texel by position.</summary>
    /// <param name="x">Its column.</param>
    /// <param name="y">Its row.</param>
    /// <returns>The texel.</returns>
    public uint At(int x, int y) => this[(y * Width) + x];

    /// <summary>Writes every texel.</summary>
    /// <param name="value">The colour.</param>
    public void Fill(uint value) {
        for (var index = 0; index < Width * Height; index++) {
            this[index] = value;
        }
    }

    /// <summary>Packs four 0…1 channels into a texel.</summary>
    /// <param name="r">Red.</param>
    /// <param name="g">Green.</param>
    /// <param name="b">Blue.</param>
    /// <param name="a">Alpha.</param>
    /// <returns>The texel.</returns>
    public static uint Pack(float r, float g, float b, float a) =>
        Quantise(r) | ((uint)Quantise(g) << 8) | ((uint)Quantise(b) << 16) | ((uint)Quantise(a) << 24);

    /// <summary>The channel of a texel, 0…1.</summary>
    /// <param name="texel">The texel.</param>
    /// <param name="channel">Which channel, 0 for red through 3 for alpha.</param>
    /// <returns>The value.</returns>
    public static float Channel(uint texel, int channel) => ((texel >> (channel * 8)) & 0xFF) / 255f;

    /// <summary>
    ///     One texel of <paramref name="from" /> mixed toward <paramref name="to" />.
    /// </summary>
    /// <param name="from">Where the texel started.</param>
    /// <param name="to">The colour being painted.</param>
    /// <param name="amount">How far, 0…1.</param>
    /// <returns>The mixed texel.</returns>
    /// <remarks>
    ///     ⚠ <b>From the value the texel had <em>before the stroke</em>, never from the value it has
    ///     now.</b> Compositing a stamp onto the current value makes a texel that two stamps cross
    ///     darker than a texel one stamp crosses, which is the defect where a slow drag paints
    ///     stronger than a fast one over the same ground. <c>PaintStroke</c> keeps the before-value —
    ///     the undo record already holds it, so the correct arithmetic costs nothing extra.
    /// </remarks>
    public static uint Mix(uint from, uint to, float amount) {
        var t = Math.Clamp(amount, 0f, 1f);
        var mixed = 0u;

        for (var channel = 0; channel < 4; channel++) {
            var a = (from >> (channel * 8)) & 0xFF;
            var b = (to >> (channel * 8)) & 0xFF;

            mixed |= (uint)Quantise(((a + ((b - (float)a) * t)) / 255f)) << (channel * 8);
        }

        return mixed;
    }

    static byte Quantise(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
}
