// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Vixen.Rendering;
using Vixen.Ui;
using Vixen.Ui.Controls.Advanced;

// `Viewport` is both a rectangle of the interface and a rectangle of a render target, and this file
// is where the two meet — so both names are in scope and neither can be the unqualified one.
using ViewportControl = Vixen.Ui.Controls.Advanced.Viewport;

namespace Vixen.Editor.SceneView;

/// <summary>How a drag with a given button and modifiers is read.</summary>
public enum NavigationAction {
    /// <summary>Nothing to do with the camera.</summary>
    None,

    /// <summary>Turn about the pivot.</summary>
    Orbit,

    /// <summary>Slide sideways and up.</summary>
    Pan,

    /// <summary>Move towards or away.</summary>
    Dolly,

    /// <summary>Drive the gizmo, or start a selection.</summary>
    Manipulate
}

/// <summary>One pane of the scene view: a camera, a gizmo, picking, and the control they live in.</summary>
/// <remarks>
///     <para>
///         <b>What joins the halves.</b> <c>Viewport</c> says where and how big in render pixels and
///         reports the input inside it, and deliberately knows nothing about rendering;
///         <c>RenderView</c> is what a frame is drawn from and knows nothing about interfaces. This is
///         where the one drives the other, and it is why neither of them had to.
///     </para>
///     <para>
///         <b>Several of these at once is the point.</b> A four-pane layout is four
///         <see cref="SceneViewport" />s with their own cameras, their own view modes and their own
///         render views — see <see cref="ViewportLayout" />, and see <c>ViewModes.ApplyTo</c> for the
///         one thing they must not share.
///     </para>
///     <para>
///         <b>The navigation mapping is a method, not a table of bindings.</b> Which button orbits is
///         the sort of thing every user wants different, and the way to give them that is the shell's
///         keymap over commands — not a second, parallel binding system inside the viewport. What is
///         here is one documented default: right or middle drags the camera, left drives the gizmo,
///         and Alt makes left orbit for people whose hands remember Maya.
///     </para>
/// </remarks>
public sealed class SceneViewport : IDisposable {
    readonly Selection<Vixen.Core.Entity> selection;
    bool disposed;

    /// <summary>The control the scene is drawn in.</summary>
    public ViewportControl Control { get; }

    /// <summary>Where the camera is looking.</summary>
    public EditorCamera Camera { get; } = new();

    /// <summary>The handles over the selection.</summary>
    public TransformGizmo Gizmo { get; } = new();

    /// <summary>The floor grid.</summary>
    public SceneGrid Grid { get; } = new();

    /// <summary>Which way the scene is being drawn.</summary>
    public ViewModes Modes { get; } = new();

    /// <summary>Where a drop would land.</summary>
    public ScenePlacement Placement { get; } = new();

    /// <summary>The view a frame is rendered from.</summary>
    public RenderView View { get; }

    /// <summary>The bookmarks this pane has saved.</summary>
    public IList<ViewBookmark> Bookmarks { get; } = [];

    /// <summary>Where a gizmo drag is recorded.</summary>
    public EditorDocument? Document { get; set; }

    /// <summary>What can answer "what is under this ray", for placement and surface snapping.</summary>
    public ISurfaceProbe? Surfaces { get; set; }

    /// <summary>Where the answers to picks are collected, once a device exists.</summary>
    /// <remarks>
    ///     Null until the host has a device, and picking simply does not answer until it is set —
    ///     which is what makes the whole of this type constructible in a test with no GPU.
    /// </remarks>
    public PickingBuffer? Picking { get; set; }

    /// <summary>Raised after a gizmo drag has been recorded.</summary>
    public event Action<SceneViewport>? Transformed;

    /// <summary>Raised when the pane asks for what is under a point. The host answers it.</summary>
    public event Action<SceneViewport, PickRequest>? PickRequested;

    /// <summary>Puts a scene into a viewport control.</summary>
    /// <param name="control">The control.</param>
    /// <param name="selection">What is selected, shared with the rest of the editor.</param>
    /// <param name="name">What the render view is called, for profiling.</param>
    public SceneViewport(ViewportControl control, Selection<Vixen.Core.Entity> selection, string name = "SceneView") {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(selection);

        Control = control;
        this.selection = selection;
        View = new(name);

        control.Dragged += OnDragged;
        control.Zoomed += OnZoomed;
    }

