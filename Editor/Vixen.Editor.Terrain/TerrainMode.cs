// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Input;
using Vixen.Terrain;
using Vixen.Ui;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Editor.Terrain;

/// <summary>The viewport mode the sculpt tools live in.</summary>
/// <remarks>
///     <para>
///         <b>The third mode, and the first whose gestures <em>are</em> the feature — [docs/plan/31
///         § Two modes, not three, and not one].</b> Blockout proved the seam by claiming keys that
///         already meant something; this one claims eight of them and then spends the whole of every
///         drag inside <see cref="Pointer(SceneViewport, PointerEvent)" />, because a brush is not a
///         verb with a shortcut — it is a pointer that means something else while the mode is active.
///     </para>
///     <para>
///         ⚠ <b>Terrain and Foliage are two modes because they filter different things.</b> Sculpt
///         and paint require a terrain and act on its texels; foliage paints onto <em>any</em>
///         surface and its filter set is the feature. One mode that did both would have to answer
///         "what is the target surface" twice with different answers. Unreal splits them for this
///         reason and it is right.
///     </para>
///     <para>
///         ⚠ <b>Entering the mode with no terrain shows the create panel rather than an empty
///         toolbar.</b> <see cref="HasTerrain" /> is what the panel reads, and the tool commands are
///         disabled rather than hidden — a mode that does nothing and says nothing is the state every
///         one of these toolsets puts a new user in.
///     </para>
/// </remarks>
public sealed class TerrainMode : IEditorMode, IViewportInput {
    /// <summary>What the mode is called, everywhere an id is wanted.</summary>
    public const string ModeId = "terrain";

    /// <summary>The command context the mode claims while it is active.</summary>
    /// <remarks>
    ///     ⚠ <b>The same string as <see cref="ModeId" /> and deliberately a separate constant</b>, for
    ///     <c>BlockoutMode.BlockoutContext</c>'s reason: one is what the mode bar's button is called
    ///     and the other is what a saved keymap files a binding under.
    /// </remarks>
    public const string TerrainContext = "terrain";

    /// <summary>What the panel the mode opens is registered as.</summary>
    public const string PanelId = "terrain.panel";

    EditorShell? shell;

    /// <inheritdoc />
    public string Id => ModeId;

    /// <inheritdoc />
    public StringId Title { get; } = new("editor.mode.terrain", "Terrain");

    /// <inheritdoc />
    /// <remarks>None, so the mode bar draws the word — <c>BlockoutMode.Icon</c>'s reason.</remarks>
    public PathBuilder? Icon => null;

    /// <inheritdoc />
    public string? Context => TerrainContext;

    /// <inheritdoc />
    /// <remarks>
    ///     The terrain panel: create and manage, the edit-layer stack, the target layers and the
    ///     brush. Opened on activation and closed on deactivation by the shell, because a settings
    ///     panel for a tool nobody is holding is one somebody has to close by hand.
    /// </remarks>
    public string? Panel => PanelId;

    /// <inheritdoc />
    /// <remarks>
    ///     The eight tools as one segmented control, which is what they are: a choice, not eight
    ///     switches. Nothing else is on the strip — every other verb in this mode is a panel row.
    /// </remarks>
    public IReadOnlyList<ToolbarEntry> Toolbar { get; } = [
        new ToolbarGroup([.. Tools.Select(ToolCommand)])
    ];

    /// <summary>Every tool, in the order the strip lists them.</summary>
    /// <remarks>
    ///     ⚠ <b>Written out rather than taken from <c>Enum.GetValues</c></b>, for
    ///     <c>BlockoutMode.Kinds</c>'s reason: the enum's order is a file format's business and a
    ///     strip's order is what somebody reaches for most. They coincide today and a reordering of
    ///     either must not silently move the other's keys.
    /// </remarks>
    public static IReadOnlyList<TerrainTool> Tools { get; } = [
        TerrainTool.Sculpt,
        TerrainTool.Smooth,
        TerrainTool.Flatten,
        TerrainTool.Ramp,
        TerrainTool.Erosion,
        TerrainTool.Hydro,
        TerrainTool.Noise,
        TerrainTool.Holes
    ];

