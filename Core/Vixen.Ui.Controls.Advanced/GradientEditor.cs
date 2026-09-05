// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
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
/// <remarks>
///     <para>
///         ⚠ <b>The keyboard came before the role, which is #420's whole ordering.</b> This was
///         pointer-only and roleless together, and the pairing was deliberate: announcing a rail a
///         mouse alone can reach converts "not available to me" into "available and does nothing".
///         So the arrows landed with the role, in one change, and neither is correct alone.
///     </para>
///     <para>
///         ⚠ <b>Two axes and two meanings, because a rail is a list and not a value.</b> Left and
///         Right move the selected stop along the gradient — a hundredth a press, a tenth with Page,
///         the ends with Home and End, which is <c>ColorStrip</c>'s contract. Up and Down select the
///         previous and next stop, which on a horizontal rail is an axis with nothing else to mean.
///         Any of the six with nothing selected selects the first stop rather than doing nothing:
///         a keyboard user who has just tabbed in has no selection and would otherwise be stuck in a
///         control that answers no key.
///     </para>
///     <para>
///         ⚠ <b>One tab stop for the whole rail, not one per stop.</b> A stop is drawn rather than
///         built — see <c>GradientRail.OnDraw</c> — so there is no element to focus, and a gradient
///         of sixteen stops would otherwise be sixteen tab stops between the bar and the picker.
///     </para>
/// </remarks>
public sealed partial class GradientRail : Control {
    /// <summary>How far one arrow press moves the selected stop. <c>ColorStrip.KeyStep</c>'s value.</summary>
    public const float KeyStep = 0.01f;

    /// <inheritdoc />
    protected override string TagName => "gradient-rail";

    /// <inheritdoc />
    protected override bool AcceptsFocus => true;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b><c>Group</c> rather than <c>Slider</c>, and it is <c>RangeSlider</c>'s refusal
    ///     rather than a new one.</b> Doc 46's table put this row under "a 1-D value with arrow-key
    ///     stepping — the shape <c>Slider</c> already has"; that is wrong for the same reason
    ///     <c>RangeSlider</c> gives for its second thumb, only more so. <c>aria-valuenow</c> is one
    ///     number and a rail carries N — a screen reader told the selected stop's position is the
    ///     control's value would announce a slider that jumps whenever the <i>selection</i> moves
    ///     and never moves when the other stops do. A group with a composite value says what is
    ///     actually true.
    /// </remarks>
    protected override AccessibleRole NativeRole => AccessibleRole.Group;

    /// <inheritdoc />
    /// <remarks>
    ///     From the catalogue, on <c>ButtonBase.NativeAccessibleName</c>'s terms: the two rails are
    ///     identical in every way a screen reader can perceive except which list they carry, and
    ///     nothing near either of them says which.
    /// </remarks>
    protected override string? NativeAccessibleName =>
        IsAlpha ? ControlStrings.GradientEditorAlphaStops.Text : ControlStrings.GradientEditorColorStops.Text;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Which stop of how many, and where — and <c>null</c> when nothing is selected.</b> The
    ///     position alone would be a number that means nothing without the count, since the arrows
    ///     move one stop out of several and the other keys change which one that is. Invariant and
    ///     unitless for <c>Slider</c>'s reason: a bare float in the current culture is a string a
    ///     bridge has to parse back.
    /// </remarks>
    protected override string? NativeAccessibleValue {
        get {
            var index = SelectedIndex;

            return index < 0
                ? null
                : string.Create(CultureInfo.InvariantCulture, $"{index + 1} of {Count} at {SelectedPosition:0.###}");
        }
    }

    /// <summary>The editor it belongs to.</summary>
    public GradientEditor? Owner { get; internal set; }

    /// <summary>Whether it carries the alpha stops rather than the colour ones.</summary>
    public bool IsAlpha { get; internal set; }

    /// <summary>How many stops this rail carries.</summary>
    public int Count => Owner is not { } owner ? 0 : IsAlpha ? owner.Gradient.AlphaStops.Count : owner.Gradient.ColorStops.Count;

    /// <summary>Where the selected stop is on this rail, or <c>-1</c> when none of them is.</summary>
    /// <remarks>
    ///     ⚠ <b>The editor holds one selection across both rails</b> — selecting a colour stop clears
    ///     the alpha one — so a rail asking "which of mine is selected" has to check that the
    ///     selection is on <i>its</i> list at all, and the alpha rail correctly answers <c>-1</c>
    ///     while a colour stop is chosen.
    /// </remarks>
    public int SelectedIndex {
        get {
            if (Owner is not { } owner) {
                return -1;
            }

            return IsAlpha
                ? owner.SelectedAlphaStop is { } alpha ? IndexOf(owner.Gradient.AlphaStops, alpha) : -1
                : owner.SelectedColorStop is { } colour ? IndexOf(owner.Gradient.ColorStops, colour) : -1;
        }
    }

