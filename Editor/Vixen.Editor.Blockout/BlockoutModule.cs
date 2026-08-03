// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Plugin;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Rendering.Ecs;

namespace Vixen.Editor.Blockout;

/// <summary>Blockout, registering itself through the door a third-party plugin comes through.</summary>
/// <remarks>
///     <para>
///         <b>Doc 36 § F2 named this assembly twice: "Blockout is not a plugin; it is a project
///         reference."</b> It still is one — the editor's executable has to name the module in order
///         to activate it — but nothing about the <i>registration</i> is a shortcut any more. What is
///         below could be an assembly on somebody's disk without changing a line, and the assembly it
///         lives in cannot see the editor's application at all, which is the part a compiler enforces
///         rather than a convention.
///     </para>
///     <para>
///         ⚠ <b>What it takes from the host, it asks for.</b> Five things: the shared mesh-editing
///         state, the work plane, something that can bake a mesh into an asset, something that can
///         read one back, and the Scene menu. Each is a <c>Require</c> or a <c>FindMenu</c>, so a host
///         that has not got one refuses the module with a sentence naming it rather than throwing a
///         null reference from inside <see cref="Activate" />.
///     </para>
///     <para>
///         ⚠ <b>The editing state and the work plane are the <i>editor's</i>, not this module's.</b> A
///         mode outlives every scene the editor opens, and the plane is the one thing
///         <c>SceneGrid</c> draws, <c>SnapContext</c> snaps to and the cube-grid tool counts in — doc
///         24 § D5 in one line, and in more than one shipping editor it is two numbers and a bug
///         nobody manages to describe.
///     </para>
/// </remarks>
public sealed class BlockoutModule : IEditorPlugin {
    /// <summary>What the host activates it under, and what a plugin depending on it names.</summary>
    public const string ModuleId = "vixen.blockout";

    /// <summary>What a plugin-management panel calls it.</summary>
    public const string ModuleName = "Blockout";

    /// <summary>The Scene menu's title id, which is where its five submenus go.</summary>
    /// <remarks>
    ///     ⚠ <b>Not a menu of its own, and <c>IEditorMode</c>'s remarks say why.</b> A mode with a
    ///     top-level menu is a menu that appears and disappears as somebody presses keys; these
    ///     entries are greyed while the mode is inactive instead, which is one stable menu and honest.
    /// </remarks>
    const string SceneMenu = "editor.menu.scene";

    /// <summary>The submenu these go in after — doc 24 § D5's placement-and-precision group.</summary>
    const string After = "editor.menu.precision";

    /// <inheritdoc />
    public void Activate(PluginContext context) {
        ArgumentNullException.ThrowIfNull(context);

        var baker = context.Services.Require<IMeshBaker>();

        context.AddMode(
            new BlockoutMode {
                Editing = context.Services.Require<MeshEdit>(),
                Plane = context.Services.Require<WorkPlane>(),
                Baker = baker,
                Meshes = context.Services.Require<IMeshSource>(),
                Export = (text, extension) => baker.Bake("Export", extension, text)
            }
        );

        Menus(context);
    }

