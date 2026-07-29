// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Engine.Diagnostics.Overlays;

/// <summary>Which corner of the screen a panel is pinned to.</summary>
/// <remarks>
///     Corners rather than free coordinates, because overlays are written independently and must not
///     have to agree on where each other is. Each corner is a stack: panels pinned to it are laid out
///     one after another in registration order, so turning one on does not move the rest sideways.
/// </remarks>
public enum OverlayAnchor {
    /// <summary>The top left, which is where frame statistics conventionally live.</summary>
    TopLeft,

    /// <summary>The top right.</summary>
    TopRight,

    /// <summary>The bottom left.</summary>
    BottomLeft,

    /// <summary>The bottom right.</summary>
    BottomRight
}

/// <summary>What the overlays look like: colours, text size, and the space around things.</summary>
/// <remarks>
///     One value passed down rather than each overlay picking its own, so that "everything readable"
///     and "everything out of the way" are one adjustment instead of nine. The defaults are tuned for
///     a bright scene: a dark, mostly opaque backing and light text, because a diagnostic that is
///     legible only over a dark skybox is a diagnostic that vanishes exactly when a level is bright.
/// </remarks>
public readonly record struct OverlayTheme {
    /// <summary>The settings an overlay gets when nobody has said otherwise.</summary>
    public static OverlayTheme Default => new();

    /// <summary>The cap height of overlay text, in pixels.</summary>
    public float TextSize { get; init; } = 9f;

    /// <summary>How far a panel's contents sit inside its edges, in pixels.</summary>
    public float Padding { get; init; } = 6f;

    /// <summary>How far a panel sits from the edge of the screen, in pixels.</summary>
    public float Margin { get; init; } = 8f;

    /// <summary>How far apart two stacked panels sit, in pixels.</summary>
    public float Gap { get; init; } = 6f;

    /// <summary>How far apart a fill's scanlines are. One is solid; two is half the segments.</summary>
    public float FillSpacing { get; init; } = 1f;

    /// <summary>What is drawn behind a panel.</summary>
    public Color4 Background { get; init; } = new(0.04f, 0.05f, 0.07f, 0.82f);

    /// <summary>The line round a panel's edge.</summary>
    public Color4 Border { get; init; } = new(0.35f, 0.40f, 0.48f, 0.85f);

    /// <summary>Ordinary text.</summary>
    public Color4 Text { get; init; } = new(0.88f, 0.90f, 0.94f, 1f);

    /// <summary>A panel's title, and anything else that labels rather than reports.</summary>
    public Color4 Heading { get; init; } = new(0.55f, 0.78f, 1f, 1f);

    /// <summary>Text that has faded out of relevance — an older log line, a disabled entry.</summary>
    public Color4 Muted { get; init; } = new(0.55f, 0.58f, 0.64f, 1f);

    /// <summary>A number that is where it should be.</summary>
    public Color4 Good { get; init; } = new(0.40f, 0.85f, 0.45f, 1f);

    /// <summary>A number that is close to a limit.</summary>
    public Color4 Warning { get; init; } = new(0.95f, 0.75f, 0.25f, 1f);

    /// <summary>A number that is past one.</summary>
    public Color4 Bad { get; init; } = new(0.95f, 0.35f, 0.35f, 1f);

    /// <summary>Builds the default theme.</summary>
    public OverlayTheme() { }

    /// <summary>The same colour at a different opacity.</summary>
    /// <param name="colour">The colour.</param>
    /// <param name="alpha">How opaque to make it.</param>
    /// <returns>The faded colour.</returns>
    /// <remarks>
    ///     Here because an overlay wants a rule or a bar backing at a fraction of a theme colour
    ///     constantly, and <see cref="Color4" /> has readonly fields — so <c>with</c> does not reach
    ///     them and every call site would otherwise repeat the four components.
    /// </remarks>
    public static Color4 Fade(Color4 colour, float alpha) => new(colour.R, colour.G, colour.B, alpha);
}

