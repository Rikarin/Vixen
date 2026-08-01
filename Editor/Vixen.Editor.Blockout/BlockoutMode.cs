// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Geometry;
using Vixen.Input;
using Vixen.Ui;

namespace Vixen.Editor.Blockout;

/// <summary>The viewport mode the grey-boxing tools live in.</summary>
/// <remarks>
///     <para>
///         <b>The second mode, and the one doc 20's <c>IEditorMode</c> was written for.</b> A1 asks
///         for the interface to ship with one mode so the seam is proven; doc 24's B2 is the argument
///         that a seam with one implementation is a hypothesis, and that blockout is what turns it
///         into a consumer — because it needs first refusal on viewport input, its own toolbar, and a
///         claim on keys that already mean something.
///     </para>
///     <para>
///         ⚠ <b>So far it owns its keys and nothing else, and that is doc 24's P0 exactly.</b> There
///         is no editable mesh in the engine yet — <c>Core/Vixen.Geometry</c> is P1 — so
///         <see cref="Element" /> is a statement about what a click <i>would</i> select rather than
///         something that selects it, and <see cref="Pointer" /> declines every event. What is real
///         is the arbitration: while this mode is active <c>1</c>, <c>2</c>, <c>3</c> and <c>4</c> in
///         the viewport are the element modes, and while it is not they are view-bookmark recall. That
///         is the thing that could not be retrofitted, and it is the thing this mode is here to prove.
///     </para>
///     <para>
///         ⚠ <b>The element commands are registered whether or not the mode is active, and scoped so
///         that they are only <i>reachable</i> while it is.</b> Registering them from
///         <see cref="Activated" /> would keep them out of the keybinding editor and out of the
///         palette until somebody had entered the mode once, which is how a rebindable shortcut
///         becomes undiscoverable.
///     </para>
/// </remarks>
public sealed class BlockoutMode : IEditorMode, IViewportInput {
    /// <summary>What the mode is called, everywhere an id is wanted.</summary>
    public const string ModeId = "blockout";

    /// <summary>The command context the mode claims while it is active.</summary>
    /// <remarks>
    ///     ⚠ <b>The same string as <see cref="ModeId" /> and deliberately a separate constant.</b> One
    ///     is what the mode bar's button is called and the other is what a saved keymap files a
    ///     binding under; they coincide today and a rename of either must not silently move the other.
    /// </remarks>
    public const string BlockoutContext = "blockout";

    EditorShell? shell;

    /// <summary>The element mode <c>Tab</c> goes back into, which is the last one that was not Object.</summary>
    BlockoutElement inside = BlockoutElement.Face;

    /// <inheritdoc />
    public string Id => ModeId;

    /// <inheritdoc />
    public StringId Title { get; } = new("editor.mode.blockout", "Blockout");

    /// <inheritdoc />
    /// <remarks>
    ///     None, so the mode bar draws the word. Two glyphs on a strip that decides what every gesture
    ///     in the viewport means is two glyphs somebody has to learn — see <see cref="IEditorMode.Icon" />.
    /// </remarks>
    public PathBuilder? Icon => null;

    /// <inheritdoc />
    public string? Context => BlockoutContext;

    /// <inheritdoc />
    /// <remarks>None yet. The tool settings panel arrives with the tools, in doc 24's P3.</remarks>
    public string? Panel => null;

    /// <inheritdoc />
    /// <remarks>
    ///     The four element modes as one segmented control, which is what they are: a choice, not four
    ///     switches. The verbs join it a phase at a time.
    /// </remarks>
    public IReadOnlyList<ToolbarEntry> Toolbar { get; } = [
        new ToolbarGroup(ElementCommand(BlockoutElement.Object),
            ElementCommand(BlockoutElement.Vertex),
            ElementCommand(BlockoutElement.Edge),
            ElementCommand(BlockoutElement.Face)),

        // ⚠ Four verbs on the strip and ten in the menu, which is doc 24's own ordering rather than a
        // cut for space. Extrude, inset, bevel and loop cut are what a blockout pass is made of; a
        // toolbar with fourteen buttons is one nobody reads, and every one of them is a command with a
        // key and a place in the palette whether or not it has a button.
        new ToolbarSeparator(),
        new ToolbarButton(ExtrudeCommand),
        new ToolbarButton(InsetCommand),
        new ToolbarButton(BevelCommand),
        new ToolbarButton(LoopCutCommand)
    ];

