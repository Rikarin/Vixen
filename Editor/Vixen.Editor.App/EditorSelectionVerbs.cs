// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.Ui;
using Vixen.Engine.Scenes;
using Vixen.Engine.Transforms;
using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.App;

/// <summary>Doc 20 § Part D's selection and transform verbs, which had no command id at all.</summary>
/// <remarks>
///     <para>
///         <b>Part D exists to prevent "a menu line without the verb behind it", and nine of its own
///         entries were the other failure: no line, no palette entry and no <c>Planned</c>
///         registration.</b> That is strictly worse than a greyed line — doc 20's first bar is that
///         an unimplemented verb is <i>visibly</i> unimplemented, and silence tells a reader the
///         editor was never meant to do it.
///     </para>
///     <para>
///         ⚠ <b>They are here because none of them needed anything built.</b> Every one is a command
///         over machinery that already exists: <c>Selection&lt;T&gt;</c>, the undo stack,
///         <c>SceneDocument</c>'s own hidden set, <c>SceneComponentRegistry</c> and the drawn
///         picker. Nothing here needs a renderer, a device or a runtime concept — which is what
///         separates them from the rows doc 20 correctly defers.
///     </para>
///     <para>
///         ⚠ <b>A selection set and an isolate are editor state and are not saved</b>, on exactly the
///         argument <c>entity.toggle-hidden</c> makes one file over: what you were looking at is not
///         what ships, and an undo step spent on it is a step not spent on what you changed. The
///         consequence is stated rather than hidden — they are lost when the scene is reloaded,
///         because they hold entity handles and a reload mints new ones.
///     </para>
/// </remarks>
sealed partial class EditorApplication {
    /// <summary>What Copy Transform put aside, or <see langword="null" /> before anything did.</summary>
    LocalTransform? copiedTransform;

    /// <summary>The named selections, in the order they were saved.</summary>
    readonly Dictionary<string, List<Entity>> selectionSets = new(StringComparer.Ordinal);

    /// <summary>What was hidden before Isolate hid everything else, or null when not isolating.</summary>
    List<Entity>? isolatedFrom;

    /// <summary>The three axes, which four of these verbs ask about in the same words.</summary>
    static readonly (string Label, int Index)[] Axes = [("X", 0), ("Y", 1), ("Z", 2)];

    /// <summary>Whether an entity is still alive and carries a transform to write.</summary>
    /// <param name="entity">The entity, which may be a handle nothing answers to any more.</param>
    /// <returns>Whether a transform verb can touch it.</returns>
    /// <remarks>
    ///     ⚠ <b>The liveness check is not defensive tidiness: <c>World.Has</c> <i>throws</i> on a dead
    ///     handle, and an enablement predicate runs from inside a menu refresh.</b> The selection
    ///     keeps what it held across an undo that destroyed it — undoing a Duplicate is the ordinary
    ///     way to produce one — so a predicate that dereferenced the primary without asking took the
    ///     whole frame down rather than greying a button. Every other enablement in the editor reads
    ///     <c>Selection.Count</c> or a set, which is why these are the first to have met it.
    /// </remarks>
    bool Transformable(Entity entity) => world.IsAlive(entity) && world.Has<LocalTransform>(entity);

