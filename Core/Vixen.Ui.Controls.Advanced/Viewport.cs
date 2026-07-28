// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Input;
using Vixen.Ui.Styling;

namespace Vixen.Ui.Controls.Advanced;

/// <summary>A pointer doing something inside a viewport, in the viewport's own coordinates.</summary>
/// <remarks>
///     ⚠ <b>Deltas and totals both</b>, for the reason <c>DragEvent</c> carries both: an orbit
///     integrates the delta and a rubber-band selection wants the total, and summing the deltas does
///     not give the second.
/// </remarks>
/// <param name="Button">Which button is down.</param>
/// <param name="Modifiers">What is held on the keyboard.</param>
/// <param name="X">Where the pointer is, relative to the viewport's top-left, in render pixels.</param>
/// <param name="Y">Ditto.</param>
/// <param name="DeltaX">How far it moved since the last report, in render pixels.</param>
/// <param name="DeltaY">Ditto.</param>
/// <param name="TotalX">How far it has moved since the press.</param>
/// <param name="TotalY">Ditto.</param>
public readonly record struct ViewportDrag(
    PointerButton Button,
    ModifierKeys Modifiers,
    float X,
    float Y,
    float DeltaX,
    float DeltaY,
    float TotalX,
    float TotalY
);

/// <summary>The little axis cross in the corner.</summary>
/// <remarks>
///     Drawn rather than built, because it is three lines whose ends are a matrix multiply — and
///     because it has to be redrawn every frame the camera moves, which as elements would be six
///     inline styles a frame for a picture twenty pixels across.
/// </remarks>
public sealed class ViewportGizmo : UiElement {
    readonly PathBuilder path = new();

    /// <inheritdoc />
    protected override string TagName => "viewport-gizmo";

    /// <summary>The viewport it belongs to.</summary>
    public Viewport? Owner { get; internal set; }

    /// <inheritdoc />
    protected override void OnDraw(DrawContext context) {
        base.OnDraw(context);

        if (Owner is not { } viewport) {
            return;
        }

        var bounds = context.Bounds;
        var centre = bounds.Center;
        var radius = MathF.Min(bounds.Width, bounds.Height) * 0.4f;

        if (radius <= 0f) {
            return;
        }

        var basis = viewport.ViewRotation;

        // ⚠ Painted back to front by the axis's z, so the arm pointing away is drawn under the ones
        // pointing towards. Without it a gizmo reads as flat and the two ends of an axis look the
        // same, which is the one thing it exists to tell apart.
        Span<int> order = [0, 1, 2];

        for (var i = 0; i < 3; i++) {
            for (var j = i + 1; j < 3; j++) {
                if (Depth(basis, order[j]) < Depth(basis, order[i])) {
                    (order[i], order[j]) = (order[j], order[i]);
                }
            }
        }

        foreach (var axis in order) {
            var direction = Axis(basis, axis);
            var end = new Vector2(centre.X + (direction.X * radius), centre.Y - (direction.Y * radius));

            path.Clear().MoveTo(centre).LineTo(end);
            context.Stroke(path, viewport.AxisColor(axis), 2f, cap: LineCap.Round);

            context.FillRectangle(
                new Rectangle(end.X - 2.5f, end.Y - 2.5f, 5f, 5f),
                viewport.AxisColor(axis),
                2.5f
            );
        }
    }

    static Vector3 Axis(Matrix4x4 basis, int index) =>
        index switch {
            0 => new Vector3(basis.M11, basis.M12, basis.M13),
            1 => new Vector3(basis.M21, basis.M22, basis.M23),
            _ => new Vector3(basis.M31, basis.M32, basis.M33)
        };

    static float Depth(Matrix4x4 basis, int index) => Axis(basis, index).Z;
}

/// <summary>A rectangle of the interface that something else renders into.</summary>
/// <remarks>
///     <para>
///         <b>Three jobs, and none of them is drawing a scene.</b> It says <i>where</i> and
///         <i>how big</i> in render pixels; it says <i>when that changed</i>, which is when a render
///         target has to be recreated; and it takes the input that happens inside it and reports it
///         in the coordinates a camera controller wants. What goes in the rectangle belongs to
///         <c>Vixen.Rendering</c>, and this assembly is deliberately unaware of it.
///     </para>
///     <para>
///         ⚠ <b>The size is reported in <i>render</i> pixels, not layout pixels.</b> A layout is in
///         device-independent units and a render target is in real ones, so the two differ by the
///         display scale on every machine anybody actually uses. A viewport that handed a renderer
///         its layout size would produce a soft image on every retina display and nowhere else,
///         which is the bug nobody sees until somebody complains about a screenshot.
///     </para>
///     <para>
///         ⚠ <b>It draws a placeholder rather than the scene</b>, because the draw list has no
///         texture command yet — the same gap <c>Image</c> has, said again here because a viewport
///         is where it is most conspicuous. <see cref="RenderTarget" /> is the handle a host writes,
///         and a renderer composites over the interface until the command exists.
///     </para>
///     <para>
///         ⚠ <b>Capture does not lock the pointer</b> and cannot: a pointer lock is a platform
///         request, and this assembly has no platform. <see cref="PointerLockRequested" /> is what
///         an app head subscribes to, and until something does, capture is "every move comes here"
///         and nothing more.
///     </para>
/// </remarks>
public sealed partial class Viewport : Control {
    int placeholderColor;
    int xAxisColor;
    int yAxisColor;
    int zAxisColor;

