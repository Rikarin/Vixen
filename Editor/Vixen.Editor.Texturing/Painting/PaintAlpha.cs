// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Terrain;

namespace Vixen.Editor.Texturing.Painting;

/// <summary>A brush alpha read out of a <see cref="PaintImage" />, one channel of it, bilinearly.</summary>
/// <remarks>
///     <para>
///         <b>⚠ The first implementation of <c>IBrushMask</c> anywhere in this tree, which is why
///         the rotation knob was unreachable</b> —
///         <a href="https://github.com/Rikarin/Vixen/issues/1083">#1083</a>. <c>Vixen.Terrain</c>
///         declared the interface and every caller in the repository passed null, so
///         <c>BrushShape.Alpha</c> and <c>BrushRotation</c> were settings whose effect was
///         unreachable: <c>PaintBrush.KernelFor</c> picks <c>BrushShape.Circle</c> for a null mask
///         and a disc turned is a disc.
///     </para>
///     <para>
///         ⚠ <b>A <see cref="PaintImage" /> and not a decoded file, and that is what makes this the
///         path an artist's own alpha will take.</b> <see cref="PaintAlphas" /> renders the shipped
///         shelf into images and wraps them here rather than sampling its shapes analytically — so
///         the sampling, the addressing and the unit-square contract are exercised by the shelf
///         today and are not a second implementation waiting for the day a texture picker arrives.
///         An imported picture is Rgba8 bytes, which is what a <see cref="PaintImage" /> is.
///     </para>
///     <para>
///         ⚠ <b>Outside the unit square it is zero, because <c>IBrushMask</c> says so and because
///         the alternative silently changes the stamp's shape.</b> Clamping the address instead
///         would smear the mask's border row over everything the footprint reaches past the square —
///         and the footprint rectangle is <c>√2</c> radii, so there is always such a region.
///     </para>
/// </remarks>
sealed class PaintImageMask : IBrushMask {
    readonly PaintImage image;
    readonly int channel;

    /// <summary>Reads a mask out of an image.</summary>
    /// <param name="image">The image. Held, not copied.</param>
    /// <param name="channel">Which channel carries the weight, 0 for red through 3 for alpha.</param>
    /// <exception cref="ArgumentNullException"><paramref name="image" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The channel is not one of the four.</exception>
    public PaintImageMask(PaintImage image, int channel = 3) {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channel, 3);

        this.image = image;
        this.channel = channel;
    }

    /// <summary>The image behind it, for a preview that wants to draw the brush's shape.</summary>
    public PaintImage Image => image;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Bilinear rather than nearest, and the reason is the size a stamp is used at.</b> A
    ///     shelf alpha is a few hundred texels across and a radius may be five hundred, so a nearest
    ///     tap makes the stamp's edge a staircase of mask texels — an artefact that only appears on
    ///     the large brushes, which are the ones an artist blocks a material in with. The taps are
    ///     clamped to the edge <em>after</em> the unit-square test above, so clamping never widens
    ///     the mask; it only stops a half-texel at the border reading off the end of the row.
    /// </remarks>
    public float Sample(Vector2 uv) {
        if (!(uv.X >= 0f) || !(uv.X <= 1f) || !(uv.Y >= 0f) || !(uv.Y <= 1f)) {
            return 0f;
        }

        var x = (uv.X * image.Width) - 0.5f;
        var y = (uv.Y * image.Height) - 0.5f;
        var x0 = (int)MathF.Floor(x);
        var y0 = (int)MathF.Floor(y);
        var fx = x - x0;
        var fy = y - y0;

        var top = Lerp(Texel(x0, y0), Texel(x0 + 1, y0), fx);
        var bottom = Lerp(Texel(x0, y0 + 1), Texel(x0 + 1, y0 + 1), fx);

        return Math.Clamp(Lerp(top, bottom, fy), 0f, 1f);
    }

    /// <summary>One tap, as the one byte it is.</summary>
    /// <remarks>
    ///     ⚠ <b>Straight out of the byte array rather than through <c>PaintImage.At</c> and
    ///     <c>PaintImage.Channel</c>.</b> Those two assemble a texel out of four bytes so that this
    ///     can shift three of them straight back out — four times per texel painted, in the inner
    ///     loop of a stamp whose footprint is already twice a disc's. One load against four loads,
    ///     three shifts, three ors, a shift and a mask, for the same number.
    ///     <para>
    ///         ⚠ <b>The saving is argued from the work and not from a measurement, deliberately.</b>
    ///         <c>PaintAlphaTests</c>' milliseconds move by four times with nothing but the order the
    ///         two brushes are measured in — two hundred megabytes of atlas apiece — so a wall clock
    ///         here says more about the collector than about the tap.
    ///     </para>
    /// </remarks>
    float Texel(int x, int y) {
        var column = Math.Clamp(x, 0, image.Width - 1);
        var row = Math.Clamp(y, 0, image.Height - 1);

        return image.Texels[(((row * image.Width) + column) * PaintImage.BytesPerTexel) + channel] / 255f;
    }

    static float Lerp(float from, float to, float amount) => from + ((to - from) * amount);
}