    /// <summary>Where the selected stop sits along the gradient, or zero when none is selected.</summary>
    public float SelectedPosition =>
        Owner is not { } owner ? 0f
        : IsAlpha ? owner.SelectedAlphaStop?.Position ?? 0f
        : owner.SelectedColorStop?.Position ?? 0f;

    static int IndexOf<T>(IReadOnlyList<T> stops, T stop) where T : class {
        for (var i = 0; i < stops.Count; i++) {
            if (ReferenceEquals(stops[i], stop)) {
                return i;
            }
        }

        return -1;
    }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        AddHandler<KeyEvent>(static (element, args) => ((GradientRail) element).Keyed(args));
    }

    /// <summary>Selects the stop at an index of this rail's list, or nothing for an index outside it.</summary>
    /// <param name="index">Which.</param>
    public void SelectAt(int index) {
        if (Owner is not { } owner) {
            return;
        }

        if (IsAlpha) {
            owner.Select(index >= 0 && index < owner.Gradient.AlphaStops.Count ? owner.Gradient.AlphaStops[index] : null);
        } else {
            owner.Select(index >= 0 && index < owner.Gradient.ColorStops.Count ? owner.Gradient.ColorStops[index] : null);
        }
    }

    /// <summary>Moves the selected stop to a position along the gradient.</summary>
    /// <param name="position">Where, from zero to one. Clamped.</param>
    /// <remarks>
    ///     ⚠ Through <c>Gradient.Move</c>, which re-sorts — so a stop arrowed past its neighbour
    ///     changes places with it and stays selected, exactly as a dragged one does. Writing
    ///     <c>Position</c> directly would leave the list out of order and the bar drawn from it
    ///     wrong.
    /// </remarks>
    public void MoveSelected(float position) {
        if (Owner is not { } owner) {
            return;
        }

        if (IsAlpha) {
            if (owner.SelectedAlphaStop is { } alpha) {
                owner.Gradient.Move(alpha, Math.Clamp(position, 0f, 1f));
            }
        } else if (owner.SelectedColorStop is { } colour) {
            owner.Gradient.Move(colour, Math.Clamp(position, 0f, 1f));
        }
    }

    /// <remarks>
    ///     ⚠ <b>Delete and Backspace are deliberately not handled here.</b> <c>GradientEditor</c>
    ///     already owns them and this rail is one of its parts, so an unhandled key bubbles to it —
    ///     answering them here would be a second implementation of "remove the selected stop" that
    ///     could disagree with the first about which stop that is.
    /// </remarks>
    void Keyed(KeyEvent args) {
        if (args.Action != KeyAction.Pressed || Count == 0) {
            return;
        }

        var index = SelectedIndex;

        // Nothing selected: the first key press picks a stop rather than doing nothing, so a rail
        // that has just been tabbed into is operable without a mouse having been anywhere near it.
        if (index < 0) {
            switch (args.Key) {
                case InputKey.Left or InputKey.Right or InputKey.Up or InputKey.Down:
                case InputKey.Home or InputKey.End or InputKey.PageUp or InputKey.PageDown:
                    SelectAt(0);
                    args.Handled = true;

                    return;

                default:
                    return;
            }
        }

        var position = SelectedPosition;

        switch (args.Key) {
            case InputKey.Left:
                MoveSelected(position - KeyStep);
                break;

            case InputKey.Right:
                MoveSelected(position + KeyStep);
                break;

            case InputKey.PageDown:
                MoveSelected(position - (KeyStep * 10f));
                break;

            case InputKey.PageUp:
                MoveSelected(position + (KeyStep * 10f));
                break;

            case InputKey.Home:
                MoveSelected(0f);
                break;

            case InputKey.End:
                MoveSelected(1f);
                break;

            // ⚠ Up is the *previous* stop, which is leftwards along the rail. A rail is horizontal
            // and its list is sorted by position, so "up the list" and "towards the start" are the
            // same direction — the opposite of the vertical-slider convention, and right here.
            case InputKey.Up:
                SelectAt(Math.Max(0, index - 1));
                break;

            case InputKey.Down:
                SelectAt(Math.Min(Count - 1, index + 1));
                break;

            default:
                return;
        }

        args.Handled = true;
    }

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

                // ⚠ The rail and not the editor, now that a rail answers keys. `Pointed` focused
                // the editor a moment ago, which is right for a press that grabbed nothing; a press
                // that grabbed a stop should leave the arrows moving that stop, and the arrows are
                // the rail's.
                Document.Focus(ColorRail);

                draggingColor = stop;
                Document.CapturePointer(this);

                break;

            case Rail.Alpha when AlphaStopAt(args.X) is { } alpha:
                Select(alpha);
                Document.Focus(AlphaRail);

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