    /// <summary>What a click in the viewport would select.</summary>
    public BlockoutElement Element {
        get;
        private set {
            if (field == value) {
                return;
            }

            field = value;

            // The one to come back to when `Tab` leaves the mesh and goes in again. Object is not a
            // place to come back to, so it is not remembered as one.
            if (value != BlockoutElement.Object) {
                inside = value;
            }

            Apply(value);
            ElementChanged?.Invoke(value);
        }
    } = BlockoutElement.Object;

    /// <summary>The editing state the mode drives, or <see langword="null" /> when it drives none.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Set by the application rather than made here, because it needs a scene.</b>
    ///         <see cref="MeshEdit" /> follows the entity selection and pushes the demotion onto the
    ///         document's undo stack, neither of which a mode can know about — a mode is registered
    ///         once and outlives every scene the editor opens.
    ///     </para>
    ///     <para>
    ///         Null leaves the mode owning its keys and nothing else, which is what it was before doc
    ///         24's P2 and what a test that only cares about arbitration still wants.
    ///     </para>
    /// </remarks>
    public MeshEdit? Editing {
        get;
        set {
            field = value;
            Apply(Element);
        }
    }

    /// <summary>Which element kind an element mode means to the kernel.</summary>
    /// <param name="element">The element mode.</param>
    /// <returns>The kind, or null for <see cref="BlockoutElement.Object" />.</returns>
    public static MeshElementKind? Kind(BlockoutElement element) =>
        element switch {
            BlockoutElement.Vertex => MeshElementKind.Vertex,
            BlockoutElement.Edge => MeshElementKind.Edge,
            BlockoutElement.Face => MeshElementKind.Face,
            _ => null
        };

    /// <summary>Puts the editing state into whichever element mode is chosen.</summary>
    void Apply(BlockoutElement element) {
        if (Editing is not { } editing) {
            return;
        }

        if (Kind(element) is { } kind) {
            editing.Enter(kind);
        } else {
            editing.Exit();
        }
    }

    /// <summary>Raised when <see cref="Element" /> changes.</summary>
    public event Action<BlockoutElement>? ElementChanged;

    /// <summary>What the command that selects an element mode is called.</summary>
    /// <param name="element">The element mode.</param>
    /// <returns>The command id.</returns>
    public static string ElementCommand(BlockoutElement element) =>
        "blockout.element." + element switch {
            BlockoutElement.Vertex => "vertex",
            BlockoutElement.Edge => "edge",
            BlockoutElement.Face => "face",
            _ => "object"
        };

    /// <summary>The command that enters and leaves the mesh.</summary>
    public const string ToggleMeshCommand = "blockout.toggle-mesh";

    /// <summary>Selects the edge loop through the active edge.</summary>
    public const string SelectLoopCommand = "blockout.select-loop";

    /// <summary>Selects the edge ring through it.</summary>
    public const string SelectRingCommand = "blockout.select-ring";

    /// <summary>Takes in everything touching the selection.</summary>
    public const string GrowCommand = "blockout.grow";

    /// <summary>Gives back everything on its rim.</summary>
    public const string ShrinkCommand = "blockout.shrink";

    /// <summary>Selects every face in the active face's group.</summary>
    public const string SelectGroupCommand = "blockout.select-group";

    /// <summary>Selects every face coplanar with and joined to it.</summary>
    public const string SelectCoplanarCommand = "blockout.select-coplanar";

    /// <summary>Selects every face joined to it.</summary>
    public const string SelectLinkedCommand = "blockout.select-linked";

    /// <summary>Selects every element of the current mode.</summary>
    public const string SelectAllCommand = "blockout.select-all";

    /// <summary>Deselects everything.</summary>
    public const string SelectNoneCommand = "blockout.select-none";