    /// <summary>Brings the render view up to date with the camera and the control's size.</summary>
    /// <remarks>
    ///     ⚠ <b>Called once a frame, after the layout pass</b>, for the reason <c>Viewport.Refresh</c>
    ///     gives: nothing announces that an element's box changed, so a splitter moving is something
    ///     the application notices rather than something the viewport is told.
    /// </remarks>
    public void Update() {
        Control.Refresh();
        Control.ViewRotation = Camera.Rotation;

        View.Position = Camera.Position;
        View.ViewProjection = Camera.ViewProjection(Control.AspectRatio);
    }

    /// <summary>Asks what is under a point, in render pixels.</summary>
    /// <param name="point">Where.</param>
    /// <param name="additive">Whether the answer extends the selection.</param>
    /// <returns>Whether the question could be asked.</returns>
    /// <remarks>
    ///     The answer arrives frames later — see <see cref="PickingBuffer" /> — and reaches the
    ///     selection through <see cref="Resolve" />. Splitting the two is what keeps a click off the
    ///     GPU's critical path.
    /// </remarks>
    public bool Pick(Vector2 point, bool additive = false) {
        if (Picking is not { } buffer || Control.RenderWidth <= 0 || Control.RenderHeight <= 0) {
            return false;
        }

        var x = Math.Clamp((int) point.X, 0, Control.RenderWidth - 1);
        var y = Math.Clamp((int) point.Y, 0, Control.RenderHeight - 1);
        var sequence = buffer.Request(x, y, additive);

        PickRequested?.Invoke(this, new(x, y, additive, sequence));
        return true;
    }

    /// <summary>Turns a pick that has come back into a selection change.</summary>
    /// <param name="result">The answer.</param>
    /// <param name="resolve">Turns an id into an entity. The host owns the mapping.</param>
    /// <remarks>
    ///     ⚠ <b>A miss clears the selection, and only when the pick was not additive.</b> Clicking
    ///     empty space deselects — which every editor does — but shift-clicking empty space must not,
    ///     because that is the miss at the end of a rubber-band that grabbed nothing.
    /// </remarks>
    public void Resolve(PickResult result, Func<uint, Vixen.Core.Entity> resolve) {
        ArgumentNullException.ThrowIfNull(resolve);

        if (!result.IsHit) {
            if (!result.Additive) {
                selection.Clear();
            }

            return;
        }

        var entity = resolve(result.Id);

        if (entity.IsNull) {
            return;
        }

        if (result.Additive) {
            selection.Toggle(entity);
        } else {
            selection.Set(entity);
        }
    }

    /// <summary>Points the camera at what is selected.</summary>
    /// <param name="bounds">What the selection occupies, or <see langword="null" /> to leave it.</param>
    public void FocusSelection(BoundingBox? bounds) {
        if (bounds is { } box) {
            Camera.Focus(box);
        }
    }

    /// <summary>Saves where the camera is.</summary>
    /// <param name="name">What to call it.</param>
    /// <returns>The bookmark.</returns>
    public ViewBookmark SaveBookmark(string name) {
        var bookmark = Camera.Bookmark(name);
        Bookmarks.Add(bookmark);

        return bookmark;
    }

    /// <summary>Goes back to a saved view.</summary>
    /// <param name="index">Which one.</param>
    /// <returns>Whether there was one.</returns>
    public bool RestoreBookmark(int index) {
        if ((uint) index >= (uint) Bookmarks.Count) {
            return false;
        }

        Camera.Restore(Bookmarks[index]);
        return true;
    }

    /// <summary>What a drag with this button and these modifiers means.</summary>
    /// <param name="button">Which button is down.</param>
    /// <param name="modifiers">What is held on the keyboard.</param>
    /// <returns>What to do.</returns>
    public static NavigationAction Interpret(PointerButton button, ModifierKeys modifiers) {
        var alt = (modifiers & ModifierKeys.Alt) != 0;
        var shift = (modifiers & ModifierKeys.Shift) != 0;

        return button switch {
            // Alt+left is the Maya convention and the one most people's hands know. It is checked
            // first, so holding Alt takes the left button away from the gizmo rather than fighting it.
            PointerButton.Primary when alt => NavigationAction.Orbit,
            PointerButton.Primary => NavigationAction.Manipulate,
            PointerButton.Middle when shift => NavigationAction.Pan,
            PointerButton.Middle => NavigationAction.Pan,
            PointerButton.Secondary when alt => NavigationAction.Dolly,
            PointerButton.Secondary => NavigationAction.Orbit,
            _ => NavigationAction.None
        };
    }

