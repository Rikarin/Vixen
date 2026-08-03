// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Foliage;
using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.Terrain;

/// <summary>The viewport mode foliage is painted in.</summary>
/// <remarks>
///     <para>
///         <b>The fourth mode, and the first that requires nothing — [docs/plan/31 § Two modes, not
///         three, and not one].</b> Sculpt and paint need a terrain and act on its texels; foliage
///         paints onto <em>any</em> surface — a terrain, a blockout mesh, an imported cliff, a roof —
///         and its filter set is the feature rather than an accident. One mode that did both would
///         have to answer "what is the target surface" twice with different answers.
///     </para>
///     <para>
///         ⚠ <b>The digits are slots here too, and for the same reason they are in
///         <see cref="TerrainMode" />.</b> Six tools rather than eight, and a digit past the sixth
///         does nothing — a slot command means "the third tool", which is what the design sentence
///         means, and the named commands keep the words the palette is searched with.
///     </para>
///     <para>
///         ⚠ <b>An empty palette shows the palette, not an empty strip.</b> Entering a mode that does
///         nothing and says nothing is the state every one of these toolsets puts a new user in, and
///         <see cref="FoliageEdit.Refusal" /> is what the panel says instead.
///     </para>
/// </remarks>
public sealed class FoliageMode : IEditorMode, IViewportInput {
    /// <summary>What the mode is called, everywhere an id is wanted.</summary>
    public const string ModeId = "foliage";

    /// <summary>The command context the mode claims while it is active.</summary>
    public const string FoliageContext = "foliage";

    /// <summary>What the panel the mode opens is registered as.</summary>
    public const string PanelId = "foliage.panel";

    EditorShell? shell;

    /// <inheritdoc />
    public string Id => ModeId;

    /// <inheritdoc />
    public StringId Title { get; } = new("editor.mode.foliage", "Foliage");

    /// <inheritdoc />
    /// <remarks>None, so the mode bar draws the word — <c>BlockoutMode.Icon</c>'s reason.</remarks>
    public PathBuilder? Icon => null;

    /// <inheritdoc />
    public IconArt? Art => ModeArt.Foliage;

    /// <inheritdoc />
    public string? Context => FoliageContext;

    /// <inheritdoc />
    /// <remarks>The palette, the brush and the filters.</remarks>
    public string? Panel => PanelId;

    /// <inheritdoc />
    public IReadOnlyList<ToolbarEntry> Toolbar { get; } = [
        new ToolbarGroup([.. Tools.Select(ToolCommand)])
    ];

    /// <summary>The six tools, in the order the strip lists them.</summary>
    public static IReadOnlyList<FoliageTool> Tools { get; } = [
        FoliageTool.Paint,
        FoliageTool.Single,
        FoliageTool.Fill,
        FoliageTool.Erase,
        FoliageTool.Reapply,
        FoliageTool.Select
    ];

    /// <summary>How many digits the mode claims.</summary>
    public const int SlotCount = 6;

    /// <summary>The editing state the mode drives.</summary>
    public FoliageEdit Editing { get; } = new();

    /// <summary>The document a committed stroke goes onto, or null while the mode drives none.</summary>
    public EditorDocument? Document { get; set; }

    /// <summary>How far a pointer ray looks for a surface, in metres.</summary>
    public float Reach { get; set; } = 100_000f;

    /// <summary>Where the ground is, for a ray that has to meet something.</summary>
    /// <remarks>
    ///     ⚠ <b>A plane rather than a refusal, for <c>ScenePlacement</c>'s reason.</b> A stroke aimed
    ///     past the terrain has to land somewhere or the tool reads as broken at the edges of a level;
    ///     the filters are what decide whether what it landed on accepts it.
    /// </remarks>
    public float GroundHeight { get; set; }

    /// <summary>Whether there is a palette to paint from.</summary>
    public bool HasPalette => Editing.Volume is { Palette.Count: > 0 };

    /// <summary>Raised when a stroke has been committed, with the entry that undoes it.</summary>
    public event Action<IEditorCommand>? Committed;

    /// <summary>Which tool a drag runs.</summary>
    public FoliageTool Tool {
        get => Editing.Settings.Tool;
        set {
            if (Editing.Settings.Tool == value) {
                return;
            }

            Editing.Cancel();
            Editing.Settings.Tool = value;
            ToolChanged?.Invoke(value);
        }
    }

    /// <summary>Raised when <see cref="Tool" /> changes.</summary>
    public event Action<FoliageTool>? ToolChanged;

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
    public static string ToolCommand(FoliageTool tool) => "foliage.tool." + tool.ToString().ToLowerInvariant();

    /// <summary>What the command bound to a digit is called.</summary>
    /// <param name="slot">Which digit, 0-based.</param>
    /// <returns>The command id.</returns>
    public static string SlotCommand(int slot) => "foliage.tool-" + (slot + 1);

