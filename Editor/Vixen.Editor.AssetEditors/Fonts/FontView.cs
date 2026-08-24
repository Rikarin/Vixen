// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Text;
using Vixen.Ui.Text.Outlines;

namespace Vixen.Editor.AssetEditors.Fonts;

/// <summary>A page of glyphs, drawn from the face's own outlines.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The outlines, not the atlas, and the difference is what it can be honest about.</b>
///         A real atlas page is a distance field on a GPU texture, which this assembly has no device
///         to make — the same split every other preview in it makes. What the outlines can show is
///         the thing somebody opens this panel for: whether the glyphs are the shapes they expected,
///         at the size they chose, with the padding they set. The <i>packing</i> is drawn to scale,
///         so a padding that wastes half the page is visible as half the page.
///     </para>
///     <para>
///         ⚠ <b>Curves are flattened here and are curves in the font.</b> <c>GlyphOutline</c> keeps
///         quadratics and cubics because a distance field wants the curve; a preview is drawn at tens
///         of pixels, where sixteen segments a curve is beyond what anybody can see.
///     </para>
/// </remarks>
public sealed class FontAtlasView : UiElement {
    readonly PathBuilder path = new();

    /// <inheritdoc />
    protected override string TagName => "font-atlas";

    /// <summary>The document whose faces are drawn.</summary>
    public FontDocument? Font { get; set; }

    /// <summary>The first code point drawn.</summary>
    public int First { get; set; } = 0x0020;

    /// <summary>How many are drawn.</summary>
    public int Count { get; set; } = 96;

