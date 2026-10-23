// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Core;
using Vixen.Editor.Plugin;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Editor.Ui.Documents;
using Vixen.Input;
using Vixen.Platform;
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
///         ⚠ <b>Not every Scene line's command is this module's.</b> The Edit menu's selection verbs
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
///     <para>
///         <b>The fifth made the Scene menu, the Scenes panel and Open Scene Additively / Close Scene
///         the module's</b> — <see cref="SceneMenuBar" /> and <see cref="ScenesPanel" />, over
///         <see cref="ISceneEditing.OpenAdditively" /> and <see cref="ISceneEditing.Close" />. The outliner's
///         context menu was already <see cref="SceneOutliner" />'s.
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

        // ── The Entity menu's planned lines and the two marks (#1296) ───────────────────────────
        // Declared and disabled with the reason, which is doc 20's E0: a verb that is not implemented
        // is visibly not implemented rather than absent. Moved out of `EditorParity.EntityCommands`
        // with the rest of the Entity menu's lines, so switching this module off takes all of them.
        foreach (var (id, title) in PlannedEntityLines) {
            context.AddCommand(
                new EditorCommand(id, title, () => { }) {
                    Category = EditorStrings.CategoryEntity,
                    Unavailable = EditorStrings.PlannedReasons[id]
                }
            );
        }

        // ⚠ Editor state and not scene state, which is the line both Unreal and Unity draw: hiding
        // something to work on what is behind it must not change what ships. So these write
        // `SceneDocument`'s own sets rather than a component, they are not saved, and they are not
        // undoable — an undo that put an eye back would be one step of history spent on where the
        // user was looking rather than on what they changed, which is `Selection`'s argument.
        context.AddCommand(
            new EditorCommand(
                "entity.toggle-hidden",
                EditorStrings.CommandEntityToggleHidden,
                () => Mark(editing, static (scene, entity) => scene.IsHiddenDirectly(entity), static (scene, entity, on) => scene.SetHidden(entity, on))
            ) {
                Category = EditorStrings.CategoryEntity,
                Enablement = Selected,
                Checked = () => Selected() && editing.Active.IsHiddenDirectly(editing.Active.Selection[0])
            }
        );

        context.AddCommand(
            new EditorCommand(
                "entity.toggle-lock",
                EditorStrings.CommandEntityToggleLock,
                () => Mark(editing, static (scene, entity) => scene.IsLockedDirectly(entity), static (scene, entity, on) => scene.SetLocked(entity, on))
            ) {
                Category = EditorStrings.CategoryEntity,
                Enablement = Selected,
                Checked = () => Selected() && editing.Active.IsLockedDirectly(editing.Active.Selection[0])
            }
        );

        context.AddDefaultBinding("entity.toggle-hidden", new KeyChord(InputKey.H, ModifierKeys.None));
        context.AddDefaultBinding("entity.toggle-lock", new KeyChord(InputKey.L, ModifierKeys.None));

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

        // ── The outliner (#1290's fourth slice) ─────────────────────────────────────────────────
        // ⚠ Its filter and order are the workspace's per-view state rather than the module's, so they
        // outlive the panel and the module both; the outliner itself is published for the two things
        // the application still asks of the tree — F2's in-place rename and Deselect's highlight.
        var outliner = context.Owns(
            new SceneOutliner(
                context.Shell,
                editing,
                registry,
                context.Services.Require<IWorkspace>().ViewState<OutlinerState>(SceneOutliner.PanelId),
                EditorApplication.SceneContext
            )
        );

        context.Publish(outliner);
        context.AddPanel(
            new PanelDescriptor(SceneOutliner.PanelId, EditorStrings.PanelHierarchy, outliner.Build) { Closed = outliner.Closed }
        );
        context.OnUpdate(_ => outliner.Update());

        // ── File ▸ New Scene, Open Scene and Save As (#1296) ────────────────────────────────────
        // Over the file behind the active scene, which is a service now — see `SceneFiles`.
        SceneFileQuestions.Register(context, context.Services.Require<SceneFiles>());

        // ── Search Everywhere's entities (#1518) ────────────────────────────────────────────────
        // Over the scene the editor is *showing* — the prefab while one is inspected — and through the
        // context, so switching this module off takes its rows out of the overlay. Choosing one selects
        // it in the outliner and puts the editor in the scene's context.
        var shown = context.Services.Require<IActiveScene>();

        context.AddSearchSource(
            new EntitySearchSource(
                () => shown.Current,
                entity => {
                    context.Shell.Workspace.Open(SceneOutliner.PanelId);

                    shown.Current.Selection.Set([entity]);
                    context.Shell.Context = EditorApplication.SceneContext;

                    outliner.Invalidate();
                }
            )
        );

        // ── The Edit menu's scene verbs (#1296) ─────────────────────────────────────────────────
        // ⚠ Scoped to the outliner, which is what lets the content browser's twin have the same key.
        // Without a context the second Delete could not be bound at all, and an enablement predicate
        // guessing from "is anything selected" gets it wrong the moment both panels have a selection
        // — which after clicking an asset and then a row is most of the time.
        context.AddCommand(
            new EditorCommand("edit.delete", EditorStrings.CommandEditDelete, () => editing.Active.Delete([.. editing.Active.Selection])) {
                Category = EditorStrings.CategoryEdit,
                Context = EditorApplication.SceneContext,
                Enablement = Selected
            }
        );

        // In place, in the outliner — which is why it needs the outliner open.
        context.AddCommand(
            new EditorCommand(
                "edit.rename",
                EditorStrings.CommandEditRename,
                () => {
                    if (Selected()) {
                        outliner.BeginRename(editing.Active.Selection[0]);
                    }
                }
            ) {
                Category = EditorStrings.CategoryEdit,
                Context = EditorApplication.SceneContext,
                Enablement = () => outliner.IsOpen && Selected()
            }
        );

        context.AddDefaultBinding("edit.delete", new KeyChord(InputKey.Delete, ModifierKeys.None));
        context.AddDefaultBinding("edit.rename", new KeyChord(InputKey.F2, ModifierKeys.None));

        // ⚠ This block said "Cut, Copy, Paste and Duplicate need a subtree *clone*, which the engine
        // does not have", and `SceneClone`'s own remarks call themselves "the thing doc 20's
        // clipboard was blocked on". Duplicate is that clone with nothing in between, so it is a verb
        // now. The other three still are not, and the reason is no longer the copy: a clipboard is a
        // *buffer that outlives the selection* — what was cut has to survive being deleted, and a
        // paste has to land somewhere the copy did not come from.
        EditVerb(context, "edit.duplicate", EditorStrings.CommandEditDuplicate, () => editing.DuplicateSelection(), Selected);

        foreach (var (id, title, key) in Clipboard()) {
            if (context.Shell.Commands[id] is null) {
                context.AddCommand(
                    new EditorCommand(id, title, () => { }) {
                        Category = EditorStrings.CategoryEdit,
                        Unavailable = EditorStrings.PlannedReasons[id]
                    }
                );
            }

            if (key.IsBound) {
                context.AddDefaultBinding(id, key);
            }
        }

        EditVerb(context, "edit.select-all", EditorStrings.CommandEditSelectAll, () => editing.Active.Selection.Set([.. editing.Active.Entities]));

        // Through the outliner when it is open: the rows' highlight is the tree's own state and the
        // document's selection is written from it, so clearing only the document would leave a row
        // that looks selected.
        EditVerb(
            context,
            "edit.deselect-all",
            EditorStrings.CommandEditDeselectAll,
            () => {
                if (Selected() && !outliner.ClearSelection()) {
                    editing.Active.Selection.Clear();
                }
            },
            Selected
        );

        EditVerb(context, "edit.invert-selection", EditorStrings.CommandEditInvertSelection, editing.InvertSelection);
        EditVerb(context, "edit.select-children", EditorStrings.CommandEditSelectChildren, editing.SelectChildren, Selected);
        EditVerb(context, "edit.select-parent", EditorStrings.CommandEditSelectParent, editing.SelectParent, Selected);

        context.AddDefaultBinding("edit.select-all", new KeyChord(InputKey.A, ModifierKeys.Control));
        context.AddDefaultBinding("edit.deselect-all", new KeyChord(InputKey.A, ModifierKeys.Control | ModifierKeys.Shift));

        // ── The Scenes panel and the multi-scene verbs (#1290's fifth slice) ───────────────────
        // ⚠ Over `ISceneEditing.OpenAdditively`/`Close`; the picker is the published one, so a host
        // with none greys Open Scene Additively rather than offering a line that does nothing.
        context.Services.TryGet(out INativeDialogs? dialogs);

        var session = context.Services.Require<IProjectSession>();
        var scenes = context.Owns(
            new ScenesPanel(
                context.Shell,
                editing,
                EditorApplication.SceneContext,
                dialogs,
                () => session.Project.Paths.Assets,
                () => (editing as SceneEditing)?.Remarked()
            )
        );

        scenes.Register(context);
        context.OnUpdate(_ => scenes.Update());

        // ── The Scene menu (#1290's fifth slice) ────────────────────────────────────────────────
        // ⚠ Last, so every line it names that is this module's is registered; and before Blockout and
        // Water activate, which place their submenus in it by finding it.
        SceneMenuBar.Register(context);
    }

    /// <summary>The four clipboard verbs and the keys everybody expects them on.</summary>
    static (string Id, StringId Title, KeyChord Key)[] Clipboard() => [
        ("edit.cut", EditorStrings.CommandEditCut, new KeyChord(InputKey.X, ModifierKeys.Control)),
        ("edit.copy", EditorStrings.CommandEditCopy, new KeyChord(InputKey.C, ModifierKeys.Control)),
        ("edit.paste", EditorStrings.CommandEditPaste, new KeyChord(InputKey.V, ModifierKeys.Control)),
        ("edit.paste-as-child", EditorStrings.CommandEditPasteAsChild, KeyChord.None),
        ("edit.duplicate", EditorStrings.CommandEditDuplicate, new KeyChord(InputKey.D, ModifierKeys.Control))
    ];

    /// <summary>Registers one of the Edit menu's verbs.</summary>
    static void EditVerb(PluginContext context, string id, StringId title, Action run, Func<bool>? enabled = null) =>
        context.AddCommand(
            new EditorCommand(id, title, run) {
                Category = EditorStrings.CategoryEdit,
                Enablement = enabled
            }
        );

    /// <summary>The Entity menu's lines that are declared and not yet built, in the order the menu shows them.</summary>
    static readonly (string Id, StringId Title)[] PlannedEntityLines = [
        ("entity.create-audio", EditorStrings.CommandEntityCreateAudio),
        ("entity.create-ui", EditorStrings.CommandEntityCreateUi),
        ("entity.create-vfx", EditorStrings.CommandEntityCreateVfx),
        ("entity.make-prefab", EditorStrings.CommandEntityMakePrefab),
        ("entity.unpack-prefab", EditorStrings.CommandEntityUnpackPrefab),
        ("entity.apply-overrides", EditorStrings.CommandEntityApplyOverrides),
        ("entity.ungroup", EditorStrings.CommandEntityUngroup),
        ("entity.toggle-active", EditorStrings.CommandEntityToggleActive)
    ];

    /// <summary>Flips a mark across the selection, taking the first entity's state as the answer.</summary>
    /// <remarks>
    ///     ⚠ <b>All of them get what the <i>first</i> one is not, rather than each being flipped.</b>
    ///     Toggling a mixed selection per entity swaps which half is hidden, which is the one outcome
    ///     nobody ever means — and it makes pressing the key twice a no-op that looks like the key
    ///     not working.
    /// </remarks>
    static void Mark(
        ISceneEditing editing,
        Func<SceneDocument, Entity, bool> read,
        Action<SceneDocument, Entity, bool> write
    ) {
        var scene = editing.Active;

        if (scene.Selection.Count == 0) {
            return;
        }

        var wanted = !read(scene, scene.Selection[0]);

        foreach (var entity in scene.Selection) {
            write(scene, entity, wanted);
        }

        // ⚠ Through the service's own event, which the outliner and the viewport follow; the cast is
        // the one `ScenesPanel` makes, because raising it is not a verb a plugin should have.
        (editing as SceneEditing)?.Remarked();
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
