// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Ui.Controls;

/// <summary>A small piece of vector art, drawn in the current text colour or in colours of its own.</summary>
/// <remarks>
///     <para>
///         <b>Two properties, and <see cref="Art" /> is the general one.</b> <see cref="Geometry" />
///         is one path in the inherited colour, which is what an editor's chrome is; <see cref="Art" />
///         is several paths each with its own paint, which is what a file-type glyph needs and what a
///         plugin declares for its own asset type. See <see cref="IconArt" />.
///     </para>
///     <para>
///         <b>A path rather than an image</b>, because an icon is not a picture: it is drawn at
///         whatever size the layout gives it, it changes colour with the text around it, and there
///         are several hundred of them in an editor. A texture per icon per size per colour is how
///         icon sets become the largest thing in a build.
///     </para>
///     <para>
///         ⚠ <b>The geometry is handed in rather than parsed from a string.</b> There is no SVG path
///         parser here, and that is a decision rather than an omission: an icon set is compiled
///         content, so the place to turn <c>"M12 2L2 22h20z"</c> into segments is the asset pipeline,
///         once, rather than every application at start-up. A <see cref="PathBuilder" /> is what
///         comes out the other end, and it is what this takes.
///     </para>
///     <para>
///         <see cref="ViewBox" /> is what makes an icon set interchangeable. Geometry authored
///         against a 24×24 grid is drawn into whatever box the layout produced, scaled uniformly and
///         centred — so a 16-pixel toolbar and a 32-pixel button share one definition, and an icon
///         drawn against a different grid says so instead of coming out the wrong size.
///     </para>
/// </remarks>
public sealed partial class Icon : Control {
    readonly PathBuilder scaled = new();

    /// <summary>Style property identifiers for the tokens this icon's paints name.</summary>
    /// <remarks>
    ///     ⚠ <b>Interned once each rather than per draw.</b> <see cref="UiDocument.PropertyId" /> is a
    ///     dictionary probe and an icon redraws every frame it is on screen; an icon set with a token
    ///     per glyph would otherwise pay one probe per path per frame for a number that never changes.
    ///     Per document, which is what the identifiers mean — and an element does not move between
    ///     documents, so this cannot outlive the table it indexes.
    /// </remarks>
    Dictionary<string, int>? tokens;

    /// <inheritdoc />
    protected override string TagName => "icon";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The art, in <see cref="ViewBox" /> coordinates.</summary>
    /// <remarks>
    ///     Shared freely — an icon set is one <see cref="PathBuilder" /> per icon and a hundred
    ///     elements pointing at the same one. Nothing here mutates it; the scaling happens into a
    ///     buffer of this element's own.
    /// </remarks>
    [UiProperty]
    public partial PathBuilder? Geometry { get; set; }

    /// <summary>Several paths, each with its own paint and its own grid.</summary>
    /// <remarks>
    ///     ⚠ <b>Wins over <see cref="Geometry" /> when both are set, and neither is deprecated.</b>
    ///     A glyph that is one shape in the inherited colour is most of an editor's chrome and says so
    ///     in one property; art that a plugin declared for its own asset type is several paths in
    ///     colours of their own. Making the second replace the first would have meant rewriting
    ///     thirty-four hand-drawn icons to say what they already said.
    /// </remarks>
    [UiProperty]
    public partial IconArt? Art { get; set; }

    /// <summary>The grid the geometry was authored against.</summary>
    [UiProperty]
    public partial Rectangle ViewBox { get; set; }