/// <summary>
///     Somewhere for one overlay to draw: a rectangle it has been given and coordinates relative to
///     it.
/// </summary>
/// <remarks>
///     Relative, so an overlay never has to know which corner it ended up in or what is stacked above
///     it — which is what makes the panels rearrangeable without touching any of them.
/// </remarks>
public readonly struct OverlayRegion {
    readonly OverlaySurface? surface;

    /// <summary>The top-left of the whole panel, in pixels from the top-left of the screen.</summary>
    public Vector2 Origin { get; }

    /// <summary>How big the whole panel is.</summary>
    public Vector2 Size { get; }

    /// <summary>The top-left of the area inside the padding and below the title.</summary>
    public Vector2 Content { get; }

    /// <summary>Whether there is anywhere to draw. A panel that did not fit is empty rather than off-screen.</summary>
    public bool IsEmpty => surface is null;

    /// <summary>How far apart two rows of text sit.</summary>
    public float RowHeight => surface?.RowHeight ?? 0f;

    /// <summary>How wide the area inside the padding is.</summary>
    public float ContentWidth => surface is null ? 0f : Size.X - (2f * surface.Theme.Padding);

    /// <summary>How tall the area inside the padding and below the title is.</summary>
    public float ContentHeight => surface is null ? 0f : Origin.Y + Size.Y - surface.Theme.Padding - Content.Y;

    internal OverlayRegion(OverlaySurface surface, Vector2 origin, Vector2 size, Vector2 content) {
        this.surface = surface;
        Origin = origin;
        Size = size;
        Content = content;
    }

    /// <summary>Writes one row of text, counting from the first row under the title.</summary>
    /// <param name="row">Which row, from zero.</param>
    /// <param name="text">What it says.</param>
    /// <param name="colour">What colour.</param>
    public void Text(int row, ReadOnlySpan<char> text, Color4 colour) =>
        surface?.Text(Content + new Vector2(0f, row * RowHeight), text, colour);

    /// <summary>Writes text at an offset from the content origin.</summary>
    /// <param name="offset">Where, relative to <see cref="Content" />.</param>
    /// <param name="text">What it says.</param>
    /// <param name="colour">What colour.</param>
    public void Text(Vector2 offset, ReadOnlySpan<char> text, Color4 colour) =>
        surface?.Text(Content + offset, text, colour);

    /// <summary>Writes text right-aligned to the content's right edge.</summary>
    /// <param name="row">Which row, from zero.</param>
    /// <param name="text">What it says.</param>
    /// <param name="colour">What colour.</param>
    /// <remarks>
    ///     What a column of numbers needs, and the reason the font is fixed-advance: aligning by
    ///     measuring works because every glyph is the same width.
    /// </remarks>
    public void TextRight(int row, ReadOnlySpan<char> text, Color4 colour) {
        if (surface is null) {
            return;
        }

        var width = DebugFont.MeasureWidth(text, surface.Theme.TextSize);
        surface.Text(Content + new Vector2(ContentWidth - width, row * RowHeight), text, colour);
    }

    /// <summary>Draws a line inside the panel.</summary>
    /// <param name="from">One end, relative to <see cref="Content" />.</param>
    /// <param name="to">The other.</param>
    /// <param name="colour">What colour.</param>
    public void Line(Vector2 from, Vector2 to, Color4 colour) =>
        surface?.Draw.ScreenLine(Content + from, Content + to, colour);

    /// <summary>Draws a rectangle's outline inside the panel.</summary>
    /// <param name="offset">Its corner, relative to <see cref="Content" />.</param>
    /// <param name="size">How big it is.</param>
    /// <param name="colour">What colour.</param>
    public void Rect(Vector2 offset, Vector2 size, Color4 colour) =>
        surface?.Draw.ScreenRect(Content + offset, size, colour);

    /// <summary>Fills a rectangle inside the panel.</summary>
    /// <param name="offset">Its corner, relative to <see cref="Content" />.</param>
    /// <param name="size">How big it is.</param>
    /// <param name="colour">What colour.</param>
    public void Fill(Vector2 offset, Vector2 size, Color4 colour) =>
        surface?.Draw.ScreenFill(Content + offset, size, colour, surface.Theme.FillSpacing);

    /// <summary>Draws a labelled meter: a name, a value, and a bar showing how full it is.</summary>
    /// <param name="row">Which row, from zero.</param>
    /// <param name="label">What it measures.</param>
    /// <param name="value">The reading, already formatted.</param>
    /// <param name="fraction">How full, clamped to zero and one.</param>
    /// <param name="colour">What colour the bar is.</param>
    /// <remarks>
    ///     A bar and a number rather than either alone. The number is what gets quoted in a bug
    ///     report; the bar is what makes "this one is nearly full" visible without reading nine of
    ///     them.
    /// </remarks>
    public void Meter(int row, ReadOnlySpan<char> label, ReadOnlySpan<char> value, float fraction, Color4 colour) {
        if (surface is null) {
            return;
        }

        Text(row, label, surface.Theme.Text);
        TextRight(row, value, colour);

        var top = ((row + 1) * RowHeight) - (RowHeight * 0.28f);
        var height = RowHeight * 0.16f;
        var full = ContentWidth;

        Fill(new(0f, top), new(full, height), OverlayTheme.Fade(surface.Theme.Muted, 0.25f));

        var filled = full * Math.Clamp(fraction, 0f, 1f);

        if (filled > 0f) {
            Fill(new(0f, top), new(filled, height), colour);
        }
    }
}