    /// <summary>Registers Part D's selection and transform verbs.</summary>
    void SelectionAndTransformCommands() {
        Verb(
            "edit.isolate",
            EditorStrings.CommandEditIsolate,
            EditorStrings.CategoryEdit,
            Isolate,
            enabled: () => isolatedFrom is not null || scene.Selection.Count > 0,
            on: () => isolatedFrom is not null
        );

        Shell.Keys.SetDefault("edit.isolate", new KeyChord(InputKey.I, ModifierKeys.Control | ModifierKeys.Shift));

        Verb(
            "edit.select-by-name",
            EditorStrings.CommandEditSelectByName,
            EditorStrings.CategoryEdit,
            SelectByName
        );

        Verb(
            "edit.select-by-type",
            EditorStrings.CommandEditSelectByType,
            EditorStrings.CategoryEdit,
            SelectByType
        );

        Verb(
            "edit.save-selection-set",
            EditorStrings.CommandEditSaveSelectionSet,
            EditorStrings.CategoryEdit,
            SaveSelectionSet,
            enabled: () => scene.Selection.Count > 0
        );

        Verb(
            "edit.recall-selection-set",
            EditorStrings.CommandEditRecallSelectionSet,
            EditorStrings.CategoryEdit,
            RecallSelectionSet,
            enabled: () => selectionSets.Count > 0
        );

        Verb(
            "entity.reset-transform",
            EditorStrings.CommandEntityResetTransform,
            EditorStrings.CategoryEntity,
            ResetTransform,
            enabled: () => scene.Selection.Count > 0
        );

        Verb(
            "entity.copy-transform",
            EditorStrings.CommandEntityCopyTransform,
            EditorStrings.CategoryEntity,
            CopyTransform,
            enabled: () => Transformable(scene.Selection.Primary)
        );

        Verb(
            "entity.paste-transform",
            EditorStrings.CommandEntityPasteTransform,
            EditorStrings.CategoryEntity,
            PasteTransform,
            enabled: () => copiedTransform is not null && scene.Selection.Count > 0
        );

        // ⚠ Two commands rather than six, and the axis is asked for rather than spelled into the id.
        // Align X, Align Y, Align Z, Distribute X … is six palette entries whose names differ by one
        // character, in a palette people search by typing — and the axis is the one thing about the
        // gesture that changes every time it is used.
        Verb(
            "entity.align",
            EditorStrings.CommandEntityAlign,
            EditorStrings.CategoryEntity,
            Align,
            enabled: () => scene.Selection.Count > 1
        );

        Verb(
            "entity.distribute",
            EditorStrings.CommandEntityDistribute,
            EditorStrings.CategoryEntity,
            Distribute,
            enabled: () => scene.Selection.Count > 2
        );

        // ⚠ The one of the nine that is not a command, which is why it is declared rather than
        // written. "Type +5 into Y and mean five more" is a property of the *field*, so it belongs to
        // the inspector's Vector3 drawer and to whatever parses what was typed — a verb that opened a
        // dialog asking for a delta would be a different feature wearing its name.
        Planned("entity.relative-transform", EditorStrings.CommandEntityRelativeTransform, EditorStrings.CategoryEntity);
    }

    // ── Selection ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Hides everything the selection is not part of, or puts it all back.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The ancestors and the descendants stay visible, and both halves are needed.</b>
    ///         Hidden-ness is inherited (<c>SceneDocument.IsHidden</c>), so hiding a selected
    ///         entity's parent would hide the entity — and isolating a crate without what is inside
    ///         it shows an empty crate, which reads as the command having deleted something.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What was hidden before is restored, not "everything becomes visible".</b> Somebody
    ///         who had turned three eyes off and then isolated would otherwise find the three back on
    ///         afterwards, with nothing to say why — the same rule <c>SubtreeSnapshot</c> follows about
    ///         restoring exactly what was there.
    ///     </para>
    /// </remarks>
    void Isolate() {
        if (isolatedFrom is { } was) {
            foreach (var entity in scene.Entities) {
                scene.SetHidden(entity, false);
            }

            foreach (var entity in was) {
                if (world.IsAlive(entity)) {
                    scene.SetHidden(entity, true);
                }
            }

            isolatedFrom = null;
            RefreshMarks();

            return;
        }

        if (scene.Selection.Count == 0) {
            return;
        }

        HashSet<Entity> kept = [];

        foreach (var entity in scene.Selection) {
            kept.Add(entity);

            for (var parent = Hierarchy.ParentOf(world, entity); !parent.IsNull;
                 parent = Hierarchy.ParentOf(world, parent)) {
                kept.Add(parent);
            }
        }

        // Descendants in a second pass, because an entity is kept when any *selected* ancestor of it
        // is — walking down from each selected root is the cheap way to say that.
        foreach (var entity in scene.Entities) {
            for (var parent = Hierarchy.ParentOf(world, entity); !parent.IsNull;
                 parent = Hierarchy.ParentOf(world, parent)) {
                if (scene.Selection.Contains(parent)) {
                    kept.Add(entity);
                    break;
                }
            }
        }

        isolatedFrom = [.. scene.Entities.Where(scene.IsHiddenDirectly)];

        foreach (var entity in scene.Entities) {
            scene.SetHidden(entity, !kept.Contains(entity));
        }

        RefreshMarks();
    }

    /// <summary>Selects every entity whose name contains what was typed.</summary>
    /// <remarks>
    ///     ⚠ <b>Contains and case-insensitive, which is what the outliner's own filter does.</b> Two
    ///     surfaces answering "which entities are called Lamp" differently is how a person concludes
    ///     one of them is broken; <c>Matches</c> is the filter and this is the same predicate said
    ///     once.
    /// </remarks>
    void SelectByName() {
        _ = Ask();

        async Task Ask() {
            var typed = await Shell.Dialogs.PromptAsync(
                "Select By Name",
                "Every entity whose name contains this.",
                confirm: "Select"
            ).ConfigureAwait(true);

            if (typed is not { Length: > 0 } text) {
                return;
            }

            List<Entity> found = [
                .. scene.Entities.Where(entity => scene.NameOf(entity).Contains(text, StringComparison.OrdinalIgnoreCase))
            ];

            Chose(found, $"Nothing is called '{text}'.");
        }
    }