    /// <summary>Selects what is not selected.</summary>
    public const string InvertCommand = "blockout.invert";

    /// <summary>Pulls the selected faces out along their normal.</summary>
    public const string ExtrudeCommand = "blockout.extrude";

    /// <summary>Ditto, one face at a time.</summary>
    public const string ExtrudeIndividualCommand = "blockout.extrude-individual";

    /// <summary>Shrinks them towards their own centre.</summary>
    public const string InsetCommand = "blockout.inset";

    /// <summary>Ditto, one face at a time.</summary>
    public const string InsetIndividualCommand = "blockout.inset-individual";

    /// <summary>Cuts the corner off the selected edges.</summary>
    public const string BevelCommand = "blockout.bevel";

    /// <summary>Puts a loop across the ring the active edge is part of.</summary>
    public const string LoopCutCommand = "blockout.loop-cut";

    /// <summary>Splits the selected faces into one face per corner.</summary>
    public const string SubdivideCommand = "blockout.subdivide";

    /// <summary>Joins two selected faces with a tube.</summary>
    public const string BridgeCommand = "blockout.bridge";

    /// <summary>Puts a face across a hole.</summary>
    public const string FillCommand = "blockout.fill";

    /// <summary>Turns the selected faces inside out.</summary>
    public const string FlipCommand = "blockout.flip";

    /// <summary>Merges the selected positions into one.</summary>
    public const string WeldCommand = "blockout.weld";

    /// <summary>Removes the selected edges and keeps the surface.</summary>
    public const string DissolveCommand = "blockout.dissolve";

    /// <summary>Removes the selected faces, leaving a hole.</summary>
    public const string DeleteCommand = "blockout.delete";

    /// <summary>Takes the selected faces out into an entity of their own.</summary>
    public const string DetachCommand = "blockout.detach";

    /// <summary>Every geometry verb, in the order the menu lists them.</summary>
    public static IReadOnlyList<string> GeometryCommands { get; } = [
        ExtrudeCommand,
        ExtrudeIndividualCommand,
        InsetCommand,
        InsetIndividualCommand,
        BevelCommand,
        LoopCutCommand,
        SubdivideCommand,
        BridgeCommand,
        FillCommand,
        FlipCommand,
        WeldCommand,
        DissolveCommand,
        DeleteCommand,
        DetachCommand
    ];

    /// <summary>How far a verb run from the keyboard moves geometry, in the mesh's own space.</summary>
    /// <remarks>
    ///     ⚠ <b>A step rather than a drag, and it is the honest shape of a keyboard verb.</b> Doc 24's
    ///     inventory has extrude on <c>E</c> <i>and</i> on <c>Ctrl</c>+drag; the drag is what a designer
    ///     uses and the key is what makes the verb reachable, testable and rebindable. Typing an exact
    ///     distance afterwards is what <c>NumericEntry</c> already does for the gizmo, and it is the
    ///     same answer here.
    /// </remarks>
    public float Step { get; set; } = 1f;

    /// <summary>How many faces across a bevel run from the keyboard is.</summary>
    public int BevelSegments { get; set; } = 1;

    /// <summary>Every command the mode registers, in the order the menu lists them.</summary>
    /// <remarks>
    ///     ⚠ <b>One list, read by <see cref="Unregister" /> and by whatever builds a menu.</b> A mode
    ///     that removed its commands by naming them a second time is one where adding a verb and
    ///     forgetting the removal leaves a command bound to a mode that is gone — which is a key that
    ///     works until the shell is rebuilt.
    /// </remarks>
    public static IReadOnlyList<string> SelectionCommands { get; } = [
        SelectLoopCommand,
        SelectRingCommand,
        GrowCommand,
        ShrinkCommand,
        SelectGroupCommand,
        SelectCoplanarCommand,
        SelectLinkedCommand,
        SelectAllCommand,
        SelectNoneCommand,
        InvertCommand
    ];

