// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Input;
using Vixen.Ui.Styling;

namespace Vixen.Ui.Controls.Advanced;

/// <summary>Which of a picker's one-dimensional controls a strip is.</summary>
public enum ColorChannel : byte {
    /// <summary>The hue, all the way round.</summary>
    Hue,

    /// <summary>The alpha, over a chequerboard.</summary>
    Alpha
}

/// <summary>A strip that a drag moves along: the hue band, or the alpha band.</summary>
public sealed partial class ColorStrip : Control {
    bool dragging;

    /// <inheritdoc />
    protected override string TagName => "color-strip";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>Which one it is.</summary>
    public ColorChannel Channel { get; internal set; }

    /// <summary>The picker it belongs to.</summary>
    public ColorPicker? Owner { get; internal set; }

    /// <summary>Where the marker is, from zero at the left to one at the right.</summary>
    public float Fraction { get; internal set; }

    /// <summary>Raised while it is being dragged.</summary>
    public event Action<ColorStrip, float>? Moved;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();
        AddHandler<PointerEvent>(static (element, args) => ((ColorStrip) element).Pointed(args));
    }

    /// <inheritdoc />
    protected override void OnDraw(DrawContext context) {
        base.OnDraw(context);

        var bounds = context.Bounds;

        if (bounds.Width <= 0f || bounds.Height <= 0f || Owner is not { } owner) {
            return;
        }

        if (Channel == ColorChannel.Hue) {
            // ⚠ Six bands, because the shader's gradient has two stops and a hue wheel has seven.
            // Sampling it any finer buys nothing: within a band the interpolation is exactly right,
            // since a sixth of the hue circle is a straight line in RGB.
            for (var i = 0; i < 6; i++) {
                var slice = bounds.Width / 6f;

                context.FillRectangle(
                    new Rectangle(bounds.X + (i * slice), bounds.Y, slice + 1f, bounds.Height),
                    new Hsv(i * 60f, 1f, 1f).ToRgb(),
                    new BoxStyle(default, new Hsv((i + 1) * 60f, 1f, 1f).ToRgb(), new Vector2(1f, 0f))
                );
            }
        } else {
            Chequer(context, bounds);

            var solid = owner.Value;

            context.FillRectangle(
                bounds,
                new Color4(solid.R, solid.G, solid.B, 0f),
                new BoxStyle(default, new Color4(solid.R, solid.G, solid.B, 1f), new Vector2(1f, 0f))
            );
        }

        var x = bounds.X + (Math.Clamp(Fraction, 0f, 1f) * bounds.Width);

        context.FillRectangle(new Rectangle(x - 1.5f, bounds.Y - 1f, 3f, bounds.Height + 2f), Color4.White, 1.5f);
        context.StrokeRectangle(new Rectangle(x - 2.5f, bounds.Y - 2f, 5f, bounds.Height + 4f), Color4.Black, 1f);
    }

    /// <summary>The grey chequerboard that says "this part is see-through".</summary>
    /// <remarks>
    ///     Drawn rather than an image, because there is no texture command — and because a
    ///     chequerboard whose squares are a fixed number of pixels is the one thing that reads as
    ///     transparency at any size, which a scaled bitmap would not.
    /// </remarks>
    internal static void Chequer(DrawContext context, Rectangle bounds) {
        const float Square = 5f;

        context.FillRectangle(bounds, Color4.White);

        var dark = new Color4(0.8f, 0.8f, 0.8f, 1f);

        for (var y = 0f; y < bounds.Height; y += Square) {
            for (var x = (y / Square % 2f) * Square; x < bounds.Width; x += Square * 2f) {
                context.FillRectangle(
                    new Rectangle(
                        bounds.X + x,
                        bounds.Y + y,
                        MathF.Min(Square, bounds.Width - x),
                        MathF.Min(Square, bounds.Height - y)
                    ),
                    dark
                );
            }
        }
    }

    void Pointed(PointerEvent args) {
        switch (args.Action) {
            case PointerAction.Pressed when args.Button == PointerButton.Primary:
                dragging = true;
                Document.CapturePointer(this);

                break;

            case PointerAction.Moved when dragging:
                break;

            case PointerAction.Released when dragging:
                dragging = false;
                Document.ReleasePointer();

                break;

            default:
                return;
        }

        var bounds = Bounds;

        if (bounds.Width > 0f) {
            Moved?.Invoke(this, Math.Clamp((args.X - bounds.X) / bounds.Width, 0f, 1f));
        }

        args.Handled = true;
    }
}

