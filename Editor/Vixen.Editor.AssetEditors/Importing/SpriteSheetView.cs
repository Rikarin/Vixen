// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Editor.Assets.Textures;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.AssetEditors.Importing;

/// <summary>
///     A texture with the sprites cut out of it drawn on top, and the controls that cut them.
/// </summary>
/// <remarks>
///     <para>
///         <b>The panel a sprite sheet is sliced in.</b> Three ways to cut — a grid by cell size, a
///         grid by cell count, and one sprite per island of opaque texels — then the rects as boxes
///         over the picture, a list beside it, and the selected rect's numbers underneath. What it
///         does <i>not</i> have is a mode of its own: slicing writes rects onto the texture's own
///         import settings, so this is a second view over <see cref="TextureImportDocument" /> rather
///         than a second document over the same file. Two documents over one <c>.meta</c> would be
///         two undo histories over one set of bytes, and whichever saved last would win.
///     </para>
///     <para>
///         ⚠ <b>It shows the picture and does not draw it</b>, exactly as
///         <see cref="TextureImportView" /> does and for the same reason: nothing in this assembly has
///         a graphics device, so a texture reaches the interface as a number a <c>UiRenderer</c>
///         handed out for one it uploaded. The view decodes the file — CPU work, and what the slicer
///         needs anyway — and the application puts the number in <see cref="Preview" />.
///     </para>
///     <para>
///         ⚠ <b>Every rect on the overlay is positioned inline in texels times <see cref="Zoom" />,
///         not by layout.</b> A stylesheet cannot express "this box is at texel 96 of that picture",
///         and computing it here rather than reading it back out of the layout is what lets the
///         overlay be asserted in a test without a frame having been drawn.
///     </para>
///     <para>
///         ⚠ <b>Slicing is a suggestion.</b> What it produces goes into the document as rects an
///         author then drags, renames and deletes — the sidecar records the answer rather than the
///         question. <c>SpriteSlicer</c>'s own remarks say why: an automatic slice depends on the
///         pixels, so a re-export with one frame nudged would renumber the sheet and quietly repoint
///         every reference into it.
///     </para>
/// </remarks>
public sealed class SpriteSheetView : Control {
    readonly List<UiElement> rects = [];
    readonly Dictionary<UiElement, int> indices = [];
    readonly List<NumericInput> geometry = [];

    TextureImportDocument? document;
    TextureData? source;
    float zoom = 1f;
    bool restating;
    bool listening;

    /// <inheritdoc />
    protected override string TagName => "sprite-editor";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>Where the texture is drawn once something has uploaded it.</summary>
    public Image Preview { get; private set; } = null!;

    /// <summary>The picture and the boxes over it, sized to the texture times <see cref="Zoom" />.</summary>
    public UiElement Canvas { get; private set; } = null!;

    /// <summary>The boxes, one per sprite, in the document's order.</summary>
    public IReadOnlyList<UiElement> Rects => rects;

    /// <summary>The sprite names, one row each.</summary>
    public UiElement List { get; private set; } = null!;

    /// <summary>The selected sprite's numbers.</summary>
    public UiElement Fields { get; private set; } = null!;

    /// <summary>How the texture is cut.</summary>
    public Select Method { get; private set; } = null!;

    /// <summary>The cell's width, or the column count — whichever the method reads.</summary>
    public NumericInput CellWidth { get; private set; } = null!;

    /// <summary>The cell's height, or the row count.</summary>
    public NumericInput CellHeight { get; private set; } = null!;

    /// <summary>Where the grid starts, horizontally.</summary>
    public NumericInput GridOffsetX { get; private set; } = null!;

    /// <summary>Where the grid starts, vertically.</summary>
    public NumericInput GridOffsetY { get; private set; } = null!;

    /// <summary>The gap between cells, horizontally.</summary>
    public NumericInput PaddingX { get; private set; } = null!;

    /// <summary>The gap between cells, vertically.</summary>
    public NumericInput PaddingY { get; private set; } = null!;

    /// <summary>Whether each rect is shrunk to what is drawn inside it.</summary>
    public ToggleButton TrimToggle { get; private set; } = null!;

