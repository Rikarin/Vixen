// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Ui.Controls;

/// <summary>A picture over a document of what <see cref="DiagnosticsPanel" /> says in rows.</summary>
/// <remarks>
///     <para>
///         <b>The drawn half of doc 13's UI-debug view.</b> The panel reports the element under
///         the pointer as four rectangles of numbers and the last pass's invalidations as a count;
///         this draws them where they are — four nested outlines round the element, in the CSS
///         colours every browser's inspector uses for the same four boxes, and a translucent wash
///         over every region the last pass invalidated. A number is exact and a picture is
///         legible, and "which element is this" is a question a picture answers first.
///     </para>
///     <para>
///         ⚠ <b>It lives in the document it describes, and that is the only arrangement in which
///         its coordinates mean anything.</b> The boxes are in the subject's space, so an overlay in
///         another document would draw them somewhere else. That is the opposite of the panel's
///         advice — the panel is exact when it is <i>not</i> in the document it counts — and the
///         two are separate controls for exactly this reason: the numbers go in a tools window and
///         the picture goes over the thing. Placed over the whole surface with
///         <c>pointer-events: none</c>, by the theme rule, so that it is never the element under
///         the pointer itself.
///     </para>
///     <para>
///         ⚠ <b>It draws and never writes.</b> <see cref="UiElement.OnDraw" /> runs in the middle of
///         a walk emitting commands in painting order, so everything here is a read of the
///         aggregator — the hovered element, its boxes from the layout results, the regions the
///         previous pass recorded — and a stroke or a fill for each. Nothing is styled, added or
///         invalidated, which is what keeps a debug overlay from being part of the churn it shows.
///         A region wash is the <i>previous</i> pass's answer, for the same reason the panel's
///         numbers are a frame old: the regions this pass records are turned at its exit.
///     </para>
///     <para>
///         The colours are theme tokens — <c>--diagnostics-margin</c> and its siblings — so a dark
///         palette can lift them, with the inspector's conventional values as the fallback a theme
///         that says nothing gets. The wash is deliberately faint: it is drawn over live controls
///         and its job is to say <i>where</i> rather than to hide what.
///     </para>
/// </remarks>
public sealed class DiagnosticsOverlay : Control {
    int marginColor;
    int borderColor;
    int paddingColor;
    int contentColor;
    int dirtyColor;

    /// <inheritdoc />
    protected override string TagName => "diagnostics-overlay";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The document whose element and regions are drawn. Its own, unless another is named.</summary>
    /// <remarks>
    ///     ⚠ Naming another document draws that document's boxes in this one's coordinates, which is
    ///     only right when the two share a surface and an origin — a tools overlay on a second window
    ///     is not that. The property exists so a host that <i>has</i> that arrangement can say so;
    ///     the default is the honest one.
    /// </remarks>
    public UiDocument? Subject { get; set; }

    /// <summary>A point to describe instead of the pointer, in the subject's coordinates.</summary>
    /// <remarks>
    ///     The same rule as <see cref="DiagnosticsPanel.Probe" />: a probe wins when one is set, and
    ///     with none the subject's own <see cref="UiDocument.Hovered" /> is the element drawn.
    /// </remarks>
    public Vector2? Probe { get; set; }

    /// <summary>Whether the last pass's invalidated regions are washed. On by default.</summary>
    /// <remarks>
    ///     Off is for reading the box model on a document that is animating, where the wash would
    ///     cover the thing being looked at. In a build that does not record regions there is nothing
    ///     to draw and this changes nothing — see <see cref="UiDiagnostics.RecordsRegions" />.
    /// </remarks>
    public bool ShowsRegions { get; set; } = true;

    /// <summary>How many outlines the last draw stroked: four for an element, none for nothing.</summary>
    /// <remarks>
    ///     The instrument for this control's tests, for <see cref="DiagnosticsPanel.RowCount" />'s
    ///     reason: "it drew the element" and "it drew nothing" are otherwise the same draw list from
    ///     outside, once other elements' commands are in it.
    /// </remarks>
    public int Outlines { get; private set; }

    /// <summary>How many regions the last draw washed.</summary>
    public int Regions { get; private set; }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        marginColor = Document.PropertyId("--diagnostics-margin");
        borderColor = Document.PropertyId("--diagnostics-border");
        paddingColor = Document.PropertyId("--diagnostics-padding");
        contentColor = Document.PropertyId("--diagnostics-content");
        dirtyColor = Document.PropertyId("--diagnostics-dirty");
    }

    /// <inheritdoc />
    protected override void OnDraw(DrawContext context) {
        base.OnDraw(context);

        Outlines = 0;
        Regions = 0;

        var subject = Subject ?? Document;
        var diagnostics = subject.Diagnostics;

        if (ShowsRegions) {
            var wash = Document.ColorOf(Style, dirtyColor) ?? new Color4(1f, 0.25f, 0.25f, 0.18f);

            foreach (var region in diagnostics.DirtyRegions) {
                // ⚠ A cold pass is recorded as the root's box, and washing it would cover the whole
                // document — which says nothing about *where* and hides everything about *what*.
                // The panel's row still counts it; the picture draws only the records that name a
                // place. A region with no area is one recorded before its element was laid out.
                if (region.Kind == UiInvalidationKind.Document || region.Bounds.Width <= 0f || region.Bounds.Height <= 0f) {
                    continue;
                }

                context.FillRectangle(region.Bounds, wash);
                Regions++;
            }
        }

        if (!TryDescribe(subject, Probe, diagnostics, out var element, out var box) || element is null) {
            return;
        }

        // Outermost first, so that where two boxes coincide — an element with no margin, no border
        // or no padding, which is most of them — the innermost outline is the one on top and the
        // reading is "content", which is the box the element's words are in.
        Stroke(context, box.Margin, Document.ColorOf(Style, marginColor) ?? new Color4(0.98f, 0.62f, 0.25f, 0.9f));
        Stroke(context, box.Border, Document.ColorOf(Style, borderColor) ?? new Color4(0.98f, 0.85f, 0.35f, 0.9f));
        Stroke(context, box.Padding, Document.ColorOf(Style, paddingColor) ?? new Color4(0.45f, 0.78f, 0.45f, 0.9f));
        Stroke(context, box.Content, Document.ColorOf(Style, contentColor) ?? new Color4(0.35f, 0.6f, 0.98f, 0.9f));
    }

    void Stroke(DrawContext context, Rectangle box, Color4 color) {
        if (box.Width <= 0f || box.Height <= 0f) {
            return;
        }

        context.StrokeRectangle(box, color, 1f);
        Outlines++;
    }

    /// <summary>The element a probe or the pointer names, and its boxes.</summary>
    /// <param name="subject">The document asked.</param>
    /// <param name="probe">A point, or none for the pointer.</param>
    /// <param name="diagnostics">The subject's aggregator.</param>
    /// <param name="element">What is there.</param>
    /// <param name="box">Its boxes.</param>
    /// <returns>Whether there is anything to describe.</returns>
    /// <remarks>
    ///     Shared with <see cref="DiagnosticsPanel" /> so the rows and the picture cannot disagree
    ///     about which element they mean.
    /// </remarks>
    internal static bool TryDescribe(
        UiDocument subject,
        Vector2? probe,
        in UiDiagnostics diagnostics,
        out UiElement? element,
        out UiBoxModel box
    ) {
        if (probe is { } point) {
            return diagnostics.TryDescribe(point.X, point.Y, out element, out box);
        }

        element = subject.Hovered;

        if (element is null) {
            box = default;
            return false;
        }

        box = diagnostics.BoxOf(element);

        return true;
    }
}
