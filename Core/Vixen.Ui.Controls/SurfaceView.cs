// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Ui.Controls;

/// <summary>What to do when a picture and the box it goes in are different shapes.</summary>
/// <remarks>
///     ⚠ <b>The same three <c>VideoScaling</c> has, and it is a second enum rather than that one.</b>
///     There are only three answers — change the shape, letterbox, or crop — so any two libraries that
///     solve this agree on the list; what they must not do is share a type, because
///     <c>Vixen.Ui.Controls</c> naming <c>Vixen.Video</c>'s enum would put a WebM demuxer behind every
///     button. CSS's <c>object-fit</c> is the third independent copy of the same three words.
/// </remarks>
public enum SurfaceFit : byte {
    /// <summary>Fill the box exactly, changing the aspect ratio.</summary>
    Stretch,

    /// <summary>Fit inside the box, keeping the shape. The leftover is not drawn on.</summary>
    Contain,

    /// <summary>Fill the box, keeping the shape. What does not fit is clipped away.</summary>
    Cover
}

/// <summary>A picture the UI does not own: a video, a render target, a camera feed.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>It holds an <see langword="object" />, and that is the whole design.</b>
///         <c>Vixen.Ui</c> touches no device, so it cannot hold a texture; what it can do is carry a
///         reference and an index, and let <c>Vixen.Ui.Renderer</c>'s surface drawer resolve them —
///         see <c>DrawCommandKind.Surface</c>. So this control works for any picture anybody teaches
///         the renderer about, and knows about none of them. It is what <c>Avatar</c>'s remarks
///         called owed.
///     </para>
///     <para>
///         <b>The two fits are two mechanisms the UI already has, and neither is new.</b>
///         <see cref="SurfaceFit.Contain" /> shrinks the rectangle before it is drawn;
///         <see cref="SurfaceFit.Cover" /> pushes a clip and draws past it. Nothing here paints
///         letterbox bars — a control that did would paint opaque black over whatever it was laid
///         over, which is wrong the first time somebody puts a video behind a menu.
///     </para>
/// </remarks>
public partial class SurfaceView : Control {
    /// <inheritdoc />
    protected override string TagName => "surface";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The picture. Null draws nothing at all — not a placeholder.</summary>
    /// <remarks>
    ///     ⚠ Reference-compared by the draw list's frame diff, so replacing it is what makes a cached
    ///     frame rebuild. Setting it to the same object every frame — which is what a video does —
    ///     costs nothing.
    /// </remarks>
    [UiProperty]
    public partial object? Source { get; set; }

    /// <summary>How big the picture is, for the fit. Zero in either axis means fill the box.</summary>
    /// <remarks>
    ///     ⚠ <b>The size it is meant to <i>look</i>, not its texel count.</b> Anamorphic video is the
    ///     case: a 720×480 clip shown at 853×480 fits by the second pair and is a fifth too narrow if
    ///     it fits by the first. Whoever sets <see cref="Source" /> knows which is which.
    /// </remarks>
    [UiProperty]
    public partial Vector2 SourceSize { get; set; }

    /// <summary>What to do when the shapes disagree.</summary>
    [UiProperty]
    public partial SurfaceFit Fit { get; set; }

    /// <summary>Multiplied into the picture. The default is the picture untouched.</summary>
    [UiProperty]
    public partial Color4 Tint { get; set; }

    /// <summary>Where a picture lands inside a box under a fit.</summary>
    /// <param name="fit">Which answer.</param>
    /// <param name="source">The picture's displayed size. Zero in either axis fills the box.</param>
    /// <param name="box">The box.</param>
    /// <returns>The rectangle to draw. Larger than the box for <see cref="SurfaceFit.Cover" />.</returns>
    /// <remarks>
    ///     Static and pure so that a test can check the arithmetic without a document, a layout pass
    ///     or a device — which is the property every part of <c>Vixen.Ui</c> above the renderer has.
    /// </remarks>
    public static Rectangle Place(SurfaceFit fit, Vector2 source, Rectangle box) {
        if (fit == SurfaceFit.Stretch || source.X <= 0 || source.Y <= 0 || box.Width <= 0 || box.Height <= 0) {
            return box;
        }

        var wanted = source.X / source.Y;
        var available = box.Width / box.Height;

        // Within a pixel of the box's own height, the bars are thinner than the edge they would sit
        // against — so the answer is the box, and one seam fewer.
        if (MathF.Abs(wanted - available) * box.Height < 1f) {
            return box;
        }

        // Contain constrains by whichever axis runs out first; Cover constrains by the other one.
        // That is the whole difference between them, and it is one comparison.
        var byWidth = fit == SurfaceFit.Contain ? wanted > available : wanted <= available;

        var width = byWidth ? box.Width : box.Height * wanted;
        var height = byWidth ? box.Width / wanted : box.Height;

        return new Rectangle(
            box.X + ((box.Width - width) / 2f),
            box.Y + ((box.Height - height) / 2f),
            width,
            height
        );
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Fitted on every draw rather than when the source changes</b>, for the reason
    ///     <c>Icon</c> rescales on every draw: what it is fitted to is the layout result, and nothing
    ///     tells an element that its box moved — a sibling growing resizes this one without touching
    ///     a property on it.
    /// </remarks>
    protected override void OnDraw(DrawContext context) {
        base.OnDraw(context);

        if (Source is not { } source) {
            return;
        }

        var bounds = context.Bounds;

        if (bounds.Width <= 0f || bounds.Height <= 0f) {
            return;
        }

        var placed = Place(Fit, SourceSize, bounds);
        var clipped = Fit == SurfaceFit.Cover && (placed.Width > bounds.Width || placed.Height > bounds.Height);

        if (clipped) {
            context.List.Add(
                new DrawCommand(
                    DrawCommandKind.ClipPush,
                    bounds.X,
                    bounds.Y,
                    bounds.Width,
                    bounds.Height,
                    default,
                    0f,
                    0f
                )
            );
        }

        context.Surface(placed, source, Tint);

        if (clipped) {
            context.List.Add(new DrawCommand(DrawCommandKind.ClipPop, 0, 0, 0, 0, default, 0f, 0f));
        }
    }
}
