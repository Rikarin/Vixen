// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Texturing.Painting;

/// <summary>What a stroke lays at each texel, when that is not one colour.</summary>
/// <remarks>
///     <para>
///         <b>A stroke's weight and its colour are separate questions, and this is only the second.</b> Where a stamp
///         lands, how much, the gutter and undo stay <see cref="PaintStroke" />'s; a source only says what each texel
///         mixes towards. So a material, a clone, a projected picture and a smear are each a stroke like any other to
///         a journal, and each works through a camera, in the atlas and with a polygon fill.
///     </para>
///     <para>
///         ⚠ <b>Everything a source reads has to be what a replay would read.</b> A stroke is replayed from its stamps
///         after a lost Worker, so a source holds a picture fixed when the stroke began, or reads the canvas the
///         stroke paints, which the replay brings back to the same state first.
///     </para>
/// </remarks>
public interface IPaintSource {
    /// <summary>
    ///     Whether the colours are read from the canvas being painted, as a smear is: then each stamp moves what is
    ///     there, rather than mixing towards a colour from what the texel held before the stroke.
    /// </summary>
    bool ReadsCanvas { get; }

    /// <summary>Told before each stamp is laid.</summary>
    /// <param name="stamp">The stamp.</param>
    /// <param name="atlas">Where its centre is in the atlas, in texels, or null where it lands on nothing.</param>
    void Stamp(in PaintStamp stamp, Vector2? atlas);

    /// <summary>What one texel mixes towards.</summary>
    /// <param name="texel">The texel, row-major.</param>
    /// <param name="picture">Where it lands on the stroke's picture: through a camera, where it projects.</param>
    /// <param name="colour">The colour, packed <c>0xAABBGGRR</c>.</param>
    /// <returns>Whether there is anything to lay there: a clone reading past its picture has nothing.</returns>
    bool TryColour(int texel, Vector2 picture, out uint colour);
}

/// <summary>What masks a brush where each texel lands on the stroke's picture: a stencil.</summary>
public interface IPaintStencil {
    /// <summary>How much of the brush gets through at a place on the picture, 0 to 1.</summary>
    /// <param name="picture">The place.</param>
    /// <returns>The share.</returns>
    float At(Vector2 picture);
}

/// <summary>
///     A picture laid over the atlas texel for texel, read at an offset: a material's map, painted as it is, or the
///     composite read from elsewhere on the atlas, which is a clone.
/// </summary>
/// <param name="image">What is laid. A picture of another size than the atlas is stretched over it.</param>
/// <param name="atlasWidth">The atlas's width.</param>
/// <param name="atlasHeight">Its height.</param>
/// <param name="offset">How far, in texels of the atlas, the texel read is from the one painted.</param>
/// <remarks>
///     ⚠ <b>A clone reads past the picture's edge as nothing, and not as its edge clamped or wrapped.</b> A clone
///     that wrapped would paint the far side of the atlas into a stroke near this one, which is another island.
/// </remarks>
public sealed class PaintAtlasSource(PaintImage image, int atlasWidth, int atlasHeight, Vector2 offset = default)
    : IPaintSource {
    /// <inheritdoc />
    public bool ReadsCanvas => false;

    /// <inheritdoc />
    public void Stamp(in PaintStamp stamp, Vector2? atlas) { }

    /// <inheritdoc />
    public bool TryColour(int texel, Vector2 picture, out uint colour) {
        var x = texel % atlasWidth + 0.5f + offset.X;
        var y = texel / atlasWidth + 0.5f + offset.Y;
        var column = (int)MathF.Floor(x * image.Width / atlasWidth);
        var row = (int)MathF.Floor(y * image.Height / atlasHeight);

        if (column < 0 || row < 0 || column >= image.Width || row >= image.Height) {
            colour = 0u;

            return false;
        }

        colour = image.At(column, row);

        return true;
    }
}