    /// <summary>Whether a grid keeps cells with nothing in them.</summary>
    public ToggleButton KeepEmptyToggle { get; private set; } = null!;

    /// <summary>Cuts the texture with the options as they stand.</summary>
    public Button SliceButton { get; private set; } = null!;

    /// <summary>Throws every sprite away.</summary>
    public Button ClearButton { get; private set; } = null!;

    /// <summary>Adds one sprite covering the whole texture, for a sheet cut by hand.</summary>
    public Button AddButton { get; private set; } = null!;

    /// <summary>Removes the selected sprite.</summary>
    public Button RemoveButton { get; private set; } = null!;

    /// <summary>Shown when there are no pixels to slice.</summary>
    public Alert Unavailable { get; private set; } = null!;

    /// <summary>The decoded source, or <see langword="null" /> if nothing could decode it.</summary>
    public TextureData? Source => source;

    /// <summary>Which sprite is selected, or -1 for none.</summary>
    public int Selected { get; private set; } = -1;

    /// <summary>How many screen pixels one texel takes.</summary>
    /// <remarks>
    ///     ⚠ <b>Settable rather than fitted automatically, and <see cref="FitTo" /> is the other
    ///     half.</b> A sprite editor is looked at at 1:1 as often as it is looked at whole — a
    ///     32-texel tile inspected for a seam wants magnification, and a 4096 sheet wants to fit —
    ///     and a view that decided for itself would be fighting the person who wanted the other one.
    /// </remarks>
    public float Zoom {
        get => zoom;
        set {
            var clamped = Math.Clamp(value, 0.05f, 32f);

            if (MathF.Abs(clamped - zoom) < 0.0001f) {
                return;
            }

            zoom = clamped;
            Restate();
        }
    }

    /// <summary>The options the toolbar is asking for.</summary>
    public SpriteSliceOptions Options =>
        new(MethodOf(Method.Value)) {
            CellSize = new((int) CellWidth.Number, (int) CellHeight.Number),
            CellCount = new((int) CellWidth.Number, (int) CellHeight.Number),
            Offset = new((int) GridOffsetX.Number, (int) GridOffsetY.Number),
            Padding = new((int) PaddingX.Number, (int) PaddingY.Number),
            Trim = TrimToggle.IsChecked,
            KeepEmpty = KeepEmptyToggle.IsChecked,
            NamePrefix = document is { } open ? Path.GetFileNameWithoutExtension(open.AssetPath) : "sprite"
        };

    /// <summary>Raised when a different sprite is selected.</summary>
    public event Action<SpriteSheetView>? SelectionChanged;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        var bar = Part("sprite-toolbar");

        Method = bar.Add<Select>();
        Method.Value = "grid-size";
        Option(Method, "grid-size", "Grid by cell size");
        Option(Method, "grid-count", "Grid by cell count");
        Option(Method, "automatic", "Automatic");
        Method.SelectionChanged += (_, _) => Relabel();

        CellWidth = Number(bar, "Cell", 32, minimum: 1);
        CellHeight = Number(bar, "×", 32, minimum: 1);
        GridOffsetX = Number(bar, "Offset", 0);
        GridOffsetY = Number(bar, "×", 0);
        PaddingX = Number(bar, "Padding", 0);
        PaddingY = Number(bar, "×", 0);

        TrimToggle = bar.Add<ToggleButton>();
        TrimToggle.Label = "Trim";

        KeepEmptyToggle = bar.Add<ToggleButton>();
        KeepEmptyToggle.Label = "Keep empty";

        SliceButton = bar.Add<Button>();
        SliceButton.Label = "Slice";

        ClearButton = bar.Add<Button>();
        ClearButton.Label = "Clear";
        ClearButton.Variant = ControlVariant.Subtle;

        Unavailable = Part<Alert>();
        Unavailable.AddClass("hidden");
        Unavailable.Title = "No pixels";

        var body = Part("sprite-body");

        Canvas = body.Add("sprite-canvas");

        Preview = Canvas.Add<Image>();
        Preview.Description = "The sprite sheet";