/// <summary>The two-dimensional field: saturation against value, or chroma against lightness.</summary>
public sealed partial class ColorField : Control {
    bool dragging;

    /// <inheritdoc />
    protected override string TagName => "color-field";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The picker it belongs to.</summary>
    public ColorPicker? Owner { get; internal set; }

    /// <summary>Where the marker is, from (0,0) at the top left to (1,1) at the bottom right.</summary>
    public Vector2 Marker { get; internal set; }

    /// <summary>Raised while it is being dragged.</summary>
    public event Action<ColorField, Vector2>? Moved;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();
        AddHandler<PointerEvent>(static (element, args) => ((ColorField) element).Pointed(args));
    }

    /// <summary>How many columns the perceptual field is sampled into.</summary>
    /// <remarks>
    ///     ⚠ <b>Sampled, where the HSV field is exact.</b> Saturation-against-value is white to the
    ///     hue across and transparent to black down — two gradients, and the shader has gradients.
    ///     Chroma against lightness in Oklab is neither, so it is drawn as columns of vertical
    ///     gradients. Sixteen is where the banding stops being visible at the size a picker is.
    /// </remarks>
    public const int Samples = 16;

    /// <inheritdoc />
    protected override void OnDraw(DrawContext context) {
        base.OnDraw(context);

        var bounds = context.Bounds;

        if (bounds.Width <= 0f || bounds.Height <= 0f || Owner is not { } owner) {
            return;
        }

        if (owner.Model == ColorModel.Hsv) {
            var hue = new Hsv(owner.Hue, 1f, 1f).ToRgb();

            context.FillRectangle(bounds, Color4.White, new BoxStyle(default, hue, new Vector2(1f, 0f)));
            context.FillRectangle(
                bounds,
                new Color4(0f, 0f, 0f, 0f),
                new BoxStyle(default, Color4.Black, new Vector2(0f, 1f))
            );
        } else {
            var slice = bounds.Width / Samples;

            for (var i = 0; i < Samples; i++) {
                var chroma = (i + 0.5f) / Samples * ColorPicker.MaximumChroma;

                for (var j = 0; j < Samples; j++) {
                    var top = 1f - (j / (float) Samples);
                    var bottom = 1f - ((j + 1f) / Samples);

                    context.FillRectangle(
                        new Rectangle(bounds.X + (i * slice), bounds.Y + (j * bounds.Height / Samples), slice + 1f, (bounds.Height / Samples) + 1f),
                        new OkLch(top, chroma, owner.Hue).ToSrgb(),
                        new BoxStyle(default, new OkLch(bottom, chroma, owner.Hue).ToSrgb(), new Vector2(0f, 1f))
                    );
                }
            }
        }

        var centre = new Vector2(
            bounds.X + (Math.Clamp(Marker.X, 0f, 1f) * bounds.Width),
            bounds.Y + (Math.Clamp(Marker.Y, 0f, 1f) * bounds.Height)
        );

        context.StrokeRectangle(new Rectangle(centre.X - 6f, centre.Y - 6f, 12f, 12f), Color4.White, 2f, BoxStyle.Rounded(CornerRadii.Uniform(6f)));
        context.StrokeRectangle(new Rectangle(centre.X - 7f, centre.Y - 7f, 14f, 14f), Color4.Black, 1f, BoxStyle.Rounded(CornerRadii.Uniform(7f)));
    }

    void Pointed(PointerEvent args) {
        switch (args.Action) {
            case PointerAction.Pressed when args.Button == PointerButton.Primary:
                dragging = true;
                Document.CapturePointer(this);

                break;

            case PointerAction.Moved when dragging:
                break;

            case PointerAction.Released when dragging:
                dragging = false;
                Document.ReleasePointer();

                break;

            default:
                return;
        }

        var bounds = Bounds;

        if (bounds.Width > 0f && bounds.Height > 0f) {
            Moved?.Invoke(
                this,
                new Vector2(
                    Math.Clamp((args.X - bounds.X) / bounds.Width, 0f, 1f),
                    Math.Clamp((args.Y - bounds.Y) / bounds.Height, 0f, 1f)
                )
            );
        }

        args.Handled = true;
    }
}

