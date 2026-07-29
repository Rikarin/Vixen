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
public sealed class FontView : Control {
    FontDocument? document;

    /// <inheritdoc />
    protected override string TagName => "font-editor";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The glyph page.</summary>
    public FontAtlasView Atlas { get; private set; } = null!;

    /// <summary>The column beside it.</summary>
    public UiElement Side { get; private set; } = null!;

    /// <summary>The face's own facts and the atlas settings.</summary>
    public UiElement Facts { get; private set; } = null!;

    /// <summary>The fallback chain.</summary>
    public UiElement Chain { get; private set; } = null!;

    /// <summary>How much of each block the chain covers.</summary>
    public UiElement Blocks { get; private set; } = null!;

    /// <summary>Which block the page is showing.</summary>
    public Select BlockChooser { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        var bar = Part("font-bar");

        bar.Add("fact-name").Text = "Block";
        BlockChooser = bar.Add<Select>();

        Atlas = Part<FontAtlasView>();

        Side = Part("font-side");
        Facts = Side.Add("font-facts");
        Chain = Side.Add("font-chain");
        Blocks = Side.Add("font-blocks");

        BlockChooser.SelectionChanged += (_, _) => Choose();
    }

    /// <summary>Shows a font.</summary>
    /// <param name="font">The document.</param>
    public void Show(FontDocument font) {
        ArgumentNullException.ThrowIfNull(font);

        if (document is { } previous) {
            previous.Changed -= Reload;
        }

        document = font;
        font.Changed += Reload;

        Atlas.Font = font;
        Reload(font);
    }

    /// <summary>Rebuilds everything from the document.</summary>
    /// <param name="font">The document.</param>
    public void Reload(FontDocument font) {
        ArgumentNullException.ThrowIfNull(font);

        Facts.Empty();
        Chain.Empty();
        Blocks.Empty();

        Facts.Add("font-title").Text = font.Font.Name;

        if (font.Face is { } face) {
            Fact("Family", face.Name);
            Fact("Units per em", face.UnitsPerEm.ToString(CultureInfo.InvariantCulture));
            Fact("Glyphs", face.GlyphCount.ToString(CultureInfo.InvariantCulture));
            Fact("Line height", face.Metrics.LineHeight.ToString(CultureInfo.InvariantCulture));
            Fact("Variable", face.IsVariable ? string.Join(", ", face.Axes.Select(axis => axis.Tag)) : "no");
        } else {
            Fact("Family", "no face loaded");
        }

        Number("Pixel size", font.Font.PixelSize, value => font.Edit("Set Pixel Size", asset => asset.PixelSize = value));
        Number("Padding", font.Font.Padding, value => font.Edit("Set Padding", asset => asset.Padding = (int) value));
        Number("Atlas width", font.Font.AtlasWidth, value => font.Edit("Set Atlas Width", asset => asset.AtlasWidth = (int) value));
        Number("Atlas height", font.Font.AtlasHeight, value => font.Edit("Set Atlas Height", asset => asset.AtlasHeight = (int) value));

        var field = Facts.Add("fact-row");
        field.Add("fact-name").Text = "Distance field";

        var toggle = field.Add("fact-value").Add<CheckBox>();
        toggle.IsChecked = font.Font.DistanceField;
        toggle.CheckedChanged += (_, on) => font.Edit("Set Distance Field", asset => asset.DistanceField = on);

        Chain.Add("font-title").Text = "Fallback chain";
        Chain.Add("font-chain-row").Text = font.Face is { } primary ? $"1. {primary.Name}" : "1. (no face)";

        for (var index = 0; index < font.Fallbacks.Count; index++) {
            Chain.Add("font-chain-row").Text = $"{index + 2}. {font.Fallbacks[index].Name}";
        }

        foreach (var problem in font.Problems) {
            var row = Chain.Add("font-chain-row");

            row.AddClass("error");
            row.Text = problem;
        }

        Blocks.Add("font-title").Text = "Coverage";

        var options = BlockChooser.Value;

        BlockChooser.ClearOptions();

        foreach (var coverage in font.Coverage()) {
            var row = Blocks.Add("fact-row");
            row.Add("fact-name").Text = coverage.Name;

            var value = row.Add("fact-value").Add("text");

            value.Text = coverage.Assigned == 0
                ? "—"
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"{coverage.Covered}/{coverage.Assigned}  ({coverage.Fraction * 100f:0}%)"
                );

            if (coverage.Covered == 0) {
                row.AddClass("error");
            } else if (coverage.Covered < coverage.Assigned) {
                row.AddClass("warning");
            }

            BlockChooser.AddOption(coverage.Name);
        }

        var blocks = font.Coverage();

        BlockChooser.Value = options ?? (blocks.Count > 0 ? blocks[0].Name : null);
        Choose();
    }

    void Choose() {
        if (document is not { } font || BlockChooser.Value is not { Length: > 0 } chosen) {
            return;
        }

        foreach (var coverage in font.Coverage()) {
            if (!string.Equals(coverage.Name, chosen, StringComparison.Ordinal)) {
                continue;
            }

            Atlas.First = coverage.First;
            Atlas.Count = coverage.Last - coverage.First + 1;

            return;
        }
    }

    void Fact(string label, string value) {
        var row = Facts.Add("fact-row");

        row.Add("fact-name").Text = label;
        row.Add("fact-value").Add("text").Text = value;
    }

    void Number(string label, float value, Action<float> write) {
        var row = Facts.Add("fact-row");
        row.Add("fact-name").Text = label;

        var box = row.Add("fact-value").Add<NumericInput>();

        box.Decimals = 0;
        box.Number = value;
        box.NumberChanged += (_, number) => write((float) number);
    }
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
