// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Input;

namespace Vixen.Ui.Controls.Advanced;

/// <summary>The strip itself, with a marker rail above it and another below.</summary>
/// <remarks>
///     ⚠ <b>Sampled into bands.</b> The shader's gradient has two stops and a gradient has as many
///     as somebody put in it, so the bar is drawn as a run of two-stop rectangles. The sampling is
///     per band rather than per pixel because within a band between two adjacent stops the
///     interpolation the shader does is the one that was asked for — except in Oklab, which is
///     curved, and where the bands are what makes it look right.
/// </remarks>
public sealed partial class GradientBar : Control {
    /// <inheritdoc />
    protected override string TagName => "gradient-bar";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The editor it belongs to.</summary>
    public GradientEditor? Owner { get; internal set; }

    /// <summary>How many rectangles the strip is drawn as.</summary>
    public const int Bands = 48;

    /// <inheritdoc />
    protected override void OnDraw(DrawContext context) {
        base.OnDraw(context);

        var bounds = context.Bounds;

        if (bounds.Width <= 0f || bounds.Height <= 0f || Owner is not { } owner) {
            return;
        }

        ColorStrip.Chequer(context, bounds);

        var gradient = owner.Gradient;
        var slice = bounds.Width / Bands;

        for (var i = 0; i < Bands; i++) {
            context.FillRectangle(
                new Rectangle(bounds.X + (i * slice), bounds.Y, slice + 1f, bounds.Height),
                gradient.Evaluate(i / (float) Bands),
                new BoxStyle(default, gradient.Evaluate((i + 1f) / Bands), new Vector2(1f, 0f))
            );
        }
    }
}

/// <summary>The rail of markers above or below the bar.</summary>
public sealed partial class GradientRail : Control {
    /// <inheritdoc />
    protected override string TagName => "gradient-rail";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The editor it belongs to.</summary>
    public GradientEditor? Owner { get; internal set; }

    /// <summary>Whether it carries the alpha stops rather than the colour ones.</summary>
    public bool IsAlpha { get; internal set; }

    /// <inheritdoc />
    protected override void OnDraw(DrawContext context) {
        base.OnDraw(context);

        var bounds = context.Bounds;

        if (bounds.Width <= 0f || bounds.Height <= 0f || Owner is not { } owner) {
            return;
        }

        var accent = context.Foreground;

        if (IsAlpha) {
            foreach (var stop in owner.Gradient.AlphaStops) {
                Marker(
                    context,
                    bounds,
                    stop.Position,
                    new Color4(stop.Alpha, stop.Alpha, stop.Alpha, 1f),
                    accent,
                    ReferenceEquals(owner.SelectedAlphaStop, stop)
                );
            }

            return;
        }

        foreach (var stop in owner.Gradient.ColorStops) {
            Marker(context, bounds, stop.Position, stop.Color, accent, ReferenceEquals(owner.SelectedColorStop, stop));
        }
    }

    static void Marker(DrawContext context, Rectangle bounds, float position, Color4 fill, Color4 outline, bool selected) {
        var size = MathF.Min(bounds.Height, 12f);
        var x = bounds.X + (Math.Clamp(position, 0f, 1f) * bounds.Width);

        var box = new Rectangle(x - (size * 0.5f), bounds.Y + ((bounds.Height - size) * 0.5f), size, size);

        context.FillRectangle(box, fill, 2f);
        context.StrokeRectangle(
            box,
            selected ? outline : Color4.Black,
            selected ? 2f : 1f,
            BoxStyle.Rounded(CornerRadii.Uniform(2f))
        );
    }
}

/// <summary>Editing a gradient: two rails of stops, a bar, and a picker for whichever is selected.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two rails because there are two lists.</b> Colour and alpha are edited separately —
///         see <see cref="Gradient" /> for why they are stored separately — so the alpha stops sit
///         above the bar and the colour stops below it, which is the arrangement every tool that
///         made the same choice arrived at.
///     </para>
///     <para>
///         <b>The picker is part of the control</b> rather than something an application wires up.
///         Selecting a colour stop and then having to find where its colour is edited is the whole
///         friction of a gradient editor, and a picker that appears where the selection is removes
///         it. An application that wants its own puts <see cref="Picker" />'s class in
///         <c>display: none</c> and listens to <see cref="SelectionChanged" />.
///     </para>
/// </remarks>
public sealed partial class GradientEditor : Control {
    Gradient gradient = new(new Color4(0f, 0f, 0f, 1f), new Color4(1f, 1f, 1f, 1f));

