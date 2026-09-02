// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Engine.Transforms;
using Vixen.Input;
using Vixen.Rendering.Water;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Water;

namespace Vixen.Editor.Water;

/// <summary>The viewport mode water is authored in.</summary>
/// <remarks>
///     <para>
///         <b>One mode, and most of the time not even that</b> —
///         [35 § One mode, not two](../../docs/plan/35-water.md#one-mode-not-two). Doc 31 needed a
///         sculpt mode and a foliage mode because each owns the viewport and has an incompatible idea
///         of what a click means. Placing a lake is placing an <em>entity</em> and editing its shape
///         is editing a <em>spline</em>, and the editor already does both — so this mode exists for
///         the three things that are neither.
///     </para>
///     <para>
///         <b>Draw</b> lays a body's curve by clicking points on the ground, at the ground's own
///         height, closing it for a lake or an ocean and leaving it open for a river. It is the one
///         gesture <c>SplineEdit</c> does not already serve. <b>Profile</b> drags the width handles on
///         each side of a river and the depth handle down, which is Unreal's three viewport
///         visualisations and the reason its river authoring is good. <b>Preview</b> toggles the
///         reserved layer's contribution, so an author can see what the water did to the ground.
///     </para>
///     <para>
///         ⚠ <b>Escape cancels the draw and does not leave the mode</b>, which is <c>FoliageMode</c>'s
///         rule: a half-laid curve belongs to a gesture, and a gesture that survived a mode switch
///         would be committed by the next click in a mode that does not know what it is.
///     </para>
/// </remarks>
public sealed class WaterMode : IEditorMode, IViewportInput {
    /// <summary>What the mode is called, everywhere an id is wanted.</summary>
    public const string ModeId = "water";

    /// <summary>The command context the mode claims while it is active.</summary>
    public const string WaterContext = "water";

    /// <summary>What the panel the mode opens is registered as.</summary>
    public const string PanelId = "water.panel";

    EditorShell? shell;

    /// <summary>The pane the handles are currently drawn in, or null for none.</summary>
    SceneViewport? hovered;

    /// <summary>The body the Profile tool is aimed at, refreshed as the pointer moves.</summary>
    /// <remarks>
    ///     Cached rather than recomputed inside <see cref="SceneViewport.Cursor" />, which is read
    ///     every frame and must not allocate — see that property's remarks.
    /// </remarks>
    (Vixen.Core.Entity Entity, WaterBodyComponent Component, Spline Curve)? aimed;

    /// <summary>What the held handle's body looked like before the drag began.</summary>
    WaterBodyComponent profiledBefore;

    /// <summary>And its profile, which is what <see cref="WaterEdit.Drag" /> is measured against.</summary>
    WaterProfilePoint grabbedProfile;

    /// <summary>Where the held handle was when it was grabbed, in world space.</summary>
    Vector3 grabbedAt;

    /// <summary>And which way it may slide.</summary>
    Vector3 grabbedAxis;

    /// <inheritdoc />
    public string Id => ModeId;

    /// <inheritdoc />
    public StringId Title { get; } = new("editor.mode.water", "Water");

    /// <inheritdoc />
    /// <remarks>None, so the mode bar draws the word — <c>FoliageMode.Icon</c>'s reason.</remarks>
    public PathBuilder? Icon => null;

    /// <inheritdoc />
    public string? Context => WaterContext;

    /// <inheritdoc />
    public string? Panel => PanelId;

    /// <inheritdoc />
    public IReadOnlyList<ToolbarEntry> Toolbar { get; } = [
        new ToolbarGroup([.. Tools.Select(ToolCommand)])
    ];

    /// <summary>The three tools, in the order the strip lists them.</summary>
    public static IReadOnlyList<WaterTool> Tools { get; } = [
        WaterTool.Draw,
        WaterTool.Profile,
        WaterTool.Preview
    ];

