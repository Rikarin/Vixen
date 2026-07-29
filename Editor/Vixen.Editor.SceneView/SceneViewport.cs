// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Vixen.Input;
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

/// <summary>What an orbit swings around.</summary>
/// <remarks>
///     Blender's <i>Orbit Around Selection</i>, and it is a preference because both answers are right
///     for different work. The view's own pivot is predictable — the thing in the middle of the pane
///     stays in the middle — and the selection is what you want the moment you are working on
///     something that is not in the middle, which is most of the time.
/// </remarks>
public enum OrbitPivot {
    /// <summary>The camera's own pivot, which is what focus and pan move.</summary>
    View,

    /// <summary>Whatever is selected, falling back to the view's pivot when nothing is.</summary>
    Selection
}

/// <summary>Which way a viewport is being flown, as the keys that are down.</summary>
/// <remarks>
///     Flags rather than one direction, because holding two is the ordinary case: forward and right
///     at once is how anybody actually crosses a scene.
/// </remarks>
[Flags]
public enum FlyAxis {
    /// <summary>Nothing held.</summary>
    None = 0,

    /// <summary>Along the camera's forward.</summary>
    Forward = 1,

    /// <summary>Against it.</summary>
    Back = 2,

    /// <summary>Along the camera's −X.</summary>
    Left = 4,

    /// <summary>Along its +X.</summary>
    Right = 8,

    /// <summary>Straight down the world's up.</summary>
    Down = 16,

    /// <summary>Straight up it.</summary>
    Up = 32
}