    /// <summary>Asks which component, and selects everything carrying it.</summary>
    /// <remarks>
    ///     ⚠ <b>Only the components something in this scene actually has.</b> The registry holds every
    ///     declared type in the process — a list of two hundred, most of which would select nothing —
    ///     and a picker whose rows are mostly dead ends is a picker people stop opening. The count is
    ///     on the row for the same reason.
    /// </remarks>
    void SelectByType() {
        List<(ISceneComponentBinder Binder, int Count)> present = [];

        foreach (var binder in SceneComponentRegistry.Binders) {
            var count = scene.Entities.Count(entity => binder.Has(world, entity));

            if (count > 0) {
                present.Add((binder, count));
            }
        }

        if (present.Count == 0) {
            return;
        }

        present.Sort(static (left, right) => string.CompareOrdinal(left.Binder.Name, right.Binder.Name));

        _ = Ask();

        async Task Ask() {
            var chosen = await ChooseAsync(
                "Select everything with which component?",
                present.Select(static entry => new Row(entry.Binder.Name, entry.Count)),
                static row => row.Label,
                static row => row.Count == 1 ? "1 entity" : $"{row.Count} entities"
            ).ConfigureAwait(true);

            if (chosen is null || !SceneComponentRegistry.TryGet(chosen.Label, out var binder)) {
                return;
            }

            Chose([.. scene.Entities.Where(entity => binder.Has(world, entity))], "Nothing carries that.");
        }
    }

    /// <summary>One row of a picker: something to choose, and how many it would take.</summary>
    /// <param name="Label">What the row says.</param>
    /// <param name="Count">How many entities it covers.</param>
    sealed record Row(string Label, int Count);

    /// <summary>Names the current selection so it can be brought back.</summary>
    void SaveSelectionSet() {
        if (scene.Selection.Count == 0) {
            return;
        }

        var members = scene.Selection.ToList();

        _ = Ask();

        async Task Ask() {
            var typed = await Shell.Dialogs.PromptAsync(
                "Save Selection Set",
                $"{members.Count} entit{(members.Count == 1 ? "y" : "ies")}, under what name?",
                confirm: "Save"
            ).ConfigureAwait(true);

            if (typed is not { Length: > 0 } name) {
                return;
            }

            selectionSets[name] = members;
            Shell.Notifications.Success($"Saved '{name}'");
        }
    }

    /// <summary>Brings a named selection back, less whatever has since been deleted.</summary>
    /// <remarks>
    ///     ⚠ <b>A set that has lost members is recalled anyway and says so.</b> Deleting one of five
    ///     is not a reason to refuse the other four, and a set that silently came back short would be
    ///     worse than one that came back short and mentioned it.
    /// </remarks>
    void RecallSelectionSet() {
        if (selectionSets.Count == 0) {
            return;
        }

        var sets = selectionSets
            .Select(entry => new Row(entry.Key, entry.Value.Count))
            .OrderBy(static row => row.Label, StringComparer.Ordinal)
            .ToList();

        _ = Ask();

        async Task Ask() {
            var chosen = await ChooseAsync(
                "Which selection set?",
                sets,
                static row => row.Label,
                static row => row.Count == 1 ? "1 entity" : $"{row.Count} entities"
            ).ConfigureAwait(true);

            if (chosen is null || !selectionSets.TryGetValue(chosen.Label, out var members)) {
                return;
            }

            List<Entity> alive = [.. members.Where(world.IsAlive)];

            if (alive.Count < members.Count) {
                Shell.Notifications.Show(
                    $"'{chosen.Label}' is {alive.Count} of {members.Count}",
                    NotificationSeverity.Warning,
                    "The rest have been deleted."
                );
            }

            Chose(alive, $"Nothing in '{chosen.Label}' is still in the scene.");
        }
    }

    /// <summary>Makes a found list the selection, or says that it found nothing.</summary>
    void Chose(List<Entity> found, string empty) {
        if (found.Count == 0) {
            Shell.Notifications.Show(empty, NotificationSeverity.Info);
            return;
        }

        scene.Selection.Set(found);
        Shell.Context = SceneContext;
        Shell.Notifications.Success(found.Count == 1 ? "1 entity" : $"{found.Count} entities");
    }

    // ── Transform ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Puts the selection back at its parent's origin, unrotated and unscaled.</summary>
    void ResetTransform() => Rewrite("Reset Transform", static _ => LocalTransform.Identity);

