// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
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
/// <remarks>
///     ⚠ <b>The keyboard came before the role, and that order is the whole of #420.</b> This was
///     pointer-only and roleless, and the two facts belonged together: a screen reader told about a
///     hue band that only a mouse can move announces a control the user cannot operate, which is
///     strictly worse than announcing nothing — it converts "not available to me" into "available
///     and does nothing", a state a screen-reader user has no way to diagnose. So the arrows landed
///     first and <see cref="NativeRole" /> second, in one change, and neither is correct alone.
/// </remarks>
public sealed partial class ColorStrip : Control {
    bool dragging;

    /// <summary>How far one arrow key moves the marker along the band.</summary>
    /// <remarks>
    ///     A hundredth of the band, which is <c>Slider</c>'s own fallback for a step nobody
    ///     declared, and on the hue band works out at 3.6°. Page is ten of them.
    /// </remarks>
    public const float KeyStep = 0.01f;

    /// <inheritdoc />
    protected override string TagName => "color-strip";

    /// <inheritdoc />
    protected override bool AcceptsFocus => true;

    /// <inheritdoc />
    /// <remarks>
    ///     ARIA <c>slider</c>, and it is the honest one: a band is a single number between two
    ///     bounds, moved by the same arrows, Page and Home/End that <c>Slider</c> answers to.
    /// </remarks>
    protected override AccessibleRole NativeRole => AccessibleRole.Slider;

    /// <inheritdoc />
    /// <remarks>
    ///     From the catalogue, on <c>ButtonBase.NativeAccessibleName</c>'s terms — a band has no
    ///     caption anywhere near it, so this is the only word it ever says.
    /// </remarks>
    protected override string? NativeAccessibleName =>
        Channel == ColorChannel.Hue ? ControlStrings.ColorPickerHue.Text : ControlStrings.ColorPickerAlpha.Text;

    /// <inheritdoc />
    /// <remarks>
    ///     Invariant and unitless, for <c>Slider</c>'s reason: what the fraction means in the user's
    ///     locale — degrees, or a percentage — is a decision the application makes, and a bare float
    ///     formatted in the current culture is a string a bridge has to parse back.
    /// </remarks>
    protected override string? NativeAccessibleValue => Fraction.ToString("0.###", CultureInfo.InvariantCulture);

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
        AddHandler<KeyEvent>(static (element, args) => ((ColorStrip) element).Keyed(args));
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

                Document.Focus(this);
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

    /// <remarks>
    ///     ⚠ <b>Raises <see cref="Moved" /> rather than assigning <see cref="Fraction" />.</b> The
    ///     fraction is written by the owner's sync pass and is a report of where the marker is, not
    ///     the place the value lives — a strip that moved its own marker would show a hue the
    ///     picker's colour does not have until the next sync overwrote it. This is the same door the
    ///     drag goes through, which is what makes the two paths impossible to drift apart.
    /// </remarks>
    void Keyed(KeyEvent args) {
        if (args.Action != KeyAction.Pressed) {
            return;
        }

        var moved = args.Key switch {
            InputKey.Left or InputKey.Down => Fraction - KeyStep,
            InputKey.Right or InputKey.Up => Fraction + KeyStep,
            InputKey.PageDown => Fraction - (KeyStep * 10f),
            InputKey.PageUp => Fraction + (KeyStep * 10f),
            InputKey.Home => 0f,
            InputKey.End => 1f,
            _ => float.NaN
        };

        if (float.IsNaN(moved)) {
            return;
        }

        Moved?.Invoke(this, Math.Clamp(moved, 0f, 1f));
        args.Handled = true;
    }
}

/// <summary>The two-dimensional field: saturation against value, or chroma against lightness.</summary>
public sealed partial class ColorField : Control {
    bool dragging;

    /// <summary>The perceptual plane's colours, or <c>null</c> until one has been drawn.</summary>
    Color4[]? plane;

    /// <summary>The hue <see cref="plane" /> was built for. <c>NaN</c> so the first draw builds it.</summary>
    float planeHue = float.NaN;