/// <summary>Which keys fly the camera while the fly button is held.</summary>
/// <remarks>
///     ⚠ <b>Positions, not letters.</b> <c>InputKey</c> names the physical key by its US-QWERTY
///     legend, so this is the same block of six under the left hand on an AZERTY keyboard — where
///     <see cref="InputKey.Q" /> is the key printed <c>A</c>. A binding by typed character would move
///     the whole gesture across the keyboard for everyone who does not have a US layout.
/// </remarks>
/// <param name="Forward">Towards what the camera is looking at.</param>
/// <param name="Back">Away from it.</param>
/// <param name="Left">Sideways, along the camera's own basis.</param>
/// <param name="Right">Ditto.</param>
/// <param name="Down">Down the world's up, not the camera's — see <see cref="EditorCamera.Fly" />.</param>
/// <param name="Up">Ditto.</param>
public readonly record struct FlyBindings(
    InputKey Forward,
    InputKey Back,
    InputKey Left,
    InputKey Right,
    InputKey Down,
    InputKey Up
) {
    /// <summary>The six every 3D editor ships with.</summary>
    public static FlyBindings Wasdqe { get; } =
        new(InputKey.W, InputKey.S, InputKey.A, InputKey.D, InputKey.Q, InputKey.E);

    /// <summary>Which way a key flies, if it flies at all.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The axis, or <see cref="FlyAxis.None" />.</returns>
    public FlyAxis Axis(InputKey key) {
        if (key == InputKey.Unknown) {
            return FlyAxis.None;
        }

        return key == Forward ? FlyAxis.Forward
            : key == Back ? FlyAxis.Back
            : key == Left ? FlyAxis.Left
            : key == Right ? FlyAxis.Right
            : key == Down ? FlyAxis.Down
            : key == Up ? FlyAxis.Up
            : FlyAxis.None;
    }
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
///     <para>
///         ⚠ <b>Flight is the exception to that, and it has to be.</b> WASDQE while the right button
///         is held is a <i>held</i> gesture over six keys that already mean something else — W, E and
///         R are the gizmo modes and A is frame-all — and a keymap of chords over commands can
///         express neither half of that: it fires once on the press and it has no idea a mouse button
///         is down. So the keys are read here, only while <see cref="IsFlying" />, and consumed, so
///         the shell's own bindings cannot fire underneath them. It stays one gesture rather than a
///         binding system: <see cref="FlyKeys" /> is the whole of it, and it is settable.
///     </para>
/// </remarks>
public sealed class SceneViewport : IDisposable {
    /// <summary>The longest frame flight will integrate, in seconds.</summary>
    /// <remarks>
    ///     ⚠ <b>A stall is not travel.</b> A shader compile, a scene load or a window dragged
    ///     between displays produces one frame worth half a second, and multiplying that by the fly
    ///     speed puts the camera somewhere the user was not going — which reads as the editor having
    ///     lost the scene rather than as a hitch.
    /// </remarks>
    public const float MaximumFlyStep = 0.1f;

    readonly Selection<Vixen.Core.Entity> selection;
    FlyAxis held;
    bool fast;
    bool disposed;

    /// <summary>Where the pointer last was, in render pixels, for the gestures that need it.</summary>
    /// <remarks>
    ///     ⚠ <b>Remembered rather than asked for.</b> <c>Viewport.Zoomed</c> carries a scroll distance
    ///     and nothing else — it is the wheel's own event and a wheel has no position — so a zoom at
    ///     the pointer has to know where the pointer got to on the last move. The middle of the pane
    ///     is the honest answer before there has been one, which is what a wheel from a pointer that
    ///     entered the pane without moving inside it gets.
    /// </remarks>
    Vector2 pointer;

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

    /// <summary>What this pane draws.</summary>
    /// <remarks>
    ///     ⚠ <b>This is what the Show menu writes, and <c>SceneGrid.Enabled</c> is not.</b> The grid
    ///     keeps its own switch for a host that has no show flags — a preview pane, a test — but the
    ///     editor toggles exactly one of the two, because two writers to one idea is how a menu tick
    ///     and a panel toggle come to disagree. See <see cref="SceneShow" />.
    /// </remarks>
    public SceneShow Show { get; set; } = SceneShow.Default;

    /// <summary>What this pane cost last frame, for the readout in its corner.</summary>
    public ViewportStats Stats { get; } = new();

    /// <summary>Where a drop would land.</summary>
    public ScenePlacement Placement { get; } = new();

    /// <summary>The view a frame is rendered from.</summary>
    public RenderView View { get; }

    /// <summary>How many numbered bookmark slots a pane has.</summary>
    /// <remarks>
    ///     Nine, because the gesture is <c>Ctrl+1..9</c> to set and <c>1..9</c> to recall and a tenth
    ///     slot would have no key. An unbounded list of named views is a different feature and belongs
    ///     in the palette rather than on the number row.
    /// </remarks>
    public const int BookmarkSlots = 9;

    readonly ViewBookmark?[] bookmarks = new ViewBookmark?[BookmarkSlots];

    /// <summary>The nine slots, empty where nothing has been saved.</summary>
    public IReadOnlyList<ViewBookmark?> Bookmarks => bookmarks;

    /// <summary>Where a gizmo drag is recorded.</summary>
    public EditorDocument? Document { get; set; }

    /// <summary>How far the wheel scrolls in one notch, in device-independent pixels.</summary>
    /// <remarks>
    ///     ⚠ <b>The wheel arrives in pixels and the camera zooms in notches, and the conversion has
    ///     to happen somewhere.</b> <c>WheelEvent</c> carries a distance the backend has already
    ///     resolved from the device and the machine's settings — see its own remarks for why a
    ///     framework must not invent that number — while <see cref="EditorCamera.ZoomSpeed" /> is a
    ///     fraction of the distance <i>per notch</i>. Handing one straight to the other is a zoom of
    ///     forty-odd notches per click of the wheel, which is one notch to go from a room to a
    ///     continent and reads as a viewport that cannot be aimed. The default is the editor host's
    ///     own line height; a host whose backend resolves the wheel differently sets this.
    /// </remarks>
    public float WheelNotch { get; set; } = 48f;

    /// <summary>Which keys fly the camera while the fly button is held.</summary>
    public FlyBindings FlyKeys { get; set; } = FlyBindings.Wasdqe;

    /// <summary>What an orbit swings around.</summary>
    /// <remarks>
    ///     The view's own pivot by default, which is what every navigation in
    ///     <see cref="EditorCamera" /> is expressed against. <see cref="OrbitPivot.Selection" /> is
    ///     Blender's preference of the same name and reaches the selection through
    ///     <see cref="OrbitAnchor" />.
    /// </remarks>
    public OrbitPivot OrbitAround { get; set; } = OrbitPivot.View;

    /// <summary>Whether the wheel zooms at the pointer rather than at the middle of the pane.</summary>
    /// <remarks>
    ///     ⚠ <b>Off by default, as it is in Blender.</b> It is the better gesture for approaching
    ///     something and the worse one for framing: a zoom at the pointer drags the view sideways, so
    ///     somebody who is nudging the wheel while reading the middle of the pane watches the middle
    ///     of the pane wander off. Both camps are large, which is what makes it a setting.
    /// </remarks>
    public bool ZoomToCursor { get; set; }

    /// <summary>Where the selection is, for <see cref="OrbitPivot.Selection" />.</summary>
    /// <remarks>
    ///     ⚠ <b>Read from the gizmo rather than from the selection directly</b>, because the gizmo is
    ///     already the thing that answers "where is the selection" — it honours
    ///     <c>TransformGizmo.Pivot</c>, so orbiting around a multiple selection swings around whatever
    ///     the handles are sitting on rather than around a second, differently-computed middle. A pane
    ///     with nothing selected has no anchor and falls back to the view's pivot.
    /// </remarks>
    public Vector3? OrbitAnchor => Gizmo.Targets.Count > 0 ? Gizmo.Origin : null;

    /// <summary>Whether the fly button is down, so that the fly keys are live.</summary>
    /// <remarks>
    ///     ⚠ <b>A mode, and one that ends when the button does.</b> Flight that latched would leave
    ///     the six keys captured after the gesture people think of as "holding right-drag" is over,
    ///     and the next W would move the camera instead of choosing the translate gizmo.
    /// </remarks>
    public bool IsFlying { get; private set; }

    /// <summary>What can answer "what is under this ray", for placement and for snapping.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="ISceneProbe" /> rather than <c>ISurfaceProbe</c>, and the difference is the
    ///     exclusion.</b> A drop asks about a scene it is not yet part of; a <i>drag</i> asks about a
    ///     scene the dragged object is in the middle of, and a probe that could not be told to leave
    ///     it out would answer with the object's own surface for the whole of every drag — a snap that
    ///     never moves. Nothing is lost: <see cref="Drop" /> hands this straight to
    ///     <c>ScenePlacement.Resolve</c>, which takes the narrower interface.
    /// </remarks>
    public ISceneProbe? Surfaces { get; set; }

    /// <summary>Where the answers to picks are collected, once a device exists.</summary>
    /// <remarks>
    ///     Null until the host has a device, and picking simply does not answer until it is set —
    ///     which is what makes the whole of this type constructible in a test with no GPU.
    /// </remarks>
    public PickingBuffer? Picking { get; set; }

    /// <summary>What answers "which entity is under this ray" when there is no device to ask.</summary>
    /// <remarks>
    ///     ⚠ <b>The fallback, not the replacement.</b> <see cref="Picking" /> is checked first and is
    ///     the right answer for geometry a shader moved — see <c>ScenePicker</c> for why both exist.
    ///     Null leaves clicking in the viewport doing nothing, which is where the editor was.
    /// </remarks>
    public IScenePicker? Picker { get; set; }

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

        // ⚠ handledEventsToo, because Viewport marks every pointer event handled — it has to, or a
        // press inside a viewport would also be a press on whatever is behind it. This is the
        // listener AddHandler's own remarks describe: one that needs to know an event happened
        // rather than to compete for it.
        control.AddHandler<PointerEvent>(OnPointer, handledEventsToo: true);

        // ⚠ Not handledEventsToo, and the other way round from the pointer above: this one competes
        // for the key and wins it, because a W that flies must not also be a W that switches the
        // gizmo. The route from the focus outwards is what makes that work — the viewport is the
        // focused element while it is being flown, so it sees the key before the shell's dispatcher
        // on the root does.
        control.AddHandler<KeyEvent>(OnKey);

        // Losing the focus ends flight, because the keys that would end it are about to be going
        // somewhere else. Alt-tabbing away with W down and coming back to a camera still moving is
        // the version of this bug every first-person control has had once.
        control.AddHandler<FocusEvent>(OnFocus, handledEventsToo: true);
    }

    /// <summary>Advances the pane by a frame: flight, then the render view.</summary>
    /// <param name="delta">How long the last frame took.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Called once a frame, after the layout pass</b>, for the reason
    ///         <c>Viewport.Refresh</c> gives: nothing announces that an element's box changed, so a
    ///         splitter moving is something the application notices rather than something the
    ///         viewport is told.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Flight is integrated before the view is read, not after.</b> The other order
    ///         renders every frame of a flight from where the camera was at the end of the previous
    ///         one, which is a whole frame of lag on the only navigation that is continuous — and it
    ///         is invisible until somebody flies alongside a moving object and finds it swimming.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The gizmo is pointed at the selection here, once a frame, and not at the press
    ///         that grabs a handle.</b> <c>GizmoGeometry</c> draws what the gizmo is attached to, so
    ///         a gizmo attached only by <see cref="BeginManipulate" /> has no handles to draw until
    ///         somebody has already clicked — and the click that would attach them is the click that
    ///         hit-tests against nothing. That is a gizmo which appears one click late and never over
    ///         a selection made in the hierarchy panel, which reads exactly like handles that cannot
    ///         be dragged. It also keeps the targets honest against an undo, a delete or a script
    ///         that changed the selection since the last frame.
    ///     </para>
    /// </remarks>
    public void Update(TimeSpan delta) {
        Control.Refresh();
        Fly(delta);

        // Skipped mid-drag, which `Attach` refuses anyway: the targets a drag started on are the
        // ones the rest of it has to be applied to.
        if (!Gizmo.IsDragging) {
            Gizmo.Attach(Targets());
        }

        Control.ViewRotation = Camera.Rotation;

        View.Position = Camera.Position;
        View.ViewProjection = Camera.ViewProjection(Control.AspectRatio);
    }

    /// <summary>Moves the camera by whatever fly keys are held.</summary>
    /// <param name="delta">How long the last frame took.</param>
    /// <remarks>
    ///     Public and separate from <see cref="Update(TimeSpan)" /> because it is the half that has a
    ///     clock in it: a test drives it with a frame it chose rather than with whatever the machine
    ///     managed. Called for you by <see cref="Update(TimeSpan)" />.
    /// </remarks>
    public void Fly(TimeSpan delta) {
        if (!IsFlying || held == FlyAxis.None) {
            return;
        }

        var seconds = MathF.Min((float) delta.TotalSeconds, MaximumFlyStep);

        if (seconds <= 0f) {
            return;
        }

        var direction = new Vector3(
            Along(FlyAxis.Right, FlyAxis.Left),
            Along(FlyAxis.Up, FlyAxis.Down),
            Along(FlyAxis.Forward, FlyAxis.Back)
        );

        if (direction.IsZero) {
            // Both ends of an axis held, which is what happens the instant somebody changes their
            // mind mid-flight. Normalising it would be a division by zero and moving by it would be
            // a drift in whichever direction the rounding went.
            return;
        }

        // Normalised, so that forwards and sideways at once is not forty per cent faster than either
        // alone — the diagonal being the quickest way across a level is the oldest bug in flight.
        direction = Vector3.Normalize(direction);
        Camera.Fly(direction.X, direction.Y, direction.Z, seconds, fast);

        float Along(FlyAxis positive, FlyAxis negative) =>
            ((held & positive) != 0 ? 1f : 0f) - ((held & negative) != 0 ? 1f : 0f);
    }

    /// <summary>Asks what is under a point, in render pixels.</summary>
    /// <param name="point">Where.</param>
    /// <param name="additive">Whether the answer extends the selection.</param>
    /// <returns>Whether the question could be asked.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Two ways to answer it, and the one that needs a device is preferred.</b> With a
    ///         <see cref="Picking" /> buffer the question goes to the GPU and the answer arrives
    ///         frames later through <see cref="Resolve" />, which is what keeps a click off the
    ///         critical path and what is right for geometry a shader moved. With a
    ///         <see cref="Picker" /> it is a ray test and the answer is immediate.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>With neither, a click in the viewport selected nothing at all.</b> That was the
    ///         state the editor shipped in: the picking stage is written and tested, nothing sets a
    ///         buffer for it, and so the only way to select an entity was the hierarchy panel — and
    ///         clicking empty space did not even deselect. This returning <see langword="false" /> is
    ///         still that state, and it is now reachable only by a host that has set up neither.
    ///     </para>
    /// </remarks>
    public bool Pick(Vector2 point, bool additive = false) {
        if (Control.RenderWidth <= 0 || Control.RenderHeight <= 0) {
            return false;
        }

        if (Picking is { } buffer) {
            var x = Math.Clamp((int) point.X, 0, Control.RenderWidth - 1);
            var y = Math.Clamp((int) point.Y, 0, Control.RenderHeight - 1);
            var sequence = buffer.Request(x, y, additive);

            PickRequested?.Invoke(this, new(x, y, additive, sequence));
            return true;
        }

        if (Picker is not { } picker) {
            return false;
        }

        Select(picker.Under(Ray(point), Camera, Control.RenderWidth, Control.RenderHeight), additive);
        return true;
    }

    /// <summary>Turns a pick that has come back into a selection change.</summary>
    /// <param name="result">The answer.</param>
    /// <param name="resolve">Turns an id into an entity. The host owns the mapping.</param>
    public void Resolve(PickResult result, Func<uint, Vixen.Core.Entity> resolve) {
        ArgumentNullException.ThrowIfNull(resolve);

        Select(result.IsHit ? resolve(result.Id) : Vixen.Core.Entity.Null, result.Additive);
    }

    /// <summary>Puts an entity in the selection, or clears it.</summary>
    /// <param name="entity">What was picked, or <see cref="Vixen.Core.Entity.Null" /> for a miss.</param>
    /// <param name="additive">Whether the answer extends the selection rather than replacing it.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A miss clears the selection, and only when the pick was not additive.</b> Clicking
    ///         empty space deselects — which every editor does — but shift-clicking empty space must
    ///         not, because that is the miss at the end of a rubber-band that grabbed nothing.
    ///     </para>
    ///     <para>
    ///         <b>Additive <i>toggles</i> rather than adds</b>, so the same modifier that extends a
    ///         selection is the one that takes something back out of it. Two gestures for the two
    ///         halves of one idea is what makes people click an already-selected object and wonder why
    ///         nothing happened.
    ///     </para>
    /// </remarks>
    public void Select(Vixen.Core.Entity entity, bool additive) {
        if (entity.IsNull) {
            if (!additive) {
                selection.Clear();
            }

            return;
        }

        if (additive) {
            selection.Toggle(entity);
        } else {
            selection.Set(entity);
        }
    }

    /// <summary>The rubber-band being dragged, or <see langword="null" />.</summary>
    /// <remarks>
    ///     What the overlay draws. Read once a frame rather than raised as an event, because it
    ///     changes on every pointer move and an event per move would be a subscription the drawing
    ///     already has in the form of "it is redrawn every frame anyway".
    /// </remarks>
    public Marquee? Selecting { get; private set; }

    /// <summary>Raised when a band has taken a set of entities. The host owns the selection.</summary>
    /// <remarks>
    ///     ⚠ <b>The pane cannot resolve a band on its own and must not pretend to.</b>
    ///     <see cref="Select" /> takes one entity because a click has one answer; a band has a list,
    ///     and turning a list into a selection is <c>Selection&lt;T&gt;.Set</c> or a run of
    ///     <c>Add</c> — which is the caller's decision about whether shift extends or replaces. This
    ///     type does the geometry and hands the answer over.
    /// </remarks>
    public event Action<SceneViewport, IReadOnlyList<Vixen.Core.Entity>, bool>? Banded;

    /// <summary>Starts a rubber-band at a point.</summary>
    /// <param name="point">Where, in render pixels.</param>
    /// <param name="additive">Whether what it takes extends the selection.</param>
    public void BeginSelect(Vector2 point, bool additive) => Selecting = new Marquee(point, point, additive);

    /// <summary>Drags the band's free corner.</summary>
    /// <param name="point">Where the pointer is, in render pixels.</param>
    public void DragSelect(Vector2 point) {
        if (Selecting is { } band) {
            Selecting = band.To(point);
        }
    }

    /// <summary>Ends a band and selects what it took.</summary>
    /// <returns>Whether anything was resolved.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A band too small to be a band is a click, and is answered by the picker.</b>
    ///         Every press in empty space starts one of these, because the alternative is deciding
    ///         whether a drag has begun before the pointer has moved. So the release is where the two
    ///         gestures part, and the click path is exactly <see cref="Pick" /> — not a
    ///         one-pixel band, which would take whatever happened to project into it rather than what
    ///         is under the pointer.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An empty band that is not additive clears the selection.</b> That is the same rule
    ///         a miss follows and for the same reason: dragging a box round nothing is how people
    ///         deselect without hunting for empty space to click on.
    ///     </para>
    /// </remarks>
    public bool EndSelect() {
        if (Selecting is not { } band) {
            return false;
        }

        Selecting = null;

        if (!band.IsBand) {
            return Pick(band.Anchor, band.Additive);
        }

        if (Picker is not { } picker) {
            return false;
        }

        List<Vixen.Core.Entity> taken = [];
        picker.Within(band, Camera, Control.RenderWidth, Control.RenderHeight, taken);

        if (taken.Count == 0 && !band.Additive) {
            selection.Clear();
        } else if (band.Additive) {
            foreach (var entity in taken) {
                selection.Add(entity);
            }
        } else {
            selection.Set(taken);
        }

        Banded?.Invoke(this, taken, band.Additive);
        return true;
    }

    /// <summary>Abandons a band without selecting anything.</summary>
    /// <returns>Whether there was one.</returns>
    /// <remarks>
    ///     Escape, and losing the focus. A band left running because the pointer went somewhere else
    ///     is one that resolves on the next click anywhere in the pane, which selects a rectangle
    ///     nobody drew.
    /// </remarks>
    public bool CancelSelect() {
        if (Selecting is null) {
            return false;
        }

        Selecting = null;
        return true;
    }

    /// <summary>Points the camera at what is selected.</summary>
    /// <param name="bounds">What the selection occupies, or <see langword="null" /> to leave it.</param>
    public void FocusSelection(BoundingBox? bounds) {
        if (bounds is { } box) {
            Camera.Focus(box);
        }
    }

    /// <summary>Saves where the camera is into one of the nine slots.</summary>
    /// <param name="slot">Which, from zero to <see cref="BookmarkSlots" /> minus one.</param>
    /// <returns>The bookmark, or <see langword="null" /> for a slot that does not exist.</returns>
    /// <remarks>
    ///     ⚠ <b>It overwrites without asking, which is what every editor's numbered bookmarks do.</b>
    ///     The gesture is "put the view I am in on key three", and a prompt would make it two
    ///     gestures; the previous view is a camera position rather than work, and recovering it is
    ///     pressing the key that saved it again from where you were.
    /// </remarks>
    public ViewBookmark? SaveBookmark(int slot) {
        if ((uint) slot >= (uint) BookmarkSlots) {
            return null;
        }

        var bookmark = Camera.Bookmark("View " + (slot + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        bookmarks[slot] = bookmark;

        return bookmark;
    }

    /// <summary>Goes back to a saved view.</summary>
    /// <param name="slot">Which one.</param>
    /// <returns>Whether there was one.</returns>
    public bool RestoreBookmark(int slot) {
        if ((uint) slot >= (uint) BookmarkSlots || bookmarks[slot] is not { } bookmark) {
            return false;
        }

        Camera.Restore(bookmark);
        return true;
    }

    /// <summary>Whether a slot has a view in it.</summary>
    /// <param name="slot">Which one.</param>
    /// <returns>Whether it does.</returns>
    public bool HasBookmark(int slot) => (uint) slot < (uint) BookmarkSlots && bookmarks[slot] is not null;

    /// <summary>What a drag with this button and these modifiers means.</summary>
    /// <param name="button">Which button is down.</param>
    /// <param name="modifiers">What is held on the keyboard.</param>
    /// <returns>What to do.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Blender's middle-button set, plus Maya's Alt chords, plus the right button that
    ///         flies.</b> The middle button orbits, shift pans and control dollies — which is the
    ///         gesture most people bring with them and the one the whole rest of this type is written
    ///         against. Alt with left, middle and right is the same three in Maya's spelling, and the
    ///         two sets do not collide because Blender's use no modifier where Maya's use Alt.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The middle button used to pan whatever was held with it</b>, so shift+middle and
    ///         middle were one branch written as two and there was no orbit on the middle button at
    ///         all. Somebody arriving from Blender found the button that turns the view slides it
    ///         instead, and the only orbit was on the right button — which is also the one that
    ///         captures WASDQE, so trying it started a flight.
    ///     </para>
    ///     <para>
    ///         Alt is checked before the plain button on the left, so holding it takes the left button
    ///         away from the gizmo rather than fighting it.
    ///     </para>
    /// </remarks>
    public static NavigationAction Interpret(PointerButton button, ModifierKeys modifiers) {
        var alt = (modifiers & ModifierKeys.Alt) != 0;
        var shift = (modifiers & ModifierKeys.Shift) != 0;
        var control = (modifiers & ModifierKeys.Control) != 0;

        return button switch {
            PointerButton.Primary when alt => NavigationAction.Orbit,
            PointerButton.Primary => NavigationAction.Manipulate,

            // ⚠ Control before shift. Both held is one gesture and it has to resolve to one action;
            // control wins because a dolly is the harder of the two to reach any other way.
            PointerButton.Middle when control => NavigationAction.Dolly,
            PointerButton.Middle when shift || alt => NavigationAction.Pan,
            PointerButton.Middle => NavigationAction.Orbit,

            PointerButton.Secondary when alt => NavigationAction.Dolly,
            PointerButton.Secondary => NavigationAction.Orbit,
            _ => NavigationAction.None
        };
    }

    /// <summary>What a wheel event is worth to the camera.</summary>
    /// <param name="delta">How far it scrolled, in device-independent pixels, positive downwards.</param>
    /// <param name="pixelsPerNotch">What <see cref="WheelNotch" /> says a notch is worth.</param>
    /// <returns>Notches, positive meaning closer.</returns>
    /// <remarks>
    ///     ⚠ <b>Negated.</b> The wheel's positive direction is towards the content's end — down a
    ///     document — and pushing the wheel away from you moves the camera in, in every 3D view there
    ///     is. They are the same gesture with opposite signs, which is exactly the sort of thing that
    ///     survives review by being written down.
    /// </remarks>
    public static float Notches(float delta, float pixelsPerNotch) =>
        pixelsPerNotch > 0f ? -delta / pixelsPerNotch : -delta;

    /// <summary>Zooms by one wheel event's worth of scrolling.</summary>
    /// <param name="delta">How far it scrolled, in device-independent pixels, positive downwards.</param>
    public void Wheel(float delta) => Camera.Zoom(Notches(delta, WheelNotch));

    /// <summary>Zooms by one wheel event's worth of scrolling, at a point in the pane.</summary>
    /// <param name="delta">How far it scrolled, in device-independent pixels, positive downwards.</param>
    /// <param name="point">Where the pointer is, in render pixels.</param>
    /// <remarks>
    ///     Honours <see cref="ZoomToCursor" />: with it off this is <see cref="Wheel(float)" />, and
    ///     the point is read and discarded rather than the caller having to know which it wants.
    /// </remarks>
    public void Wheel(float delta, Vector2 point) {
        var notches = Notches(delta, WheelNotch);

        if (!ZoomToCursor) {
            Camera.Zoom(notches);
            return;
        }

        Camera.ZoomTowards(Camera.OnPivotPlane(Ray(point)), notches);
    }

    /// <summary>Turns the camera, honouring <see cref="OrbitAround" />.</summary>
    /// <param name="deltaX">How far the pointer moved horizontally, in render pixels.</param>
    /// <param name="deltaY">How far it moved vertically, positive downwards.</param>
    public void Orbit(float deltaX, float deltaY) {
        if (OrbitAround == OrbitPivot.Selection && OrbitAnchor is { } anchor) {
            Camera.OrbitAround(anchor, deltaX, deltaY);
            return;
        }

        Camera.Orbit(deltaX, deltaY);
    }

    /// <summary>Whether holding this button also flies the camera.</summary>
    /// <param name="button">Which button.</param>
    /// <returns>Whether the fly keys are live while it is down.</returns>
    /// <remarks>
    ///     The right button, which is the one that already orbits — and flying <i>is</i> orbiting
    ///     from where you are, which is what makes the pair one gesture rather than two modes. The
    ///     modifiers are deliberately not asked about: Alt turns the same drag into a dolly, and
    ///     somebody who is flying does not expect the keyboard to stop working because of it.
    /// </remarks>
    public static bool Flies(PointerButton button) => button == PointerButton.Secondary;

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

                // The combined gizmo's middle box is the uniform scale handle, so the undo entry has
                // to say what the drag did rather than what the tool is called.
                GizmoMode.Transform when Gizmo.Active == GizmoHandle.Uniform => "Scale",
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

    /// <summary>Turns a move into a highlight, a press into a grab and a release into a recorded drag.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Only the primary button grabs, and only when it did not start a camera move.</b>
    ///         Alt+left orbits, and a press that also grabbed a handle would move the object the
    ///         camera was supposed to be swinging around.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A move with nothing held carries <see cref="PointerButton.None" />, which is why
    ///         the hover test is before the primary-button check rather than inside it.</b> Nothing
    ///         else in the editor asks what is under the pointer, so without this the handle the
    ///         cursor is over never lights up and the only way to find out whether a press will grab
    ///         an arm is to press and see what moves.
    ///     </para>
    /// </remarks>
    void OnPointer(UiElement element, PointerEvent args) {
        if (Flies(args.Button) && args.Action is PointerAction.Pressed or PointerAction.Released) {
            Flying(args.Action == PointerAction.Pressed);
        }

        var point = Control.ToRender(args.X, args.Y);
        pointer = point;

        if (args.Action == PointerAction.Moved && args.Button == PointerButton.None) {
            Hover(point);
            return;
        }

        if (args.Button != PointerButton.Primary) {
            return;
        }

        switch (args.Action) {
            case PointerAction.Pressed
                when Interpret(args.Button, args.Modifiers) == NavigationAction.Manipulate:
                if (!BeginManipulate(point)) {
                    // ⚠ Nothing under the pointer, so the press starts a rubber-band rather than
                    // picking outright. Which of the two gestures it is cannot be known yet — a click
                    // and a band begin identically — so the band is started every time and the
                    // release decides, which is what `EndSelect` is for.
                    BeginSelect(point, (args.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) != 0);
                }

                break;

            case PointerAction.Released when Gizmo.IsDragging:
                EndManipulate();
                break;

            case PointerAction.Released when Selecting is not null:
                EndSelect();
                break;

            default:
                break;
        }
    }

    void OnDragged(ViewportControl control, ViewportDrag drag) {
        if (Gizmo.IsDragging) {
            var at = new Vector2(drag.X, drag.Y);

            // ⚠ Written before the drag is applied, not after. `Move` reads it, and a snap point one
            // frame behind is a drag that lags the pointer by a frame only while snapping — which
            // reads as the snap being sticky rather than as an ordering mistake.
            Gizmo.SnapTo = SnapPoint(at);
            Gizmo.Drag(Ray(at), Camera);

            return;
        }

        // ⚠ Before the navigation switch, not inside its Manipulate case. A band is dragged with the
        // left button and Alt+left orbits — so a band that was started and then had Alt pressed
        // during it would silently become an orbit, leaving a rectangle on screen that nothing is
        // updating. The band owns the drag until it is released.
        if (Selecting is not null) {
            DragSelect(new Vector2(drag.X, drag.Y));
            return;
        }

        switch (Interpret(drag.Button, drag.Modifiers)) {
            case NavigationAction.Orbit:
                Orbit(drag.DeltaX, drag.DeltaY);
                break;

            case NavigationAction.Pan:
                Camera.Pan(drag.DeltaX, drag.DeltaY, control.RenderHeight);
                break;

            case NavigationAction.Dolly:
                // Pushing away is closer and pulling back is further, which is the same sign
                // convention the wheel gets through Notches — a dolly that went the other way from
                // the wheel is the complaint people report as "the mouse is inverted in the
                // viewport". Twenty render pixels to the notch.
                Camera.Zoom(Notches(drag.DeltaY, 20f));
                break;

            default:
                break;
        }
    }

    /// <summary>Where the scene says a translate drag should land, or <see langword="null" />.</summary>
    /// <param name="point">Where the pointer is, in render pixels.</param>
    /// <returns>The point, in world space.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Vertex first, then surface.</b> Both can be on at once and they are not the same
    ///         request: a vertex snap is "on that corner" and only answers when there is a corner
    ///         within reach, so falling through to the surface when it does not is what makes holding
    ///         both a strictly better drag rather than a mode switch.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The selection is excluded, not the gizmo's targets.</b> They are the same list —
    ///         the targets are built from the selection once a frame — and the selection is the one
    ///         expressed in entities, which is what a probe over a world can compare against.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Only a translate.</b> A rotation or a scale has no position to land on, and the
    ///         uniform box is a scale wearing a translate gizmo's clothes.
    ///     </para>
    /// </remarks>
    Vector3? SnapPoint(Vector2 point) {
        if (Gizmo.Mode is not (GizmoMode.Translate or GizmoMode.Transform)
            || Gizmo.Active == GizmoHandle.Uniform
            || Surfaces is not { } probe) {
            return null;
        }

        var snap = Gizmo.Snap;

        if (snap.SnapToVertex
            && probe.TryNearestVertex(
                point,
                Camera,
                Control.RenderWidth,
                Control.RenderHeight,
                snap.VertexRadius,
                selection.Items,
                out var vertex
            )) {
            return vertex;
        }

        return snap.SnapToSurface && probe.Raycast(Ray(point), selection.Items, out var hit) ? hit.Point : null;
    }

    void OnZoomed(ViewportControl control, float delta) =>
        Wheel(delta, pointer.IsZero ? Middle(control) : pointer);

    static Vector2 Middle(ViewportControl control) => new(control.RenderWidth * 0.5f, control.RenderHeight * 0.5f);

    /// <summary>Tracks which fly keys are down, and takes them from everything else.</summary>
    /// <remarks>
    ///     ⚠ <b>Auto-repeat is left alone rather than filtered.</b> A held key repeats and each
    ///     repeat sets a bit that is already set; the release is what clears it. Everything that
    ///     acts <i>on</i> a press — the shell's dispatcher — has to ignore repeats, and a state that
    ///     is a set of held keys does not.
    /// </remarks>
    void OnKey(UiElement element, KeyEvent args) {
        // ⚠ Escape before flight, and it is a drag being cancelled rather than a command. `Cancel`
        // puts the targets back at once instead of pushing a command to be rolled back, so the
        // viewport is redrawn from the model on the frame the key was pressed — see its own remarks.
        // A drag with no way out is one people finish by dragging back to roughly where they started.
        // ⚠ And it cancels a rubber-band on the same terms, before flight and before the shell. A
        // band abandoned with Escape has to stop being drawn on the frame the key was pressed, for
        // the same reason a cancelled drag does — and a band that survived Escape resolves on the
        // next release anywhere in the pane, selecting a rectangle nobody drew.
        if (args is { Key: InputKey.Escape, Action: KeyAction.Pressed } && (Gizmo.Cancel() | CancelSelect())) {
            args.Handled = true;
            return;
        }

        if (!IsFlying) {
            return;
        }

        // Shift is read off whichever key event carries it, and off its own press directly: the
        // modifier state is on every event, but somebody who presses shift while already holding W
        // gets no other event until they let go of something.
        fast = args.Key is InputKey.LeftShift or InputKey.RightShift
            ? args.Action == KeyAction.Pressed
            : (args.Modifiers & ModifierKeys.Shift) != 0;

        if (FlyKeys.Axis(args.Key) is var axis && axis == FlyAxis.None) {
            return;
        }

        if (args.Action == KeyAction.Pressed) {
            held |= axis;
        } else {
            held &= ~axis;
        }

        // ⚠ Consumed. W, E and R are the gizmo modes and A is frame-all, so a flight that let its
        // keys through would swap the tool and reframe the scene on the way past — and the user
        // would find out about it when they let go of the mouse.
        args.Handled = true;
    }

    void OnFocus(UiElement element, FocusEvent args) {
        if (!args.Gained) {
            Flying(false);
            CancelSelect();
        }
    }

    /// <summary>Enters or leaves fly mode.</summary>
    /// <remarks>
    ///     ⚠ <b>The held keys are dropped on the way out rather than waited for.</b> Most people
    ///     release the mouse before the keyboard, so the release of W arrives when the viewport is no
    ///     longer listening — and a bit left set is a camera that sets off by itself the next time
    ///     the button goes down.
    /// </remarks>
    void Flying(bool value) {
        IsFlying = value;

        if (!value) {
            held = FlyAxis.None;
            fast = false;
        }
    }
}
