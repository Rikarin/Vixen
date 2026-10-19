// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Editor.Plugin;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Input;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
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
///     <para>
///         <b>The second slice added Part D's four verbs that ask nothing</b> — Isolate and Reset, Copy
///         and Paste Transform. <b>The third added their six neighbours that ask</b> an axis, a name
///         or a set (Align, Distribute, Select By Name/Type, the selection sets), through
///         <see cref="SceneQuestions" />: the chooser they ask with is the shell's now, not the
///         application's.
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

        // ── Part D's verbs that ask nothing (#1290's second slice) ──────────────────────────────
        context.AddCommand(
            new EditorCommand("edit.isolate", EditorStrings.CommandEditIsolate, editing.Isolate) {
                Category = EditorStrings.CategoryEdit,
                Enablement = () => editing.IsIsolating || Selected(),
                Checked = () => editing.IsIsolating
            }
        );

        context.AddDefaultBinding("edit.isolate", new KeyChord(InputKey.I, ModifierKeys.Control | ModifierKeys.Shift));

        Verb(context, "entity.reset-transform", EditorStrings.CommandEntityResetTransform, editing.ResetTransform, Selected);

        Verb(
            context,
            "entity.copy-transform",
            EditorStrings.CommandEntityCopyTransform,
            () => {
                if (editing.CopyTransform()) {
                    context.Shell.Notifications.Success("Transform copied");
                }
            },
            () => editing.CanCopyTransform
        );

        Verb(
            context,
            "entity.paste-transform",
            EditorStrings.CommandEntityPasteTransform,
            editing.PasteTransform,
            () => editing.HasCopiedTransform && Selected()
        );

        // ── Part D's verbs that ask first (#1290's third slice) ─────────────────────────────────
        new SceneQuestions(context, editing).Register();

        // ── The create verbs (#1290's third slice) ──────────────────────────────────────────────
        // Enabled from the selection, so the menu item greys itself out with nothing here polling.
        context.AddCommand(
            new EditorCommand(
                "scene.create-entity",
                EditorStrings.CommandCreateEntity,
                () => editing.CreateEntity()
            ) {
                Category = EditorStrings.CategoryEdit
            }
        );

        context.AddDefaultBinding("scene.create-entity", new KeyChord(InputKey.N, ModifierKeys.Control | ModifierKeys.Shift));

        ShapeCommands(context, editing);
        ObjectCommands(context, editing);

        // ── The viewport's context list (#1489) ─────────────────────────────────────────────────
        // Its lines for this module's commands, offered by this module, so they leave with it. The
        // orders are the slots `SceneMenus.RegisterSceneMenus` gave them beside the application's.
        var registry = context.Services.Require<IEditorRegistry>();

        context.Owns(registry.Add(new SceneMenuItem("entity.snap-to-floor", SceneMenuSurface.Context) { Order = 4 }));
        context.Owns(registry.Add(new SceneMenuItem("entity.move-to-view", SceneMenuSurface.Context) { Order = 5 }));
        context.Owns(registry.Add(new SceneMenuItem("scene.create-entity", SceneMenuSurface.Context) { Order = 6 }));
    }

    /// <summary>One command per built-in shape, so that spawning one is not the menu's private trick.</summary>
    /// <remarks>
    ///     ⚠ <b>Eight commands rather than one that takes an argument.</b> The registry's unit is a
    ///     command with an id, a title and an enablement — that is what the palette searches, what the
    ///     keymap binds and what a menu line is built from — so "Create Cube" being findable in the
    ///     palette and bindable to a key means it has to be its own entry. Generated from
    ///     <see cref="PrimitiveShapes.All" />, so a shape added there appears everywhere without anything
    ///     here being edited.
    /// </remarks>
    static void ShapeCommands(PluginContext context, ISceneEditing editing) {
        foreach (var kind in PrimitiveShapes.All) {
            var shape = kind;
            var name = PrimitiveShapes.NameOf(shape);

            context.AddCommand(
                new EditorCommand(
                    ShapeCommandId(shape),
                    EditorStrings.CreateShapes[name.ToLowerInvariant()],
                    () => editing.CreateShape(shape)
                ) {
                    Category = EditorStrings.CategoryCreate
                }
            );
        }
    }

    /// <summary>What a shape's create command is called in the registry.</summary>
    internal static string ShapeCommandId(PrimitiveKind kind) =>
        "scene.create-" + PrimitiveShapes.NameOf(kind).ToLowerInvariant();

    /// <summary>One command per kind of light, and one for a camera.</summary>
    /// <remarks>
    ///     ⚠ <b>Commands rather than menu lines, which is <see cref="ShapeCommands" />' argument
    ///     applied to the other half of a Create menu.</b> "Create Point Light" being findable in the
    ///     palette and bindable to a key is what makes it a thing the editor can do, rather than a
    ///     thing one menu happens to offer — and it is why the Scene menu and the hierarchy's context
    ///     menu can both name it without either of them owning it.
    /// </remarks>
    static void ObjectCommands(PluginContext context, ISceneEditing editing) {
        foreach (var kind in Lights.All) {
            var light = kind;

            context.AddCommand(
                new EditorCommand(
                    LightCommandId(light),
                    EditorStrings.CreateLights[Lights.NameOf(light).ToLowerInvariant()],
                    () => editing.CreateLight(light)
                ) {
                    Category = EditorStrings.CategoryCreate
                }
            );
        }

        context.AddCommand(
            new EditorCommand(
                "scene.create-camera",
                EditorStrings.CommandCreateCamera,
                () => editing.CreateCamera()
            ) {
                Category = EditorStrings.CategoryCreate
            }
        );
    }

    /// <summary>What a light's create command is called in the registry.</summary>
    internal static string LightCommandId(LightKind kind) =>
        "scene.create-light-" + Lights.NameOf(kind).ToLowerInvariant();

    static void Verb(PluginContext context, string id, StringId title, Action run, Func<bool> enabled) =>
        context.AddCommand(
            new EditorCommand(id, title, run) {
                Category = EditorStrings.CategoryEntity,
                Enablement = enabled
            }
        );
}