    /// <summary>The editing state the mode drives.</summary>
    /// <remarks>
    ///     ⚠ <b>Owned here rather than set by the application, unlike <c>BlockoutMode.Editing</c>.</b>
    ///     A <c>MeshEdit</c> follows the entity selection and pushes a demotion onto the document's
    ///     undo stack, neither of which a mode can know about; a <see cref="TerrainEdit" /> holds a
    ///     terrain, a layer and a brush and knows about none of that. What the application sets is
    ///     <see cref="TerrainEdit.Terrain" /> and <see cref="Document" />.
    /// </remarks>
    public TerrainEdit Editing { get; } = new();

    /// <summary>The document a committed stroke goes onto, or null while the mode drives none.</summary>
    /// <remarks>
    ///     ⚠ <b>Null is a mode that sculpts and does not record.</b> That is the right behaviour for
    ///     a test and the wrong one to ship, so the application sets it with the terrain — and
    ///     <see cref="Committed" /> is raised either way, so a host without a document can still
    ///     watch what happened.
    /// </remarks>
    public EditorDocument? Document { get; set; }

    /// <summary>Where the terrain's sample origin is, in world space.</summary>
    /// <remarks>
    ///     ⚠ <b>A translation and nothing else, which is <c>TerrainComponent</c>'s own constraint.</b>
    ///     A terrain's samples are its space, so rotating one would make every tool that maps a world
    ///     position to a sample carry an inverse — the component does not offer rotation and this does
    ///     not undo one.
    /// </remarks>
    public Vector3 Origin { get; set; }

    /// <summary>How far a pointer ray looks for the ground, in metres.</summary>
    public float Reach { get; set; } = 100_000f;

    /// <summary>Whether there is a terrain to sculpt.</summary>
    public bool HasTerrain => Editing.Terrain is not null;

    /// <summary>The form a new terrain is created from, which is the panel's first section.</summary>
    public TerrainCreateSettings Create { get; } = new();

    /// <summary>Raised when a stroke has been committed, with the entry that undoes it.</summary>
    public event Action<IEditorCommand>? Committed;

    /// <summary>Raised when the mode has made a terrain, so the application can put it in a scene.</summary>
    /// <remarks>
    ///     ⚠ <b>An event rather than an asset write, because this assembly has no asset database.</b>
    ///     Making the heightfield is arithmetic and belongs here; deciding what <c>.vxterrain</c> it
    ///     is saved as, and which entity gets a <c>TerrainComponent</c> naming it, is the
    ///     application's — the same split <c>BlockoutMode.Baker</c> uses for a baked mesh.
    /// </remarks>
    public event Action<TerrainMap>? Created;

    /// <summary>Which tool a drag runs.</summary>
    public TerrainTool Tool {
        get => Editing.Tools.Tool;
        set {
            if (Editing.Tools.Tool == value) {
                return;
            }

            // ⚠ Whatever is in flight is abandoned rather than finished with the new tool. A stroke
            // is a record of what one tool did; switching mid-drag would commit an entry whose name
            // and whose footprint belong to two different operations.
            Editing.Cancel();
            Editing.Tools.Tool = value;
            ToolChanged?.Invoke(value);
        }
    }

    /// <summary>Raised when <see cref="Tool" /> changes.</summary>
    public event Action<TerrainTool>? ToolChanged;

    // --- Command ids --------------------------------------------------------

    /// <summary>What the command that selects a tool is called.</summary>
    /// <param name="tool">The tool.</param>
    /// <returns>The command id.</returns>
    public static string ToolCommand(TerrainTool tool) => "terrain.tool." + tool.ToString().ToLowerInvariant();

    /// <summary>Makes the brush bigger.</summary>
    public const string GrowBrushCommand = "terrain.brush-grow";

    /// <summary>And smaller.</summary>
    public const string ShrinkBrushCommand = "terrain.brush-shrink";

    /// <summary>Presses harder.</summary>
    public const string HarderCommand = "terrain.brush-harder";

    /// <summary>And softer.</summary>
    public const string SofterCommand = "terrain.brush-softer";

    /// <summary>Creates the terrain the create form describes.</summary>
    public const string CreateCommand = "terrain.create";

    /// <summary>Adds an edit layer on top of the stack.</summary>
    public const string AddLayerCommand = "terrain.layer-add";

    /// <summary>Removes the selected one.</summary>
    public const string RemoveLayerCommand = "terrain.layer-remove";

    /// <summary>Copies it, deltas and all.</summary>
    public const string DuplicateLayerCommand = "terrain.layer-duplicate";

