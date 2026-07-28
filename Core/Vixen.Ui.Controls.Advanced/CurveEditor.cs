// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Input;

namespace Vixen.Ui.Controls.Advanced;

/// <summary>What a drag on a curve is moving.</summary>
enum CurveDrag : byte {
    None,
    Key,
    InHandle,
    OutHandle,
    Pan
}

/// <summary>Editing a value over time: keys, tangent handles, and a pannable graph.</summary>
/// <remarks>
///     <para>
///         <b>Entirely drawn.</b> A curve is a polyline, a key is a five-pixel diamond and a handle
///         is a line with a dot on the end — none of them is a box, and as elements they would be a
///         style node and a layout box each for something whose position is a matrix multiply. This
///         is the case <see cref="UiElement.OnDraw" /> exists for, and the theme still owns every
///         colour through custom properties.
///     </para>
///     <para>
///         ⚠ <b>The curve is sampled per pixel column</b>, not per key. A cubic drawn as one segment
///         per key is faceted wherever the keys are far apart, and one sampled finely everywhere is
///         work that scales with the curve rather than with the window. A column is the finest thing
///         anybody can see.
///     </para>
///     <para>
///         ⚠ <b>Handles are drawn at a fixed pixel length</b> rather than at a third of the interval.
///         The slope is what is being edited, and a handle whose length depended on the neighbouring
///         key's time would change length when a key nowhere near it moved — which makes it look
///         like the tangent changed when it did not.
///     </para>
/// </remarks>
public sealed partial class CurveEditor : Control {
    readonly PathBuilder path = new();
    readonly PathBuilder grid = new();
    readonly HashSet<CurveKey> selection = [];

    AnimationCurve curve = AnimationCurve.EaseInOut();
    CurveDrag drag;
    CurveKey? active;
    Vector2 panStart;
    Vector2 panOrigin;

    int gridColor;
    int curveColor;
    int keyColor;
    int handleColor;

    /// <inheritdoc />
    protected override string TagName => "curve-editor";

    /// <inheritdoc />
    protected override bool AcceptsFocus => true;

    /// <summary>The curve being edited.</summary>
    public AnimationCurve Curve {
        get => curve;
        set {
            ArgumentNullException.ThrowIfNull(value);

            if (ReferenceEquals(curve, value)) {
                return;
            }

            curve.Changed -= OnCurveChanged;
            curve = value;
            curve.Changed += OnCurveChanged;

            selection.Clear();
            active = null;

            Document.Invalidate();
        }
    }

    /// <summary>What the graph is showing: time across, value up.</summary>
    /// <remarks>
    ///     ⚠ <b>Y grows upwards here and downwards on screen.</b> A curve editor is the one place in
    ///     an interface where the mathematical convention wins, because the thing being drawn is a
    ///     graph and a graph with its value axis upside down is unreadable. Every conversion in this
    ///     control flips; nothing else in the assembly does.
    /// </remarks>
    public Rectangle View { get; set; } = new(-0.1f, -0.2f, 1.2f, 1.4f);

    /// <summary>Which keys are selected.</summary>
    public IReadOnlyCollection<CurveKey> Selection => selection;

    /// <summary>The key whose handles are shown, if any.</summary>
    public CurveKey? Active => active;

    /// <summary>Whether a dragged key snaps to the grid.</summary>
    [UiProperty]
    public partial bool SnapToGrid { get; set; }

    /// <summary>How far apart the grid lines are in time.</summary>
    [UiProperty(Default = 0.1f)]
    public partial float TimeStep { get; set; }

    /// <summary>How far apart they are in value.</summary>
    [UiProperty(Default = 0.1f)]
    public partial float ValueStep { get; set; }

    /// <summary>How long a tangent handle is drawn, in pixels.</summary>
    [UiProperty(Default = 44f)]
    public partial float HandleLength { get; set; }

    /// <summary>Raised after a key is added, moved or removed.</summary>
    public event Action<CurveEditor>? CurveChanged;

    /// <summary>Raised when the selection changes.</summary>
    public event Action<CurveEditor>? SelectionChanged;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        gridColor = Document.PropertyId("--grid-color");
        curveColor = Document.PropertyId("--curve-color");
        keyColor = Document.PropertyId("--key-color");
        handleColor = Document.PropertyId("--handle-color");