/// <summary>A smear: each stamp pulls along what lies behind it on the canvas the stroke is painting.</summary>
/// <param name="canvas">The canvas the stroke paints, read as it stands.</param>
/// <param name="coverage">Which texels are surface, so a smear never pulls colour in from the gutter.</param>
/// <remarks>
///     <para>
///         <b>Each texel takes the colour that lay where the brush has just come from.</b> The motion is the step
///         from the last stamp's centre to this one's, in the atlas, so a smear through a camera moves paint along
///         the surface as the pointer moves it on the screen. With the stroke's flow under one, the colour mixes into
///         what is there rather than replacing it, which is how much a smudge drags.
///     </para>
///     <para>
///         ⚠ <b>A step longer than the brush is a jump to another island and not a motion</b>, and moves nothing:
///         pulling colour from across the atlas would paint some other part of the model into this one.
///     </para>
///     <para>
///         ⚠ <b>What the smear has laid is kept unrounded until the stroke ends.</b> A stamp a fraction of a texel on
///         moves a smooth picture by less than one level of eight bits, and each stamp reads what the last wrote: a
///         canvas rounded at every stamp lost every one of those moves, so a fine spacing smeared nothing at all and a
///         coarse one smeared in steps, which was the scalloped edge.
///     </para>
/// </remarks>
public sealed class PaintSmudge(PaintImage canvas, PaintCoverage coverage) : IPaintSource {
    /// <summary>What the smear has laid at each texel it moved, before rounding: lanes 0 to 1.</summary>
    readonly Dictionary<int, Vector4> held = [];

    Vector2? last;
    Vector2 motion;

    /// <inheritdoc />
    public bool ReadsCanvas => true;

    /// <inheritdoc />
    public void Stamp(in PaintStamp stamp, Vector2? atlas) {
        motion = Vector2.Zero;

        if (atlas is not { } here) {
            last = null;

            return;
        }

        if (last is { } before && Vector2.Distance(before, here) <= MathF.Max(stamp.Radius, 1f) * 4f) {
            motion = here - before;
        }

        last = here;
    }

    /// <inheritdoc />
    public bool TryColour(int texel, Vector2 picture, out uint colour) {
        var found = TryCarried(texel, out var carried);

        colour = found ? Packed(carried) : 0u;

        return found;
    }

    /// <summary>What the brush carries onto a texel, unrounded: what lay where it has just come from.</summary>
    /// <param name="texel">The texel, by index in the canvas.</param>
    /// <param name="carried">The colour, lanes 0 to 1.</param>
    /// <returns>Whether the stamp moves anything there.</returns>
    internal bool TryCarried(int texel, out Vector4 carried) {
        carried = default;

        if (motion == Vector2.Zero) {
            return false;
        }

        // ⚠ Between texels and not at the nearest: a step is rarely a whole number of texels, and rounding it moves some
        // columns one texel and their neighbours none, which combs the edge of the smear.
        var x = texel % canvas.Width - motion.X;
        var y = texel / canvas.Width - motion.Y;
        var left = (int)MathF.Floor(x);
        var top = (int)MathF.Floor(y);
        var across = x - left;
        var down = y - top;
        var upper = Between(Tap(left, top), Tap(left + 1, top), across);
        var lower = Between(Tap(left, top + 1), Tap(left + 1, top + 1), across);

        if (Between(upper, lower, down) is not { } mixed) {
            return false;
        }

        carried = mixed;

        return true;
    }

    /// <summary>Moves a texel towards what the brush carries, keeping the result unrounded for the stamps after.</summary>
    /// <param name="texel">The texel, by index in the canvas.</param>
    /// <param name="carried">What the brush carries there.</param>
    /// <param name="weight">How far, 0 to 1.</param>
    /// <returns>The texel as the canvas stores it.</returns>
    internal uint Smeared(int texel, Vector4 carried, float weight) {
        var now = Held(texel);
        var moved = now + (carried - now) * Math.Clamp(weight, 0f, 1f);

        held[texel] = moved;

        return Packed(moved);
    }

    /// <summary>A texel as the smear has left it, or as the canvas holds it where the smear has not been.</summary>
    Vector4 Held(int texel) {
        if (held.TryGetValue(texel, out var kept)) {
            return kept;
        }

        var stored = canvas[texel];

        return new(
            PaintImage.Channel(stored, 0),
            PaintImage.Channel(stored, 1),
            PaintImage.Channel(stored, 2),
            PaintImage.Channel(stored, 3)
        );
    }

    static uint Packed(Vector4 colour) => PaintImage.Pack(colour.X, colour.Y, colour.Z, colour.W);