    /// <summary>Empties it without removing it.</summary>
    public const string ClearLayerCommand = "terrain.layer-clear";

    /// <summary>Adds it into the layer below and drops it.</summary>
    public const string CollapseLayerCommand = "terrain.layer-collapse";

    /// <summary>Moves it up the stack.</summary>
    public const string RaiseLayerCommand = "terrain.layer-raise";

    /// <summary>And down.</summary>
    public const string LowerLayerCommand = "terrain.layer-lower";

    /// <summary>Shows or hides it.</summary>
    public const string ToggleLayerCommand = "terrain.layer-toggle";

    /// <summary>Every brush verb, in the order the menu lists them.</summary>
    public static IReadOnlyList<string> BrushCommands { get; } = [
        GrowBrushCommand,
        ShrinkBrushCommand,
        HarderCommand,
        SofterCommand
    ];

    /// <summary>And every layer verb.</summary>
    public static IReadOnlyList<string> LayerCommands { get; } = [
        AddLayerCommand,
        RemoveLayerCommand,
        DuplicateLayerCommand,
        ClearLayerCommand,
        CollapseLayerCommand,
        RaiseLayerCommand,
        LowerLayerCommand,
        ToggleLayerCommand
    ];

    // --- Registration -------------------------------------------------------

    /// <inheritdoc />
    public void Register(EditorShell shell) {
        ArgumentNullException.ThrowIfNull(shell);
        this.shell = shell;

        // ⚠ The digits, and this is doc 24's B2 a second time. `1`…`8` are the tools while the
        // terrain context has the focus and view-bookmark recall everywhere else, because these
        // commands declare a context and the bookmarks declare none. The seam took no new machinery
        // the second time it was used, which is the whole claim it was built to make.
        for (var index = 0; index < Tools.Count; index++) {
            Declare(Tools[index], (InputKey)((int)InputKey.Number1 + index));
        }

        Verb(GrowBrushCommand, "Grow Brush", () => Editing.Brush.Resize(1), InputKey.RightBracket);
        Verb(ShrinkBrushCommand, "Shrink Brush", () => Editing.Brush.Resize(-1), InputKey.LeftBracket);
        Verb(HarderCommand, "Press Harder", () => Editing.Brush.Press(1), InputKey.Equals);
        Verb(SofterCommand, "Press Softer", () => Editing.Brush.Press(-1), InputKey.Minus);

        shell.Commands.Add(
            new EditorCommand(CreateCommand, new StringId("editor.command." + CreateCommand, "Create Terrain"), Made) {
                Category = CategoryTerrain,
                Context = TerrainContext,

                // ⚠ The one verb in the mode that is enabled *without* a terrain, and the only one
                // that could be. Everything else acts on ground that does not exist yet.
                Enablement = () => IsActive() && Create.IsValid
            }
        );

        Layer(AddLayerCommand, "Add Terrain Layer", () => {
            var (command, layer) = TerrainLayerCommands.Add(Editing.Terrain!, NextLayerName());

            Run(command);
            Editing.Layer = layer;
        }, needsLayer: false);

        Layer(RemoveLayerCommand, "Remove Terrain Layer", () => {
            var terrain = Editing.Terrain!;
            var index = terrain.IndexOf(Editing.Layer!);

            Run(TerrainLayerCommands.Remove(terrain, Editing.Layer!));
            Editing.Layer = terrain.Layers.Count == 0
                ? null
                : terrain.Layers[Math.Clamp(index - 1, 0, terrain.Layers.Count - 1)];
        });

        Layer(DuplicateLayerCommand, "Duplicate Terrain Layer", () => {
            var (command, layer) = TerrainLayerCommands.Duplicate(Editing.Terrain!, Editing.Layer!);

            Run(command);
            Editing.Layer = layer;
        });

        Layer(
            ClearLayerCommand,
            "Clear Terrain Layer",
            () => Run(TerrainLayerCommands.Clear(Editing.Terrain!, Editing.Layer!))
        );

        Layer(
            CollapseLayerCommand,
            "Collapse Terrain Layer",
            () => Run(TerrainLayerCommands.Collapse(Editing.Terrain!, Editing.Terrain!.IndexOf(Editing.Layer!))),
            enabled: () => Editing.Terrain?.IndexOf(Editing.Layer!) > 0
        );

        Layer(RaiseLayerCommand, "Raise Terrain Layer", () => Moved(1), enabled: () => CanMove(1));
        Layer(LowerLayerCommand, "Lower Terrain Layer", () => Moved(-1), enabled: () => CanMove(-1));

        Layer(
            ToggleLayerCommand,
            "Show / Hide Terrain Layer",
            () => Run(TerrainLayerCommands.SetVisible(Editing.Terrain!, Editing.Layer!, !Editing.Layer!.IsVisible))
        );

        void Declare(TerrainTool tool, InputKey key) {
            var id = ToolCommand(tool);

            shell.Commands.Add(
                new EditorCommand(id, new StringId("editor.command." + id, tool + " Tool"), () => Tool = tool) {
                    Category = CategoryTerrain,
                    Context = TerrainContext,
                    RadioGroup = ToolGroup,
                    Checked = () => Tool == tool,

                    // ⚠ A terrain, not merely the mode. Choosing "Erosion" with nothing selected sets
                    // a state nothing is reading and ticks a button over an empty viewport, which in
                    // the palette reads as a feature that silently does nothing.
                    Enablement = () => IsActive() && HasTerrain
                }
            );

            shell.Keys.SetDefault(id, new KeyChord(key, ModifierKeys.None));
        }

        void Verb(string id, string label, Action run, InputKey key) {
            shell.Commands.Add(
                new EditorCommand(id, new StringId("editor.command." + id, label), run) {
                    Category = CategoryTerrain,
                    Context = TerrainContext,
                    Enablement = () => IsActive() && HasTerrain
                }
            );

            shell.Keys.SetDefault(id, new KeyChord(key, ModifierKeys.None));
        }

        void Layer(string id, string label, Action run, bool needsLayer = true, Func<bool>? enabled = null) {
            shell.Commands.Add(
                new EditorCommand(id, new StringId("editor.command." + id, label), () => {
                    if (Editing.Terrain is not null && (!needsLayer || Editing.Layer is not null)) {
                        run();
                    }
                }) {
                    Category = CategoryTerrain,
                    Context = TerrainContext,
                    Enablement = () =>
                        IsActive()
                        && HasTerrain
                        && (!needsLayer || Editing.Layer is not null)
                        && (enabled is null || enabled())
                }
            );
        }
    }