    bool dragging;
    PointerButton held;
    Vector2 previous;
    Vector2 origin;

    int renderWidth;
    int renderHeight;

    /// <inheritdoc />
    protected override string TagName => "viewport";

    /// <inheritdoc />
    protected override bool AcceptsFocus => true;

    /// <summary>Where gizmos, overlays and heads-up text go.</summary>
    /// <remarks>
    ///     A real element over the scene rather than something the application draws into the render
    ///     target, so a selection outline is styled by the cascade and a toolbar over a viewport is
    ///     an ordinary toolbar. It takes no pointer events itself; the controls put in it do.
    /// </remarks>
    public UiElement Overlay { get; private set; } = null!;

    /// <summary>The axis cross in the corner.</summary>
    public ViewportGizmo Gizmo { get; private set; } = null!;

    /// <summary>How many render pixels one layout pixel is.</summary>
    /// <remarks>
    ///     Written by the app head from the surface's scale. One until something says otherwise,
    ///     which is right for a test and for every non-scaled display.
    /// </remarks>
    [UiProperty(Default = 1f, Coerce = nameof(ClampScale), Changed = nameof(OnScaleChanged))]
    public partial float RenderScale { get; set; }

    /// <summary>Whether the gizmo is shown.</summary>
    [UiProperty(Default = true, Changed = nameof(OnShowGizmoChanged))]
    public partial bool ShowGizmo { get; set; }

    /// <summary>The renderer's handle for whatever is being drawn here.</summary>
    /// <remarks>
    ///     Opaque on purpose. A texture id, a render-target index, a pointer — this assembly cannot
    ///     name the type without depending on the graphics layer, and the only thing it does with
    ///     the value is hand it back on <see cref="Resized" />.
    /// </remarks>
    public ulong RenderTarget { get; set; }

    /// <summary>How wide the render target should be, in render pixels.</summary>
    public int RenderWidth => renderWidth;

    /// <summary>How tall it should be.</summary>
    public int RenderHeight => renderHeight;

    /// <summary>The shape of it, for a projection matrix.</summary>
    public float AspectRatio => renderHeight <= 0 ? 1f : (float) renderWidth / renderHeight;

    /// <summary>Whether every pointer event is coming here.</summary>
    public bool IsCapturing { get; private set; }

    /// <summary>The camera's orientation, which is what the gizmo shows.</summary>
    /// <remarks>
    ///     A rotation rather than a full view matrix: the gizmo needs the basis and nothing else, and
    ///     a translation in it would move the cross off the corner it is pinned to.
    /// </remarks>
    public Matrix4x4 ViewRotation { get; set; } = Matrix4x4.Identity;

    /// <summary>Raised when the render size changes, which is when a target has to be recreated.</summary>
    /// <remarks>
    ///     ⚠ <b>Not raised for a zero size.</b> A collapsed dock panel, a hidden tab and the frame
    ///     before the first layout are all zero, and a renderer asked for a zero-by-zero target
    ///     either throws or silently makes one nothing can be drawn into.
    /// </remarks>
    public event Action<Viewport>? Resized;

    /// <summary>Raised for every pointer move with a button down inside the viewport.</summary>
    public event Action<Viewport, ViewportDrag>? Dragged;

    /// <summary>Raised for the wheel, in notches rather than pixels.</summary>
    public event Action<Viewport, float>? Zoomed;

    /// <summary>Raised when the viewport takes or gives up the pointer.</summary>
    public event Action<Viewport, bool>? CaptureChanged;

    /// <summary>Raised when the viewport would like the platform to lock the pointer, or unlock it.</summary>
    public event Action<Viewport, bool>? PointerLockRequested;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        placeholderColor = Document.PropertyId("--viewport-color");
        xAxisColor = Document.PropertyId("--axis-x-color");
        yAxisColor = Document.PropertyId("--axis-y-color");
        zAxisColor = Document.PropertyId("--axis-z-color");

        Overlay = Part("viewport-overlay");

        Gizmo = Part<ViewportGizmo>();
        Gizmo.Owner = this;