        curve.Changed += OnCurveChanged;

        AddHandler<PointerEvent>(static (element, args) => ((CurveEditor) element).Pointed(args));
        AddHandler<WheelEvent>(static (element, args) => ((CurveEditor) element).Wheeled(args));
        AddHandler<KeyEvent>(static (element, args) => ((CurveEditor) element).Keyed(args));
        AddHandler<TapEvent>(static (element, args) => ((CurveEditor) element).Tapped(args));
    }

    // ── Coordinates ──────────────────────────────────────────────────────────

    /// <summary>Where a point of the graph is, in document space.</summary>
    /// <param name="time">Its time.</param>
    /// <param name="value">Its value.</param>
    /// <returns>The point.</returns>
    public Vector2 ToScreen(float time, float value) {
        var bounds = Bounds;

        return new Vector2(
            bounds.X + ((time - View.X) / View.Width * bounds.Width),
            bounds.Bottom - ((value - View.Y) / View.Height * bounds.Height)
        );
    }

    /// <summary>Where a document-space point is on the graph.</summary>
    /// <param name="x">Its x.</param>
    /// <param name="y">Its y.</param>
    /// <returns>The time and the value.</returns>
    public Vector2 ToCurve(float x, float y) {
        var bounds = Bounds;

        return new Vector2(
            bounds.Width <= 0f ? View.X : View.X + ((x - bounds.X) / bounds.Width * View.Width),
            bounds.Height <= 0f ? View.Y : View.Y + ((bounds.Bottom - y) / bounds.Height * View.Height)
        );
    }

    /// <summary>Pans and zooms until every key fits, with a little room round it.</summary>
    public void Frame() {
        if (curve.Keys.Count == 0) {
            return;
        }

        var minimumTime = curve.Keys[0].Time;
        var maximumTime = curve.Keys[^1].Time;

        var minimumValue = float.MaxValue;
        var maximumValue = float.MinValue;

        foreach (var key in curve.Keys) {
            minimumValue = MathF.Min(minimumValue, key.Value);
            maximumValue = MathF.Max(maximumValue, key.Value);
        }

        // ⚠ A minimum span, because a constant curve has no height and a flat curve is exactly what
        // somebody frames just before they start editing one.
        var width = MathF.Max(0.001f, maximumTime - minimumTime);
        var height = MathF.Max(0.001f, maximumValue - minimumValue);

        View = new Rectangle(
            minimumTime - (width * 0.1f),
            minimumValue - (height * 0.2f),
            width * 1.2f,
            height * 1.4f
        );

        Document.Invalidate();
    }

    /// <summary>Replaces the curve with a preset, keeping the object the caller is holding.</summary>
    /// <param name="preset">The shape.</param>
    /// <remarks>
    ///     ⚠ <b>The keys are copied into the existing curve rather than the curve being replaced.</b>
    ///     Everything that is bound to a curve holds the object, so swapping it would leave a
    ///     material, a particle system and an inspector all pointing at the previous one.
    /// </remarks>
    public void Apply(AnimationCurve preset) {
        ArgumentNullException.ThrowIfNull(preset);

        foreach (var key in curve.Keys.ToArray()) {
            curve.Remove(key);
        }

        foreach (var key in preset.Keys) {
            curve.Add(
                new CurveKey(key.Time, key.Value, key.Mode) {
                    InTangent = key.InTangent,
                    OutTangent = key.OutTangent
                }
            );
        }

        selection.Clear();
        active = null;

        SelectionChanged?.Invoke(this);
    }

    /// <summary>Sets the tangent mode of every selected key.</summary>
    /// <param name="mode">The mode.</param>
    public void SetTangentMode(TangentMode mode) {
        if (selection.Count == 0) {
            return;
        }

        foreach (var key in selection) {
            key.Mode = mode;
        }

        curve.Touch();
    }

    /// <summary>The key nearest a document-space point, if one is within reach.</summary>
    /// <param name="x">The x.</param>
    /// <param name="y">The y.</param>
    /// <param name="radius">How near, in pixels.</param>
    /// <returns>The key, or <c>null</c>.</returns>
    public CurveKey? KeyAt(float x, float y, float radius = 9f) {
        CurveKey? found = null;
        var best = radius * radius;

        foreach (var key in curve.Keys) {
            var point = ToScreen(key.Time, key.Value);
            var distance = ((point.X - x) * (point.X - x)) + ((point.Y - y) * (point.Y - y));

            if (distance <= best) {
                best = distance;
                found = key;
            }
        }

        return found;
    }

    // ── Drawing ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    protected override void OnDraw(DrawContext context) {
        base.OnDraw(context);

        var bounds = context.Bounds;

        if (bounds.Width <= 1f || bounds.Height <= 1f) {
            return;
        }

        DrawGrid(context, bounds);
        DrawCurve(context, bounds);

        var keys = Document.ColorOf(Style, keyColor) ?? Document.ForegroundOf(this);
        var handles = Document.ColorOf(Style, handleColor) ?? new Color4(0.55f, 0.58f, 0.63f, 1f);

        if (active is { } current) {
            DrawHandles(context, current, handles);
        }

        foreach (var key in curve.Keys) {
            var point = ToScreen(key.Time, key.Value);
            var size = selection.Contains(key) ? 5f : 4f;

            context.FillRectangle(
                new Rectangle(point.X - size, point.Y - size, size * 2f, size * 2f),
                selection.Contains(key) ? keys : Color4.White,
                size
            );

            context.StrokeRectangle(
                new Rectangle(point.X - size, point.Y - size, size * 2f, size * 2f),
                keys,
                1.5f,
                BoxStyle.Rounded(CornerRadii.Uniform(size))
            );
        }
    }

    void DrawGrid(DrawContext context, Rectangle bounds) {
        if (TimeStep <= 0f || ValueStep <= 0f) {
            return;
        }

        grid.Clear();

        var step = TimeStep;

        while (step / View.Width * bounds.Width < 6f) {
            step *= 2f;

            if (step > 1e6f) {
                return;
            }
        }

        for (var time = MathF.Ceiling(View.X / step) * step; time < View.Right; time += step) {
            var x = ToScreen(time, 0f).X;
            grid.MoveTo(new Vector2(x, bounds.Top)).LineTo(new Vector2(x, bounds.Bottom));
        }

        var vertical = ValueStep;

        while (vertical / View.Height * bounds.Height < 6f) {
            vertical *= 2f;

            if (vertical > 1e6f) {
                return;
            }
        }

        for (var value = MathF.Ceiling(View.Y / vertical) * vertical; value < View.Bottom; value += vertical) {
            var y = ToScreen(0f, value).Y;
            grid.MoveTo(new Vector2(bounds.Left, y)).LineTo(new Vector2(bounds.Right, y));
        }

        context.Stroke(grid, Document.ColorOf(Style, gridColor) ?? new Color4(0f, 0f, 0f, 0.08f), 1f);
    }

    void DrawCurve(DrawContext context, Rectangle bounds) {
        if (curve.Keys.Count == 0) {
            return;
        }

        path.Clear();

        var columns = (int) MathF.Ceiling(bounds.Width);

        for (var i = 0; i <= columns; i++) {
            var x = bounds.X + i;
            var time = ToCurve(x, bounds.Y).X;
            var point = ToScreen(time, curve.Evaluate(time));

            if (i == 0) {
                path.MoveTo(point);
            } else {
                path.LineTo(point);
            }
        }

        context.Stroke(
            path,
            Document.ColorOf(Style, curveColor) ?? Document.ForegroundOf(this),
            2f,
            LineJoin.Round,
            LineCap.Round
        );
    }

    void DrawHandles(DrawContext context, CurveKey key, Color4 colour) {
        if (key.Mode is TangentMode.Auto or TangentMode.Linear or TangentMode.Constant) {
            return;
        }

        var centre = ToScreen(key.Time, key.Value);

        foreach (var outgoing in (ReadOnlySpan<bool>) [false, true]) {
            var end = HandlePoint(key, outgoing);

            path.Clear().MoveTo(centre).LineTo(end);
            context.Stroke(path, colour, 1f);

            context.FillRectangle(new Rectangle(end.X - 3f, end.Y - 3f, 6f, 6f), colour, 3f);
        }
    }

    /// <summary>Where a key's handle dot is, in document space.</summary>
    /// <param name="key">The key.</param>
    /// <param name="outgoing">Whether the one on the right.</param>
    /// <returns>The point.</returns>
    public Vector2 HandlePoint(CurveKey key, bool outgoing) {
        ArgumentNullException.ThrowIfNull(key);

        var centre = ToScreen(key.Time, key.Value);
        var slope = outgoing ? key.OutTangent : key.InTangent;

        // The slope is in curve units; the handle is drawn in pixels, so the direction has to be
        // converted through the view before it is normalised — otherwise a zoomed graph shows a
        // horizontal tangent as a diagonal.
        var bounds = Bounds;

        var direction = new Vector2(
            (outgoing ? 1f : -1f) / MathF.Max(1e-6f, View.Width) * bounds.Width,
            -(outgoing ? slope : -slope) / MathF.Max(1e-6f, View.Height) * bounds.Height
        );

        var length = MathF.Sqrt((direction.X * direction.X) + (direction.Y * direction.Y));

        return length <= 1e-6f
            ? centre
            : new Vector2(centre.X + (direction.X / length * HandleLength), centre.Y + (direction.Y / length * HandleLength));
    }

    // ── Input ────────────────────────────────────────────────────────────────

    void OnCurveChanged(AnimationCurve changed) {
        CurveChanged?.Invoke(this);
        Document.Invalidate();
    }

    void Pointed(PointerEvent args) {
        switch (args.Action) {
            case PointerAction.Pressed:
                Begin(args);
                break;

            case PointerAction.Moved when drag != CurveDrag.None:
                Track(args);
                break;

            case PointerAction.Released when drag != CurveDrag.None:
                drag = CurveDrag.None;
                Document.ReleasePointer();

                break;

            default:
                return;
        }

        args.Handled = true;
    }

    void Begin(PointerEvent args) {
        Document.Focus(this);

        if (args.Button is PointerButton.Middle or PointerButton.Secondary) {
            drag = CurveDrag.Pan;
            panStart = new Vector2(args.X, args.Y);
            panOrigin = View.Position;

            Document.CapturePointer(this);
            return;
        }

        if (args.Button != PointerButton.Primary) {
            return;
        }

        // The handles are tested before the keys, because a handle dragged all the way in sits on
        // top of its own key and would otherwise be unreachable.
        if (active is { } current && current.Mode is TangentMode.Free or TangentMode.Broken) {
            foreach (var outgoing in (ReadOnlySpan<bool>) [false, true]) {
                var point = HandlePoint(current, outgoing);

                if (Near(point, args.X, args.Y, 8f)) {
                    drag = outgoing ? CurveDrag.OutHandle : CurveDrag.InHandle;
                    Document.CapturePointer(this);

                    return;
                }
            }
        }

        if (KeyAt(args.X, args.Y) is { } key) {
            Select(key, args.Modifiers);

            drag = CurveDrag.Key;
            Document.CapturePointer(this);

            return;
        }

        Select(null, args.Modifiers);
    }

    void Track(PointerEvent args) {
        var point = ToCurve(args.X, args.Y);

        switch (drag) {
            case CurveDrag.Pan:
                var scale = new Vector2(
                    View.Width / MathF.Max(1f, Bounds.Width),
                    View.Height / MathF.Max(1f, Bounds.Height)
                );

                View = new Rectangle(
                    panOrigin.X - ((args.X - panStart.X) * scale.X),
                    panOrigin.Y + ((args.Y - panStart.Y) * scale.Y),
                    View.Width,
                    View.Height
                );

                Document.Invalidate();
                break;

            case CurveDrag.Key when active is { } key:
                var snapped = Snap(point);

                foreach (var selected in selection) {
                    if (ReferenceEquals(selected, key)) {
                        continue;
                    }

                    curve.Move(selected, selected.Time + (snapped.X - key.Time), selected.Value + (snapped.Y - key.Value));
                }

                curve.Move(key, snapped.X, snapped.Y);
                break;

            case CurveDrag.InHandle or CurveDrag.OutHandle when active is { } handled:
                Aim(handled, args, drag == CurveDrag.OutHandle);
                break;

            default:
                break;
        }
    }

    /// <remarks>
    ///     ⚠ <b>A free key's two handles move together and a broken one's do not.</b> That is the
    ///     whole difference between the two modes, and it lives here rather than in the model because
    ///     it is about what a drag means rather than about what a curve is.
    /// </remarks>
    void Aim(CurveKey key, PointerEvent args, bool outgoing) {
        var centre = ToScreen(key.Time, key.Value);
        var bounds = Bounds;

        var dx = (args.X - centre.X) / MathF.Max(1f, bounds.Width) * View.Width;
        var dy = (centre.Y - args.Y) / MathF.Max(1f, bounds.Height) * View.Height;

        if (MathF.Abs(dx) < 1e-6f) {
            return;
        }

        var slope = dy / MathF.Abs(dx) * (outgoing ? 1f : -1f) * (dx < 0f ? -1f : 1f);

        if (outgoing) {
            key.OutTangent = slope;
        } else {
            key.InTangent = slope;
        }

        if (key.Mode == TangentMode.Free) {
            key.InTangent = slope;
            key.OutTangent = slope;
        }

        curve.Touch();
    }

    void Tapped(TapEvent args) {
        if (args.Count != 2) {
            return;
        }

        if (KeyAt(args.X, args.Y) is { } key) {
            curve.Remove(key);

            selection.Remove(key);
            active = ReferenceEquals(active, key) ? null : active;

            SelectionChanged?.Invoke(this);
        } else {
            var point = Snap(ToCurve(args.X, args.Y));
            Select(curve.Add(point.X, point.Y), ModifierKeys.None);
        }

        args.Handled = true;
    }

    void Keyed(KeyEvent args) {
        if (args.Action != KeyAction.Pressed) {
            return;
        }

        switch (args.Key) {
            case InputKey.Delete or InputKey.Backspace when selection.Count > 0:
                foreach (var key in selection.ToArray()) {
                    curve.Remove(key);
                }

                selection.Clear();
                active = null;

                SelectionChanged?.Invoke(this);
                break;

            case InputKey.F:
                Frame();
                break;

            case InputKey.A when args.Modifiers.HasFlag(ModifierKeys.Control):
                selection.Clear();

                foreach (var key in curve.Keys) {
                    selection.Add(key);
                }

                active = curve.Keys.Count > 0 ? curve.Keys[0] : null;
                SelectionChanged?.Invoke(this);

                break;

            default:
                return;
        }

        args.Handled = true;
    }

    void Wheeled(WheelEvent args) {
        var before = ToCurve(args.X, args.Y);
        var factor = MathF.Exp(args.DeltaY * 0.0015f);

        View = new Rectangle(View.X, View.Y, View.Width * factor, View.Height * factor);

        var after = ToCurve(args.X, args.Y);

        View = new Rectangle(View.X + before.X - after.X, View.Y + before.Y - after.Y, View.Width, View.Height);

        Document.Invalidate();
        args.Handled = true;
    }

    void Select(CurveKey? key, ModifierKeys modifiers) {
        if (key is null) {
            if (selection.Count == 0) {
                return;
            }

            selection.Clear();
            active = null;

            SelectionChanged?.Invoke(this);
            return;
        }

        if (modifiers.HasFlag(ModifierKeys.Control) || modifiers.HasFlag(ModifierKeys.Shift)) {
            selection.Add(key);
        } else if (!selection.Contains(key)) {
            selection.Clear();
            selection.Add(key);
        }

        active = key;
        SelectionChanged?.Invoke(this);
    }

    Vector2 Snap(Vector2 point) =>
        SnapToGrid && TimeStep > 0f && ValueStep > 0f
            ? new Vector2(MathF.Round(point.X / TimeStep) * TimeStep, MathF.Round(point.Y / ValueStep) * ValueStep)
            : point;

    static bool Near(Vector2 point, float x, float y, float radius) =>
        ((point.X - x) * (point.X - x)) + ((point.Y - y) * (point.Y - y)) <= radius * radius;
}