/// <summary>
///     The screen an overlay draws on: it hands out panels from the four corners and turns text into
///     the same debug lines everything else here produces.
/// </summary>
/// <remarks>
///     <para>
///         <b>Everything an overlay draws is a <see cref="DebugDraw" /> screen line.</b> There is no
///         second renderer, no font atlas and no UI document — which is what makes the overlays
///         available in a shipping build, in the frame where the asset system or the interface is the
///         thing that is broken, and on a platform where the editor is not running.
///     </para>
///     <para>
///         ⚠ <b>Panels are laid out in the order they are drawn</b>, per corner. Registration order in
///         <see cref="DiagnosticOverlays" /> is therefore the order down the screen, and turning one
///         off closes the gap rather than leaving one.
///     </para>
/// </remarks>
public sealed class OverlaySurface {
    // One cursor per anchor: how far along that corner's edge has already been given out.
    readonly float[] cursors = new float[4];

    internal OverlaySurface() { }

    /// <summary>Where the lines go.</summary>
    public DebugDraw Draw { get; private set; } = new();

    /// <summary>How big the screen is, in pixels.</summary>
    public Vector2 Viewport { get; private set; }

    /// <summary>The colours and sizes in force.</summary>
    public OverlayTheme Theme { get; private set; } = OverlayTheme.Default;

    /// <summary>How far apart two rows of text sit.</summary>
    public float RowHeight => DebugFont.LineHeightFor(Theme.TextSize);

    /// <summary>How wide a string draws at the theme's text size.</summary>
    /// <param name="text">The string.</param>
    /// <returns>The width, in pixels.</returns>
    public float MeasureWidth(ReadOnlySpan<char> text) => DebugFont.MeasureWidth(text, Theme.TextSize);

    /// <summary>How tall a panel with this many rows and an optional title needs to be.</summary>
    /// <param name="rows">How many rows of content.</param>
    /// <param name="titled">Whether it has a title.</param>
    /// <returns>The height, in pixels.</returns>
    public float HeightForRows(int rows, bool titled = true) =>
        (2f * Theme.Padding) + ((rows + (titled ? 1 : 0)) * RowHeight);