    /// <summary>
    ///     How many <c>OkLch.ToSrgb</c> calls the perceptual plane has made since this field was
    ///     created.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A work counter, not a timer</b> — the same instrument
    ///     <c>UiGeometryBuilder.ColourSearches</c> is, and for the same reason: most of these
    ///     colours are outside sRGB on purpose, so most of them are a <c>GamutMap.Map</c> binary
    ///     search rather than arithmetic, and a count says whether one ran where a millisecond on
    ///     an idle machine says nothing.
    /// </remarks>
    internal int PlaneConversions { get; private set; }

    /// <summary>How many times the perceptual plane has been built.</summary>
    /// <remarks>
    ///     Moves only when <see cref="ColorPicker.Hue" /> does. Growing once per draw means the
    ///     cache key is wrong, which is the failure this counter exists to make visible.
    /// </remarks>
    internal int PlaneRebuilds { get; private set; }

    /// <inheritdoc />
    protected override string TagName => "color-field";

    /// <inheritdoc />
    /// <remarks>Keyboard first, role second — see <see cref="ColorStrip" />.</remarks>
    protected override bool AcceptsFocus => true;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>ARIA <c>application</c> and deliberately not <c>slider</c>, which is where this
    ///     parts company with the bands beside it.</b> <c>aria-valuenow</c> is one number and this
    ///     field is two, so a <c>slider</c> role would announce a control that never changes when
    ///     the other axis moves — the same lie <c>RangeSlider</c> refused for its second thumb.
    ///     There is no two-dimensional widget role to reach for instead, so this says what is
    ///     actually true: a surface with a keyboard model of its own that assistive technology
    ///     should pass keys straight through to, which is what <c>GradientEditor</c> and the other
    ///     canvases say for the same reason.
    /// </remarks>
    protected override AccessibleRole NativeRole => AccessibleRole.Application;

    /// <inheritdoc />
    protected override string? NativeAccessibleName => ControlStrings.ColorPickerField.Text;

    /// <inheritdoc />
    /// <remarks>Both axes, because either alone is a different control's value.</remarks>
    protected override string? NativeAccessibleValue =>
        string.Create(CultureInfo.InvariantCulture, $"{Marker.X:0.###}, {Marker.Y:0.###}");

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
        AddHandler<KeyEvent>(static (element, args) => ((ColorField) element).Keyed(args));
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
            var colours = Plane(owner.Hue);
            var slice = bounds.Width / Samples;