    /// <summary>A texel of the canvas that is surface, or nothing.</summary>
    Vector4? Tap(int column, int row) =>
        column < 0
        || row < 0
        || column >= canvas.Width
        || row >= canvas.Height
        || !coverage.IsCovered(row * canvas.Width + column)
            ? null
            : Held(row * canvas.Width + column);

    /// <summary>Two taps mixed, or the one there is.</summary>
    static Vector4? Between(Vector4? first, Vector4? second, float share) =>
        (first, second) switch {
            ({ } a, { } b) => a + (b - a) * share,
            ({ } a, null) => a,
            (null, { } b) => b,
            _ => null
        };
}

/// <summary>Where a picture stands on a stroke's own picture: moved, scaled and turned, and perhaps tiled.</summary>
/// <param name="Centre">Where the picture's middle is, in the stroke's units.</param>
/// <param name="Scale">How many of the stroke's units one pixel of the picture covers.</param>
/// <param name="Angle">How far it is turned, in radians, clockwise on the screen.</param>
/// <param name="Tiled">Whether it repeats past its edges, or is nothing there.</param>
public readonly record struct PaintFrame(Vector2 Centre, float Scale, float Angle, bool Tiled) {
    /// <summary>Where a place on the stroke's picture is on the picture framed, in its pixels.</summary>
    /// <param name="at">The place.</param>
    /// <param name="width">The framed picture's width.</param>
    /// <param name="height">Its height.</param>
    /// <returns>The pixel position, which may be outside it.</returns>
    public Vector2 Into(Vector2 at, int width, int height) {
        var (sin, cos) = MathF.SinCos(-Angle);
        var offset = (at - Centre) / MathF.Max(Scale, 1e-6f);

        return new Vector2(offset.X * cos - offset.Y * sin, offset.X * sin + offset.Y * cos)
            + new Vector2(width * 0.5f, height * 0.5f);
    }
}

/// <summary>
///     A picture fixed on the screen and painted through the brush: each texel takes the picture's colour where it
///     lands on the stroke's picture. As a stencil, its brightness is how much of the brush gets through.
/// </summary>
/// <param name="image">The picture.</param>
/// <param name="frame">Where it stands on the stroke's picture.</param>
/// <remarks>
///     ⚠ <b>Read where the texel lands, and not where the stamp is.</b> A stroke through a camera knows where each
///     texel projects, so the picture lands on the surface as a slide projector puts it there, whatever the surface's
///     slope or layout; in the atlas, the stroke's picture is the atlas itself.
/// </remarks>
public sealed class PaintProjectedPicture(PaintImage image, PaintFrame frame) : IPaintSource, IPaintStencil {
    /// <inheritdoc />
    public bool ReadsCanvas => false;

    /// <inheritdoc />
    public void Stamp(in PaintStamp stamp, Vector2? atlas) { }

    /// <inheritdoc />
    public bool TryColour(int texel, Vector2 picture, out uint colour) {
        if (!TryRead(picture, out var found)) {
            colour = 0u;

            return false;
        }

        // The picture's own transparency is how much it covers, so what is laid is opaque where it lands.
        colour = found | 0xFF000000u;

        return PaintImage.Channel(found, 3) > 0f;
    }

    /// <inheritdoc />
    /// <remarks>Its brightness times its alpha: white lets the brush through and black, or nothing, holds it back.</remarks>
    public float At(Vector2 picture) {
        if (!TryRead(picture, out var found)) {
            return 0f;
        }

        var luminance = 0.2126f * PaintImage.Channel(found, 0)
            + 0.7152f * PaintImage.Channel(found, 1)
            + 0.0722f * PaintImage.Channel(found, 2);

        return luminance * PaintImage.Channel(found, 3);
    }

    bool TryRead(Vector2 at, out uint found) {
        var into = frame.Into(at, image.Width, image.Height);
        var column = (int)MathF.Floor(into.X);
        var row = (int)MathF.Floor(into.Y);

        if (frame.Tiled) {
            column = (column % image.Width + image.Width) % image.Width;
            row = (row % image.Height + image.Height) % image.Height;
        } else if (column < 0 || row < 0 || column >= image.Width || row >= image.Height) {
            found = 0u;

            return false;
        }

        found = image.At(column, row);

        return true;
    }
}