    /// <inheritdoc />
    public void Unregister(EditorShell shell) {
        ArgumentNullException.ThrowIfNull(shell);

        foreach (var tool in Tools) {
            shell.Commands.Remove(ToolCommand(tool));
        }

        foreach (var command in BrushCommands.Concat(LayerCommands).Append(CreateCommand)) {
            shell.Commands.Remove(command);
        }

        this.shell = null;
    }

    /// <inheritdoc />
    public void Activated() {
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>A half-finished stroke does not survive leaving the mode.</b> It holds a layer and a
    ///     brush that were current a moment ago; committing it on the way out would put an entry on
    ///     the stack that the artist did not finish, and keeping it would apply the rest of it to
    ///     whatever is selected when they come back.
    /// </remarks>
    public void Deactivated() => Editing.Cancel();

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Only the pane-aware overload does anything, and this one cannot.</b> A brush needs a
    ///     ray, a ray needs a camera and a render size, and none of that is on a
    ///     <see cref="PointerEvent" /> — <c>BlockoutMode.Pointer</c>'s reason, in a mode where it is
    ///     the whole feature rather than one gesture.
    /// </remarks>
    public bool Pointer(PointerEvent args) => false;

    /// <inheritdoc />
    /// <remarks>Everything the mode owns from the keyboard is a command, and a command's key comes
    ///     through the keymap.</remarks>
    public bool Key(KeyEvent args) => false;

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The press <em>is</em> taken, unlike blockout's.</b> A press that is going to
    ///         become a stroke must not also start the pane's rubber-band, or releasing it would
    ///         select whatever the band swept over — and unlike an element selection, there is nothing
    ///         under a brush for a marquee to mean.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A press that misses the ground is not taken.</b> Aiming past the terrain at the
    ///         sky is how somebody frames a shot, and a mode that swallowed it would make the viewport
    ///         unusable everywhere the terrain is not.
    ///     </para>
    /// </remarks>
    public bool Pointer(SceneViewport pane, PointerEvent args) {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(args);

        if (!HasTerrain) {
            return false;
        }

        switch (args.Action) {
            case PointerAction.Pressed when args.Button == PointerButton.Primary:
                return Ground(pane, args) is { } start
                    && Editing.Begin(start, (args.Modifiers & ModifierKeys.Shift) != 0);

            case PointerAction.Moved when Editing.IsStroking:
                if (Ground(pane, args) is { } over) {
                    Editing.Extend(over);
                }

                return true;

            case PointerAction.Released when Editing.IsStroking:
                Commit();
                return true;

            default:
                return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Only <c>Escape</c>, and only while a stroke is in flight</b> — <c>BlockoutMode</c>'s
    ///     rule. A mode that took the key unconditionally would break cancelling a gizmo drag, and a
    ///     half-applied stroke is the one thing the pane cannot know how to abandon.
    /// </remarks>
    public bool Key(SceneViewport pane, KeyEvent args) {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Key != InputKey.Escape || !Editing.IsStroking) {
            return false;
        }

        Editing.Cancel();
        return true;
    }

    /// <summary>Ends the stroke and puts it on the document's stack.</summary>
    /// <returns>The entry, or null if the stroke touched nothing.</returns>
    public IEditorCommand? Commit() {
        var command = Editing.Commit();

        if (command is null) {
            return null;
        }

        // ⚠ Executed rather than merely pushed, because the command is a *redo* of a stroke that has
        // already been applied — see TerrainStrokeCommand's remarks. `Execute` runs `Do`, which
        // reapplies exactly what is already there, which is the price of one vocabulary for undo.
        Document?.Stack.Execute(command);
        Committed?.Invoke(command);

        return command;
    }

    /// <summary>Where a pointer meets the ground, in the terrain's own XZ, or null if it misses.</summary>
    Vector2? Ground(SceneViewport pane, PointerEvent args) {
        if (Editing.Terrain is not { } terrain) {
            return null;
        }

        var ray = pane.Ray(pane.Control.ToRender(args.X, args.Y));

        return TerrainPick.Cast(terrain, ray.Origin - Origin, ray.Direction, out var hit, Reach)
            ? hit.Ground
            : null;
    }

    /// <summary>Builds the terrain the create form describes and hands it to whoever asked.</summary>
    void Made() {
        if (!Create.IsValid) {
            return;
        }

        var terrain = Create.Build();

        terrain.AddLayer("Sculpt");

        Editing.Terrain = terrain;
        Created?.Invoke(terrain);
    }

    void Run(IEditorCommand command) {
        if (Document is { } document) {
            document.Stack.Execute(command);
        } else {
            command.Do(null!);
        }
    }

    bool CanMove(int direction) {
        if (Editing.Terrain is not { } terrain || Editing.Layer is not { } layer) {
            return false;
        }

        var index = terrain.IndexOf(layer) + direction;

        return index >= 0 && index < terrain.Layers.Count;
    }

    void Moved(int direction) {
        var terrain = Editing.Terrain!;
        var from = terrain.IndexOf(Editing.Layer!);

        if (CanMove(direction)) {
            Run(TerrainLayerCommands.Move(terrain, from, from + direction));
        }
    }

    /// <summary>What the next layer is called, which is the first "Layer N" nothing is using.</summary>
    string NextLayerName() {
        var terrain = Editing.Terrain!;

        for (var index = terrain.Layers.Count + 1; ; index++) {
            var name = "Layer " + index;

            if (!terrain.Layers.Any(layer => string.Equals(layer.Name, name, StringComparison.Ordinal))) {
                return name;
            }
        }
    }

    /// <summary>Whether this mode is the shell's active one.</summary>
    /// <remarks>
    ///     ⚠ <b>Enablement as well as context, because the palette does not go through the
    ///     keymap</b> — <c>BlockoutMode.IsActive</c>'s reason. Scoping keeps the chord from firing
    ///     outside the mode and does nothing about somebody choosing "Erosion Tool" out of the
    ///     palette while they are in Select.
    /// </remarks>
    bool IsActive() => shell?.Modes.IsActive(ModeId) == true;

    /// <summary>Where the palette files the mode's verbs.</summary>
    static readonly StringId CategoryTerrain = new("editor.category.terrain", "Terrain");

    /// <summary>The radio group the eight tools are in.</summary>
    const string ToolGroup = "terrain.tool";
}