    GradientColorStop? draggingColor;
    GradientAlphaStop? draggingAlpha;
    bool suppress;

    /// <inheritdoc />
    protected override string TagName => "gradient-editor";

    /// <inheritdoc />
    protected override bool AcceptsFocus => true;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>ARIA <c>application</c>, and it is a role with a cost that is worth paying
    ///     here.</b> It tells assistive technology to stop intercepting the keyboard and pass every
    ///     key through, because this element has a keyboard model of its own that no generic widget
    ///     vocabulary describes. That is exactly true of a direct-manipulation surface — a rail of stops dragged along a bar — and it
    ///     is exactly false of a text field, which is why <c>CodeEditor</c> is a <c>textbox</c>
    ///     instead. Unnamed by default: what this one is a view of is the application's sentence,
    ///     and it is usually the panel title above it.
    /// </remarks>
    protected override AccessibleRole NativeRole => AccessibleRole.Application;

    /// <summary>The gradient being edited.</summary>
    public Gradient Gradient {
        get => gradient;
        set {
            ArgumentNullException.ThrowIfNull(value);

            if (ReferenceEquals(gradient, value)) {
                return;
            }

            gradient.Changed -= OnGradientChanged;
            gradient = value;
            gradient.Changed += OnGradientChanged;

            SelectedColorStop = null;
            SelectedAlphaStop = null;

            Sync();
        }
    }

    /// <summary>The rail of alpha stops, above the bar.</summary>
    public GradientRail AlphaRail { get; private set; } = null!;

    /// <summary>The strip.</summary>
    public GradientBar Bar { get; private set; } = null!;

    /// <summary>The rail of colour stops, below it.</summary>
    public GradientRail ColorRail { get; private set; } = null!;

    /// <summary>How the colours are mixed.</summary>
    public Select Space { get; private set; } = null!;

    /// <summary>The picker, shown when a colour stop is selected.</summary>
    public ColorPicker Picker { get; private set; } = null!;

    /// <summary>The opacity slider, shown when an alpha stop is selected.</summary>
    public Slider Opacity { get; private set; } = null!;

    /// <summary>Which colour stop is selected, if one is.</summary>
    public GradientColorStop? SelectedColorStop { get; private set; }

    /// <summary>Which alpha stop is selected, if one is.</summary>
    public GradientAlphaStop? SelectedAlphaStop { get; private set; }

    /// <summary>Raised after the gradient changes.</summary>
    public event Action<GradientEditor>? GradientChanged;

    /// <summary>Raised when a different stop is selected.</summary>
    public event Action<GradientEditor>? SelectionChanged;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        AlphaRail = Part<GradientRail>(null, "alpha");
        AlphaRail.Owner = this;
        AlphaRail.IsAlpha = true;

        Bar = Part<GradientBar>();
        Bar.Owner = this;

        ColorRail = Part<GradientRail>(null, "color");
        ColorRail.Owner = this;

        Space = Part<Select>();

        // ⚠ Named, because there is no caption: the field shows the colour space it is set to, and
        // "sRGB" answers *which* without ever saying what the question was. The three option labels
        // stay out of the catalogue on purpose — see the declaration's own remarks.
        Space.AccessibleName = ControlStrings.GradientEditorSpace.Text;

        foreach (var space in Enum.GetValues<GradientInterpolation>()) {
            Space.AddOption(space.ToString(), Label(space));
        }

        Space.Value = gradient.Interpolation.ToString();
        Space.SelectionChanged += (_, value) => SpaceChosen(value);

        Opacity = Part<Slider>();
        Opacity.AccessibleName = ControlStrings.GradientEditorOpacity.Text;
        Opacity.AddClass("hidden");
        Opacity.ValueChanged += (_, value) => OpacityChosen(value);