    /// <inheritdoc />
    public void Register(EditorShell shell) {
        ArgumentNullException.ThrowIfNull(shell);
        this.shell = shell;

        Declare(BlockoutElement.Object, "Object Mode", InputKey.Number1);
        Declare(BlockoutElement.Vertex, "Vertex Mode", InputKey.Number2);
        Declare(BlockoutElement.Edge, "Edge Mode", InputKey.Number3);
        Declare(BlockoutElement.Face, "Face Mode", InputKey.Number4);

        shell.Commands.Add(
            new EditorCommand(ToggleMeshCommand, new StringId("editor.command.blockout.toggle-mesh", "Enter / Leave Mesh"), Toggle) {
                Category = CategoryBlockout,
                Context = BlockoutContext,
                Enablement = IsActive
            }
        );

        // ⚠ Tab, and it beats the interface's own focus traversal rather than fighting it.
        // `Keyboard.Dispatch` moves the focus only when the route left the event unhandled, and the
        // command dispatcher is on that route — so the binding wins while the blockout context has
        // the focus and Tab is ordinary focus movement everywhere else.
        shell.Keys.SetDefault(ToggleMeshCommand, new KeyChord(InputKey.Tab, ModifierKeys.None));

        // ⚠ The bindings doc 24's selection table names, and the four with no chord are deliberate.
        // Loop, ring, grow and shrink are gestures a designer runs constantly and the table gives each
        // of them a key; select-by-group, coplanar and linked are menu verbs there, because they are
        // run once per wall rather than once per second, and a chord for each would spend three keys
        // out of a mode that has to leave room for the geometry verbs.
        Verb(SelectLoopCommand, "Select Loop", editing => BlockoutSelection.Loop(editing), InputKey.L);
        Verb(SelectRingCommand, "Select Ring", editing => BlockoutSelection.Ring(editing), InputKey.R, ModifierKeys.Control);
        Verb(GrowCommand, "Grow Selection", BlockoutSelection.Grow, InputKey.Up, ModifierKeys.Control);
        Verb(ShrinkCommand, "Shrink Selection", BlockoutSelection.Shrink, InputKey.Down, ModifierKeys.Control);
        Verb(SelectGroupCommand, "Select Group", editing => BlockoutSelection.Group(editing));
        Verb(SelectCoplanarCommand, "Select Coplanar", editing => BlockoutSelection.Coplanar(editing));
        Verb(SelectLinkedCommand, "Select Linked", editing => BlockoutSelection.Linked(editing));
        Verb(SelectAllCommand, "Select All Elements", BlockoutSelection.All, InputKey.A, ModifierKeys.Control);
        Verb(SelectNoneCommand, "Deselect Elements", BlockoutSelection.None, InputKey.A, ModifierKeys.Alt);
        Verb(InvertCommand, "Invert Element Selection", BlockoutSelection.Invert, InputKey.I, ModifierKeys.Control);

        // ⚠ Doc 24's Geometry table, with the bindings it names. Extrude is first and alone in the
        // plan's ordering for a reason — every other verb is judged against how that one feels — and
        // the ones with no chord here are the ones the table itself files under "menu".
        Verb(ExtrudeCommand, "Extrude", editing => BlockoutGeometry.Extrude(editing, Step), InputKey.E);
        Verb(ExtrudeIndividualCommand, "Extrude Individual", editing => BlockoutGeometry.Extrude(editing, Step, individually: true), InputKey.E, ModifierKeys.Alt);
        Verb(InsetCommand, "Inset", editing => BlockoutGeometry.Inset(editing, Step * 0.25f), InputKey.I);
        Verb(InsetIndividualCommand, "Inset Individual", editing => BlockoutGeometry.Inset(editing, Step * 0.25f, individually: true), InputKey.I, ModifierKeys.Alt);
        Verb(BevelCommand, "Bevel", editing => BlockoutGeometry.Bevel(editing, Step * 0.25f, BevelSegments, out _), InputKey.B, ModifierKeys.Control);
        Verb(LoopCutCommand, "Loop Cut", editing => BlockoutGeometry.LoopCut(editing), InputKey.R, ModifierKeys.Control | ModifierKeys.Shift);
        Verb(SubdivideCommand, "Subdivide", editing => BlockoutGeometry.Subdivide(editing));
        Verb(BridgeCommand, "Bridge", BlockoutGeometry.Bridge, InputKey.E, ModifierKeys.Control);
        Verb(FillCommand, "Fill Hole", BlockoutGeometry.FillHole, InputKey.F);
        Verb(FlipCommand, "Flip Normals", BlockoutGeometry.Flip);
        Verb(WeldCommand, "Weld to Centre", editing => BlockoutGeometry.Weld(editing), InputKey.M);
        Verb(DissolveCommand, "Dissolve Edges", BlockoutGeometry.Dissolve, InputKey.X, ModifierKeys.Control);
        Verb(DeleteCommand, "Delete Faces", BlockoutGeometry.Delete, InputKey.X);
        Verb(DetachCommand, "Detach Faces", editing => BlockoutGeometry.Detach(editing) is not null, InputKey.P);

        void Verb(string id, string label, Func<MeshEdit, bool> run, InputKey key = InputKey.Unknown, ModifierKeys modifiers = ModifierKeys.None) {
            shell.Commands.Add(
                new EditorCommand(id, new StringId("editor.command." + id, label), () => Run(run)) {
                    Category = CategoryBlockout,
                    Context = BlockoutContext,

                    // ⚠ Inside the mesh rather than merely in the mode. Every one of these is a
                    // statement about elements, and offering "Grow Selection" while the mode is in
                    // Object is offering a verb whose subject does not exist — which in the palette
                    // reads as a feature that silently does nothing.
                    Enablement = () => IsActive() && Element != BlockoutElement.Object
                }
            );

            if (key != InputKey.Unknown) {
                shell.Keys.SetDefault(id, new KeyChord(key, modifiers));
            }
        }

        void Declare(BlockoutElement element, string label, InputKey key) {
            var id = ElementCommand(element);

            shell.Commands.Add(
                new EditorCommand(id, new StringId("editor.command." + id, label), () => this.Element = element) {
                    Category = CategoryBlockout,

                    // ⚠ This is the whole of doc 24's B2 in one line. The command belongs to the
                    // blockout context, so `KeyMap` files its chord under that context rather than
                    // globally — and `scene.bookmark-go-1`, which is bound to the same key with no
                    // context at all, keeps it everywhere the blockout context does not have the
                    // focus. Neither had to give up the key and neither had to move.
                    Context = BlockoutContext,

                    RadioGroup = ElementGroup,
                    Checked = () => this.Element == element,
                    Enablement = IsActive
                }
            );

            shell.Keys.SetDefault(id, new KeyChord(key, ModifierKeys.None));
        }
    }