    /// <summary>How many digits the mode claims.</summary>
    /// <remarks>
    ///     Three, and a digit past the third does nothing. A slot command means "the third tool",
    ///     which is what the design sentence means, and the named commands keep the words the palette
    ///     is searched with — <c>FoliageMode.SlotCount</c>'s arrangement.
    /// </remarks>
    public const int SlotCount = 3;

    /// <summary>The editing state the mode drives.</summary>
    public WaterEdit Editing { get; } = new();

    /// <summary>The document a committed gesture goes onto, or null while the mode drives none.</summary>
    public SceneDocument? Document { get; set; }

    /// <summary>What a new body is created with.</summary>
    public WaterBodySettings Body { get; } = new();

    /// <summary>And what a new zone is.</summary>
    public WaterZoneSettings Zone { get; } = new();

    /// <summary>How far a pointer ray looks for a surface, in metres.</summary>
    public float Reach { get; set; } = 100_000f;

    /// <summary>Where the ground is, for a ray that meets no surface.</summary>
    /// <remarks>
    ///     ⚠ <b>A plane rather than a refusal</b>, on <c>FoliageMode.GroundHeight</c>'s reasoning: a
    ///     point aimed past the terrain has to land somewhere or the tool reads as broken at the edges
    ///     of a level, and an ocean is drawn exactly there.
    /// </remarks>
    public float GroundHeight { get; set; }

    /// <summary>What a body's spline name means, or null while nothing can say.</summary>
    /// <remarks>
    ///     ⚠ <b>The Profile tool cannot work without it, and it is a seam rather than a reference to
    ///     the asset database</b> — <c>IWaterScene</c>'s own argument. A handle sits on the curve, the
    ///     curve is a name in a component, and turning a name into geometry means reading a file. What
    ///     supplies one here is <c>WaterModule.WaterScene</c>, which is the same object the viewport
    ///     draws the water from — so the handles are on the surface the author is looking at rather
    ///     than on a second reading of the same file.
    /// </remarks>
    public IWaterScene? Curves { get; set; }

    /// <summary>How near a profile handle a click has to be to grab it, in render pixels.</summary>
    /// <remarks>
    ///     ⚠ <b>Pixels rather than metres</b>, on <c>TransformGizmo.GrabRadius</c>'s terms: the same
    ///     half-width is forty pixels across on a canal and one on an ocean, so a tolerance in metres
    ///     is a handle that cannot be missed at one scale and cannot be hit at another.
    /// </remarks>
    public float HandlePixels { get; set; } = 14f;

    /// <summary>Raised when a curve has been laid, with the curve and the kind it was drawn as.</summary>
    /// <remarks>
    ///     An event rather than a call into the module, so the gesture is testable with no project,
    ///     no asset writer and no world — which is what makes
    ///     [§ Part 4](../../docs/plan/35-water.md#part-4--testing)'s gesture row a unit test.
    /// </remarks>
    public event Action<Spline, WaterBodyKind>? Drawn;

    /// <summary>Which tool a click runs.</summary>
    public WaterTool Tool {
        get => Editing.Tool;
        set {
            if (Editing.Tool == value) {
                return;
            }

            Editing.Cancel();
            Editing.Tool = value;

            // ⚠ The handles come off with the tool that owns them. A cursor left pointed at this mode
            // draws three crosses on a body in a pane whose next click lays a draw point, and nothing
            // on screen would say which tool it is in — SceneViewport.Cursor's "clear it on
            // deactivate" rule, applied to a tool change as well.
            Unaim();

            ToolChanged?.Invoke(value);
        }
    }

    /// <summary>Raised when <see cref="Tool" /> changes.</summary>
    public event Action<WaterTool>? ToolChanged;

    /// <summary>Selects the <paramref name="slot" />th tool, 0-based.</summary>
    /// <param name="slot">Which one.</param>
    /// <returns>Whether there was one.</returns>
    public bool SelectSlot(int slot) {
        if ((uint)slot >= (uint)Tools.Count) {
            return false;
        }

        Tool = Tools[slot];

        return true;
    }

    // --- Command ids --------------------------------------------------------

