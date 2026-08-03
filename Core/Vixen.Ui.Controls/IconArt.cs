// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Ui.Controls;

/// <summary>Where a path's colour comes from.</summary>
/// <remarks>
///     ⚠ <b>Three cases, and all three are needed.</b> An icon set that only offers literals looks
///     correct in the theme it was drawn for and wrong in the other one; one that only offers the
///     inherited colour cannot draw a file-type glyph anybody can scan a grid by. The middle case is
///     what lets an icon say "the warning colour" and still follow a retheme.
/// </remarks>
public enum IconPaintKind : byte {
    /// <summary>Not painted at all. What a path with no stroke has for a stroke.</summary>
    /// <remarks>
    ///     First so that it is the default: an <see cref="IconPath" /> written with a fill and nothing
    ///     else must not acquire a stroke it never asked for.
    /// </remarks>
    None,

    /// <summary>The element's <c>color</c> — what every icon drawn before this type existed used.</summary>
    Foreground,

    /// <summary>A custom property read off the element, so the cascade decides it.</summary>
    Token,

    /// <summary>A colour written into the icon.</summary>
    Literal
}

/// <summary>What one path of an icon is painted with.</summary>
/// <remarks>
///     ⚠ <b>A struct with a kind rather than a nullable colour, because "inherit" is not a
///     colour.</b> The distinction that matters is between a path that will follow the theme and one
///     that will not, and a <see cref="Color4" /> cannot carry it — which is how an icon set ends up
///     with the theme's foreground baked in as a literal that stops inverting the day somebody
///     switches to a light theme.
/// </remarks>
public readonly record struct IconPaint {
    /// <summary>Which of the three cases this is.</summary>
    public IconPaintKind Kind { get; private init; }

    /// <summary>The custom property, for <see cref="IconPaintKind.Token" />. Empty otherwise.</summary>
    public string Token { get; private init; }

    /// <summary>The colour, for <see cref="IconPaintKind.Literal" />.</summary>
    public Color4 Color { get; private init; }

    /// <summary>Nothing is drawn.</summary>
    public static IconPaint None => default;

    /// <summary>The element's inherited <c>color</c>, which follows the theme.</summary>
    public static IconPaint Foreground { get; } = new() { Kind = IconPaintKind.Foreground, Token = string.Empty };

    /// <summary>A colour the cascade supplies.</summary>
    /// <param name="token">A custom property, written as a stylesheet writes it — <c>--icon-warning</c>.</param>
    /// <returns>The paint.</returns>
    /// <remarks>
    ///     ⚠ <b>Resolved against the icon element, so it inherits.</b> A custom property set on the
    ///     document root reaches every icon under it, and a rule that sets it again under a
    ///     <c>.dark</c> ancestor reaches the ones inside that — which is the whole reason this is a
    ///     style property rather than a lookup in a table the icon holds.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="token" /> is empty.</exception>
    public static IconPaint Named(string token) {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return new() { Kind = IconPaintKind.Token, Token = token };
    }

    /// <summary>A colour written into the icon, which does not follow the theme.</summary>
    /// <param name="color">The colour.</param>
    /// <returns>The paint.</returns>
    /// <remarks>
    ///     For the brand colours a file-type glyph actually needs. Anything that could reasonably
    ///     differ between a light and a dark theme wants <see cref="Named" /> instead.
    /// </remarks>
    public static IconPaint Of(Color4 color) =>
        new() { Kind = IconPaintKind.Literal, Token = string.Empty, Color = color };
}

/// <summary>One path of an icon, and how it is painted.</summary>
/// <param name="Geometry">The path, in the art's view box.</param>
/// <param name="Fill">What the inside is filled with.</param>
/// <param name="Stroke">What the outline is drawn with, or nothing.</param>
/// <param name="Width">How wide the stroke is, in view-box units.</param>
/// <param name="FillRule">How the inside is decided.</param>
/// <remarks>
///     ⚠ <b>The stroke width is in view-box units and is scaled with the geometry.</b> An icon drawn
///     on a 24 grid with a 1.5 stroke reads the same at 16 pixels and at 32; a width in device pixels
///     would make the same icon hairline in a toolbar and heavy in a header.
/// </remarks>
public readonly record struct IconPath(
    PathBuilder Geometry,
    IconPaint Fill,
    IconPaint Stroke = default,
    float Width = 1.5f,
    PathFillRule FillRule = PathFillRule.NonZero
);