    /// <summary>Makes the brush bigger.</summary>
    public const string GrowBrushCommand = "foliage.brush-grow";

    /// <summary>And smaller.</summary>
    public const string ShrinkBrushCommand = "foliage.brush-shrink";

    /// <summary>Adds a type to the palette.</summary>
    public const string AddTypeCommand = "foliage.type-add";

    /// <summary>Removes the chosen ones, and every instance of them.</summary>
    public const string RemoveTypeCommand = "foliage.type-remove";

    /// <summary>Deselects every instance.</summary>
    public const string DeselectCommand = "foliage.deselect";

    /// <summary>Deletes the selected instances.</summary>
    public const string DeleteSelectionCommand = "foliage.delete-selection";

    /// <summary>Every verb the mode registers besides the tools.</summary>
    public static IReadOnlyList<string> Commands { get; } = [
        GrowBrushCommand,
        ShrinkBrushCommand,
        AddTypeCommand,
        RemoveTypeCommand,
        DeselectCommand,
        DeleteSelectionCommand
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
                    Category = CategoryFoliage,
                    Context = FoliageContext,
                    Enablement = () => IsActive() && slot < Tools.Count
                }
            );

            shell.Keys.SetDefault(id, new KeyChord((InputKey)((int)InputKey.Number1 + slot), ModifierKeys.None));
        }

        foreach (var tool in Tools) {
            var chosen = tool;
            var id = ToolCommand(chosen);

            shell.Commands.Add(
                new EditorCommand(id, new StringId("editor.command." + id, chosen + " Foliage"), () => Tool = chosen) {
                    Category = CategoryFoliage,
                    Context = FoliageContext,
                    RadioGroup = ToolGroup,
                    Checked = () => Tool == chosen,

                    // ⚠ Select and Erase work over an empty palette because they act on what is
                    // already there; the four that place things do not.
                    Enablement = () => IsActive()
                        && (chosen is FoliageTool.Select or FoliageTool.Erase || HasPalette)
                }
            );
        }

        Verb(GrowBrushCommand, "Grow Brush", () => Editing.Brush.Resize(1), InputKey.RightBracket);
        Verb(ShrinkBrushCommand, "Shrink Brush", () => Editing.Brush.Resize(-1), InputKey.LeftBracket);

        shell.Commands.Add(
            new EditorCommand(
                AddTypeCommand,
                new StringId("editor.command." + AddTypeCommand, "Add Foliage Type"),
                () => {
                    if (Editing.Volume is { } volume) {
                        Run(new AddFoliageTypeCommand(volume, FoliageType.Of(NextTypeName())));
                        Editing.Choose(volume.Palette.Count - 1);
                    }
                }
            ) {
                Category = CategoryFoliage,
                Context = FoliageContext,
                Enablement = () => IsActive() && Editing.Volume is not null
            }
        );

        shell.Commands.Add(
            new EditorCommand(
                RemoveTypeCommand,
                new StringId("editor.command." + RemoveTypeCommand, "Remove Foliage Type"),
                () => { }
            ) {
                Category = CategoryFoliage,
                Context = FoliageContext,

                // ⚠ Unimplemented rather than absent, and the enablement says so. Removing a palette
                // entry renumbers every index above it — in the volume's chunks, in the selection and
                // in every undo entry on the stack — which is [§ T5]'s owed item rather than a line.
                Unavailable = new("editor.command.foliage.type-remove.unavailable",
                    "Removing a type renumbers every instance above it; not yet built."),
                Enablement = () => false
            }
        );

        Verb(DeselectCommand, "Deselect Foliage", Editing.Deselect, InputKey.Escape);

        shell.Commands.Add(
            new EditorCommand(
                DeleteSelectionCommand,
                new StringId("editor.command." + DeleteSelectionCommand, "Delete Selected Foliage"),
                () => DeleteSelection()
            ) {
                Category = CategoryFoliage,
                Context = FoliageContext,
                Enablement = () => IsActive() && Editing.Selection.Count > 0
            }
        );

        shell.Keys.SetDefault(DeleteSelectionCommand, new KeyChord(InputKey.Delete, ModifierKeys.None));

        void Verb(string id, string label, Action run, InputKey key) {
            shell.Commands.Add(
                new EditorCommand(id, new StringId("editor.command." + id, label), run) {
                    Category = CategoryFoliage,
                    Context = FoliageContext,
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
    ///     ⚠ <b>The stroke goes and the selection stays.</b> A half-painted stroke belongs to a
    ///     gesture that is over; a selection is a statement about which trees somebody is working on,
    ///     and losing it on a trip to the outliner is the version people complain about.
    /// </remarks>
    public void Deactivated() => Editing.Cancel();

    /// <inheritdoc />
    public bool Pointer(PointerEvent args) => false;

    /// <inheritdoc />
    public bool Key(KeyEvent args) => false;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The press is taken, for <see cref="TerrainMode" />'s reason</b>: a press that is going
    ///     to become a stroke must not also start the pane's rubber-band. What is <em>not</em> taken
    ///     is a press that misses every surface, because aiming at the sky is how somebody frames a
    ///     shot.
    /// </remarks>
    public bool Pointer(SceneViewport pane, PointerEvent args) {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(args);

        switch (args.Action) {
            case PointerAction.Pressed when args.Button == PointerButton.Primary:
                if (Ground(pane, args) is not { } start) {
                    return false;
                }

                if (Tool == FoliageTool.Select) {
                    Editing.Select(start, (args.Modifiers & ModifierKeys.Shift) != 0);
                    return true;
                }

                return Editing.Begin(start, (args.Modifiers & ModifierKeys.Shift) != 0);

            case PointerAction.Moved when Editing.IsStroking:
                if (Ground(pane, args) is { } over) {
                    Editing.Extend(over, (args.Modifiers & ModifierKeys.Shift) != 0);
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
    public bool Key(SceneViewport pane, KeyEvent args) {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Key != InputKey.Escape || !Editing.IsStroking) {
            return false;
        }

        Editing.Cancel();
        return true;
    }

    /// <summary>Ends the stroke and puts it on the document's stack.</summary>
    /// <returns>The entry, or null if the stroke changed nothing.</returns>
    public IEditorCommand? Commit() {
        var command = Editing.Commit();

        if (command is null) {
            return null;
        }

        Document?.Stack.Execute(command);
        Committed?.Invoke(command);

        return command;
    }

    /// <summary>Deletes what is selected, as one entry.</summary>
    public IEditorCommand? DeleteSelection() {
        if (Editing.Volume is not { } volume || Editing.Selection.Count == 0) {
            return null;
        }

        var addresses = Editing.Selection.ToArray();
        var instances = new List<FoliageInstance>(addresses.Length);
        var types = new List<int>(addresses.Length);

        foreach (var address in addresses) {
            if (volume.At(address) is { } instance) {
                instances.Add(instance);
                types.Add(address.Type);
            }
        }

        volume.Remove(addresses);
        Editing.Deselect();

        var command = new FoliageStrokeCommand(volume, [], instances, types, "Delete Foliage");

        Document?.Stack.Execute(command);
        Committed?.Invoke(command);

        return command;
    }

    /// <summary>Where a pointer meets a surface, in world XZ, or null if it misses.</summary>
    Vector2? Ground(SceneViewport pane, PointerEvent args) {
        var ray = pane.Ray(pane.Control.ToRender(args.X, args.Y));

        if (pane.Surfaces is { } probe && probe.Raycast(ray, out var hit)) {
            return new(hit.Point.X, hit.Point.Z);
        }

        // The ground plane, so a stroke at the edge of a level lands rather than doing nothing.
        if (MathF.Abs(ray.Direction.Y) < 1e-6f) {
            return null;
        }

        var distance = (GroundHeight - ray.Origin.Y) / ray.Direction.Y;

        if (distance <= 0f || distance > Reach) {
            return null;
        }

        var at = ray.GetPoint(distance);

        return new(at.X, at.Z);
    }

    void Run(IEditorCommand command) {
        if (Document is { } document) {
            document.Stack.Execute(command);
        } else {
            command.Do(null!);
        }
    }

    string NextTypeName() {
        var volume = Editing.Volume!;

        for (var index = volume.Palette.Count + 1; ; index++) {
            var name = "Type " + index;

            if (!volume.Palette.Any(type => string.Equals(type.Name, name, StringComparison.Ordinal))) {
                return name;
            }
        }
    }

    bool IsActive() => shell?.Modes.IsActive(ModeId) == true;

    /// <summary>Where the palette files the mode's verbs.</summary>
    static readonly StringId CategoryFoliage = new("editor.category.foliage", "Foliage");

    /// <summary>The radio group the six tools are in.</summary>
    const string ToolGroup = "foliage.tool";
}

/// <summary>Adding a palette entry, as one entry.</summary>
sealed class AddFoliageTypeCommand(FoliageVolume volume, FoliageType type) : IEditorCommand {
    /// <inheritdoc />
    public string Name => "Add Foliage Type";

    /// <inheritdoc />
    public void Do(EditorContext context) => volume.AddType(type);

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Only the last entry can be taken back, and this is only ever the last.</b> Removing
    ///     one from the middle renumbers every index above it — in the chunks, in the selection and in
    ///     every undo entry on the stack — which is why the panel's Remove is not built yet.
    /// </remarks>
    public void Undo(EditorContext context) {
        if (volume.Palette.Count > 0) {
            volume.ClearType(volume.Palette.Count - 1);
            volume.SetType(volume.Palette.Count - 1, type);
        }
    }
}