    /// <summary>Takes a panel out of a corner's stack.</summary>
    /// <param name="anchor">Which corner.</param>
    /// <param name="width">How wide.</param>
    /// <param name="height">How tall.</param>
    /// <param name="title">What to write across the top, or nothing.</param>
    /// <returns>Where to draw, or an empty region if it would not fit on screen.</returns>
    /// <remarks>
    ///     ⚠ <b>A panel that does not fit is refused rather than clipped.</b> Nothing here clips —
    ///     lines are drawn wherever they are put — so a panel drawn past the bottom of the screen
    ///     would be invisible while still taking its turn in the stack, which reads as an overlay that
    ///     is toggled on and does nothing. An empty region draws nothing and says so.
    /// </remarks>
    public OverlayRegion Panel(OverlayAnchor anchor, float width, float height, ReadOnlySpan<char> title = default) {
        var cursor = cursors[(int) anchor];
        var used = cursor + height + (cursor > 0f ? Theme.Gap : 0f);

        if (width <= 0f || height <= 0f
            || width + (2f * Theme.Margin) > Viewport.X
            || used + (2f * Theme.Margin) > Viewport.Y) {
            return default;
        }

        var top = Theme.Margin + cursor + (cursor > 0f ? Theme.Gap : 0f);

        var origin = new Vector2(
            anchor is OverlayAnchor.TopLeft or OverlayAnchor.BottomLeft
                ? Theme.Margin
                : Viewport.X - Theme.Margin - width,
            anchor is OverlayAnchor.TopLeft or OverlayAnchor.TopRight
                ? top
                : Viewport.Y - top - height
        );

        cursors[(int) anchor] = used;

        var size = new Vector2(width, height);

        // Background first, then the border, then the title — the screen pass has no depth, so the
        // list order is the paint order and anything drawn before is drawn under.
        Draw.ScreenFill(origin, size, Theme.Background, Theme.FillSpacing);
        Draw.ScreenRect(origin, size, Theme.Border);

        var content = origin + new Vector2(Theme.Padding, Theme.Padding);

        if (!title.IsEmpty) {
            Text(content, title, Theme.Heading);

            var rule = content.Y + (RowHeight * 0.82f);
            Draw.ScreenLine(new(content.X, rule), new(origin.X + width - Theme.Padding, rule), Theme.Border);

            content = new(content.X, content.Y + RowHeight);
        }

        return new(this, origin, size, content);
    }

    /// <summary>Takes a panel sized to hold a given number of rows.</summary>
    /// <param name="anchor">Which corner.</param>
    /// <param name="width">How wide.</param>
    /// <param name="rows">How many rows of content.</param>
    /// <param name="title">What to write across the top, or nothing.</param>
    /// <returns>Where to draw, or an empty region if it would not fit.</returns>
    public OverlayRegion Panel(OverlayAnchor anchor, float width, int rows, ReadOnlySpan<char> title = default) =>
        Panel(anchor, width, HeightForRows(rows, !title.IsEmpty), title);

    /// <summary>Writes text in screen coordinates, at the theme's size.</summary>
    /// <param name="position">The top-left of the first glyph's cap box.</param>
    /// <param name="text">What it says.</param>
    /// <param name="colour">What colour.</param>
    public void Text(Vector2 position, ReadOnlySpan<char> text, Color4 colour) =>
        Draw.ScreenText(position, text, colour, Theme.TextSize);

    /// <summary>Points the surface at a frame's accumulator and empties the corner stacks.</summary>
    /// <param name="draw">Where the lines go.</param>
    /// <param name="viewport">How big the screen is.</param>
    /// <param name="theme">The colours and sizes.</param>
    internal void Begin(DebugDraw draw, Vector2 viewport, in OverlayTheme theme) {
        Draw = draw;
        Viewport = viewport;
        Theme = theme;

        Array.Clear(cursors);
    }
}
