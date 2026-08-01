// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Geometry;
using Vixen.Input;
using Vixen.Rendering.Ecs;
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
            drag = value is null ? null : new(value.Document) { Kind = Shape, Plane = Plane };

            Apply(Element);
        }
    }

    /// <summary>The shape-tool gesture, or null while the mode drives no scene.</summary>
    /// <remarks>Exposed so that a test can drive the two stages with world points and assert the mesh
    ///     — which is the seam <see cref="ShapeDrag" /> exists to put there.</remarks>
    public ShapeDrag? Drag => drag;

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

    /// <summary>Arms the shape tool, so a drag on the work plane makes geometry.</summary>
    public const string ShapeToolCommand = "blockout.shape-tool";

    /// <summary>Makes one of the current shape at its default size, at the work plane's origin.</summary>
    public const string CreateShapeCommand = "blockout.create-shape";

    /// <summary>Puts a box on the lattice at the work plane's origin.</summary>
    public const string CubeGridCommand = "blockout.cube-grid";

    /// <summary>Pushes the selected box's far side out by a cell, and its near side with <c>Shift</c>.</summary>
    public const string PushOutCommand = "blockout.push-out";

    /// <summary>And pulls it in.</summary>
    public const string PushInCommand = "blockout.push-in";

    /// <summary>Copies what is selected.</summary>
    public const string DuplicateCommand = "blockout.duplicate";

    /// <summary>Reflects a copy of it across the work plane.</summary>
    public const string MirrorCommand = "blockout.mirror";

    /// <summary>Repeats it along a line.</summary>
    public const string ArrayCommand = "blockout.array";

    /// <summary>Repeats it round a circle.</summary>
    public const string RadialCommand = "blockout.radial";

    /// <summary>Projects the selected faces' texture coordinates in world space.</summary>
    public const string ProjectWorldCommand = "blockout.project-world";

    /// <summary>Ditto in the object's own space.</summary>
    public const string ProjectBoxCommand = "blockout.project-box";

    /// <summary>Stretches each selected face's coordinates to cover one repeat.</summary>
    public const string FitUvCommand = "blockout.fit-uv";

    /// <summary>Puts the selected faces in a smoothing group.</summary>
    public const string SmoothCommand = "blockout.smooth";

    /// <summary>Takes them out of one.</summary>
    public const string HardenCommand = "blockout.harden";

    /// <summary>Groups the whole mesh's faces by how sharply they meet.</summary>
    public const string AutoSmoothCommand = "blockout.auto-smooth";

    /// <summary>Puts the selected faces in a face group of their own.</summary>
    public const string NewGroupCommand = "blockout.new-group";

    /// <summary>What the command that creates a shape of a kind is called.</summary>
    /// <param name="kind">Which shape.</param>
    /// <returns>The command id.</returns>
    public static string KindCommand(ShapeKind kind) =>
        "blockout.shape." + kind.ToString().ToLowerInvariant();

    /// <summary>Everything in either of the selected solids, as a derived result.</summary>
    public const string UnionCommand = "blockout.union";

    /// <summary>The first selected solid, less the rest.</summary>
    public const string SubtractCommand = "blockout.subtract";

    /// <summary>Only what all the selected solids share.</summary>
    public const string IntersectCommand = "blockout.intersect";

    /// <summary>Collapses a derived result into a plain mesh and deletes its operands.</summary>
    public const string ApplyBooleanCommand = "blockout.apply-boolean";

    /// <summary>Cuts the selection with the work plane and keeps the near half.</summary>
    public const string PlaneCutCommand = "blockout.plane-cut";

    /// <summary>Cuts the first selected solid by the second's surface.</summary>
    public const string TrimCommand = "blockout.trim";

    /// <summary>Writes the selection into a mesh asset and points the entity at it.</summary>
    public const string BakeCommand = "blockout.bake";

    /// <summary>Makes an entity's mesh asset editable again.</summary>
    public const string EditableCommand = "blockout.make-editable";

    /// <summary>Writes the selection out as a Wavefront OBJ.</summary>
    public const string ExportObjCommand = "blockout.export-obj";

    /// <summary>Ditto as a glTF.</summary>
    public const string ExportGltfCommand = "blockout.export-gltf";

    /// <summary>Every boolean and cut, in the order the menu lists them.</summary>
    public static IReadOnlyList<string> BooleanCommands { get; } = [
        UnionCommand,
        SubtractCommand,
        IntersectCommand,
        ApplyBooleanCommand,
        PlaneCutCommand,
        TrimCommand
    ];

    /// <summary>And every handoff verb.</summary>
    public static IReadOnlyList<string> HandoffCommands { get; } = [
        BakeCommand,
        EditableCommand,
        ExportObjCommand,
        ExportGltfCommand
    ];

    /// <summary>Every creation verb, in the order the menu lists them.</summary>
    public static IReadOnlyList<string> CreationCommands { get; } = [
        ShapeToolCommand,
        CreateShapeCommand,
        CubeGridCommand,
        PushOutCommand,
        PushInCommand,
        DuplicateCommand,
        MirrorCommand,
        ArrayCommand,
        RadialCommand
    ];

    /// <summary>And every surface verb.</summary>
    public static IReadOnlyList<string> SurfaceCommands { get; } = [
        ProjectWorldCommand,
        ProjectBoxCommand,
        FitUvCommand,
        SmoothCommand,
        HardenCommand,
        AutoSmoothCommand,
        NewGroupCommand
    ];

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

    /// <summary>Which shape the shape tool makes.</summary>
    /// <remarks>What the palette's twelve "Create ⟨shape⟩" verbs set, and what a drag on the work
    ///     plane then produces — so choosing the shape and making one are two acts rather than
    ///     twelve tools.</remarks>
    public ShapeKind Shape {
        get;
        set {
            field = value;

            if (drag is not null) {
                drag.Kind = value;
            }
        }
    } = ShapeKind.Box;

    /// <summary>Whether a drag on the work plane makes a shape rather than starting a rubber-band.</summary>
    /// <remarks>
    ///     ⚠ <b>Armed rather than modal, and it disarms itself when a shape has been made.</b> A tool
    ///     that stayed armed turns the next attempt to select something into a shape nobody wanted; one
    ///     that has to be re-armed for each shape is what every reference toolset does and is what
    ///     <c>Shift+A</c> is for.
    /// </remarks>
    public bool IsArmed { get; set; }

    /// <summary>The work plane shapes are dragged on, or <see langword="null" /> for the ground.</summary>
    /// <remarks>The application's own — the same instance <c>SceneGrid</c> draws and
    ///     <c>SnapContext</c> snaps to, which is doc 24's D5 in one field.</remarks>
    public WorkPlane? Plane {
        get;
        set {
            field = value;

            if (drag is not null) {
                drag.Plane = value;
            }
        }
    }

    /// <summary>The shape-tool gesture in flight, or null while no scene is being edited.</summary>
    ShapeDrag? drag;

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

        // ⚠ Doc 24's Surfaces table, and every one of these is an element verb like the ones above —
        // "project these faces" needs faces. Assigning a material is the one that is not here: it
        // comes from a palette rather than from a key, and the palette is the inspector's.
        Verb(ProjectWorldCommand, "Project UVs (World)", editing => BlockoutSurfaces.Project(editing, UvProjection.World, UvScale));
        Verb(ProjectBoxCommand, "Project UVs (Object)", editing => BlockoutSurfaces.Project(editing, UvProjection.Box, UvScale));
        Verb(FitUvCommand, "Fit UVs", BlockoutSurfaces.Fit);
        Verb(SmoothCommand, "Smooth Faces", editing => BlockoutSurfaces.Smooth(editing));
        Verb(HardenCommand, "Harden Faces", editing => BlockoutSurfaces.Smooth(editing, smooth: false));
        Verb(AutoSmoothCommand, "Auto Smooth", editing => BlockoutSurfaces.AutoSmooth(editing));
        Verb(NewGroupCommand, "New Face Group", BlockoutSurfaces.Regroup);

        // ⚠ Doc 24's Creation table, and these are enabled in *Object* mode as well — unlike every
        // verb above them. Making a shape is not a statement about an element selection, and a tool
        // that could only be reached from inside a mesh would be one nobody could use to make the
        // first mesh.
        Make(ShapeToolCommand, "Shape Tool", () => IsArmed = true, InputKey.A, ModifierKeys.Shift);
        Make(CreateShapeCommand, "Create Shape", () => Created(BlockoutCreate.Shape(Scene!, Shape, Where())));
        Make(CubeGridCommand, "Cube Grid Box", () => Created(BlockoutCubeGrid.Create(Scene!, Cell(), Plane)), InputKey.G);
        Make(PushOutCommand, "Push Cells Out", () => Pushed(1), InputKey.RightBracket, ModifierKeys.Alt);
        Make(PushInCommand, "Pull Cells In", () => Pushed(-1), InputKey.LeftBracket, ModifierKeys.Alt);
        Make(DuplicateCommand, "Duplicate", () => BlockoutCreate.Duplicate(Scene!, Vector3.Zero), InputKey.D, ModifierKeys.Control);
        Make(MirrorCommand, "Mirror", () => BlockoutCreate.Mirror(Scene!, (Plane ?? Ground).AsPlane()), InputKey.M, ModifierKeys.Control);
        Make(ArrayCommand, "Array", () => Repeated(radial: false));
        Make(RadialCommand, "Radial Array", () => Repeated(radial: true));

        foreach (var kind in Kinds) {
            var chosen = kind;

            Make(KindCommand(chosen), "Shape: " + chosen, () => Shape = chosen, radio: true, kind: chosen);
        }

        // ⚠ Doc 24's P6 and P7, and both are Object-mode verbs like the creation ones above: a boolean
        // is a statement about two entities and a bake is a statement about one, and neither has
        // anything to do with which faces are selected.
        Make(UnionCommand, "Union", () => BlockoutBoolean.Union(Scene!));
        Make(SubtractCommand, "Subtract", () => BlockoutBoolean.Subtract(Scene!));
        Make(IntersectCommand, "Intersect", () => BlockoutBoolean.Intersect(Scene!));
        Make(ApplyBooleanCommand, "Apply Boolean", () => BlockoutBoolean.Collapse(Scene!));
        Make(PlaneCutCommand, "Plane Cut", () => BlockoutBoolean.PlaneCut(Scene!, (Plane ?? Ground).AsPlane()));
        Make(TrimCommand, "Trim", () => BlockoutBoolean.Trim(Scene!));

        Make(BakeCommand, "Bake To Mesh Asset", () => {
            if (Baker is { } baker) {
                BlockoutHandoff.Bake(Scene!, baker);
            }
        });

        Make(EditableCommand, "Make Mesh Editable", () => {
            if (Meshes is { } source) {
                BlockoutHandoff.Editable(Scene!, source);
            }
        });

        Make(ExportObjCommand, "Export OBJ…", () => Exported(".obj"));
        Make(ExportGltfCommand, "Export glTF…", () => Exported(".gltf"));

        void Make(
            string id,
            string label,
            Action run,
            InputKey key = InputKey.Unknown,
            ModifierKeys modifiers = ModifierKeys.None,
            bool radio = false,
            ShapeKind kind = default
        ) {
            shell.Commands.Add(
                new EditorCommand(id, new StringId("editor.command." + id, label), () => {
                    if (radio || Scene is not null) {
                        run();
                    }
                }) {
                    Category = CategoryBlockout,
                    Context = BlockoutContext,
                    RadioGroup = radio ? KindGroup : null,
                    Checked = radio ? () => Shape == kind : null,
                    Enablement = IsActive
                }
            );

            if (key != InputKey.Unknown) {
                shell.Keys.SetDefault(id, new KeyChord(key, modifiers));
            }
        }

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

        foreach (var command in SelectionCommands.Concat(GeometryCommands)
                     .Concat(CreationCommands)
                     .Concat(SurfaceCommands)
                     .Concat(BooleanCommands)
                     .Concat(HandoffCommands)) {
            shell.Commands.Remove(command);
        }

        foreach (var kind in Kinds) {
            shell.Commands.Remove(KindCommand(kind));
        }

        this.shell = null;
    }

    /// <summary>Runs a selection verb against the editing state, if there is one.</summary>
    void Run(Func<MeshEdit, bool> verb) {
        if (Editing is { } editing && editing.IsActive) {
            verb(editing);
        }
    }

    /// <summary>How many world units one repeat of a texture covers, for the projection verbs.</summary>
    /// <remarks>Kept beside <see cref="Step" /> rather than read from the work plane, because "a metre
    ///     a repeat" and "a metre a grid square" are the same number nine times in ten and are
    ///     deliberately not the same field: a level built on a four-metre grid still wants a checker
    ///     you can count.</remarks>
    public float UvScale { get; set; } = MeshSurfaces.DefaultScale;

    /// <summary>How many copies an array verb makes.</summary>
    public int Copies { get; set; } = 3;

    /// <summary>What puts a baked mesh into the project, or null while nothing can.</summary>
    /// <remarks>The application's, because importing an asset is the asset database's job and this
    ///     assembly does not reference it — see <see cref="IMeshBaker" />. Null greys the bake verb,
    ///     which is doc 20's "a verb that is not reachable right now is visibly not reachable".</remarks>
    public IMeshBaker? Baker { get; set; }

    /// <summary>Where a mesh reference's geometry comes from, for making one editable again.</summary>
    public IMeshSource? Meshes { get; set; }

    /// <summary>What to do with an exported file: its text and its extension.</summary>
    /// <remarks>⚠ <b>A callback rather than a path, because where a file goes is a dialog's answer</b>
    ///     and a viewport mode has no dialogs — the same reason <c>SceneDocument.Writer</c> is an
    ///     interface.</remarks>
    public Action<string, string>? Export { get; set; }

    /// <summary>The scene the creation verbs act on, or null while the mode drives none.</summary>
    SceneDocument? Scene => Editing?.Document;

    void Exported(string extension) {
        if (Scene is { } scene && Export is { } write) {
            var text = BlockoutHandoff.Export(scene, extension);

            if (text.Length > 0) {
                write(text, extension);
            }
        }
    }

    /// <summary>Every shape the tool can make, in the order a menu should offer them.</summary>
    /// <remarks>
    ///     ⚠ <b>Written out rather than taken from <c>Enum.GetValues</c></b>, for the reason
    ///     <c>PrimitiveShapes.All</c> gives: the enum's order is a file format's business and a menu's
    ///     order is what somebody reaches for most. The five level-design shapes come after the seven
    ///     everybody has, which is where a designer looking for "stairs" expects to find them.
    /// </remarks>
    public static IReadOnlyList<ShapeKind> Kinds { get; } = [
        ShapeKind.Box,
        ShapeKind.Plane,
        ShapeKind.Cylinder,
        ShapeKind.Cone,
        ShapeKind.Sphere,
        ShapeKind.Capsule,
        ShapeKind.Torus,
        ShapeKind.Stairs,
        ShapeKind.Ramp,
        ShapeKind.Arch,
        ShapeKind.Pipe,
        ShapeKind.DoorFrame
    ];

    /// <summary>The ground, for a mode nobody has given a work plane.</summary>
    static readonly WorkPlane Ground = new();

    /// <summary>Where a menu-run creation verb puts what it makes: the work plane's origin.</summary>
    /// <remarks>⚠ <b>Not the world origin and not the camera.</b> The work plane is where the designer
    ///     said they are building — D5's whole argument — so a shape made from a menu lands there and
    ///     a shape made from a drag lands where the drag was.</remarks>
    Vector3 Where() => Plane?.Origin ?? Vector3.Zero;

    GridBox Cell() {
        var cell = BlockoutCubeGrid.CellOf(Where(), Plane);

        return GridBox.At(cell.X, cell.Y, cell.Z);
    }

    void Created(Entity entity) {
        if (Scene is { } scene && !entity.IsNull) {
            scene.Selection.Set(entity);
        }
    }

    /// <summary>Pushes the selected box's side along the work plane's second axis.</summary>
    /// <remarks>
    ///     ⚠ <b>One axis and one side from the keyboard, which is the honest shape of a keyboard
    ///     verb.</b> Unreal's cube grid pushes whichever face the pointer is over; picking that face
    ///     needs a hover the tool does not draw yet — see <c>BlockoutCubeGrid</c> — so the keys push
    ///     upwards, which is the direction a block-out grows nine times in ten, and the other five
    ///     sides are a drag of the gizmo.
    /// </remarks>
    void Pushed(int cells) {
        if (Scene is not { } scene) {
            return;
        }

        foreach (var entity in scene.Selection.Items.ToArray()) {
            BlockoutCubeGrid.Push(scene, entity, axis: 1, positive: true, cells, Plane);
        }
    }

    void Repeated(bool radial) {
        if (Scene is not { } scene || scene.Selection.Count == 0) {
            return;
        }

        var entity = scene.Selection.Items[0];
        var step = Step;

        if (radial) {
            BlockoutCubeGrid.CellOf(Where(), Plane);
            BlockoutCreate.Radial(scene, entity, Where(), Plane?.Normal ?? Vector3.UnitY, Copies);
        } else {
            BlockoutCreate.Array(scene, entity, new(step, 0f, 0f), Copies);
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

        // ⚠ The shape tool comes first and it *takes* the gesture, which is the one place this mode
        // does. A press that is going to become a wall must not also start the pane's rubber-band, or
        // releasing it would select whatever the band swept over and leave the new wall deselected.
        if (drag is not null && (IsArmed || drag.Stage != ShapeStage.Idle) && Shaping(pane, args)) {
            return true;
        }

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
    /// <remarks>
    ///     ⚠ <b>Only <c>Escape</c>, and only while the shape tool has something in flight.</b> Numeric
    ///     entry, the gizmo's own keys and every other Escape are the pane's; a mode that took the key
    ///     unconditionally would break cancelling a gizmo drag. A half-dragged shape is the one thing
    ///     the pane cannot know how to abandon.
    /// </remarks>
    public bool Key(SceneViewport pane, KeyEvent args) {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Key != InputKey.Escape || drag is not { Stage: not ShapeStage.Idle }) {
            return false;
        }

        drag.Cancel();
        IsArmed = false;

        return true;
    }

    /// <summary>Drives the two-stage shape gesture from a pointer over a pane.</summary>
    /// <returns>Whether the event was taken.</returns>
    /// <remarks>
    ///     ⚠ <b>The footprint is read off the work plane and the height off a vertical plane through
    ///     the anchor.</b> A ray has to meet <i>something</i> for a pointer to mean a distance, and the
    ///     two stages are asking two different questions: "where on the floor" and "how far up". The
    ///     second plane faces the camera so that a pointer moved anywhere on screen has an answer,
    ///     rather than going to infinity when the view is edge-on to it.
    /// </remarks>
    bool Shaping(SceneViewport pane, PointerEvent args) {
        if (drag is not { } gesture) {
            return false;
        }

        var ray = pane.Ray(pane.Control.ToRender(args.X, args.Y));
        var plane = Plane ?? Ground;

        switch (args.Action) {
            case PointerAction.Pressed when args.Button == PointerButton.Primary && gesture.Stage == ShapeStage.Idle:
                if (!On(ray, plane.AsPlane(), out var corner)) {
                    return false;
                }

                gesture.Plane = Plane;
                gesture.Kind = Shape;
                gesture.Begin(Snapped(corner, plane));

                return true;

            case PointerAction.Moved when gesture.Stage == ShapeStage.Footprint:
                if (On(ray, plane.AsPlane(), out var opposite)) {
                    gesture.Drag(Snapped(opposite, plane));
                }

                return true;

            case PointerAction.Released when gesture.Stage == ShapeStage.Footprint:
                if (!gesture.Settle()) {
                    IsArmed = false;
                }

                return true;

            case PointerAction.Moved when gesture.Stage == ShapeStage.Height:
                var origin = gesture.Origin();
                var facing = Vector3.Cross(plane.Normal, Vector3.Cross(ray.Direction, plane.Normal));

                if (facing.IsZero) {
                    return true;
                }

                var upright = new Plane(Vector3.Normalize(facing), -Vector3.Dot(Vector3.Normalize(facing), origin));

                if (ray.Intersects(upright, out var along) && along > 0f) {
                    var raised = plane.ToLocal(ray.GetPoint(along)).Y - plane.ToLocal(origin).Y;

                    gesture.Raise(Round(raised, plane));
                }

                return true;

            case PointerAction.Pressed when gesture.Stage == ShapeStage.Height:
                gesture.Commit();
                IsArmed = false;

                return true;

            default:
                return gesture.Stage != ShapeStage.Idle;
        }
    }

    static bool On(Ray ray, Plane plane, out Vector3 point) {
        if (ray.Intersects(plane, out var distance) && distance > 0f) {
            point = ray.GetPoint(distance);
            return true;
        }

        point = default;
        return false;
    }

    /// <summary>A point on the work plane, rounded to its step.</summary>
    /// <remarks>⚠ <b>The plane's own step and not the snap context's increment</b>, because a shape
    ///     dragged out on a four-metre grid should land on it whether or not snapping is switched on —
    ///     which is what makes the two boxes beside each other line up.</remarks>
    static Vector3 Snapped(Vector3 point, WorkPlane plane) {
        if (plane.Step is not { } step || step <= WorkPlane.MinimumStep) {
            return point;
        }

        var local = plane.ToLocal(point);

        return plane.ToWorld(new(MathF.Round(local.X / step) * step, local.Y, MathF.Round(local.Z / step) * step));
    }

    static float Round(float value, WorkPlane plane) =>
        plane.Step is { } step && step > WorkPlane.MinimumStep ? MathF.Round(value / step) * step : value;

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

    /// <summary>The radio group the twelve shape kinds are in.</summary>
    const string KindGroup = "blockout.shape";
}