    /// <summary>Records which handle the pointer is over, so the viewport can highlight it.</summary>
    /// <param name="point">Where, in render pixels.</param>
    /// <returns>The handle, or <see cref="GizmoHandle.None" />.</returns>
    /// <remarks>
    ///     ⚠ <b>Not called while a drag is under way.</b> The pointer wanders off the handle it
    ///     grabbed within the first few pixels of any drag, and re-hit-testing would un-highlight the
    ///     thing being dragged — which reads as the gizmo having let go.
    /// </remarks>
    public GizmoHandle Hover(Vector2 point) {
        if (Gizmo.IsDragging) {
            return Gizmo.Active;
        }

        Gizmo.Attach(Targets());
        Gizmo.Hovered = Gizmo.HitTest(point, Camera, Control.RenderWidth, Control.RenderHeight);

        return Gizmo.Hovered;
    }

    /// <summary>Where something dropped at a point in this pane would land.</summary>
    /// <param name="point">Where, in render pixels.</param>
    /// <returns>The placement.</returns>
    /// <remarks>
    ///     What a drag from the project browser asks on every mouse-move, so that the preview is
    ///     where the object will actually be rather than where the pointer is.
    /// </remarks>
    public Placement Drop(Vector2 point) => Placement.Resolve(Ray(point), Surfaces);

    /// <summary>Starts a gizmo drag if a handle is under the point.</summary>
    /// <param name="point">Where, in render pixels.</param>
    /// <returns>Whether a drag started.</returns>
    public bool BeginManipulate(Vector2 point) {
        Gizmo.Attach(Targets());

        var handle = Gizmo.HitTest(point, Camera, Control.RenderWidth, Control.RenderHeight);

        if (handle == GizmoHandle.None) {
            return false;
        }

        return Gizmo.Begin(handle, Ray(point), Camera);
    }

    /// <summary>Ends a gizmo drag and records it.</summary>
    /// <returns>Whether anything was recorded.</returns>
    /// <remarks>
    ///     ⚠ <b>The state to undo to is taken before <c>End</c> clears it.</b> The gizmo drops what it
    ///     captured when the drag finishes, so reading it afterwards would build a command whose
    ///     "before" is empty — an undo that does nothing, which is worse than no undo at all.
    /// </remarks>
    public bool EndManipulate() {
        if (!Gizmo.IsDragging) {
            return false;
        }

        var captured = Gizmo.Captured();
        var targets = Gizmo.Targets;

        var command = new TransformTargetsCommand(
            Gizmo.Mode switch {
                GizmoMode.Rotate => "Rotate",
                GizmoMode.Scale => "Scale",
                _ => "Move"
            },
            targets,
            captured,
            Document
        );

        Gizmo.End();

        if (command.IsEmpty) {
            return false;
        }

        if (Document?.Stack is { } stack) {
            stack.Execute(command);
            stack.Seal();
        }

        Transformed?.Invoke(this);
        return true;
    }

    /// <summary>The ray under a point in this pane.</summary>
    /// <param name="point">Where, in render pixels.</param>
    /// <returns>The ray, in world space.</returns>
    public Ray Ray(Vector2 point) => Camera.PickingRay(point, Control.RenderWidth, Control.RenderHeight);

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        Control.Dragged -= OnDragged;
        Control.Zoomed -= OnZoomed;
    }

    IReadOnlyList<IGizmoTarget> Targets() => TargetsFactory?.Invoke() ?? [];

    /// <summary>Turns the shared selection into things the gizmo can move.</summary>
    /// <remarks>
    ///     A callback because this assembly cannot know which world the selected entities are in —
    ///     the editor may have several open, and a viewport is a view onto one of them. The host sets
    ///     it to <c>() =&gt; EntityGizmoTarget.For(world, selection)</c>.
    /// </remarks>
    public Func<IReadOnlyList<IGizmoTarget>>? TargetsFactory { get; set; }

    void OnDragged(ViewportControl control, ViewportDrag drag) {
        if (Gizmo.IsDragging) {
            Gizmo.Drag(Ray(new Vector2(drag.X, drag.Y)), Camera);
            return;
        }

        switch (Interpret(drag.Button, drag.Modifiers)) {
            case NavigationAction.Orbit:
                Camera.Orbit(drag.DeltaX, drag.DeltaY);
                break;

            case NavigationAction.Pan:
                Camera.Pan(drag.DeltaX, drag.DeltaY, control.RenderHeight);
                break;

            case NavigationAction.Dolly:
                // A vertical drag reads as "closer" and "further", which is the same sign convention
                // as the wheel — a dolly that went the other way from the wheel is the complaint
                // people report as "the mouse is inverted in the viewport".
                Camera.Zoom(drag.DeltaY * 0.05f);
                break;

            default:
                break;
        }
    }

    void OnZoomed(ViewportControl control, float notches) => Camera.Zoom(notches);
}