/// <summary>The brush alphas the editor ships, by name.</summary>
/// <remarks>
///     <para>
///         <b>⚠ The "somewhere for the artist to choose one" half of
///         <a href="https://github.com/Rikarin/Vixen/issues/1083">#1083</a>, and it is a fixed shelf
///         rather than a picker for a stated reason.</b> The three shapes on the table were a
///         brush-preset asset, a picker over the project's own images, and this. A picker is the one
///         an artist eventually wants and it is also the one that cannot be built here yet:
///         <c>PaintBrushInspector</c>'s remarks record why that column is C# rather than
///         <c>.vxml</c> — a plugin's entry assembly must not declare a <c>[DataContract]</c>, #881 —
///         so the asset-reference field and its picker, which are <c>PropertyField</c>'s, are not
///         available to it. A shelf needs none of that, is reachable today, and is what every
///         reference toolset opens with anyway.
///         <a href="https://github.com/Rikarin/Vixen/issues/1090">#1090</a> is the picker.
///     </para>
///     <para>
///         ⚠ <b><see cref="Round" /> is a name for <see langword="null" /> and not an image of a
///         disc.</b> A round brush is the falloff on its own, which is exactly what a null alpha
///         gives — and it is cheaper, since it skips the mask tap per texel. Shipping a disc image
///         would be a second, slightly different circle: <c>TerrainBrush</c>'s whole argument
///         against a second falloff, in miniature.
///     </para>
///     <para>
///         ⚠ <b>Every shape here fills the square rather than fitting inside the disc, which is the
///         point.</b> A mask whose support is inside the inscribed circle would be a shape whose
///         rotation an artist could not see at the edges. The corners are reachable as of #1083.
///     </para>
/// </remarks>
static class PaintAlphas {
    /// <summary>How many texels across each shipped alpha is rendered.</summary>
    /// <remarks>
    ///     Big enough that the bilinear tap is the thing softening a large stamp's edge rather than
    ///     the raster, and small enough that the whole shelf is under a megabyte. A 512-texel radius
    ///     — <c>PaintTool.MaximumRadius</c> — reads this at rather better than one mask texel per
    ///     four atlas texels.
    /// </remarks>
    public const int Resolution = 256;

    /// <summary>The plain disc: no mask at all.</summary>
    public const string Round = "Round";

    /// <summary>A filled square, which is the shape a rotation is most visible on.</summary>
    public const string Square = "Square";

    /// <summary>A flat band across the square: a chisel, whose angle is what an artist sets.</summary>
    public const string Chisel = "Chisel";

    /// <summary>Two crossed bands, so a quarter turn is visibly a different stamp from an eighth.</summary>
    public const string Cross = "Cross";

    static readonly Dictionary<string, IBrushMask> Shelf = Build();

    /// <summary>Every alpha an artist may pick, <see cref="Round" /> first.</summary>
    public static IReadOnlyList<string> Names { get; } = [Round, Square, Chisel, Cross];

    /// <summary>The mask a name stands for.</summary>
    /// <param name="name">The name, from <see cref="Names" />.</param>
    /// <returns>
    ///     The mask, or <see langword="null" /> for <see cref="Round" /> <em>and</em> for a name this
    ///     shelf does not carry.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>An unknown name is a round brush rather than an exception</b>, on
    ///     <c>TerrainBrush.WeightAt</c>'s own reasoning about a mask that has not loaded: a brush
    ///     restored from a file written by a later build should paint rather than take the panel
    ///     down. <c>PaintTool.SetAlpha</c> is what notices the difference, because it reports back
    ///     the name it settled on rather than the one it was given.
    /// </remarks>
    public static IBrushMask? Find(string? name) =>
        name is not null && Shelf.TryGetValue(name, out var mask) ? mask : null;

    static Dictionary<string, IBrushMask> Build() =>
        new(StringComparer.Ordinal) {
            [Square] = Render(static (u, v, soft) => MathF.Min(Band(u, 0.5f, soft), Band(v, 0.5f, soft))),
            [Chisel] = Render(static (u, v, soft) => MathF.Min(Band(u, 0.5f, soft), Band(v, 0.18f, soft))),
            [Cross] = Render(static (u, v, soft) =>
                MathF.Max(
                    MathF.Min(Band(u, 0.5f, soft), Band(v, 0.18f, soft)),
                    MathF.Min(Band(v, 0.5f, soft), Band(u, 0.18f, soft))
                ))
        };

    /// <summary>Renders a shape into an image and wraps it as a mask.</summary>
    /// <remarks>
    ///     The weight goes in the alpha channel and the colour channels are left at zero: nothing
    ///     reads them, and a white fill would make a mask that looked like a stamp of white paint in
    ///     any preview that drew it as an image.
    /// </remarks>
    static IBrushMask Render(Func<float, float, float, float> shape) {
        PaintImage image = new(Resolution, Resolution);

        // One texel of the mask, which is the width the edges are feathered over. Any narrower and
        // the shape aliases at the raster; any wider and a chisel's band stops having a straight edge.
        const float Soft = 1f / Resolution;

        for (var y = 0; y < Resolution; y++) {
            for (var x = 0; x < Resolution; x++) {
                var u = (x + 0.5f) / Resolution;
                var v = (y + 0.5f) / Resolution;

                image[(y * Resolution) + x] = PaintImage.Pack(0f, 0f, 0f, shape(u - 0.5f, v - 0.5f, Soft));
            }
        }

        return new PaintImageMask(image);
    }

    /// <summary>How far inside a band of a half-width a coordinate is, feathered by one texel.</summary>
    /// <param name="offset">The coordinate, measured from the middle of the square.</param>
    /// <param name="half">The band's half-width.</param>
    /// <param name="soft">How wide the feather is.</param>
    /// <returns>The coverage, 0…1.</returns>
    static float Band(float offset, float half, float soft) =>
        Math.Clamp(((half - MathF.Abs(offset)) / soft) + 0.5f, 0f, 1f);
}