/// <summary>One saved colour.</summary>
public sealed partial class ColorSwatch : Control {
    /// <inheritdoc />
    protected override string TagName => "color-swatch";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>What it shows.</summary>
    public Color4 Color { get; internal set; }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ A swatch raises its own click. <see cref="Control.Clicked" /> is raised by
    ///     <c>ButtonBase</c> and a swatch is not one — it draws itself and has no label — so without
    ///     this the event exists on the type and never fires, which is worse than not having it.
    /// </remarks>
    protected override void OnCreated() {
        base.OnCreated();

        AddHandler<PointerEvent>(
            static (element, args) => {
                if (args is not { Action: PointerAction.Released, Button: PointerButton.Primary }) {
                    return;
                }

                ((ColorSwatch) element).RaiseClick(ActivationDevice.Pointer);
                args.Handled = true;
            }
        );
    }

    /// <inheritdoc />
    protected override void OnDraw(DrawContext context) {
        base.OnDraw(context);

        var bounds = context.Bounds;

        if (bounds.Width <= 0f || bounds.Height <= 0f) {
            return;
        }

        if (Color.A < 1f) {
            ColorStrip.Chequer(context, bounds);
        }

        context.FillRectangle(bounds, Color, 3f);
    }
}

/// <summary>Choosing a colour: a field, two strips, a number, a palette and a dropper.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The model is the source of truth, not the RGB.</b> Grey has no hue and black has no
///         saturation, so a picker that recomputed its axes from the colour would lose which hue the
///         user was on the moment they dragged the saturation to nothing — and the field would snap
///         back to red when they dragged it out again. Every picker that has had that bug has had it
///         for that reason. What is stored is <see cref="Hue" />, the field's position and the
///         alpha; <see cref="Value" /> is derived, and assigning to it is the only thing that
///         reconstructs them.
///     </para>
///     <para>
///         <b>HDR is a multiplier beside a colour, not a colour with big numbers in it.</b> An
///         artist picks a hue and then says how bright the light is, and those are two decisions:
///         keeping them apart means changing the intensity does not move the picker, and the
///         chromaticity survives a round trip through a value of forty. <see cref="Intensity" /> is
///         that number and <see cref="HdrValue" /> is the product.
///     </para>
///     <para>
///         ⚠ <b>The eyedropper cannot read the screen and does not pretend to.</b> Sampling a pixel
///         is a platform capability — a screen capture permission on macOS, a compositor protocol on
///         Wayland — and this assembly has no platform. <see cref="EyedropperRequested" /> is what an
///         app head answers, and <see cref="Pick" /> is how it answers.
///     </para>
/// </remarks>
public sealed partial class ColorPicker : Control {
    readonly List<Color4> palette = [];
    readonly List<ColorSwatch> swatches = [];

    Color4 value = new(1f, 1f, 1f, 1f);
    float hue;
    Vector2 marker;
    bool updating;

    /// <summary>The chroma the perceptual field runs out to.</summary>
    /// <remarks>
    ///     Beyond about 0.37 nothing is inside sRGB at any lightness, so a field that ran further
    ///     would be mostly a picture of the clamp.
    /// </remarks>
    public const float MaximumChroma = 0.37f;

    /// <inheritdoc />
    protected override string TagName => "color-picker";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The two-dimensional field.</summary>
    public ColorField Field { get; private set; } = null!;

    /// <summary>The hue band.</summary>
    public ColorStrip HueStrip { get; private set; } = null!;

    /// <summary>The alpha band.</summary>
    public ColorStrip AlphaStrip { get; private set; } = null!;

    /// <summary>The current colour, shown large.</summary>
    public ColorSwatch Preview { get; private set; } = null!;

    /// <summary>The hexadecimal field.</summary>
    public TextBox HexField { get; private set; } = null!;

    /// <summary>The dropper.</summary>
    public IconButton Eyedropper { get; private set; } = null!;

    /// <summary>The brightness multiplier, shown only when <see cref="AllowHdr" /> is set.</summary>
    public Slider IntensitySlider { get; private set; } = null!;

    /// <summary>Where the saved colours go.</summary>
    public UiElement Palette { get; private set; } = null!;

    /// <summary>The saved colours.</summary>
    public IReadOnlyList<Color4> Swatches => palette;

    /// <summary>The chosen colour, with its alpha and without the intensity.</summary>
    public Color4 Value {
        get => value;
        set {
            if (this.value == value) {
                return;
            }

            this.value = value;
            Adopt(value);

            Sync();
            ValueChanged?.Invoke(this, value);
        }
    }

    /// <summary>The colour times the intensity, which is what a light or an emissive material wants.</summary>
    public Color4 HdrValue => new(value.R * Intensity, value.G * Intensity, value.B * Intensity, value.A);