    /// <summary>What the command that selects a tool is called.</summary>
    /// <param name="tool">The tool.</param>
    /// <returns>The command id.</returns>
    public static string ToolCommand(WaterTool tool) => "water.tool." + tool.ToString().ToLowerInvariant();

    /// <summary>What the command bound to a digit is called.</summary>
    /// <param name="slot">Which digit, 0-based.</param>
    /// <returns>The command id.</returns>
    public static string SlotCommand(int slot) => "water.tool-" + (slot + 1);

    /// <summary>Finishes the curve being drawn and makes a body of it.</summary>
    public const string FinishCommand = "water.finish";

    /// <summary>Takes the last point back.</summary>
    public const string UndoPointCommand = "water.undo-point";

    /// <summary>Drops the draw.</summary>
    public const string CancelCommand = "water.cancel";

    /// <summary>Places a zone at the view, without which nothing renders.</summary>
    public const string CreateZoneCommand = "water.zone-create";

    /// <summary>Shows or hides what the water did to the ground.</summary>
    public const string PreviewCarveCommand = "water.preview-carve";

    /// <summary>Every verb the mode registers besides the tools.</summary>
    public static IReadOnlyList<string> Commands { get; } = [
        FinishCommand,
        UndoPointCommand,
        CancelCommand,
        CreateZoneCommand,
        PreviewCarveCommand
    ];

    // --- Registration -------------------------------------------------------

    /// <inheritdoc />
    public void Register(EditorShell shell) {
        ArgumentNullException.ThrowIfNull(shell);
        this.shell = shell;

        for (var index = 0; index < SlotCount; index++) {
            var slot = index;
            var id = SlotCommand(slot);

            shell.Commands.Add(
                new EditorCommand(id, new StringId("editor.command." + id, $"Tool {slot + 1}"), () => SelectSlot(slot)) {
                    Category = CategoryWater,
                    Context = WaterContext,
                    Enablement = () => IsActive() && slot < Tools.Count
                }
            );

            shell.Keys.SetDefault(id, new KeyChord((InputKey)((int)InputKey.Number1 + slot), ModifierKeys.None));
        }

        foreach (var tool in Tools) {
            var chosen = tool;
            var id = ToolCommand(chosen);

            shell.Commands.Add(
                new EditorCommand(id, new StringId("editor.command." + id, chosen + " Water"), () => Tool = chosen) {
                    Category = CategoryWater,
                    Context = WaterContext,
                    RadioGroup = ToolGroup,
                    Checked = () => Tool == chosen,
                    Enablement = IsActive
                }
            );
        }

        shell.Commands.Add(
            new EditorCommand(
                FinishCommand,
                new StringId("editor.command." + FinishCommand, "Finish Water Body"),
                () => Finish()
            ) {
                Category = CategoryWater,
                Context = WaterContext,

                // ⚠ Enabled on the point count rather than on "is drawing", because a lake needs
                // three points and a river two. A Finish that was reachable at one point would make
                // a body whose spline the kernel refuses, reported from inside a constructor.
                Enablement = () => IsActive() && Editing.CanCommit
            }
        );

        shell.Keys.SetDefault(FinishCommand, new KeyChord(InputKey.Enter, ModifierKeys.None));

        Verb(UndoPointCommand, "Undo Water Point", () => Editing.Undo(), InputKey.Backspace);
        Verb(CancelCommand, "Cancel Water Draw", Editing.Cancel, InputKey.Escape);

        shell.Commands.Add(
            new EditorCommand(
                CreateZoneCommand,
                new StringId("editor.command." + CreateZoneCommand, "Create Water Zone"),
                () => CreateZone()
            ) {
                Category = CategoryWater,
                Context = WaterContext,

                // ⚠ Not gated on the mode being active, and deliberately: "I placed a lake and there
                // is no water" is answered by placing a zone, and a person reading that diagnostic is
                // in whatever mode they were in when they read it.
                Enablement = () => Document is not null && Zone.Validate() is null
            }
        );

        shell.Commands.Add(
            new EditorCommand(
                PreviewCarveCommand,
                new StringId("editor.command." + PreviewCarveCommand, "Preview Water Carve"),
                () => Editing.CarvePreview = !Editing.CarvePreview
            ) {
                Category = CategoryWater,
                Context = WaterContext,
                Checked = () => Editing.CarvePreview,
                Enablement = IsActive
            }
        );

        void Verb(string id, string label, Action run, InputKey key) {
            shell.Commands.Add(
                new EditorCommand(id, new StringId("editor.command." + id, label), run) {
                    Category = CategoryWater,
                    Context = WaterContext,
                    Enablement = IsActive
                }
            );

            shell.Keys.SetDefault(id, new KeyChord(key, ModifierKeys.None));
        }
    }

