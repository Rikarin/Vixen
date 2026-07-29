// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Video.Gpu;

/// <summary>What to do when the picture and the rectangle it goes in are different shapes.</summary>
/// <remarks>
///     The same three CSS's <c>object-fit</c> has, and the same three every video player has, because
///     there are only three answers: change the shape, letterbox, or crop.
/// </remarks>
public enum VideoScaling : byte {
    /// <summary>Fill the rectangle exactly, changing the aspect ratio. Rarely what anybody wants.</summary>
    Stretch,

    /// <summary>Fit inside, keeping the shape. Bars along the two sides that are left over.</summary>
    Contain,

    /// <summary>Fill, keeping the shape. The two edges that do not fit are cropped away.</summary>
    Cover
}

/// <summary>Where a picture lands and which part of it is shown.</summary>
/// <param name="Target">The rectangle to draw, in the caller's own units.</param>
/// <param name="TextureScale">What to multiply a 0–1 texture coordinate by.</param>
/// <param name="TextureOffset">What to add to it afterwards.</param>
/// <remarks>
///     ⚠ <b>Two answers rather than one, because the two scalings need different halves.</b>
///     <see cref="VideoScaling.Contain" /> shrinks the rectangle and leaves the texture coordinates
///     alone; <see cref="VideoScaling.Cover" /> keeps the rectangle and crops the coordinates. A
///     helper that returned only a rectangle could not express the second, and one that returned only
///     coordinates would have to paint the letterbox bars itself — which is wrong the moment the
///     video is drawn over something, because those bars are opaque black over whatever was behind.
/// </remarks>
public readonly record struct VideoPlacement(
    Rectangle Target,
    Vector2 TextureScale,
    Vector2 TextureOffset
) {
    /// <summary>The whole of a rectangle, with the whole of the picture.</summary>
    /// <param name="target">The rectangle.</param>
    /// <returns>The placement.</returns>
    public static VideoPlacement Filling(Rectangle target) => new(target, Vector2.One, Vector2.Zero);
}

/// <summary>Works out where a picture goes.</summary>
/// <remarks>
///     <para>
///         Pure arithmetic over four numbers, in <c>Vixen.Video</c> rather than in whichever renderer
///         needed it first — because both of them need it and they must agree. A video drawn in a
///         scene and the same video drawn in a user interface panel that letterboxed differently
///         would be a difference nobody could explain and nothing could test.
///     </para>
///     <para>
///         The display aspect is taken separately from the pixel size, which is the whole reason
///         anamorphic content works: a 720×480 clip meant to be shown at 853×480 has square-ish
///         samples that are not square, and fitting it by its <i>sample</i> count squashes it.
///     </para>
/// </remarks>
public static class VideoFit {
    /// <summary>Places a picture in a rectangle.</summary>
    /// <param name="scaling">Which of the three answers.</param>
    /// <param name="source">The picture's displayed size — width and height, in any consistent unit.</param>
    /// <param name="target">Where it goes.</param>
    /// <returns>The rectangle to draw and the texture coordinates to draw it with.</returns>
    /// <remarks>
    ///     A degenerate source or target falls back to filling the target, because the alternative is
    ///     a division by zero and the caller is usually a frame that arrived before the first picture
    ///     did.
    /// </remarks>
    public static VideoPlacement Place(VideoScaling scaling, Vector2 source, Rectangle target) {
        if (scaling == VideoScaling.Stretch
            || source.X <= 0
            || source.Y <= 0
            || target.Width <= 0
            || target.Height <= 0) {
            return VideoPlacement.Filling(target);
        }

        var wanted = source.X / source.Y;
        var available = target.Width / target.Height;

        // Equal to within a pixel of the target's own size: below that the letterbox is thinner than
        // the edge it would sit against, and computing one costs a seam rather than removing one.
        if (MathF.Abs(wanted - available) * target.Height < 1f) {
            return VideoPlacement.Filling(target);
        }

        return scaling == VideoScaling.Contain
            ? Contain(wanted, available, target)
            : Cover(wanted, available, target);
    }

    /// <summary>Places a picture in a rectangle, at the size the container says it is meant to look.</summary>
    /// <param name="scaling">Which of the three answers.</param>
    /// <param name="source">Where the picture comes from.</param>
    /// <param name="target">Where it goes.</param>
    /// <returns>The rectangle to draw and the texture coordinates to draw it with.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>The <i>display</i> size, not the sample count, and that is the whole reason this
    ///     overload exists.</b> Anamorphic content — DVD-era 720×480 shown at 16:9 — has samples that
    ///     are not square, and a player that fitted by counting them shows every frame a fifth too
    ///     narrow while every number in the pipeline looks right.
    /// </remarks>
    public static VideoPlacement Place(
        VideoScaling scaling,
        Playback.VideoPlayer source,
        Rectangle target
    ) {
        ArgumentNullException.ThrowIfNull(source);

        var size = source.DisplaySize;

        return Place(scaling, new Vector2(size.X, size.Y), target);
    }

    static VideoPlacement Contain(float wanted, float available, Rectangle target) {
        // Wider than the space: the width is the constraint and the bars are top and bottom.
        var width = wanted > available ? target.Width : target.Height * wanted;
        var height = wanted > available ? target.Width / wanted : target.Height;

        return VideoPlacement.Filling(
            new Rectangle(
                target.X + ((target.Width - width) / 2f),
                target.Y + ((target.Height - height) / 2f),
                width,
                height
            )
        );
    }

    static VideoPlacement Cover(float wanted, float available, Rectangle target) {
        // The reciprocal of Contain's, applied to the texture rather than to the rectangle: the part
        // that would have been a bar is the part that is cropped.
        var scaleX = wanted > available ? available / wanted : 1f;
        var scaleY = wanted > available ? 1f : wanted / available;

        return new VideoPlacement(
            target,
            new Vector2(scaleX, scaleY),
            new Vector2((1f - scaleX) / 2f, (1f - scaleY) / 2f)
        );
    }
}