/// <summary>A piece of vector art: several paths, each with its own paint.</summary>
/// <remarks>
///     <para>
///         <b>Multicolour from the start, and it is cheap because the drawing layer was already
///         there.</b> <see cref="DrawContext.Fill" /> and <see cref="DrawContext.Stroke" /> each take
///         a <see cref="Color4" />, so per-path paint costs one command per path and touches neither
///         the tessellator nor the batching. What was single-colour was <c>Icon.OnDraw</c> making one
///         call, which is a line rather than a rendering path.
///     </para>
///     <para>
///         ⚠ <b>Its own view box, not the element's.</b> An icon set gathers art from several places
///         — the editor's chrome on a 24 grid, a plugin's glyph on whatever grid its author used —
///         and a set whose members had to agree on a grid would be a set nobody could contribute to.
///     </para>
///     <para>
///         Shared freely, on <see cref="Icon" />'s terms: nothing here is mutated by drawing, and a
///         hundred elements pointing at one instance is the intended arrangement.
///     </para>
/// </remarks>
public sealed class IconArt {
    /// <summary>The grid the paths were authored against.</summary>
    public Rectangle ViewBox { get; }

    /// <summary>The paths, drawn in order — so a later one paints over an earlier one.</summary>
    public IReadOnlyList<IconPath> Paths { get; }

    /// <summary>Describes a piece of art.</summary>
    /// <param name="viewBox">The grid it was drawn on.</param>
    /// <param name="paths">Its paths, back to front.</param>
    /// <exception cref="ArgumentException"><paramref name="viewBox" /> has no area.</exception>
    public IconArt(Rectangle viewBox, IReadOnlyList<IconPath> paths) {
        ArgumentNullException.ThrowIfNull(paths);

        if (viewBox.Width <= 0f || viewBox.Height <= 0f) {
            throw new ArgumentException(
                "An icon's view box is what its geometry is scaled against, so a zero one would divide "
                + "by zero on the first draw. Twenty-four square is what Material, Lucide, Feather and "
                + "Fluent all author against.",
                nameof(viewBox)
            );
        }

        ViewBox = viewBox;
        Paths = paths;
    }

    /// <summary>Describes a piece of art on the usual 24-square grid.</summary>
    /// <param name="paths">Its paths, back to front.</param>
    public IconArt(params IconPath[] paths) : this(new Rectangle(0f, 0f, 24f, 24f), paths) { }

    /// <summary>One path in the inherited colour — what every icon written before this type was.</summary>
    /// <param name="geometry">The path.</param>
    /// <param name="rule">How its inside is decided.</param>
    /// <returns>The art.</returns>
    /// <remarks>
    ///     ⚠ <b>The bridge that keeps the existing set working unchanged.</b> The editor's glyphs are
    ///     <see cref="PathBuilder" />s drawn in <c>color</c>, and every one of them is still exactly
    ///     that — this is how one of them becomes something the registry can serve without being
    ///     redrawn.
    /// </remarks>
    public static IconArt Of(PathBuilder geometry, PathFillRule rule = PathFillRule.NonZero) {
        ArgumentNullException.ThrowIfNull(geometry);
        return new(new IconPath(geometry, IconPaint.Foreground, FillRule: rule));
    }

    /// <summary>One path in a colour of its own.</summary>
    /// <param name="geometry">The path.</param>
    /// <param name="tint">What to fill it with.</param>
    /// <param name="rule">How its inside is decided.</param>
    /// <returns>The art.</returns>
    public static IconArt Of(PathBuilder geometry, Color4 tint, PathFillRule rule = PathFillRule.NonZero) {
        ArgumentNullException.ThrowIfNull(geometry);
        return new(new IconPath(geometry, IconPaint.Of(tint), FillRule: rule));
    }
}