    /// <summary>Doc 24's P2–P7 tables, as five submenus of the Scene menu.</summary>
    /// <remarks>
    ///     ⚠ <b>Inserted where they were rather than appended.</b> Appending would put the geometry
    ///     verbs after the camera bookmarks — a visible reordering of somebody's menu caused by a
    ///     refactor they cannot see. <c>IndexOfSubmenu</c> is how a module says "after Measure"
    ///     without holding a number that moves the next time a line is added above it.
    /// </remarks>
    static void Menus(PluginContext context) {
        if (context.FindMenu(SceneMenu) is not { } scene) {
            // A host with no Scene menu is a host with no scene panel — a thumbnail renderer, a
            // test. The mode is still registered and its commands are still in the palette.
            return;
        }

        var at = scene.IndexOfSubmenu(After) + 1;

        // ⚠ Doc 24's P2 selection table. Each `AddSubmenu` inserts at `at` and the counter walks
        // forward, so the five read in this order rather than in reverse.
        context.AddSubmenu(scene, new StringId("editor.menu.elements", "Select Elements"), at++)
            .Add(BlockoutMode.SelectAllCommand, BlockoutMode.SelectNoneCommand, BlockoutMode.InvertCommand)
            .AddSeparator()
            .Add(BlockoutMode.SelectLoopCommand, BlockoutMode.SelectRingCommand)
            .Add(BlockoutMode.GrowCommand, BlockoutMode.ShrinkCommand)
            .AddSeparator()
            .Add(BlockoutMode.SelectGroupCommand, BlockoutMode.SelectCoplanarCommand, BlockoutMode.SelectLinkedCommand);

        // ⚠ Doc 24's P3 Geometry table, all fourteen of it, where the mode's toolbar shows four. A
        // strip of fourteen buttons is one nobody reads; a menu of fourteen verbs is where somebody
        // goes to find out what a mode can do, and the shortcuts are drawn beside them.
        context.AddSubmenu(scene, new StringId("editor.menu.geometry", "Geometry"), at++)
            .Add(BlockoutMode.ExtrudeCommand, BlockoutMode.ExtrudeIndividualCommand)
            .Add(BlockoutMode.InsetCommand, BlockoutMode.InsetIndividualCommand)
            .Add(BlockoutMode.BevelCommand, BlockoutMode.LoopCutCommand, BlockoutMode.SubdivideCommand)
            .AddSeparator()
            .Add(BlockoutMode.BridgeCommand, BlockoutMode.FillCommand, BlockoutMode.WeldCommand)
            .Add(BlockoutMode.DissolveCommand, BlockoutMode.DeleteCommand)
            .AddSeparator()
            .Add(BlockoutMode.FlipCommand, BlockoutMode.DetachCommand);

        // ⚠ Doc 24's P4 Creation table. The twelve shapes are a submenu of their own inside it,
        // because choosing what the tool makes and reaching for the tool are two acts — a flat list
        // of twelve "Create Stairs" entries beside "Duplicate" would bury the four verbs somebody
        // actually runs.
        var creation = context.AddSubmenu(scene, new StringId("editor.menu.blockout-create", "Create"), at++);
        var kinds = creation.AddSubmenu(new StringId("editor.menu.blockout-shape", "Shape"));

        foreach (var kind in BlockoutMode.Kinds) {
            kinds.Add(BlockoutMode.KindCommand(kind));
        }

        creation
            .Add(BlockoutMode.ShapeToolCommand, BlockoutMode.CreateShapeCommand)
            .AddSeparator()
            .Add(BlockoutMode.CubeGridCommand, BlockoutMode.PushOutCommand, BlockoutMode.PushInCommand)
            .AddSeparator()
            .Add(BlockoutMode.DuplicateCommand, BlockoutMode.MirrorCommand)
            .Add(BlockoutMode.ArrayCommand, BlockoutMode.RadialCommand);

        // And P5's, less the material assignment — which comes from a palette rather than from a
        // key, and a palette is the inspector's.
        context.AddSubmenu(scene, new StringId("editor.menu.blockout-surfaces", "Surfaces"), at++)
            .Add(BlockoutMode.ProjectWorldCommand, BlockoutMode.ProjectBoxCommand, BlockoutMode.FitUvCommand)
            .AddSeparator()
            .Add(BlockoutMode.SmoothCommand, BlockoutMode.HardenCommand, BlockoutMode.AutoSmoothCommand)
            .AddSeparator()
            .Add(BlockoutMode.NewGroupCommand);

        // ⚠ Doc 24's P6 and P7. The booleans are Object-mode verbs and sit beside the creation ones
        // rather than inside Geometry, because what they act on is entities: a subtract of two walls
        // is a statement about the outliner, not about a face selection.
        context.AddSubmenu(scene, new StringId("editor.menu.blockout-boolean", "Boolean"), at++)
            .Add(BlockoutMode.UnionCommand, BlockoutMode.SubtractCommand, BlockoutMode.IntersectCommand)
            .AddSeparator()
            .Add(BlockoutMode.PlaneCutCommand, BlockoutMode.TrimCommand)
            .AddSeparator()
            .Add(BlockoutMode.ApplyBooleanCommand);

        context.AddSubmenu(scene, new StringId("editor.menu.blockout-handoff", "Handoff"), at)
            .Add(BlockoutMode.BakeCommand, BlockoutMode.EditableCommand)
            .AddSeparator()
            .Add(BlockoutMode.ExportObjCommand, BlockoutMode.ExportGltfCommand);
    }
}