    /// <inheritdoc />
    public void Unregister(EditorShell shell) {
        ArgumentNullException.ThrowIfNull(shell);

        foreach (var tool in Tools) {
            shell.Commands.Remove(ToolCommand(tool));
        }

        for (var slot = 0; slot < SlotCount; slot++) {
            shell.Commands.Remove(SlotCommand(slot));
        }

        foreach (var command in Commands) {
            shell.Commands.Remove(command);
        }

        // ⚠ And the handles, which are a delegate a pane calls every frame. A mode taken out of the
        // shell with one still installed is a pane drawing crosses for a toolset that is gone, and
        // holding it alive to do it.
        Unaim();

        this.shell = null;
    }

    /// <inheritdoc />
    public void Activated() {
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The draw goes.</b> A half-laid curve belongs to a gesture that is over, and one that
    ///         survived a trip to the outliner would be finished by the next click in a mode that has no
    ///         idea a curve was in flight.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the carve preview goes back on, which is the one thing here that touches a
    ///         document.</b> <see cref="WaterEdit.CarvePreview" /> hides the terrain's reserved water
    ///         layer, and <c>TerrainEditLayer.IsVisible</c> is <em>saved</em> — so an author who left
    ///         the mode with the preview off would reopen the project to ground with no riverbeds in
    ///         it and nothing anywhere saying why. A view state that outlives the view it belongs to
    ///         is indistinguishable from data loss.
    ///     </para>
    /// </remarks>
    public void Deactivated() {
        Editing.Cancel();
        Editing.CarvePreview = true;
        Unaim();
    }

    /// <inheritdoc />
    public bool Pointer(PointerEvent args) => false;

    /// <inheritdoc />
    public bool Key(KeyEvent args) => false;

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Each tool is asked for what it actually wants, and one of the three wants
    ///         nothing.</b> A mode that returned true for every event in every tool would swallow the
    ///         pane's navigation, its marquee and its gizmo for the whole time it is active — which is
    ///         why this reads as three cases rather than as one guard being widened.
    ///     </para>
    ///     <para>
    ///         <b>Draw</b> wants a press on the ground, and takes it so the press does not also start
    ///         the pane's rubber-band. <b>Profile</b> wants a press <em>on a handle</em> and then the
    ///         moves and the release that follow it — a press anywhere else is a selection and is left
    ///         alone. <b>Preview</b> wants none of it: it is a state and not a gesture, and looking at
    ///         what the water did to the ground means flying around while the ground is there. What is
    ///         <em>not</em> taken by any of them is a press that misses everything, because aiming at
    ///         the sky is how somebody frames a shot.
    ///     </para>
    /// </remarks>
    public bool Pointer(SceneViewport pane, PointerEvent args) {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(args);

        return Tool switch {
            WaterTool.Draw => Drawing(pane, args),
            WaterTool.Profile => Profiling(pane, args),
            _ => false
        };
    }

    /// <summary>The draw gesture: a press on the ground lays a point or closes the curve.</summary>
    bool Drawing(SceneViewport pane, PointerEvent args) =>
        args.Action switch {
            PointerAction.Pressed when args.Button == PointerButton.Primary && Ground(pane, args) is { } point =>
                // ⚠ Taken whether or not a point was laid. A click too close to the last one lays
                // nothing — see WaterEdit.MinimumSpacing — but it was still aimed at the ground, and
                // letting it fall through would start the pane's rubber-band under the author's hand.
                Lay(point),

            _ => false
        };

    /// <summary>The profile gesture: grab a handle, drag it, and commit one undo entry on release.</summary>
    /// <remarks>
    ///     ⚠ <b>The hover is tracked and <em>not</em> taken — the <c>return false</c> is
    ///     load-bearing.</b> <c>TerrainMode.Pointer</c>'s rule: the pane's own hover is what highlights
    ///     whatever a click would select, and a mode that swallowed every move to draw three crosses
    ///     would turn that highlight off for the whole time the tool is armed.
    /// </remarks>
    bool Profiling(SceneViewport pane, PointerEvent args) {
        var pointer = pane.Control.ToRender(args.X, args.Y);

        switch (args.Action) {
            case PointerAction.Pressed when args.Button == PointerButton.Primary:
                return Grab(pane, pointer);

            case PointerAction.Moved when Editing.Holding != WaterHandle.None:
                Slide(pane, pointer);

                return true;

            case PointerAction.Released when Editing.Holding != WaterHandle.None:
                Drop();

                return true;

            case PointerAction.Moved:
                Aiming(pane);

                return false;

            // Leaving the pane takes the handles with it, for TerrainMode.Hovering's reason: a
            // cursor left in the pane the pointer has left says the tool is aimed where it is not.
            case PointerAction.Exited:
                Unaim();

                return false;

            default:
                return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Escape abandons a handle drag as well as a draw, and it puts the profile back.</b> A
    ///     drag is applied to the component as it happens so the surface follows the pointer — see
    ///     <see cref="Slide" /> — so a cancel that only let go of the handle would leave the body at
    ///     whatever half-width the pointer happened to be over, with no undo entry naming it.
    /// </remarks>
    public bool Key(SceneViewport pane, KeyEvent args) {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Key != InputKey.Escape) {
            return false;
        }

        if (Editing.Holding != WaterHandle.None) {
            Restore();
            Editing.Release();

            return true;
        }

        if (!Editing.IsDrawing) {
            return false;
        }

        Editing.Cancel();

        return true;
    }

    /// <summary>Ends the draw, makes a body of the curve, and clears the gesture.</summary>
    /// <returns>The curve, or null if there were not enough points.</returns>
    /// <remarks>
    ///     ⚠ <b>The curve is raised as an event rather than written here.</b> Turning it into a
    ///     <c>.vxspline</c> beside the scene and an entity naming it needs a project and a world; a
    ///     mode that did it could not be driven by a test, which is precisely the "built and not yet
    ///     reachable" failure doc 31 warns about and doc 35's W9 asks to be tested for.
    /// </remarks>
    public Spline? Finish() {
        if (Editing.Commit() is not { } spline) {
            return null;
        }

        var kind = Editing.Kind;

        Editing.Cancel();
        Drawn?.Invoke(spline, kind);

        return spline;
    }

    /// <summary>Places a zone at the origin of the document, undoably.</summary>
    /// <returns>The entity, or the default when there is no document to put it in.</returns>
    /// <remarks>
    ///     Through <see cref="SceneDocument.Create" /> rather than a command of water's own — see
    ///     <c>WaterCommands.cs</c>'s note for why there is no <c>CreateWaterZoneCommand</c>.
    /// </remarks>
    public Vixen.Core.Entity CreateZone() {
        if (Document is not { } document) {
            return default;
        }

        var component = Zone.Component;

        return document.Create("Water Zone", default, default, entity => document.World.Add(entity, component));
    }

    /// <summary>Lays a point — or closes the curve, if the click came back to where it started.</summary>
    /// <remarks>
    ///     ⚠ <b>Clicking the first point again is how a lake is finished</b>, because the UI layer has
    ///     no double click to offer: <c>PointerAction</c> is moves, presses and releases, and a click
    ///     count is a fact about time the event does not carry. Enter finishes as well, and is the
    ///     only way to finish a river — an open curve has no first point to come back to.
    /// </remarks>
    bool Lay(Vector3 point) {
        if (Editing.ClosesAt(point)) {
            Finish();
        } else {
            Editing.Add(point);
        }

        return true;
    }

    // --- The profile gesture ------------------------------------------------

    /// <summary>The selected body the handles belong to, or null when there is not one.</summary>
    /// <returns>Whether one was found.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The selection rather than a pick, because a body <em>is</em> an entity.</b> Doc 35
    ///         § One mode, not two: placing a lake is placing an entity and the editor already selects
    ///         entities, so a second picking path here would be a second answer to "which body am I
    ///         editing" — and the two would disagree the first time somebody used the outliner.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Liveness first, and <c>World.Has</c> throws rather than answering false for a
    ///         destroyed entity.</b> A selection outlives the thing it names for one frame after an
    ///         undo that removed it, which is <c>TerrainModuleSession.Bound</c>'s note and the same
    ///         crash on Ctrl-Z.
    ///     </para>
    /// </remarks>
    bool Aim() {
        aimed = null;

        if (Document is not { } document || Curves is not { } curves) {
            return false;
        }

        foreach (var entity in document.Selection) {
            if (!document.World.IsAlive(entity) || !document.World.Has<WaterBodyComponent>(entity)) {
                continue;
            }

            var component = document.World.Read<WaterBodyComponent>(entity);

            if (component.Spline is not { Length: > 0 } name) {
                continue;
            }

            var placement = document.World.Has<WorldTransform>(entity)
                ? document.World.Read<WorldTransform>(entity).Value
                : Matrix4x4.Identity;

            if (curves.SplineFor(name, placement) is not { } curve) {
                continue;
            }

            aimed = (entity, component, curve);

            return true;
        }

        return false;
    }

    /// <summary>Refreshes what the handles are drawn on, and keeps the pane's cursor pointed here.</summary>
    void Aiming(SceneViewport pane) {
        if (!ReferenceEquals(hovered, pane)) {
            if (hovered is { } previous) {
                previous.Cursor = null;
            }

            hovered = pane;
        }

        Aim();
        pane.Cursor = Handles;
    }

    /// <summary>Takes the handles off whichever pane is drawing them.</summary>
    void Unaim() {
        if (hovered is { } pane) {
            pane.Cursor = null;
        }

        hovered = null;
        aimed = null;
    }

    /// <summary>Draws the curve and its handles. Read every frame, so it reads fields and allocates nothing.</summary>
    /// <remarks>
    ///     ⚠ <b>The centre line first, and it is what the three handles per point are measured
    ///     from.</b> Without it the Profile tool draws two bars and a cross at each control point and
    ///     nothing between them — so a river with a bend in it reads as a row of unrelated markers,
    ///     and the author widening it cannot see the shape they are widening. <c>SplineOverlay</c> is
    ///     the one that knows how to sample a curve, and it samples by arc length rather than by
    ///     parameter for the reason that class states.
    /// </remarks>
    void Handles(GizmoDraw draw) {
        if (Tool != WaterTool.Profile || hovered is not { } pane || aimed is not { } body) {
            return;
        }

        SplineOverlay.Curve(body.Curve, draw);
        WaterProfileHandles.Draw(draw, pane, body.Curve, body.Component.Profile, Editing.Holding, Editing.HoldingPoint);
    }

    /// <summary>Takes hold of whichever handle the press landed on.</summary>
    /// <returns>Whether one was grabbed, which is whether the press is this mode's.</returns>
    /// <remarks>
    ///     ⚠ <b>A press that is not on a handle is <em>not</em> taken.</b> It is how somebody selects
    ///     the body they are about to edit, and a Profile tool that swallowed it would be a tool you
    ///     have to leave in order to choose what it works on.
    /// </remarks>
    bool Grab(SceneViewport pane, Vector2 pointer) {
        if (!Aim() || aimed is not { } body) {
            return false;
        }

        var profile = body.Component.Profile;
        var (handle, point) = WaterProfileHandles.Under(pane, pointer, body.Curve, profile, HandlePixels);

        if (handle == WaterHandle.None) {
            return false;
        }

        var handles = WaterEdit.HandlesOf(body.Curve, profile, point);

        grabbedAt = handle switch {
            WaterHandle.WidthLeft => handles.Left,
            WaterHandle.WidthRight => handles.Right,
            _ => handles.Depth
        };

        grabbedAxis = WaterEdit.AxisOf(handle, WaterEdit.SideAt(body.Curve, point));
        grabbedProfile = profile;
        profiledBefore = body.Component;

        Editing.Grab(handle, point);
        Aiming(pane);

        return true;
    }

    /// <summary>Moves the held handle along its own axis and applies the profile as it goes.</summary>
    /// <remarks>
    ///     ⚠ <b>Applied to the component on every move rather than only on release.</b> The whole
    ///     argument for a viewport handle over a number field is that the surface follows the pointer
    ///     — doc 35 § Part 2's "a width somebody types is a width somebody gets wrong twice before
    ///     looking at it". What is <em>not</em> done per move is pushing an undo entry; that is
    ///     <see cref="Drop" />, on <c>TerrainStrokeCommand</c>'s terms.
    /// </remarks>
    void Slide(SceneViewport pane, Vector2 pointer) {
        if (aimed is not { } body || Document is not { } document || !document.World.IsAlive(body.Entity)) {
            return;
        }

        var moved = WaterEdit.OnAxis(pane.Ray(pointer), grabbedAt, grabbedAxis);
        var metres = Vector3.Dot(moved - grabbedAt, grabbedAxis);
        var profile = Editing.Drag(grabbedProfile, metres);

        var after = profiledBefore;

        after.HalfWidth = profile.HalfWidth;
        after.Depth = profile.Depth;

        document.World.Set(body.Entity, after);

        // The handles follow the drag, because they are drawn from the component this just wrote.
        aimed = (body.Entity, after, body.Curve);
    }

    /// <summary>Lets go, and makes the whole drag one undo entry.</summary>
    /// <remarks>
    ///     ⚠ <b>Executed rather than merely pushed</b>, for <c>TerrainMode.Commit</c>'s reason: the
    ///     command is a redo of something already applied, and <c>Do</c> reapplies exactly what is
    ///     already there. That is the price of one vocabulary for undo.
    /// </remarks>
    void Drop() {
        if (aimed is { } body
            && Document is { } document
            && document.World.IsAlive(body.Entity)
            && (body.Component.HalfWidth != profiledBefore.HalfWidth || body.Component.Depth != profiledBefore.Depth)) {
            document.Stack.Execute(new WaterProfileCommand(document.World, body.Entity, profiledBefore, body.Component));
        }

        Editing.Release();
    }

    /// <summary>Puts the body back to what it was before the drag started.</summary>
    void Restore() {
        if (aimed is { } body && Document is { } document && document.World.IsAlive(body.Entity)) {
            document.World.Set(body.Entity, profiledBefore);
            aimed = (body.Entity, profiledBefore, body.Curve);
        }
    }

    /// <summary>Where a pointer meets a surface, in world space, or null if it misses.</summary>
    Vector3? Ground(SceneViewport pane, PointerEvent args) {
        var ray = pane.Ray(pane.Control.ToRender(args.X, args.Y));

        if (pane.Surfaces is { } probe && probe.Raycast(ray, out var hit)) {
            return hit.Point;
        }

        if (MathF.Abs(ray.Direction.Y) < 1e-6f) {
            return null;
        }

        var distance = (GroundHeight - ray.Origin.Y) / ray.Direction.Y;

        return distance <= 0f || distance > Reach ? null : ray.GetPoint(distance);
    }

    bool IsActive() => shell?.Modes.IsActive(ModeId) == true;

    /// <summary>Where the palette files the mode's verbs.</summary>
    static readonly StringId CategoryWater = new("editor.category.water", "Water");

    /// <summary>The radio group the three tools are in.</summary>
    const string ToolGroup = "water.tool";
}