    /// <summary>Which code point is under the pointer, or −1.</summary>
    public int Hovered { get; private set; } = -1;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        AddHandler<PointerEvent>(static (element, args) => ((FontAtlasView) element).Pointed(args));
    }

    void Pointed(PointerEvent args) {
        var bounds = new Rectangle(AbsoluteLeft, AbsoluteTop, Width, Height);
        var cell = CellSize(bounds);

        if (cell <= 0f) {
            return;
        }

        var columns = Math.Max(1, (int) (bounds.Width / cell));
        var column = (int) ((args.X - bounds.X) / cell);
        var line = (int) ((args.Y - bounds.Y) / cell);
        var index = (line * columns) + column;

        Hovered = column >= 0 && column < columns && index >= 0 && index < Count ? First + index : -1;
    }

    float CellSize(Rectangle bounds) {
        if (bounds.Width <= 1f || Count <= 0) {
            return 0f;
        }

        // As many columns as fit at roughly the chosen pixel size, so raising the size makes the
        // glyphs bigger rather than making the page scroll.
        var wanted = Math.Max(Font?.Font.PixelSize ?? 48f, 8f) + (2f * Math.Max(Font?.Font.Padding ?? 0, 0));
        var columns = Math.Max(1, (int) (bounds.Width / wanted));

        return bounds.Width / columns;
    }

    /// <inheritdoc />
    protected override void OnDraw(DrawContext context) {
        base.OnDraw(context);

        var bounds = context.Bounds;
        var cell = CellSize(bounds);

        if (Font is not { Face: { } face } document || cell <= 0f) {
            return;
        }

        var columns = Math.Max(1, (int) (bounds.Width / cell));
        var padding = Math.Max(document.Font.Padding, 0);
        var foreground = document.Font.DistanceField ? new Color4(0.85f, 0.87f, 0.9f, 1f) : new Color4(0.9f, 0.9f, 0.9f, 1f);

        for (var index = 0; index < Count; index++) {
            var code = First + index;
            var column = index % columns;
            var line = index / columns;

            var box = new Rectangle(
                bounds.X + (column * cell),
                bounds.Y + (line * cell),
                cell,
                cell
            );

            if (box.Y > bounds.Bottom) {
                break;
            }

            var owner = document.Resolves(code);

            // The cell's background says which face in the chain drew it, which is the whole point of
            // showing a chain rather than storing one.
            context.FillRectangle(
                box,
                owner switch {
                    0 => new Color4(1f, 1f, 1f, 0.03f),
                    < 0 => new Color4(0.87f, 0.29f, 0.33f, 0.12f),
                    _ => new Color4(0.23f, 0.62f, 0.94f, 0.12f)
                },
                2f
            );

            if (code == Hovered) {
                context.StrokeRectangle(box, new Color4(0.23f, 0.42f, 0.94f, 0.9f), 1.5f);
            }

            if (owner < 0) {
                continue;
            }

            var drawing = owner == 0 ? face : document.Fallbacks[owner - 1];
            var glyph = drawing.GlyphFor(code);

            if (glyph == 0 || !drawing.HasOutlines) {
                continue;
            }

            Glyph(context, drawing, glyph, box, padding, foreground);
        }
    }

    void Glyph(DrawContext context, FontFace face, ushort glyph, Rectangle box, int padding, Color4 colour) {
        var outline = face.GetOutline(glyph);

        if (outline.IsEmpty) {
            return;
        }

        var scale = (box.Width - (2f * padding)) / face.UnitsPerEm;

        if (scale <= 0f) {
            return;
        }

        // ⚠ Baseline placed from the metrics rather than from the glyph's own box. Centring each
        // glyph in its cell would make a comma sit where an M sits, which is exactly the mistake a
        // font preview must not make: what somebody is checking is the *relationship* between the
        // shapes, and that lives on the baseline.
        var origin = new Vector2(
            box.X + padding,
            box.Y + padding + (face.Metrics.Ascender * scale)
        );

        path.Clear();

        var cursor = Vector2.Zero;

        foreach (var segment in outline.Segments) {
            switch (segment.Verb) {
                case OutlineVerb.Move:
                    cursor = Point(origin, scale, segment.X0, segment.Y0);
                    path.MoveTo(cursor);

                    break;

                case OutlineVerb.Line:
                    cursor = Point(origin, scale, segment.X0, segment.Y0);
                    path.LineTo(cursor);

                    break;

                case OutlineVerb.Quadratic: {
                    var control = Point(origin, scale, segment.X0, segment.Y0);
                    var end = Point(origin, scale, segment.X1, segment.Y1);

                    for (var step = 1; step <= Steps; step++) {
                        var t = (float) step / Steps;
                        var inverse = 1f - t;

                        path.LineTo(
                            (inverse * inverse * cursor) + (2f * inverse * t * control) + (t * t * end)
                        );
                    }

                    cursor = end;
                    break;
                }

                case OutlineVerb.Cubic: {
                    var first = Point(origin, scale, segment.X0, segment.Y0);
                    var second = Point(origin, scale, segment.X1, segment.Y1);
                    var end = Point(origin, scale, segment.X2, segment.Y2);

                    for (var step = 1; step <= Steps; step++) {
                        var t = (float) step / Steps;
                        var inverse = 1f - t;

                        path.LineTo(
                            (inverse * inverse * inverse * cursor)
                            + (3f * inverse * inverse * t * first)
                            + (3f * inverse * t * t * second)
                            + (t * t * t * end)
                        );
                    }

                    cursor = end;
                    break;
                }

                default:
                    path.Close();
                    break;
            }
        }

        // ⚠ Stroked rather than filled, and that is honest rather than a limitation being hidden.
        // A glyph's contours use the non-zero winding rule to punch counters — the hole in an 'o' —
        // and `PathBuilder`'s fill does not carry a winding rule, so a filled 'o' would be a blob.
        // An outline shows every contour, which is what somebody checking a font wants to see.
        context.Stroke(path, colour, 1f);
    }

    /// <summary>How many line segments one curve is drawn with.</summary>
    const int Steps = 8;

    static Vector2 Point(Vector2 origin, float scale, float x, float y) =>
        new(origin.X + (x * scale), origin.Y - (y * scale));
}

/// <summary>A font asset, open for editing: coverage, the glyphs, and the fallback chain.</summary>
/// <remarks>
///     <para>
///         The panel is <c>FontView.vxml</c>; this file is the accessibility modifier, the glyph page
///         it draws beside, the four records its lists key on, and the two elements that exist only
///         so that markup can write an intrinsic tag's own <c>Text</c>.
///     </para>
///     <para>
///         ⚠ <b>Shape 2 is closed, so the field rows are ported too.</b> The ledger's row for this
///         panel read "port the readouts, keep the field rows", which was written before
///         <c>change:</c> existed. Every one of the five editable fields is a <c>[UiProperty]</c> —
///         <c>NumericInput.Number</c> and <c>CheckBox.IsChecked</c> — so all five are an ordinary
///         binding one way and a <c>change:</c> handler the other, and nothing in this panel is
///         imperative but the glyph page and the picker's options.
///     </para>
/// </remarks>
public sealed partial class FontView;

