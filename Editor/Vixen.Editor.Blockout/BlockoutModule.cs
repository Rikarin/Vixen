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
///         ⚠ <b>Its menus are declared, not built (doc 50 § P1).</b> The six Scene submenus are the
///         <c>[EditorSubmenu]</c>s on this class and every line in them is an <c>[EditorMenu]</c> on
///         the command it names, in <see cref="BlockoutMode" />. <c>Vixen.Editor.Plugin.Generator</c>
///         turns them into <c>Declared.Register</c> at compile time, and <see cref="Activate" /> calls
///         it last — so the first in-tree author of the attributes is the toolset doc 36 named as
///         "not a plugin", and the arrangement doc 24's tables give is held by
///         <c>BlockoutReachTests</c> in the editor the executable builds.
///     </para>
///     <para>
///         ⚠ <b>What it takes from the host, it asks for.</b> The shared mesh-editing state, the work
///         plane, something that can bake a mesh into an asset and something that can read one back
///         are each a <c>Require</c>, so a host that has not got one refuses the module with a
///         sentence naming it rather than throwing a null reference from inside <see cref="Activate" />.
///     </para>
///     <para>
///         ⚠ <b>The editing state and the work plane are the <i>editor's</i>, not this module's.</b> A
///         mode outlives every scene the editor opens, and the plane is the one thing
///         <c>SceneGrid</c> draws, <c>SnapContext</c> snaps to and the cube-grid tool counts in — doc
///         24 § D5 in one line, and in more than one shipping editor it is two numbers and a bug
///         nobody manages to describe.
///     </para>
/// </remarks>
[EditorSubmenu(
    ElementsMenu,
    Title = nameof(EditorStrings.MenuElements),
    Strings = typeof(EditorStrings),
    After = PrecisionMenu,
    Priority = 1
)]
[EditorSubmenu(
    GeometryMenu,
    Title = nameof(EditorStrings.MenuGeometry),
    Strings = typeof(EditorStrings),
    After = "editor.menu.elements",
    Priority = 2
)]
[EditorSubmenu(CreateMenu, Title = nameof(BlockoutStrings.MenuCreate), After = "editor.menu.geometry", Priority = 3)]
[EditorSubmenu(ShapeMenu, Title = nameof(BlockoutStrings.MenuShape))]
[EditorSubmenu(SurfacesMenu, Title = nameof(BlockoutStrings.MenuSurfaces), After = "editor.menu.blockout-create", Priority = 4)]
[EditorSubmenu(BooleanMenu, Title = nameof(BlockoutStrings.MenuBoolean), After = "editor.menu.blockout-surfaces", Priority = 5)]
[EditorSubmenu(HandoffMenu, Title = nameof(BlockoutStrings.MenuHandoff), After = "editor.menu.blockout-boolean", Priority = 6)]
public sealed partial class BlockoutModule : IEditorPlugin {
    /// <summary>What the host activates it under, and what a plugin depending on it names.</summary>
    public const string ModuleId = "vixen.blockout";

    /// <summary>What a plugin-management panel calls it.</summary>
    public const string ModuleName = "Blockout";

    /// <summary>The submenu these go in after — doc 24 § D5's placement-and-precision group.</summary>
    /// <remarks>
    ///     ⚠ <b>Inserted where they were rather than appended.</b> Appending would put the geometry
    ///     verbs after the camera bookmarks — a visible reordering of somebody's menu caused by a
    ///     refactor they cannot see. <c>After</c> names the sibling by id, so a line added above Measure
    ///     does not move them.
    /// </remarks>
    const string PrecisionMenu = "editor.menu.precision";

    // ⚠ The Scene menu, and not a menu of Blockout's own: a mode with a top-level menu is a menu
    // that appears and disappears as somebody presses keys, and `IEditorMode`'s remarks say why that
    // is worse. These lines are greyed while the mode is inactive instead — one stable menu, and
    // honest. The paths are keys for the declarations; what each submenu is called is its `Title`.

    /// <summary>Doc 24's P2 selection table.</summary>
    internal const string ElementsMenu = "Scene/Elements";

    /// <summary>
    ///     Doc 24's P3 Geometry table, all fifteen of it — fourteen verbs and the knife — where the
    ///     mode's toolbar shows four. A strip of fifteen buttons is one nobody reads; a menu of them is
    ///     where somebody goes to find out what a mode can do, and the shortcuts are drawn beside them.
    /// </summary>
    internal const string GeometryMenu = "Scene/Geometry";

    /// <summary>Doc 24's P4 Creation table.</summary>
    internal const string CreateMenu = "Scene/Create";

    /// <summary>
    ///     The twelve shapes, a submenu of their own inside Creation: choosing what the tool makes and
    ///     reaching for the tool are two acts, and twelve "Create Stairs" lines beside "Duplicate" would
    ///     bury the four verbs somebody actually runs.
    /// </summary>
    internal const string ShapeMenu = "Scene/Create/Shape";

    /// <summary>
    ///     Doc 24's P5, less the material assignment — which comes from a palette rather than from a
    ///     key, and a palette is the inspector's.
    /// </summary>
    internal const string SurfacesMenu = "Scene/Surfaces";

    /// <summary>
    ///     Doc 24's P6. Object-mode verbs, beside the creation ones rather than inside Geometry,
    ///     because what they act on is entities: a subtract of two walls is a statement about the
    ///     outliner, not about a face selection.
    /// </summary>
    internal const string BooleanMenu = "Scene/Boolean";

    /// <summary>Doc 24's P7.</summary>
    internal const string HandoffMenu = "Scene/Handoff";

    /// <inheritdoc />
    public void Activate(PluginContext context) {
        ArgumentNullException.ThrowIfNull(context);

        var baker = context.Services.Require<IMeshBaker>();

        shell = context.Shell;

        // ⚠ The toolset's own words, handed over the way its panels and its commands are. An
        // `All` list nothing walks is in no translator's template, which is the state
        // `StringFamily` exists to end one level down (#1202).
        context.AddStrings(BlockoutStrings.All);

        mode.Editing = context.Services.Require<MeshEdit>();
        mode.Plane = context.Services.Require<WorkPlane>();
        mode.Baker = baker;
        mode.Meshes = context.Services.Require<IMeshSource>();
        mode.Export = (text, extension) => baker.Bake("Export", extension, text);

        // ⚠ Before the mode, because `IEditorMode.Panel` names a panel the shell has to have heard
        // of — a mode that opens a panel nobody registered opens nothing, silently. The same
        // ordering the terrain module uses, and for the same reason.
        SettingsPanels(context);

        context.AddMode(mode);

        // ⚠ Last, and after the mode in particular: every line it declares names a command the mode's
        // `Register` made, and a hand registration made before it is the one that wins — the rule the
        // scan a disk plugin gets follows too.
        Declared.Register(context);
    }
}
