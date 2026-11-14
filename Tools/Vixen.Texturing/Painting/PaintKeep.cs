// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Texturing.Painting;

/// <summary>What a stroke is kept to, texel by texel in the atlas: a quick mask.</summary>
/// <remarks>
///     <para>
///         <b>In the atlas, where a stencil is on the stroke's picture.</b> A stencil moves with the screen; a keep is
///         painted on the surface beforehand and stays where it was painted, whichever way the camera turns.
///     </para>
///     <para>
///         ⚠ <b>It scales the cap as well as each stamp's weight</b>, for the stencil's reason: a share of the weight
///         alone is built back up by the stamps that follow, and a stroke that accumulates would paint through a grey
///         keep as if it were white.
///     </para>
/// </remarks>
public interface IPaintKeep {
    /// <summary>How much of the brush reaches a texel, 0 to 1.</summary>
    /// <param name="texel">The texel of the canvas being painted, row-major.</param>
    /// <returns>The share.</returns>
    float At(int texel);
}

/// <summary>
///     A keep read from a picture's red lane: white lets the brush through and black holds it back, or the other way
///     round when it is inverted.
/// </summary>
/// <param name="mask">The picture. One of another size than the canvas is stretched over it, nearest texel.</param>
/// <param name="canvasWidth">The width of the canvas being painted.</param>
/// <param name="canvasHeight">Its height.</param>
/// <param name="inverted">Whether black lets the brush through instead.</param>
/// <remarks>
///     ⚠ <b>Read as the stroke reaches each texel, not copied when it begins.</b> A journal replays the strokes in
///     their order, so the mask stands as it did when the stroke was painted, and a copy would only cost memory.
/// </remarks>
public sealed class PaintImageKeep(PaintImage mask, int canvasWidth, int canvasHeight, bool inverted = false)
    : IPaintKeep {
    /// <inheritdoc />
    public float At(int texel) {
        var x = texel % canvasWidth;
        var y = texel / canvasWidth;

        if (mask.Width != canvasWidth || mask.Height != canvasHeight) {
            x = Math.Min(x * mask.Width / canvasWidth, mask.Width - 1);
            y = Math.Min(y * mask.Height / canvasHeight, mask.Height - 1);
        }

        var red = PaintImage.Channel(mask.At(x, y), 0);

        return inverted ? 1f - red : red;
    }
}