            for (var i = 0; i < Samples; i++) {
                var column = i * (Samples + 1);

                for (var j = 0; j < Samples; j++) {
                    context.FillRectangle(
                        new Rectangle(bounds.X + (i * slice), bounds.Y + (j * bounds.Height / Samples), slice + 1f, (bounds.Height / Samples) + 1f),
                        colours[column + j],
                        new BoxStyle(default, colours[column + j + 1], new Vector2(0f, 1f))
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

    /// <summary>One cached plane colour: the column's chroma at the row's lightness.</summary>
    /// <param name="column">The column, <c>0</c> to <see cref="Samples" /> exclusive.</param>
    /// <param name="level">The lightness step, <c>0</c> to <see cref="Samples" /> inclusive — a
    ///     cell's top stop is <paramref name="level" /> and its bottom stop is the next one.</param>
    /// <returns>The colour the plane drew there.</returns>
    internal Color4 PlaneColour(int column, int level) =>
        plane is null ? default : plane[(column * (Samples + 1)) + level];

    /// <summary>The plane's colours for a hue, building them if that hue is not the cached one.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A row's bottom stop is the next row's top stop</b>, so the grid is
    ///         <see cref="Samples" /> columns of <c>Samples + 1</c> lightnesses — 272 colours, not
    ///         the 512 conversions a stop-per-cell loop made. Half of the saving is that alone, and
    ///         it does not depend on anything being cached.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The rest of it is that nothing here depends on the frame.</b> Chroma is the
    ///         column, lightness is the row and the only other input is the hue, so a plane drawn
    ///         again with the hue unmoved is the same 272 colours — and it was recomputing them,
    ///         every draw, most of them through a <c>GamutMap.Map</c> binary search rather than a
    ///         clamp: at hue 0, 169 of the 272 are outside sRGB by construction, because the plane
    ///         spans chroma to <see cref="ColorPicker.MaximumChroma" /> and much of that is not a
    ///         colour a monitor can make.
    ///     </para>
    /// </remarks>
    Color4[] Plane(float hue) {
        if (plane is { } cached && planeHue == hue) {
            return cached;
        }

        plane ??= new Color4[Samples * (Samples + 1)];

        for (var i = 0; i < Samples; i++) {
            var chroma = (i + 0.5f) / Samples * ColorPicker.MaximumChroma;
            var column = i * (Samples + 1);

            for (var k = 0; k <= Samples; k++) {
                plane[column + k] = new OkLch(1f - (k / (float) Samples), chroma, hue).ToSrgb();
            }
        }

        PlaneConversions += Samples * (Samples + 1);
        PlaneRebuilds++;
        planeHue = hue;

        return plane;
    }

    void Pointed(PointerEvent args) {
        switch (args.Action) {
            case PointerAction.Pressed when args.Button == PointerButton.Primary:
                dragging = true;

                Document.Focus(this);
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

    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Up decreases <see cref="Marker" />'s Y, because the marker is in the field's own
    ///         coordinates and those run down.</b> Zero is the top row — full value in HSV, full
    ///         lightness in OkLCh — so an Up arrow that added would darken the colour, which is the
    ///         one mistake in this method that no test of the arithmetic would notice.
    ///     </para>
    ///     <para>
    ///         <b>Home and End move the horizontal axis only</b>, and there is nothing for them to
    ///         do on the vertical one: "the start" of a square is not a place. Page is the vertical
    ///         axis by ten steps, which is the axis a picker is dragged along furthest.
    ///     </para>
    /// </remarks>
    void Keyed(KeyEvent args) {
        if (args.Action != KeyAction.Pressed) {
            return;
        }

        var moved = args.Key switch {
            InputKey.Left => new Vector2(Marker.X - ColorStrip.KeyStep, Marker.Y),
            InputKey.Right => new Vector2(Marker.X + ColorStrip.KeyStep, Marker.Y),
            InputKey.Up => new Vector2(Marker.X, Marker.Y - ColorStrip.KeyStep),
            InputKey.Down => new Vector2(Marker.X, Marker.Y + ColorStrip.KeyStep),
            InputKey.PageUp => new Vector2(Marker.X, Marker.Y - (ColorStrip.KeyStep * 10f)),
            InputKey.PageDown => new Vector2(Marker.X, Marker.Y + (ColorStrip.KeyStep * 10f)),
            InputKey.Home => new Vector2(0f, Marker.Y),
            InputKey.End => new Vector2(1f, Marker.Y),
            _ => new Vector2(float.NaN, float.NaN)
        };

        if (float.IsNaN(moved.X)) {
            return;
        }

        Moved?.Invoke(this, new Vector2(Math.Clamp(moved.X, 0f, 1f), Math.Clamp(moved.Y, 0f, 1f)));
        args.Handled = true;
    }
}

/// <summary>One saved colour.</summary>
/// <remarks>
///     ⚠ <b>A roving tab stop rather than one stop per chip.</b> A palette is a set the user picks
///     one of, and giving each chip a tab stop would put a sixteen-press detour in the middle of a
///     dialog — the same reason a radio group and a toolbar are one stop each. So exactly one live
///     swatch in a container has <see cref="UiElement.TabIndex" /> zero and the rest have
///     <c>-1</c>, which <c>Focus.TabOrder</c> already reads as "focusable but not a stop", and the
///     arrows move both the focus and the stop.
/// </remarks>
public sealed partial class ColorSwatch : Control {
    bool selectable = true;

    /// <inheritdoc />
    protected override string TagName => "color-swatch";

    /// <inheritdoc />
    protected override bool AcceptsFocus => selectable;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Two roles, because a swatch is used for two things and only one of them is a
    ///     control.</b> A chip in a palette is <c>option</c> — an item of a set, chosen with Enter
    ///     or Space. The preview above the hex field is the same class drawing the same rectangle
    ///     and is not operable at all, so it is <c>img</c>: announcing it as an option would offer
    ///     a screen-reader user a choice that does nothing.
    /// </remarks>
    protected override AccessibleRole NativeRole => selectable ? AccessibleRole.Option : AccessibleRole.Img;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The one name in this control set that is deliberately not in the catalogue.</b>
    ///     <c>#3b7dd8</c> is the same six characters in every language, it is what the hex field
    ///     beside it shows, and a translator handed it would have nothing to do. What a *named*
    ///     palette needs — "Skin, midtone" — is an application's sentence, and the application sets
    ///     <see cref="UiElement.AccessibleName" /> to say it.
    /// </remarks>
    protected override string? NativeAccessibleName => Hex.ToString(Color);

    /// <inheritdoc />
    /// <remarks>
    ///     Read from <see cref="ElementState.Checked" /> rather than written, on <c>TreeRow</c>'s
    ///     terms: the picker already marks the chip whose colour is the current one, for the theme.
    /// </remarks>
    protected override AccessibleStates NativeAccessibleState =>
        (State & ElementState.Checked) != 0 ? AccessibleStates.Selected : AccessibleStates.None;

    /// <inheritdoc />
    /// <remarks>
    ///     The one control outside <c>ButtonBase</c> that raises its own <see cref="ClickEvent" />,
    ///     which is exactly what this has to say — or a markup <c>on:click</c> on a swatch would
    ///     count the activation and the tap that produced it as two presses.
    /// </remarks>
    protected override bool RaisesActivation => true;

    /// <summary>Whether it is a chip that can be chosen, or a picture of a colour.</summary>
    /// <remarks>
    ///     ⚠ <b>Clearing it is what parks a swatch, and parking is why it exists.</b> The picker
    ///     pools its chips and hides the spare ones with a class — and a hidden element is still in
    ///     <c>Focus.TabOrder</c>, so a pool of sixteen behind a palette of three would leave
    ///     thirteen invisible tab stops behind it. It is also how the preview swatch stops being a
    ///     control; see <see cref="NativeRole" />.
    /// </remarks>
    public bool Selectable {
        get => selectable;
        set {
            if (selectable == value) {
                return;
            }

            selectable = value;
            Focusable = value && !Disabled;
        }
    }

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
                var swatch = (ColorSwatch) element;

                // ⚠ The press takes the roving stop as well as the focus. Without it a palette
                // clicked on and then tabbed away from and back would send the keyboard to whichever
                // chip the layout happened to make the stop, not the one the user had just chosen.
                if (args is { Action: PointerAction.Pressed, Button: PointerButton.Primary } && swatch.Selectable) {
                    swatch.TakeStop();
                    swatch.Document.Focus(swatch);

                    args.Handled = true;
                    return;
                }

                if (args is not { Action: PointerAction.Released, Button: PointerButton.Primary }) {
                    return;
                }

                swatch.RaiseClick(ActivationDevice.Pointer);
                args.Handled = true;
            }
        );

        AddHandler<KeyEvent>(static (element, args) => ((ColorSwatch) element).Keyed(args));
    }

    /// <summary>Makes this the one chip in its container the Tab key stops on.</summary>
    void TakeStop() {
        if (Parent is not { } container) {
            TabIndex = 0;
            return;
        }

        foreach (var sibling in container.Children) {
            if (sibling is ColorSwatch chip) {
                chip.TabIndex = ReferenceEquals(chip, this) ? 0 : -1;
            }
        }
    }

    /// <remarks>
    ///     ⚠ <b>Wraps rather than stopping at the ends</b>, which is what a roving stop over a set
    ///     is for: Tab leaves the palette, so the arrows have no other job and a user who reaches
    ///     the last chip is looking for the first one. <b>Parked chips are skipped</b> because
    ///     <see cref="Selectable" /> is what parking clears, so the walk sees only what is drawn.
    /// </remarks>
    void Keyed(KeyEvent args) {
        if (args.Action != KeyAction.Pressed || !selectable) {
            return;
        }

        switch (args.Key) {
            case InputKey.Space or InputKey.Enter or InputKey.KeypadEnter:
                RaiseClick(ActivationDevice.Keyboard);
                args.Handled = true;

                return;

            case InputKey.Left or InputKey.Up:
                args.Handled = Rove(-1);
                return;

            case InputKey.Right or InputKey.Down:
                args.Handled = Rove(1);
                return;

            default:
                return;
        }
    }

    /// <summary>Moves the focus and the tab stop to the next live chip beside this one.</summary>
    /// <param name="direction">Which way, <c>-1</c> or <c>1</c>.</param>
    /// <returns>Whether there was somewhere to go.</returns>
    bool Rove(int direction) {
        if (Parent is not { } container) {
            return false;
        }

        var chips = new List<ColorSwatch>();

        foreach (var sibling in container.Children) {
            if (sibling is ColorSwatch { Selectable: true } chip) {
                chips.Add(chip);
            }
        }

        var index = chips.IndexOf(this);

        if (index < 0 || chips.Count < 2) {
            return false;
        }

        var next = chips[(index + direction + chips.Count) % chips.Count];

        next.TakeStop();
        Document.Focus(next);

        return true;
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

    /// <inheritdoc />
    /// <remarks>
    ///     ARIA <c>group</c>: a picker is several controls that belong together, and a screen reader
    ///     that announced them as loose siblings of whatever is around them would lose the one fact
    ///     that makes a hex field and two bands mean anything. Unnamed by default — what colour is
    ///     being picked is the application's sentence.
    /// </remarks>
    protected override AccessibleRole NativeRole => AccessibleRole.Group;

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

        // The one swatch that is a picture rather than a chip: it shows the colour being picked and
        // choosing it would choose what is already chosen. See `ColorSwatch.NativeRole`.
        Preview.Selectable = false;

        HexField = row.Add<TextBox>();
        HexField.AddClass("color-hex");
        HexField.Submitted += _ => HexEntered();

        // ⚠ Named rather than left to a caption, because there is no caption: the field sits beside
        // a swatch and its purpose is carried entirely by the six characters in it. Through the
        // catalogue, on `PropertyGrid`'s terms — a word only a screen reader ever hears is still a
        // word a translator has to be given.
        HexField.AccessibleName = ControlStrings.ColorPickerHex.Text;

        Eyedropper = row.Add<IconButton>();
        Eyedropper.LeadingIcon.Geometry = ControlIcons.Search;
        Eyedropper.Label = ControlStrings.ColorPickerEyedropper.Text;
        Eyedropper.Variant = ControlVariant.Subtle;
        Eyedropper.Clicked += _ => RequestEyedropper();

        var hdr = Part("color-row", "hdr");
        hdr.AddClass("hidden");

        var caption = hdr.Add("text");
        caption.Text = ControlStrings.ColorPickerIntensity.Text;

        IntensitySlider = hdr.Add<Slider>();

        // ⚠ **The caption is the slider's name, and saying so is the only thing that makes the
        // translation reach a screen reader.** The words above are localised — they are
        // `ControlStrings.ColorPickerIntensity` — but a caption is a separate element, and a slider
        // beside words it is not related to announces nothing at all. One `LabelledBy` is the whole
        // of it, and it costs the caption nothing: the relation reads the target's accessible name
        // on demand, so a re-labelled caption re-labels the slider with no second write.
        IntensitySlider.AddAccessibleRelation(AccessibleRelation.LabelledBy, caption);

        IntensitySlider.Minimum = 0f;
        IntensitySlider.Maximum = 16f;
        IntensitySlider.Value = 1f;
        IntensitySlider.ValueChanged += (_, level) => Intensity = level;

        Palette = Part("color-palette");

        // ⚠ The container carries the role the chips need to mean anything. An `option` with no
        // `listbox` over it is an item of nothing, and a screen reader has no way to say how many
        // colours there are or which one is current without the set they belong to.
        Palette.Role = AccessibleRole.ListBox;
        Palette.AccessibleName = ControlStrings.ColorPickerPalette.Text;

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

            Mark();
            Document.Invalidate();

            // The bands and the field announce `Fraction` and `Marker`, which are internal fields
            // this method writes — #420 gave them a keyboard and a `slider` role, and a slider that
            // moves on every arrow press and tells nobody is #593's other half.
            InvalidateAccessibility();
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

                // ⚠ Clearing this is not decoration — a parked chip is hidden and still focusable,
                // so a pool that outgrew the palette would leave invisible tab stops behind it.
                swatches[i].Selectable = false;

                continue;
            }

            swatches[i].RemoveClass("parked");
            swatches[i].Selectable = true;
            swatches[i].Color = palette[i];

            // Exactly one stop for the set. The arrows move it from here; this is only where it
            // starts, and where it goes back to when the palette is replaced.
            swatches[i].TabIndex = i == 0 ? 0 : -1;
        }

        Mark();
        Document.Invalidate();
    }