        var side = body.Add("sprite-side");
        var listBar = side.Add("sprite-list-bar");

        AddButton = listBar.Add<Button>();
        AddButton.Label = "Add";
        AddButton.Size = ControlSize.Small;

        RemoveButton = listBar.Add<Button>();
        RemoveButton.Label = "Remove";
        RemoveButton.Size = ControlSize.Small;
        RemoveButton.Variant = ControlVariant.Subtle;

        List = side.Add("sprite-list");
        Fields = side.Add("sprite-fields");

        BuildFields();

        AddHandler<ClickEvent>(static (element, args) => ((SpriteSheetView) element).Chosen(args));

        Relabel();
    }

    /// <summary>Shows a texture's sprites.</summary>
    /// <param name="texture">The document.</param>
    /// <exception cref="ArgumentNullException"><paramref name="texture" /> is null.</exception>
    public void Show(TextureImportDocument texture) {
        ArgumentNullException.ThrowIfNull(texture);

        if (document is { } previous) {
            previous.SpritesChanged -= Reload;
        }

        document = texture;
        texture.SpritesChanged += Reload;

        source = TextureLadder.TryDecode(texture.AssetPath, out var reason);

        // ⚠ Grid slicing needs the extent and automatic slicing needs the alpha, so a texture nothing
        // could decode has no sprite editor at all — and says so rather than showing an empty canvas
        // with buttons that quietly do nothing.
        var usable = SpriteSlicer.CanReadAlpha(source);

        if (usable) {
            Unavailable.AddClass("hidden");
        } else {
            Unavailable.RemoveClass("hidden");

            Unavailable.Message = reason
                ?? "The pixels are in a format this build cannot look at, so there is nothing to slice. "
                + "A compressed source has to be decoded first.";
        }

        SliceButton.Disabled = !usable;

        Selected = document.Sprites.Count > 0 ? 0 : -1;

        if (!listening) {
            listening = true;

            SliceButton.Clicked += _ => Slice();
            ClearButton.Clicked += _ => document?.SetSprites([]);
            AddButton.Clicked += _ => AddWhole();
            RemoveButton.Clicked += _ => RemoveSelected();
        }

        Restate();
    }

    /// <summary>Cuts the texture with the options the toolbar is asking for.</summary>
    /// <returns>How many sprites the slice produced.</returns>
    /// <remarks>
    ///     One undo step for the whole slice, which is what <c>SetSprites</c> is for: sixty rects as
    ///     sixty commands would take sixty undos to take back.
    /// </remarks>
    public int Slice() {
        if (document is not { } texture || source is not { } pixels || !SpriteSlicer.CanReadAlpha(pixels)) {
            return 0;
        }

        var sliced = SpriteSlicer.Slice(pixels, Options);

        texture.SetSprites(sliced);

        // ⚠ The mode follows the slice rather than the other way round. A texture somebody has just
        // cut into sixty frames is a sprite sheet whatever its settings said a moment ago, and
        // leaving the mode at None would produce no sub-assets from rects the panel is showing.
        texture.Texture.SpriteMode = sliced.Count > 1 ? SpriteMode.Multiple
            : sliced.Count == 1 ? SpriteMode.Single
            : SpriteMode.None;

        return sliced.Count;
    }

    /// <summary>Selects a sprite, or nothing.</summary>
    /// <param name="index">Which one, or -1 for none.</param>
    public void Select(int index) {
        var clamped = document is { } texture && index >= 0 && index < texture.Sprites.Count ? index : -1;

        if (clamped == Selected) {
            return;
        }

        Selected = clamped;

        Restate();
        SelectionChanged?.Invoke(this);
    }

    /// <summary>Sets the zoom so that the whole texture fits inside a box.</summary>
    /// <param name="width">How wide the box is, in document pixels.</param>
    /// <param name="height">How tall.</param>
    /// <remarks>
    ///     ⚠ <b>Never magnifies.</b> A 32-texel tile fitted to a thousand-pixel panel would be drawn
    ///     at thirty times its size by the act of opening the panel, which is a decision the author
    ///     should make with the zoom rather than one they have to undo.
    /// </remarks>
    public void FitTo(float width, float height) {
        if (source is not { Width: > 0, Height: > 0 } pixels || width <= 0f || height <= 0f) {
            return;
        }

        Zoom = MathF.Min(1f, MathF.Min(width / pixels.Width, height / pixels.Height));
    }

    /// <summary>Rebuilds the overlay, the list and the fields from the document as it stands.</summary>
    public void Restate() {
        Clear(List);
        Clear(Canvas, keep: Preview);

        rects.Clear();
        indices.Clear();

        var width = (source?.Width ?? 0) * Zoom;
        var height = (source?.Height ?? 0) * Zoom;

        Canvas.SetStyle("width", Pixels(width));
        Canvas.SetStyle("height", Pixels(height));
        Preview.SetStyle("width", Pixels(width));
        Preview.SetStyle("height", Pixels(height));

        if (document is not { } texture) {
            Restate(null);
            return;
        }

        for (var index = 0; index < texture.Sprites.Count; index++) {
            var sprite = texture.Sprites[index];
            var box = Canvas.Add("sprite-rect");

            box.SetStyle("left", Pixels(sprite.X * Zoom));
            box.SetStyle("top", Pixels(sprite.Y * Zoom));
            box.SetStyle("width", Pixels(sprite.Width * Zoom));
            box.SetStyle("height", Pixels(sprite.Height * Zoom));

            if (index == Selected) {
                box.AddClass("selected");
                Guides(box, sprite);
            }

            box.Add("sprite-rect-label").Text = sprite.Name;

            rects.Add(box);
            indices[box] = index;

            var row = List.Add("sprite-row");

            if (index == Selected) {
                row.AddClass("selected");
            }

            row.Add("sprite-row-name").Text = sprite.Name;
            row.Add("sprite-row-size").Text = $"{sprite.Width}×{sprite.Height}";

            indices[row] = index;
        }

        RemoveButton.Disabled = Selected < 0;

        Restate(Selected >= 0 ? texture.Sprites[Selected] : null);
    }

    /// <summary>The nine-slice guides inside the selected rect.</summary>
    /// <remarks>
    ///     Four lines rather than a nine-cell grid, because what an author is looking for is where the
    ///     corners stop — and the four lines are exactly the four numbers they are editing below. A
    ///     border of nothing draws nothing, which is what a sprite that is not nine-sliced should show.
    ///     Positioned as a fraction of the rect rather than in pixels, so the guides follow the zoom
    ///     without the overlay having to know it moved.
    /// </remarks>
    static void Guides(UiElement box, SpriteRect sprite) {
        Guide(box, vertical: true, sprite.BorderLeft, sprite.Width, far: false);
        Guide(box, vertical: true, sprite.BorderRight, sprite.Width, far: true);
        Guide(box, vertical: false, sprite.BorderTop, sprite.Height, far: false);
        Guide(box, vertical: false, sprite.BorderBottom, sprite.Height, far: true);

        static void Guide(UiElement box, bool vertical, int border, int extent, bool far) {
            // A border reaching the far edge is not a guide, it is the rect's own outline — and one
            // wider than the sprite is a number somebody is still typing.
            if (border <= 0 || border >= extent) {
                return;
            }

            var fraction = (float) border / extent;
            var guide = box.Add("sprite-guide", null, vertical ? "vertical" : "horizontal");

            guide.SetStyle(vertical ? "left" : "top", Percent(far ? 1f - fraction : fraction));
        }
    }

    void BuildFields() {
        var identity = Row("Name");

        Name = identity.Add<TextBox>();
        Name.Placeholder = "sprite_0";
        Name.Size = ControlSize.Small;
        Name.ValueChanged += (_, _) => Commit();

        var region = Row("Rect");

        RectX = Number(region, "X", 0);
        RectY = Number(region, "Y", 0);
        RectWidth = Number(region, "W", 0, minimum: 0);
        RectHeight = Number(region, "H", 0, minimum: 0);

        var pivot = Row("Pivot");

        PivotX = Number(pivot, "X", 0.5, minimum: 0, maximum: 1, step: 0.05, decimals: 3);
        PivotY = Number(pivot, "Y", 0.5, minimum: 0, maximum: 1, step: 0.05, decimals: 3);

        var border = Row("Border");

        BorderLeft = Number(border, "L", 0, minimum: 0);
        BorderTop = Number(border, "T", 0, minimum: 0);
        BorderRight = Number(border, "R", 0, minimum: 0);
        BorderBottom = Number(border, "B", 0, minimum: 0);

        geometry.AddRange([RectX, RectY, RectWidth, RectHeight, PivotX, PivotY, BorderLeft, BorderTop, BorderRight, BorderBottom]);

        foreach (var field in geometry) {
            field.NumberChanged += (_, _) => Commit();
        }

        UiElement Row(string label) {
            var row = Fields.Add("sprite-field-row");
            row.Add("sprite-field-name").Text = label;

            return row;
        }
    }

    /// <summary>The selected sprite's name.</summary>
    public TextBox Name { get; private set; } = null!;

    /// <summary>Its left edge, in texels.</summary>
    public NumericInput RectX { get; private set; } = null!;

    /// <summary>Its top edge.</summary>
    public NumericInput RectY { get; private set; } = null!;

    /// <summary>Its width.</summary>
    public NumericInput RectWidth { get; private set; } = null!;

    /// <summary>Its height.</summary>
    public NumericInput RectHeight { get; private set; } = null!;

    /// <summary>Its pivot, horizontally.</summary>
    public NumericInput PivotX { get; private set; } = null!;

    /// <summary>Its pivot, vertically.</summary>
    public NumericInput PivotY { get; private set; } = null!;

    /// <summary>Its nine-slice border, from the left.</summary>
    public NumericInput BorderLeft { get; private set; } = null!;

    /// <summary>From the top.</summary>
    public NumericInput BorderTop { get; private set; } = null!;

    /// <summary>From the right.</summary>
    public NumericInput BorderRight { get; private set; } = null!;

    /// <summary>From the bottom.</summary>
    public NumericInput BorderBottom { get; private set; } = null!;

    /// <summary>Writes the fields from a sprite, without letting them write back.</summary>
    /// <remarks>
    ///     ⚠ The guard is not belt and braces. Every field raises its change event when it is
    ///     assigned, so restating ten of them from the model would post ten edits back into the
    ///     document — the last of which would be built from fields that had not been written yet.
    /// </remarks>
    void Restate(SpriteRect? sprite) {
        restating = true;

        try {
            Fields.SetStyle("display", sprite is null ? "none" : null);

            if (sprite is null) {
                return;
            }

            Name.Value = sprite.Name;
            RectX.Number = sprite.X;
            RectY.Number = sprite.Y;
            RectWidth.Number = sprite.Width;
            RectHeight.Number = sprite.Height;
            PivotX.Number = sprite.PivotX;
            PivotY.Number = sprite.PivotY;
            BorderLeft.Number = sprite.BorderLeft;
            BorderTop.Number = sprite.BorderTop;
            BorderRight.Number = sprite.BorderRight;
            BorderBottom.Number = sprite.BorderBottom;
        } finally {
            restating = false;
        }
    }

    /// <summary>Writes the fields back onto the selected sprite.</summary>
    void Commit() {
        if (restating || document is not { } texture || Selected < 0 || Selected >= texture.Sprites.Count) {
            return;
        }

        texture.UpdateSprite(
            Selected,
            new() {
                Name = Name.Value ?? string.Empty,
                X = (int) RectX.Number,
                Y = (int) RectY.Number,
                Width = (int) RectWidth.Number,
                Height = (int) RectHeight.Number,
                PivotX = (float) PivotX.Number,
                PivotY = (float) PivotY.Number,
                BorderLeft = (int) BorderLeft.Number,
                BorderTop = (int) BorderTop.Number,
                BorderRight = (int) BorderRight.Number,
                BorderBottom = (int) BorderBottom.Number
            }
        );
    }

    void AddWhole() {
        if (document is not { } texture || source is not { } pixels) {
            return;
        }

        var index = texture.AddSprite(
            SpriteSlicer.Whole(
                pixels.Width,
                pixels.Height,
                $"{Path.GetFileNameWithoutExtension(texture.AssetPath)}_{texture.Sprites.Count}"
            )
        );

        Select(index);
    }

    void RemoveSelected() {
        if (document is not { } texture || Selected < 0) {
            return;
        }

        var index = Selected;

        // Chosen before the removal, because the removal rebuilds the overlay and would leave the
        // selection pointing past the end of a list one shorter than it was.
        Selected = Math.Min(index, texture.Sprites.Count - 2);

        texture.RemoveSprite(index);
    }

    /// <summary>A click on a rect or a list row selects that sprite.</summary>
    void Chosen(ClickEvent args) {
        for (var element = args.Source; element is not null; element = element.Parent) {
            if (indices.TryGetValue(element, out var index)) {
                Select(index);
                args.Handled = true;

                return;
            }
        }
    }

    /// <summary>Reloads after the document changed, keeping the selection where it still exists.</summary>
    void Reload(TextureImportDocument texture) {
        Selected = Math.Min(Selected, texture.Sprites.Count - 1);
        Restate();
    }

    /// <summary>Renames the grid fields to what the chosen method actually reads.</summary>
    /// <remarks>
    ///     ⚠ Not cosmetic. The same two boxes are a cell size under one method and a column and row
    ///     count under another, and a panel that called them "Cell" while cutting a four-by-four grid
    ///     would be telling the author their sheet has four-texel frames.
    /// </remarks>
    void Relabel() {
        var automatic = MethodOf(Method.Value) == SliceMethod.Automatic;
        var counted = MethodOf(Method.Value) == SliceMethod.GridByCount;

        Label(CellWidth, counted ? "Columns" : "Cell");
        Label(CellHeight, counted ? "Rows" : "×");

        // Automatic reads neither the grid nor the padding — it reads the alpha — so the fields that
        // do nothing are disabled rather than left looking as though they apply.
        foreach (var field in (NumericInput[]) [CellWidth, CellHeight, GridOffsetX, GridOffsetY, PaddingX, PaddingY]) {
            field.Disabled = automatic;
        }

        KeepEmptyToggle.Disabled = automatic;

        // The caption is the sibling immediately before the field, which is how `Number` builds a
        // row. Walked rather than remembered, because remembering it would be a second list of the
        // same six elements for one string assignment.
        static void Label(NumericInput field, string text) {
            var siblings = field.Parent?.Children;

            for (var index = 1; index < (siblings?.Count ?? 0); index++) {
                if (ReferenceEquals(siblings![index], field)) {
                    siblings[index - 1].Text = text;

                    return;
                }
            }
        }
    }

    static NumericInput Number(
        UiElement parent,
        string label,
        double value,
        double minimum = double.NegativeInfinity,
        double maximum = double.PositiveInfinity,
        double step = 1,
        int decimals = 0
    ) {
        parent.Add("sprite-field-label").Text = label;

        var field = parent.Add<NumericInput>();

        field.Minimum = minimum;
        field.Maximum = maximum;
        field.Step = step;
        field.Decimals = decimals;
        field.Number = value;
        field.Size = ControlSize.Small;

        return field;
    }

    static void Option(Select select, string value, string label) {
        var option = select.Add<Option>();

        option.Value = value;
        option.Label = label;
    }

    static SliceMethod MethodOf(string? value) => value switch {
        "grid-count" => SliceMethod.GridByCount,
        "automatic" => SliceMethod.Automatic,
        _ => SliceMethod.GridBySize
    };

    static string Pixels(float value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture) + "px";

    static string Percent(float value) =>
        (value * 100f).ToString("0.####", CultureInfo.InvariantCulture) + "%";

    static void Clear(UiElement element, UiElement? keep = null) {
        for (var index = element.Children.Count - 1; index >= 0; index--) {
            if (!ReferenceEquals(element.Children[index], keep)) {
                element.Children[index].Remove();
            }
        }
    }
}
