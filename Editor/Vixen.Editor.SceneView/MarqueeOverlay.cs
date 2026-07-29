// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui;

namespace Vixen.Editor.SceneView;

/// <summary>The rubber-band rectangle, drawn over a pane.</summary>
/// <remarks>
///     <para>
///         <b>An interface element rather than geometry in the render target</b>, and that is the
///         whole reason <c>Viewport.Overlay</c> exists. A band drawn into the scene's colour target
///         would be in the scene's coordinate system, would be resolved at the render scale rather
///         than the layout one, and would arrive a frame late on every machine where the interface
///         and the renderer disagree about when a frame starts. Drawn here it is two rectangles in
///         the same pass as every other panel.
///     </para>
///     <para>
///         ⚠ <b>It takes no pointer events and must not.</b> The band is drawn on top of exactly the
///         pixels the drag is happening over, so an element that hit-tested would swallow the release
///         that ends it — a rubber-band that can be started and never finished. That is the theme's
///         <c>marquee { pointer-events: none }</c> rather than anything here: <c>viewport-overlay</c>
///         is already transparent to the pointer and its children deliberately are not, which is the
///         asymmetry that lets a toolbar sit in the same overlay.
///     </para>
/// </remarks>
public sealed class MarqueeOverlay : UiElement {
    int fillColour;
    int edgeColour;

    /// <inheritdoc />
    protected override string TagName => "marquee";

    /// <summary>The pane whose band this draws.</summary>
    public SceneViewport? Owner { get; set; }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        fillColour = Document.PropertyId("--marquee-fill");
        edgeColour = Document.PropertyId("--marquee-edge");
    }


    /// <inheritdoc />
    protected override void OnDraw(DrawContext context) {
        base.OnDraw(context);

        if (Owner is not { Selecting: { } band } owner) {
            return;
        }

        // ⚠ Render pixels back into layout pixels. The band is measured where the pointer was
        // reported — in the render target's units — and everything in a draw list is in
        // device-independent ones, so on a scaled display a band drawn without this covers a quarter
        // of the area the drag actually swept.
        var scale = owner.Control.RenderScale <= 0f ? 1f : owner.Control.RenderScale;
        var origin = owner.Control.Bounds;

        var rectangle = new Rectangle(
            origin.X + (band.Left / scale),
            origin.Y + (band.Top / scale),
            band.Width / scale,
            band.Height / scale
        );

        if (rectangle.Width <= 0f || rectangle.Height <= 0f) {
            return;
        }

        context.FillRectangle(rectangle, Document.ColorOf(Style, fillColour) ?? new Color4(0.35f, 0.6f, 1f, 0.15f));

        context.StrokeRectangle(
            rectangle,
            Document.ColorOf(Style, edgeColour) ?? new Color4(0.55f, 0.76f, 1f, 0.9f),
            1f
        );
    }
}
