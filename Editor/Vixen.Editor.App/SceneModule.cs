// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Plugin;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.App;

/// <summary>The Entity menu's hierarchy and placement verbs, as a module over <see cref="ISceneEditing" />.</summary>
/// <remarks>
///     <para>
///         <b>Doc 50 § D3, the first slice of #1290.</b> These lines were registered by
///         <c>EditorParity.EntityCommands</c> over private methods; they are now registered through the
///         <see cref="PluginContext" /> a third party is handed, over the service a third party can
///         <c>Require</c>.
///     </para>
///     <para>
///         ⚠ <b>Not yet the whole Scene menu the issue names.</b> The Edit menu's selection verbs
///         (Duplicate, Invert, Select Children, Select Parent) are still registered by the application
///         over this service — the clipboard loop beside them reserves ids by checking for a command,
///         and a module activated later would find its id taken by a planned placeholder. Set Parent
///         keeps its chooser there for the same reason <c>PlayModule</c>'s options stayed: the dialog
///         is this editor's, the move is the service's.
///     </para>
/// </remarks>
sealed class SceneModule : IEditorPlugin {
    /// <summary>What the host activates it under.</summary>
    public const string ModuleId = "vixen.scene";

    /// <summary>What the plugin manager calls it.</summary>
    public const string ModuleName = "Scene";

    /// <inheritdoc />
    public void Activate(PluginContext context) {
        var editing = context.Services.Require<ISceneEditing>();

        bool Selected() => editing.Active.Selection.Count > 0;

        Verb(context, "entity.create-child", EditorStrings.CommandEntityCreateChild, () => editing.CreateChild(), Selected);

        Verb(context, "entity.group", EditorStrings.CommandEntityGroup, () => editing.Group(), Selected);

        // ⚠ Two or more, unlike `entity.group` above, and the difference is what the verb means. A
        // group of one is a perfectly ordinary thing to want; a LOD chain of one is a group whose
        // only level is always the one drawn, which is what an author gets by doing nothing at all.
        Verb(
            context,
            "entity.group-lod",
            EditorStrings.CommandEntityGroupLod,
            () => editing.GroupAsLod(),
            () => editing.Active.Selection.Count > 1
        );

        Verb(context, "entity.clear-parent", EditorStrings.CommandEntityClearParent, editing.ClearParent, Selected);

        Verb(
            context,
            "entity.align-with-view",
            EditorStrings.CommandEntityAlignWithView,
            editing.AlignWithView,
            () => editing.View is not null && Selected()
        );

        Verb(
            context,
            "entity.move-to-view",
            EditorStrings.CommandEntityMoveToView,
            editing.MoveToView,
            () => editing.View is not null && Selected()
        );

        // ⚠ Not blocked on the picking readback after all. What Snap To Floor needs is "what does
        // this ray hit", and `SceneProbe` answers it exactly against the same geometry the viewport
        // draws — which is what makes it the right answer today and the wrong one the day a shader
        // moves a vertex. See `SceneProbe`'s own remarks.
        Verb(context, "entity.snap-to-floor", EditorStrings.CommandEntitySnapToFloor, editing.SnapToFloor, Selected);

        context.AddDefaultBinding("entity.create-child", new KeyChord(InputKey.N, ModifierKeys.Alt | ModifierKeys.Shift));
        context.AddDefaultBinding("entity.group", new KeyChord(InputKey.G, ModifierKeys.Control));
        context.AddDefaultBinding("entity.snap-to-floor", new KeyChord(InputKey.End, ModifierKeys.None));
    }

    static void Verb(PluginContext context, string id, StringId title, Action run, Func<bool> enabled) =>
        context.AddCommand(
            new EditorCommand(id, title, run) {
                Category = EditorStrings.CategoryEntity,
                Enablement = enabled
            }
        );
}