    /// <summary>Puts the chosen mark on the chip whose colour is the current one, if any.</summary>
    /// <remarks>
    ///     The flag a screen reader reads is the one the theme reads — see
    ///     <c>ColorSwatch.NativeAccessibleState</c> — so this writes it once and both are served.
    /// </remarks>
    void Mark() {
        for (var i = 0; i < swatches.Count; i++) {
            if (i < palette.Count && palette[i] == value) {
                swatches[i].State |= ElementState.Checked;
            } else {
                swatches[i].State &= ~ElementState.Checked;
            }
        }
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

/// <summary>A colour as a field: a swatch you click, and a picker that drops out of it.</summary>
/// <remarks>
///     <para>
///         <b>What a colour looks like in a property row.</b> <see cref="ColorPicker" /> is the whole
///         apparatus — a field, two bands, a hex box, an intensity slider and a palette — and it is
///         the right thing to <i>open</i>. It is the wrong thing to leave sitting in an inspector:
///         eight components with a tint each is eight of those stacked down the panel, and the row
///         somebody scrolled to is off the bottom of it. Every editor that has solved this has solved
///         it the same way, and it is the way a colour input works on the web: a swatch, and a picker
///         on demand.
///     </para>
///     <para>
///         ⚠ <b>The picker lives in a popover that is a root child, which is
///         <see cref="SelectBase" />'s arrangement and is here for its reason.</b> A panel that
///         dropped out of the field would be clipped by every scrolling ancestor between the two,
///         which for a property row in an inspector in a docked panel is three of them. The cost is
///         that the popover is not this control's child, so the subtree removal does not take it —
///         <see cref="OnRemoved" /> is what pays that.
///     </para>
///     <para>
///         ⚠ <b>The value is this control's and the picker is a view of it.</b> The picker is built
///         once and kept, so re-opening it returns to the hue the user was on rather than to whatever
///         the RGB happens to reconstruct — which is the distinction <see cref="ColorPicker" />'s own
///         remarks are about, and it would be lost by building a fresh picker per open.
///     </para>
/// </remarks>
public sealed partial class ColorInput : Control {
    /// <inheritdoc />
    protected override string TagName => "color-input";

    /// <inheritdoc />
    protected override bool AcceptsFocus => true;

    /// <inheritdoc />
    /// <remarks>
    ///     ARIA <c>button</c>: what this element does is open the picker. It is a widget role and so
    ///     carries a naming obligation, which is right — a swatch in a row is a colour of
    ///     <i>something</i>, and only the application knows what. One
    ///     <see cref="AccessibleRelation.LabelledBy" /> at the call site, and an inspector row does
    ///     it for you.
    /// </remarks>
    protected override AccessibleRole NativeRole => AccessibleRole.Button;

    /// <inheritdoc />
    /// <remarks>Expandable unconditionally, on <c>Select</c>'s terms: it always opens something.</remarks>
    protected override AccessibleStates NativeAccessibleState =>
        AccessibleStates.Expandable | (IsOpen ? AccessibleStates.Expanded : AccessibleStates.None);

    /// <summary>The box that is drawn in the row.</summary>
    public ColorSwatch Swatch { get; private set; } = null!;

    /// <summary>The floating picker.</summary>
    public Popover Popup { get; private set; } = null!;

    /// <summary>The picker inside it.</summary>
    public ColorPicker Picker { get; private set; } = null!;

    /// <summary>Whether the picker is showing.</summary>
    public bool IsOpen => Popup is { IsOpen: true };

    /// <summary>The chosen colour, with its alpha and without the intensity.</summary>
    public Color4 Value {
        get => Picker.Value;

        set {
            Picker.Value = value;

            // ⚠ Written here as well as from `ValueChanged`, because assigning a colour the picker
            // already holds raises nothing — and a row rebound to a different object with the same
            // tint would keep the previous one's swatch otherwise.
            Restate();
        }
    }

    /// <inheritdoc cref="ColorPicker.HdrValue" />
    public Color4 HdrValue => Picker.HdrValue;

    /// <inheritdoc cref="ColorPicker.AllowAlpha" />
    public bool AllowAlpha {
        get => Picker.AllowAlpha;
        set => Picker.AllowAlpha = value;
    }

    /// <inheritdoc cref="ColorPicker.AllowHdr" />
    public bool AllowHdr {
        get => Picker.AllowHdr;
        set => Picker.AllowHdr = value;
    }

    /// <inheritdoc cref="ColorPicker.Intensity" />
    public float Intensity {
        get => Picker.Intensity;
        set => Picker.Intensity = value;
    }

    /// <summary>Raised when the colour changes, however it changed.</summary>
    public event Action<ColorInput, Color4>? ValueChanged;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Swatch = Part<ColorSwatch>();

        // ⚠ The box in the row is a picture and this element is the button. Leaving it selectable
        // put a second tab stop inside a control that is already one — and, because a chip handles
        // its own press, swallowed the press this field opens the picker on.
        Swatch.Selectable = false;

        // On the root, not on this control — see the remarks. `Placement.Bottom` is what a select's
        // list uses, so a colour field and a dropdown in the same panel open the same way.
        Popup = Document.Root.Add<Popover>();
        Popup.AddClass("color-popup");
        Popup.Placement = Placement.Bottom;

        // The popup is a root child for `SelectBase`'s reason, so the same statement is needed: the
        // picker in it belongs to this element although the tree says otherwise.
        AddAccessibleRelation(AccessibleRelation.Owns, Popup);

        Picker = Popup.Content.Add<ColorPicker>();

        Picker.ValueChanged += (_, colour) => {
            Restate();
            ValueChanged?.Invoke(this, colour);
        };

        AddHandler<PointerEvent>(static (element, args) => ((ColorInput) element).Pointed(args));
        AddHandler<KeyEvent>(static (element, args) => ((ColorInput) element).Keyed(args));

        // Escape and a click outside both close through the overlay rather than through this, so the
        // field's own `:checked` is kept in step by listening rather than only by acting.
        Popup.OpenChanged += (_, isOpen) => {
            if (isOpen) {
                State |= ElementState.Checked;
            } else {
                State &= ~ElementState.Checked;
            }
        };

        Restate();
    }

    /// <inheritdoc />
    /// <remarks>The popover is a root child, so the subtree removal does not reach it. See its creation.</remarks>
    protected override void OnRemoved() {
        if (Popup is { IsRemoved: false }) {
            Document.Remove(Popup);
            Popup = null!;
        }

        base.OnRemoved();
    }

    /// <summary>Shows the picker.</summary>
    public void Open() {
        if (Disabled || Popup is not { IsRemoved: false }) {
            return;
        }

        Popup.Open(this);
    }

    /// <summary>Hides it.</summary>
    /// <param name="reason">Why.</param>
    public void Close(CloseReason reason = CloseReason.Code) {
        if (Popup is { IsRemoved: false }) {
            Popup.Close(reason);
        }
    }

    void Restate() => Swatch.Color = Value;

    void Pointed(PointerEvent args) {
        if (args is not { Action: PointerAction.Pressed, Button: PointerButton.Primary }) {
            return;
        }

        Document.Focus(this);

        // ⚠ Toggling rather than opening, which is `SelectBase.Pointed`'s note: without it a click
        // on an open field is a press the overlay's light dismiss closes and a click this reopens,
        // so the picker flickers instead of closing.
        if (IsOpen) {
            Close();
        } else {
            Open();
        }

        args.Handled = true;
    }

    void Keyed(KeyEvent args) {
        if (args.Action != KeyAction.Pressed) {
            return;
        }

        switch (args.Key) {
            case InputKey.Escape when IsOpen:
                Close(CloseReason.Cancelled);
                break;

            case InputKey.Space or InputKey.Enter or InputKey.KeypadEnter when !IsOpen:
                Open();
                break;

            default:
                return;
        }

        args.Handled = true;
    }
}