    /// <inheritdoc />
    public void Unregister(EditorShell shell) {
        ArgumentNullException.ThrowIfNull(shell);

        shell.Commands.Remove(ElementCommand(BlockoutElement.Object));
        shell.Commands.Remove(ElementCommand(BlockoutElement.Vertex));
        shell.Commands.Remove(ElementCommand(BlockoutElement.Edge));
        shell.Commands.Remove(ElementCommand(BlockoutElement.Face));
        shell.Commands.Remove(ToggleMeshCommand);

        foreach (var command in SelectionCommands.Concat(GeometryCommands)) {
            shell.Commands.Remove(command);
        }

        this.shell = null;
    }

    /// <summary>Runs a selection verb against the editing state, if there is one.</summary>
    void Run(Func<MeshEdit, bool> verb) {
        if (Editing is { } editing && editing.IsActive) {
            verb(editing);
        }
    }

    /// <inheritdoc />
    public void Activated() {
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Back to Object on the way out, and it is not tidiness.</b> A sub-object element mode
    ///     is a claim about a mesh being edited; leaving it set while the mode is inactive would mean
    ///     re-entering blockout put the viewport straight back into face selection on whatever
    ///     happens to be selected now, which is rarely what was being edited a moment ago.
    /// </remarks>
    public void Deactivated() => Element = BlockoutElement.Object;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Only the pane-aware overload does anything, and this one cannot.</b> Selecting a face
    ///     needs to know which viewport the pointer is in — which camera, which render size, which
    ///     mesh — and none of that is on a <see cref="PointerEvent" />. The application offers panes
    ///     through <see cref="IViewportInput" />, which this mode implements; a host that only calls
    ///     this one gets the mode's keys and none of its gestures, which is exactly what it asked for.
    /// </remarks>
    public bool Pointer(PointerEvent args) => false;

    /// <inheritdoc />
    /// <remarks>Everything the mode owns from the keyboard is a command, and a command's key comes
    ///     through the keymap rather than through here.</remarks>
    public bool Key(KeyEvent args) => false;

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Hover is taken and clicks are not, and the split is the whole of doc 24's P2
    ///         gesture work.</b> A press in an element mode still starts the pane's rubber-band,
    ///         because <c>SceneViewport.EndSelect</c> already resolves a band against elements when a
    ///         mesh is being edited and a band too small to be a band against the element under the
    ///         pointer — one <see cref="Marquee" />, two questions, which is what
    ///         <c>docs/plan/20 § E2</c> asks for. Taking the press here would mean writing that
    ///         gesture a second time and having it disagree about what counts as a drag.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What is taken is the <i>move</i>, and it is taken by answering false.</b> Hover
    ///         has to be computed before the pane reads the move for its own purposes — the gizmo's
    ///         own hover — and both are wanted, so this updates the element under the pointer and
    ///         declines, which leaves the pane's behaviour exactly as it was.
    ///     </para>
    /// </remarks>
    public bool Pointer(SceneViewport pane, PointerEvent args) {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(args);

        if (Element == BlockoutElement.Object) {
            return false;
        }

        if (args.Action == PointerAction.Moved) {
            pane.HoverElement(pane.Control.ToRender(args.X, args.Y));
            return false;
        }

        // ⚠ Doc 24's inventory gives extrude two bindings — `E`, or `Ctrl`+drag the gizmo — and this
        // is the second. The press makes the geometry with a distance of zero and hands the drag
        // straight back to the pane, which is why `Extrude` builds its walls at zero rather than
        // waiting for the pointer to move: the drag that follows is an ordinary gizmo drag of the
        // face the extrude just made.
        if (args.Action == PointerAction.Pressed
            && args.Button == PointerButton.Primary
            && (args.Modifiers & ModifierKeys.Control) != 0
            && Editing is { IsActive: true } editing
            && !editing.Selection.IsEmpty
            && pane.Hover(pane.Control.ToRender(args.X, args.Y)) != GizmoHandle.None
            && BlockoutGeometry.Extrude(editing, 0f)) {
            // ⚠ Now rather than on the next frame's update: the gizmo is holding the face that was
            // there a moment ago, and this press is about to grab it.
            pane.RefreshTargets();
        }

        return false;
    }

    /// <inheritdoc />
    /// <remarks>Nothing: numeric entry, Escape and the gizmo's own keys are the pane's, and the
    ///     mode's verbs are commands.</remarks>
    public bool Key(SceneViewport pane, KeyEvent args) => false;

    /// <summary>Enters the mesh, or comes back out of it.</summary>
    void Toggle() =>
        Element = Element == BlockoutElement.Object ? inside : BlockoutElement.Object;

    /// <summary>Whether this mode is the shell's active one.</summary>
    /// <remarks>
    ///     ⚠ <b>Enablement as well as context, because the palette does not go through the keymap.</b>
    ///     Scoping keeps the chord from firing outside the mode; it does nothing about somebody
    ///     choosing "Face Mode" out of the command palette while they are in Select, which would set a
    ///     state nothing is reading and tick a button nothing is drawing.
    /// </remarks>
    bool IsActive() => shell?.Modes.IsActive(ModeId) == true;

    /// <summary>Where the palette files the mode's verbs.</summary>
    static readonly StringId CategoryBlockout = new("editor.category.blockout", "Blockout");

    /// <summary>The radio group the four element modes are in.</summary>
    const string ElementGroup = "blockout.element";
}