        AddHandler<PointerEvent>(static (element, args) => ((Viewport) element).Pointed(args));
        AddHandler<WheelEvent>(static (element, args) => ((Viewport) element).Wheeled(args));
    }

    /// <summary>Brings the render size up to date and raises <see cref="Resized" /> if it moved.</summary>
    /// <remarks>
    ///     ⚠ <b>Called by the application, once a frame, after the layout pass.</b> Nothing in
    ///     <c>UiDocument</c> announces that an element's box changed — the same gap
    ///     <c>ScrollView.Refresh</c> and <c>TreeView.Refresh</c> exist for — so a viewport cannot
    ///     find out that a splitter moved without being asked. It is cheap: two comparisons when
    ///     nothing happened.
    /// </remarks>
    /// <returns>Whether the size changed.</returns>
    public bool Refresh() {
        var width = (int) MathF.Round(Width * RenderScale);
        var height = (int) MathF.Round(Height * RenderScale);

        if (width == renderWidth && height == renderHeight) {
            return false;
        }

        renderWidth = width;
        renderHeight = height;

        if (width <= 0 || height <= 0) {
            return true;
        }

        Resized?.Invoke(this);
        return true;
    }

    /// <summary>Sends every pointer event here until it is released.</summary>
    /// <param name="lockPointer">Whether to ask the platform to hide and pin the cursor.</param>
    public void Capture(bool lockPointer = false) {
        if (IsCapturing) {
            return;
        }

        IsCapturing = true;

        Document.Focus(this);
        Document.CapturePointer(this);

        CaptureChanged?.Invoke(this, true);

        if (lockPointer) {
            PointerLockRequested?.Invoke(this, true);
        }
    }

    /// <summary>Stops.</summary>
    public void ReleaseCapture() {
        if (!IsCapturing) {
            return;
        }

        IsCapturing = false;
        dragging = false;

        Document.ReleasePointer();

        CaptureChanged?.Invoke(this, false);
        PointerLockRequested?.Invoke(this, false);
    }

    /// <summary>Where a point in the viewport is, in render pixels from its top-left.</summary>
    /// <param name="x">Its x, in document space.</param>
    /// <param name="y">Its y.</param>
    /// <returns>The point.</returns>
    public Vector2 ToRender(float x, float y) =>
        new((x - AbsoluteLeft) * RenderScale, (y - AbsoluteTop) * RenderScale);

    /// <summary>The colour of one of the gizmo's arms.</summary>
    /// <param name="axis">0 for x, 1 for y, 2 for z.</param>
    /// <returns>The colour.</returns>
    public Color4 AxisColor(int axis) =>
        axis switch {
            0 => Document.ColorOf(Style, xAxisColor) ?? new Color4(0.87f, 0.29f, 0.33f, 1f),
            1 => Document.ColorOf(Style, yAxisColor) ?? new Color4(0.42f, 0.75f, 0.31f, 1f),
            _ => Document.ColorOf(Style, zAxisColor) ?? new Color4(0.29f, 0.51f, 0.90f, 1f)
        };

    /// <inheritdoc />
    protected override void OnDraw(DrawContext context) {
        base.OnDraw(context);

        // The placeholder, and it is a placeholder: the draw list has no texture command, so what
        // ships until it does is a renderer compositing over the interface and this filling the hole
        // so a viewport that nothing is drawing into reads as empty rather than as broken.
        if (Document.ColorOf(Style, placeholderColor) is { } colour) {
            context.FillRectangle(context.Bounds, colour);
        }
    }

    static float ClampScale(float value) => MathF.Max(0.05f, value);

    void OnScaleChanged(float previous, float current) => Refresh();

    void OnShowGizmoChanged(bool previous, bool current) {
        if (current) {
            Gizmo.RemoveClass("hidden");
        } else {
            Gizmo.AddClass("hidden");
        }
    }

    void Pointed(PointerEvent args) {
        var point = ToRender(args.X, args.Y);

        switch (args.Action) {
            case PointerAction.Pressed:
                dragging = true;
                held = args.Button;

                previous = point;
                origin = point;

                Document.Focus(this);
                Document.CapturePointer(this);

                break;

            case PointerAction.Moved when dragging:
                Dragged?.Invoke(
                    this,
                    new ViewportDrag(
                        held,
                        args.Modifiers,
                        point.X,
                        point.Y,
                        point.X - previous.X,
                        point.Y - previous.Y,
                        point.X - origin.X,
                        point.Y - origin.Y
                    )
                );

                previous = point;
                break;

            case PointerAction.Released when dragging:
                dragging = false;

                // ⚠ A drag's own capture is released and an explicit `Capture` is not. They are two
                // different things — one is "this button press belongs to me", the other is "the
                // camera is being flown" — and releasing both here would drop a first-person mode on
                // the first click.
                if (!IsCapturing) {
                    Document.ReleasePointer();
                }

                break;

            default:
                return;
        }

        args.Handled = true;
    }

    void Wheeled(WheelEvent args) {
        Zoomed?.Invoke(this, args.DeltaY);
        args.Handled = true;
    }
}