        Picker = Part<ColorPicker>();
        Picker.AllowAlpha = false;
        Picker.AddClass("hidden");
        Picker.ValueChanged += (_, colour) => ColorChosen(colour);

        gradient.Changed += OnGradientChanged;

        AddHandler<PointerEvent>(static (element, args) => ((GradientEditor) element).Pointed(args));
        AddHandler<TapEvent>(static (element, args) => ((GradientEditor) element).Tapped(args));
        AddHandler<KeyEvent>(static (element, args) => ((GradientEditor) element).Keyed(args));
    }

    /// <summary>Selects a colour stop, and shows the picker for it.</summary>
    /// <param name="stop">The stop, or <c>null</c> to select nothing.</param>
    public void Select(GradientColorStop? stop) {
        SelectedColorStop = stop;
        SelectedAlphaStop = null;

        Sync();
        SelectionChanged?.Invoke(this);
    }

    /// <summary>Selects an alpha stop, and shows the opacity slider for it.</summary>
    /// <param name="stop">The stop, or <c>null</c>.</param>
    public void Select(GradientAlphaStop? stop) {
        SelectedAlphaStop = stop;
        SelectedColorStop = null;

        Sync();
        SelectionChanged?.Invoke(this);
    }

    /// <summary>Where a position along the gradient is, in document space.</summary>
    /// <param name="position">The position, from zero to one.</param>
    /// <returns>The x.</returns>
    public float ToScreen(float position) => Bar.Bounds.X + (Math.Clamp(position, 0f, 1f) * Bar.Bounds.Width);

    /// <summary>Which position along the gradient a document-space x is.</summary>
    /// <param name="x">The x.</param>
    /// <returns>The position.</returns>
    public float ToPosition(float x) {
        var bounds = Bar.Bounds;
        return bounds.Width <= 0f ? 0f : Math.Clamp((x - bounds.X) / bounds.Width, 0f, 1f);
    }

    /// <summary>The colour stop nearest a point on the colour rail, if one is within reach.</summary>
    /// <param name="x">The x, in document space.</param>
    /// <param name="radius">How near, in pixels.</param>
    /// <returns>The stop, or <c>null</c>.</returns>
    public GradientColorStop? ColorStopAt(float x, float radius = 9f) {
        GradientColorStop? found = null;
        var best = radius;

        foreach (var stop in gradient.ColorStops) {
            var distance = MathF.Abs(ToScreen(stop.Position) - x);

            if (distance <= best) {
                best = distance;
                found = stop;
            }
        }

        return found;
    }

    /// <summary>The alpha stop nearest a point on the alpha rail, if one is within reach.</summary>
    /// <param name="x">The x, in document space.</param>
    /// <param name="radius">How near, in pixels.</param>
    /// <returns>The stop, or <c>null</c>.</returns>
    public GradientAlphaStop? AlphaStopAt(float x, float radius = 9f) {
        GradientAlphaStop? found = null;
        var best = radius;

        foreach (var stop in gradient.AlphaStops) {
            var distance = MathF.Abs(ToScreen(stop.Position) - x);

            if (distance <= best) {
                best = distance;
                found = stop;
            }
        }

        return found;
    }

    // ── Input ────────────────────────────────────────────────────────────────

    void Pointed(PointerEvent args) {
        switch (args.Action) {
            case PointerAction.Pressed when args.Button == PointerButton.Primary:
                Document.Focus(this);
                Grab(args);

                break;

            case PointerAction.Moved when draggingColor is { } colour:
                gradient.Move(colour, ToPosition(args.X));
                break;

            case PointerAction.Moved when draggingAlpha is { } alpha:
                gradient.Move(alpha, ToPosition(args.X));
                break;

            case PointerAction.Released when draggingColor is not null || draggingAlpha is not null:
                draggingColor = null;
                draggingAlpha = null;

                Document.ReleasePointer();
                break;

            default:
                return;
        }

        args.Handled = true;
    }

    void Grab(PointerEvent args) {
        switch (RailAt(args.X, args.Y)) {
            case Rail.Color when ColorStopAt(args.X) is { } stop:
                Select(stop);

                draggingColor = stop;
                Document.CapturePointer(this);

                break;

            case Rail.Alpha when AlphaStopAt(args.X) is { } alpha:
                Select(alpha);

                draggingAlpha = alpha;
                Document.CapturePointer(this);

                break;

            default:
                break;
        }
    }

    void Tapped(TapEvent args) {
        if (args.Count != 2) {
            return;
        }

        switch (RailAt(args.X, args.Y)) {
            case Rail.Color:
                if (ColorStopAt(args.X) is { } existing) {
                    if (gradient.Remove(existing)) {
                        Select((GradientColorStop?) null);
                    }
                } else {
                    var position = ToPosition(args.X);
                    Select(gradient.AddColorStop(position, gradient.Evaluate(position)));
                }

                break;

            case Rail.Alpha:
                if (AlphaStopAt(args.X) is { } stop) {
                    if (gradient.Remove(stop)) {
                        Select((GradientAlphaStop?) null);
                    }
                } else {
                    var position = ToPosition(args.X);
                    Select(gradient.AddAlphaStop(position, gradient.Evaluate(position).A));
                }

                break;

            default:
                return;
        }

        args.Handled = true;
    }

    /// <summary>Which rail a point is over.</summary>
    /// <remarks>
    ///     ⚠ <b>Geometry rather than the event's source</b>, and it has to be. Pressing a marker
    ///     captures the pointer, so every event after that — including the tap the gesture recogniser
    ///     builds out of them — is reported against the <i>capturing</i> element rather than against
    ///     the rail it started on. A source test works for the first click of a double click and
    ///     silently stops working for the second.
    /// </remarks>
    Rail RailAt(float x, float y) {
        var point = new Vector2(x, y);

        if (ColorRail.Bounds.Contains(point)) {
            return Rail.Color;
        }

        return AlphaRail.Bounds.Contains(point) ? Rail.Alpha : Rail.None;
    }

    enum Rail : byte {
        None,
        Color,
        Alpha
    }

    void Keyed(KeyEvent args) {
        if (args is not { Action: KeyAction.Pressed, Key: InputKey.Delete or InputKey.Backspace }) {
            return;
        }

        if (SelectedColorStop is { } colour && gradient.Remove(colour)) {
            Select((GradientColorStop?) null);
        } else if (SelectedAlphaStop is { } alpha && gradient.Remove(alpha)) {
            Select((GradientAlphaStop?) null);
        }

        args.Handled = true;
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    void OnGradientChanged(Gradient changed) {
        GradientChanged?.Invoke(this);
        Document.Invalidate();
    }

    void ColorChosen(Color4 colour) {
        if (suppress || SelectedColorStop is not { } stop) {
            return;
        }

        stop.Color = colour;
        gradient.Touch();
    }

    void OpacityChosen(float value) {
        if (suppress || SelectedAlphaStop is not { } stop) {
            return;
        }

        stop.Alpha = value;
        gradient.Touch();
    }

    void SpaceChosen(string? value) {
        if (Enum.TryParse<GradientInterpolation>(value, out var space)) {
            gradient.Interpolation = space;
        }
    }

    /// <summary>What each space is called in the list, which is not what the member is called.</summary>
    static string Label(GradientInterpolation space) =>
        space switch {
            GradientInterpolation.Srgb => "sRGB",
            GradientInterpolation.Linear => "Linear light",
            _ => "Perceptual (Oklab)"
        };

    /// <summary>Brings the picker, the slider and the visibility into agreement with the selection.</summary>
    /// <remarks>
    ///     ⚠ <b>Guarded, because writing the picker's value raises its own change.</b> Without the
    ///     flag, selecting a stop would write the picker, the picker would write the stop back, and a
    ///     rounding difference between the two would walk the colour a little further every time
    ///     somebody clicked on it.
    /// </remarks>
    void Sync() {
        suppress = true;

        try {
            if (SelectedColorStop is { } colour) {
                Picker.RemoveClass("hidden");
                Picker.Value = colour.Color;
            } else {
                Picker.AddClass("hidden");
            }

            if (SelectedAlphaStop is { } alpha) {
                Opacity.RemoveClass("hidden");
                Opacity.Value = alpha.Alpha;
            } else {
                Opacity.AddClass("hidden");
            }
        } finally {
            suppress = false;
        }

        Document.Invalidate();
    }
}