    /// <summary>Remembers the primary selection's local transform.</summary>
    /// <remarks>
    ///     ⚠ <b>The <i>local</i> transform, which is what makes paste mean something.</b> A world
    ///     matrix copied off one entity and written onto another under a different parent is a
    ///     position neither of them has; what people copy a transform for is to give a second object
    ///     the first one's pose inside the same rig.
    /// </remarks>
    void CopyTransform() {
        var primary = scene.Selection.Primary;

        if (!Transformable(primary)) {
            return;
        }

        copiedTransform = world.Read<LocalTransform>(primary);
        Shell.Notifications.Success("Transform copied");
    }

    /// <summary>Writes the copied transform onto everything selected, undoably.</summary>
    void PasteTransform() {
        if (copiedTransform is { } copied) {
            Rewrite("Paste Transform", _ => copied);
        }
    }

    /// <summary>Rewrites the selection's local transforms as one undo step.</summary>
    void Rewrite(string name, Func<LocalTransform, LocalTransform> next) {
        var targets = scene.Selection
            .Where(Transformable)
            .Select(entity => (Entity: entity, Was: world.Read<LocalTransform>(entity)))
            .ToList();

        if (targets.Count == 0) {
            return;
        }

        scene.Stack.Execute(
            new DelegateCommand(
                name,
                _ => {
                    foreach (var (entity, was) in targets) {
                        world.Set(entity, next(was));
                    }
                },
                _ => {
                    foreach (var (entity, was) in targets) {
                        world.Set(entity, was);
                    }
                }
            )
        );
    }

    /// <summary>Asks for an axis, then puts everything on the last-selected entity's line.</summary>
    /// <remarks>
    ///     ⚠ <b>Onto the <i>primary</i> — the one selected last — rather than onto the average.</b>
    ///     Every editor with this verb aligns to the active object, and the reason is that aligning to
    ///     a mean moves every object including the one you were happy with, so there is nothing on
    ///     screen that stayed still to judge the result against.
    /// </remarks>
    void Align() {
        var anchor = scene.Selection.Primary;

        if (!world.IsAlive(anchor) || !world.Has<WorldTransform>(anchor)) {
            return;
        }

        var to = world.Read<WorldTransform>(anchor).Value.Translation;

        Axis("Align on which axis?", (targets, axis) => {
            Displace(
                "Align",
                targets,
                entity => Along(axis, Component(to, axis) - Component(WorldPositionOf(entity), axis))
            );
        });
    }

    /// <summary>Asks for an axis, then spaces everything evenly between the two ends.</summary>
    /// <remarks>
    ///     ⚠ <b>Between the extremes rather than by a fixed gap, and the two ends do not move.</b>
    ///     A fixed gap is a different verb — it needs a number — and one that moved the outermost
    ///     objects would change the extent of the row somebody had already placed.
    /// </remarks>
    void Distribute() {
        Axis("Distribute along which axis?", (targets, axis) => {
            var ordered = targets
                .Select(target => (target.Entity, Position: Component(WorldPositionOf(target.Entity), axis)))
                .OrderBy(static entry => entry.Position)
                .ToList();

            if (ordered.Count < 3) {
                return;
            }

            var first = ordered[0].Position;
            var step = (ordered[^1].Position - first) / (ordered.Count - 1);

            Dictionary<Entity, float> wanted = [];

            for (var index = 0; index < ordered.Count; index++) {
                wanted[ordered[index].Entity] = first + (step * index);
            }

            Displace(
                "Distribute",
                targets,
                entity => Along(axis, wanted[entity] - Component(WorldPositionOf(entity), axis))
            );
        });
    }

    /// <summary>Asks which axis, and runs the work over the selection's transformable entities.</summary>
    void Axis(string question, Action<List<(Entity Entity, LocalTransform Was)>, int> work) {
        var targets = scene.Selection
            .Where(entity => Transformable(entity) && world.Has<WorldTransform>(entity))
            .Select(entity => (Entity: entity, Was: world.Read<LocalTransform>(entity)))
            .ToList();

        if (targets.Count == 0) {
            return;
        }

        _ = Ask();

        async Task Ask() {
            var chosen = await ChooseAsync(
                question,
                Axes.Select(static axis => new Row(axis.Label, axis.Index)),
                static row => row.Label
            ).ConfigureAwait(true);

            if (chosen is not null) {
                work(targets, chosen.Count);
            }
        }
    }

    /// <summary>Where an entity is in the world.</summary>
    Vector3 WorldPositionOf(Entity entity) => world.Read<WorldTransform>(entity).Value.Translation;

    /// <summary>One component of a vector, by index.</summary>
    static float Component(Vector3 value, int axis) => axis == 0 ? value.X : axis == 1 ? value.Y : value.Z;

    /// <summary>A distance along one axis, as a vector.</summary>
    static Vector3 Along(int axis, float distance) =>
        axis == 0 ? new Vector3(distance, 0f, 0f)
        : axis == 1 ? new Vector3(0f, distance, 0f)
        : new Vector3(0f, 0f, distance);
}