    /// <summary>How the inside of the path is decided.</summary>
    [UiProperty]
    public partial PathFillRule FillRule { get; set; }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        // A zero view box would divide by zero on the first draw, and a default that has to be
        // written by every caller is a default that is sometimes not written. Twenty-four is what
        // Material, Lucide, Feather and Fluent all author against.
        if (ViewBox.IsEmpty) {
            ViewBox = new Rectangle(0f, 0f, 24f, 24f);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Rescaled on every draw rather than when the geometry changes</b>, because the thing
    ///     it is scaled to is the layout result, and nothing tells an element that its box changed —
    ///     a sibling growing resizes this one without touching a property on it. The cost is a walk
    ///     of a path that is a dozen segments long; the alternative is an icon that keeps the size
    ///     it had when the window was a different shape.
    /// </remarks>
    protected override void OnDraw(DrawContext context) {
        base.OnDraw(context);

        var bounds = context.Bounds;
        if (bounds.Width <= 0f || bounds.Height <= 0f) {
            return;
        }

        if (Art is { Paths.Count: > 0 } art) {
            Paint(context, art, bounds);
            return;
        }

        if (Geometry is not { Count: > 0 } geometry) {
            return;
        }

        Fit(geometry, ViewBox, bounds, out _);
        context.Fill(scaled, context.Foreground, FillRule);
    }

    /// <summary>Draws each path of a piece of art in the paint it declared.</summary>
    /// <remarks>
    ///     ⚠ <b>One path scaled at a time, into the same buffer.</b> The buffer is this element's and
    ///     a draw command copies out of it — <see cref="DrawContext.Fill" /> appends to the draw
    ///     list's own path storage — so reusing it across paths costs nothing and holding one buffer
    ///     per path would be an allocation per icon per frame.
    /// </remarks>
    void Paint(DrawContext context, IconArt art, Rectangle bounds) {
        foreach (var path in art.Paths) {
            if (path.Geometry is not { Count: > 0 } geometry) {
                continue;
            }

            Fit(geometry, art.ViewBox, bounds, out var scale);

            if (Resolve(context, path.Fill) is { } fill) {
                context.Fill(scaled, fill, path.FillRule);
            }

            // ⚠ The width is scaled with the geometry, so the same art reads the same weight at
            // sixteen pixels and at thirty-two. See `IconPath.Width`.
            if (Resolve(context, path.Stroke) is { } stroke) {
                context.Stroke(scaled, stroke, path.Width * scale, LineJoin.Round, LineCap.Round);
            }
        }
    }

    /// <summary>What a paint actually draws in, here and now.</summary>
    /// <returns>The colour, or <see langword="null" /> if this paint draws nothing.</returns>
    /// <remarks>
    ///     ⚠ <b>A token nothing in the cascade answers falls back to the inherited colour rather than
    ///     disappearing.</b> An icon whose stylesheet has not been loaded — a plugin's, before its
    ///     theme is — must be a visible glyph in the wrong colour and never an invisible one, which is
    ///     the same rule <see cref="UiDocument.ForegroundOf" /> follows for <c>color</c> itself.
    /// </remarks>
    Color4? Resolve(DrawContext context, in IconPaint paint) {
        switch (paint.Kind) {
            case IconPaintKind.Foreground:
                return context.Foreground;

            case IconPaintKind.Literal:
                return paint.Color;

            case IconPaintKind.Token:
                tokens ??= new(StringComparer.Ordinal);

                if (!tokens.TryGetValue(paint.Token, out var property)) {
                    tokens[paint.Token] = property = Document.PropertyId(paint.Token);
                }

                return Document.ColorOf(Style, property) ?? context.Foreground;

            default:
                return null;
        }
    }

    /// <summary>Scales one path from a view box into the element's bounds, into <see cref="scaled" />.</summary>
    /// <param name="geometry">The path.</param>
    /// <param name="viewBox">The grid it was drawn on.</param>
    /// <param name="bounds">Where it is going.</param>
    /// <param name="scale">What it was scaled by, which a stroke width needs too.</param>
    void Fit(PathBuilder geometry, Rectangle viewBox, Rectangle bounds, out float scale) {
        // Uniform, and centred in whatever is left over. Fitting each axis separately would let a
        // wide box stretch a circle into an ellipse, which is the one thing every icon set is drawn
        // on a square grid to avoid.
        scale = MathF.Min(bounds.Width / viewBox.Width, bounds.Height / viewBox.Height);

        var offsetX = bounds.X + ((bounds.Width - (viewBox.Width * scale)) * 0.5f) - (viewBox.X * scale);
        var offsetY = bounds.Y + ((bounds.Height - (viewBox.Height * scale)) * 0.5f) - (viewBox.Y * scale);

        scaled.Clear();

        foreach (var segment in geometry.Segments) {
            var p0 = new Vector2((segment.P0.X * scale) + offsetX, (segment.P0.Y * scale) + offsetY);
            var p1 = new Vector2((segment.P1.X * scale) + offsetX, (segment.P1.Y * scale) + offsetY);
            var p2 = new Vector2((segment.P2.X * scale) + offsetX, (segment.P2.Y * scale) + offsetY);

            // ⚠ The end point is P2 for every verb, including the ones with no control points —
            // which is what makes this switch about the control points only. Reading a line's
            // destination out of P0 gives the origin, so every icon collapses onto the top-left
            // corner of its box and looks like a scaling bug rather than a transcription one.
            switch (segment.Verb) {
                case PathVerb.Move:
                    scaled.MoveTo(p2);
                    break;

                case PathVerb.Line:
                    scaled.LineTo(p2);
                    break;

                case PathVerb.Quadratic:
                    scaled.QuadraticTo(p0, p2);
                    break;

                case PathVerb.Cubic:
                    scaled.CubicTo(p0, p1, p2);
                    break;

                default:
                    scaled.Close();
                    break;
            }
        }
    }
}
