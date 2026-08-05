// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Input;
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

        this.shell = null;
    }

    /// <inheritdoc />
    public void Activated() {
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The draw goes.</b> A half-laid curve belongs to a gesture that is over, and one that
    ///     survived a trip to the outliner would be finished by the next click in a mode that has no
    ///     idea a curve was in flight.
    /// </remarks>
    public void Deactivated() => Editing.Cancel();

    /// <inheritdoc />
    public bool Pointer(PointerEvent args) => false;

    /// <inheritdoc />
    public bool Key(KeyEvent args) => false;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The press is taken when it lands on the ground, so it does not also start the pane's
    ///     rubber-band.</b> What is <em>not</em> taken is a press that misses everything, because
    ///     aiming at the sky is how somebody frames a shot.
    /// </remarks>
    public bool Pointer(SceneViewport pane, PointerEvent args) {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(args);

        if (Tool != WaterTool.Draw) {
            return false;
        }

        return args.Action switch {
            PointerAction.Pressed when args.Button == PointerButton.Primary && Ground(pane, args) is { } point =>
                // ⚠ Taken whether or not a point was laid. A click too close to the last one lays
                // nothing — see WaterEdit.MinimumSpacing — but it was still aimed at the ground, and
                // letting it fall through would start the pane's rubber-band under the author's hand.
                Lay(point),

            _ => false
        };
    }

    /// <inheritdoc />
    public bool Key(SceneViewport pane, KeyEvent args) {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Key != InputKey.Escape || !Editing.IsDrawing) {
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