    /// <summary>Which set of axes the field and the strip mean.</summary>
    [UiProperty(Changed = nameof(OnModelChanged))]
    public partial ColorModel Model { get; set; }

    /// <summary>Whether the alpha band is shown.</summary>
    [UiProperty(Default = true, Changed = nameof(OnAlphaAllowedChanged))]
    public partial bool AllowAlpha { get; set; }

    /// <summary>Whether the intensity slider is shown.</summary>
    [UiProperty(Changed = nameof(OnHdrAllowedChanged))]
    public partial bool AllowHdr { get; set; }

    /// <summary>How much brighter than the picked colour the result is.</summary>
    [UiProperty(Default = 1f, Coerce = nameof(ClampIntensity), Changed = nameof(OnIntensityChanged))]
    public partial float Intensity { get; set; }

    /// <summary>The hue, in degrees, whichever model is in use.</summary>
    /// <remarks>
    ///     ⚠ <b>Shared between the two models rather than converted between them.</b> HSV's hue and
    ///     OkLCh's are different angles for the same colour, so a picker that converted would move
    ///     the hue slider under the user when they changed model — and switching back and forth
    ///     would walk the colour away from where it started.
    /// </remarks>
    public float Hue => hue;

    /// <summary>Whether the dropper is waiting for a colour.</summary>
    public bool IsPicking { get; private set; }

    /// <summary>Raised whenever the colour changes, however it changed.</summary>
    public event Action<ColorPicker, Color4>? ValueChanged;

    /// <summary>Raised when the dropper is pressed. Answer it with <see cref="Pick" />.</summary>
    public event Action<ColorPicker>? EyedropperRequested;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Field = Part<ColorField>();
        Field.Owner = this;
        Field.Moved += (_, point) => FieldMoved(point);

        HueStrip = Part<ColorStrip>(null, "hue");
        HueStrip.Owner = this;
        HueStrip.Channel = ColorChannel.Hue;
        HueStrip.Moved += (_, fraction) => HueMoved(fraction);

        AlphaStrip = Part<ColorStrip>(null, "alpha");
        AlphaStrip.Owner = this;
        AlphaStrip.Channel = ColorChannel.Alpha;
        AlphaStrip.Moved += (_, fraction) => AlphaMoved(fraction);

        var row = Part("color-row");

        Preview = row.Add<ColorSwatch>(null, null, "preview");

        HexField = row.Add<TextBox>();
        HexField.AddClass("color-hex");
        HexField.Submitted += _ => HexEntered();

        Eyedropper = row.Add<IconButton>();
        Eyedropper.LeadingIcon.Geometry = ControlIcons.Search;
        Eyedropper.Label = "Pick a colour from the screen";
        Eyedropper.Variant = ControlVariant.Subtle;
        Eyedropper.Clicked += _ => RequestEyedropper();

        var hdr = Part("color-row", "hdr");
        hdr.AddClass("hidden");

        var caption = hdr.Add("text");
        caption.Text = "Intensity";

        IntensitySlider = hdr.Add<Slider>();
        IntensitySlider.Minimum = 0f;
        IntensitySlider.Maximum = 16f;
        IntensitySlider.Value = 1f;
        IntensitySlider.ValueChanged += (_, level) => Intensity = level;

        Palette = Part("color-palette");