/// <summary>One thing the face says about itself, as the <c>@for</c> keys it.</summary>
/// <param name="Slot">Where it is in the order the panel lists them.</param>
/// <param name="Name">What the fact is called.</param>
/// <param name="Value">What it says.</param>
/// <remarks>
///     ⚠ <b>The whole record is the key</b>, which is the immutable-data half of the <c>@for</c>
///     rule: nothing behind this panel is signal-backed, so a re-read face has to be a changed
///     identity. The slot is in it because a face with no metrics and a face with no name can print
///     the same two words, and <c>BuildContext.For</c> cannot reconcile two equal keys in one loop.
/// </remarks>
internal readonly record struct FontFactRow(int Slot, string Name, string Value);

/// <summary>One line of the fallback chain, or one complaint about it.</summary>
/// <param name="Slot">Where it is in the chain, complaints last.</param>
/// <param name="Class"><c>error</c> for a complaint, and empty for a face that loaded.</param>
/// <param name="Text">The line's own text — a numbered face, or what is wrong.</param>
internal readonly record struct FontChainRow(int Slot, string Class, string Text);

/// <summary>How much of one Unicode block the chain covers.</summary>
/// <param name="Slot">Where the block is in the order the face reports them.</param>
/// <param name="Class"><c>error</c> for nothing covered, <c>warning</c> for a gap, empty for whole.</param>
/// <param name="Name">The block's name, which is also the picker's option.</param>
/// <param name="Value">The count and the percentage, or an em dash for a block with nothing in it.</param>
internal readonly record struct FontBlockRow(int Slot, string Class, string Name, string Value);

/// <summary>The four atlas settings and the distance-field flag, as one snapshot.</summary>
/// <param name="PixelSize">What size the glyphs are rasterised at.</param>
/// <param name="Padding">How much room is left round each one.</param>
/// <param name="AtlasWidth">How wide the page is.</param>
/// <param name="AtlasHeight">And how tall.</param>
/// <param name="DistanceField">Whether the page is a distance field rather than coverage.</param>
/// <remarks>
///     ⚠ <b>One record rather than five signals, which is <c>TextureImportView</c>'s finding.</b>
///     Every field here lives on <c>FontAsset</c> — a plain mutable object no signal watches — and
///     the panel finds out one moved because <c>Edit</c> raises <c>Changed</c> and <c>Reload</c>
///     runs. Five signals over five fields would each depend on the document and none of them on the
///     edit, which is the shape a revision counter gets invented to paper over.
/// </remarks>
internal readonly record struct FontSettings(
    float PixelSize,
    int Padding,
    int AtlasWidth,
    int AtlasHeight,
    bool DistanceField
);

/// <summary>
///     ⚠ The elements that exist only so that markup can set an intrinsic tag's own <c>Text</c>.
/// </summary>
/// <remarks>
///     The panel ledger's shape 5 and its sanctioned escape; <c>FactName</c> in <c>Captions.cs</c>
///     carries the full argument, and this panel uses that one for its <c>fact-name</c> cells.
/// </remarks>
internal sealed class FontTitle : UiElement {
    /// <inheritdoc />
    protected override string TagName => "font-title";
}

/// <inheritdoc cref="FontTitle" />
internal sealed class FontChainLine : UiElement {
    /// <inheritdoc />
    protected override string TagName => "font-chain-row";
}

/// <summary>Opens a font asset.</summary>
public sealed class FontEditorFactory : IAssetEditorFactory {
    /// <inheritdoc />
    public string Name => "Font";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [FontDocument.Extension];

    /// <inheritdoc />
    public EditorDocument Open(AssetEditorRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        return new FontDocument(request.Project, request.Asset, request.Path);
    }

    /// <inheritdoc />
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(panel);

        var view = panel.Add<FontView>();
        view.Show((FontDocument) document);

        return view;
    }
}