        Adopt(value);
        Sync();
    }

    /// <summary>Replaces the saved colours.</summary>
    /// <param name="colors">The colours.</param>
    public void SetPalette(params ReadOnlySpan<Color4> colors) {
        palette.Clear();

        foreach (var colour in colors) {
            palette.Add(colour);
        }

        RealisePalette();
    }

    /// <summary>Saves the current colour.</summary>
    /// <remarks>
    ///     ⚠ <b>A colour already in the palette is not added twice.</b> A palette is what an artist
    ///     built for a scene, and one that filled up with copies of the last colour picked would stop
    ///     being that within an afternoon.
    /// </remarks>
    /// <returns>Whether it was new.</returns>
    public bool AddToPalette() {
        if (palette.Contains(value)) {
            return false;
        }

        palette.Add(value);
        RealisePalette();

        return true;
    }

    /// <summary>Asks the application for a colour from the screen.</summary>
    public void RequestEyedropper() {
        IsPicking = true;
        Eyedropper.State |= ElementState.Checked;

        EyedropperRequested?.Invoke(this);
    }

    /// <summary>Answers an eyedropper request, or just sets the colour.</summary>
    /// <param name="colour">What was under the cursor.</param>
    public void Pick(Color4 colour) {
        IsPicking = false;
        Eyedropper.State &= ~ElementState.Checked;

        Value = colour;
    }

    /// <summary>The colour as <c>#rrggbb</c>, or <c>#rrggbbaa</c> when it is not opaque.</summary>
    public string HexText => Hex.ToString(value);

    // ── Model plumbing ───────────────────────────────────────────────────────

    /// <summary>Rebuilds the axes from a colour that arrived from outside.</summary>
    void Adopt(Color4 colour) {
        if (Model == ColorModel.Hsv) {
            var hsv = Hsv.FromRgb(colour);

            // ⚠ The hue is kept when the colour has none of its own. Setting the value to black must
            // not send the field back to red, because the very next thing the user does is drag the
            // value back up and expect the hue they were on.
            if (hsv.S > 0.0001f && hsv.V > 0.0001f) {
                hue = hsv.H;
            }

            marker = new Vector2(hsv.S, 1f - hsv.V);
            return;
        }

        var lch = OkLch.FromSrgb(colour);

        if (lch.C > 0.0001f) {
            hue = lch.H;
        }

        marker = new Vector2(Math.Clamp(lch.C / MaximumChroma, 0f, 1f), Math.Clamp(1f - lch.L, 0f, 1f));
    }

    /// <summary>The colour the axes currently describe.</summary>
    Color4 Compose() =>
        Model == ColorModel.Hsv
            ? new Hsv(hue, marker.X, 1f - marker.Y).ToRgb(value.A)
            : new OkLch(1f - marker.Y, marker.X * MaximumChroma, hue).ToSrgb(value.A);

    void Commit() {
        var composed = Compose();

        if (composed == value) {
            Sync();
            return;
        }

        value = composed;

        Sync();
        ValueChanged?.Invoke(this, value);
    }

    /// <summary>Brings every part into agreement with the axes.</summary>
    void Sync() {
        if (updating) {
            return;
        }

        updating = true;

        try {
            Field.Marker = marker;
            HueStrip.Fraction = hue / 360f;
            AlphaStrip.Fraction = value.A;

            Preview.Color = value;
            HexField.Value = Hex.ToString(value);

            Document.Invalidate();
        } finally {
            updating = false;
        }
    }

    void FieldMoved(Vector2 point) {
        marker = point;
        Commit();
    }

    void HueMoved(float fraction) {
        hue = fraction * 360f;
        Commit();
    }

    void AlphaMoved(float fraction) {
        value = new Color4(value.R, value.G, value.B, fraction);

        Sync();
        ValueChanged?.Invoke(this, value);
    }

    void HexEntered() {
        if (updating) {
            return;
        }

        if (Hex.TryParse(HexField.Value, out var parsed)) {
            Value = parsed;
            return;
        }

        // ⚠ Put back rather than left as typed. A field that kept `#12zz34` would look like it had
        // been accepted, and the next thing to read the colour would disagree with what is on screen.
        Sync();
    }

    void RealisePalette() {
        while (swatches.Count < palette.Count) {
            var swatch = Palette.Add<ColorSwatch>();
            swatch.Clicked += entry => Value = ((ColorSwatch) entry).Color;

            swatches.Add(swatch);
        }

        for (var i = 0; i < swatches.Count; i++) {
            if (i >= palette.Count) {
                swatches[i].AddClass("parked");
                continue;
            }

            swatches[i].RemoveClass("parked");
            swatches[i].Color = palette[i];
        }

        Document.Invalidate();
    }

    static float ClampIntensity(float level) => MathF.Max(0f, level);

    void OnIntensityChanged(float previous, float current) {
        IntensitySlider.Value = current;
        ValueChanged?.Invoke(this, value);
    }

    void OnModelChanged(ColorModel previous, ColorModel current) {
        // The colour is what survives the switch, not the axes: re-derive them from it, keeping the
        // hue if the new model cannot see one.
        Adopt(value);
        Sync();
    }

    void OnAlphaAllowedChanged(bool previous, bool current) {
        if (current) {
            AlphaStrip.RemoveClass("hidden");
        } else {
            AlphaStrip.AddClass("hidden");
        }
    }

    void OnHdrAllowedChanged(bool previous, bool current) {
        if (IntensitySlider.Parent is not { } row) {
            return;
        }

        if (current) {
            row.RemoveClass("hidden");
        } else {
            row.AddClass("hidden");
            Intensity = 1f;
        }
    }
}
