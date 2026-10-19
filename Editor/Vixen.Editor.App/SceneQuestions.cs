// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Plugin;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Engine.Transforms;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.App;

/// <summary>Doc 20 § Part D's verbs that ask a question first: the dialogs, over <see cref="ISceneEditing" />.</summary>
/// <remarks>
///     <para>
///         <b>Part D exists to prevent "a menu line without the verb behind it", and nine of its own
///         entries were the other failure: no line, no palette entry and no <c>Planned</c>
///         registration.</b> That is strictly worse than a greyed line — doc 20's first bar is that
///         an unimplemented verb is <i>visibly</i> unimplemented, and silence tells a reader the
///         editor was never meant to do it.
///     </para>
///     <para>
///         ⚠ <b>The dialogs are <see cref="SceneModule" />'s (#1290's third slice).</b> The verbs
///         themselves — select by name or type, selection sets, align and distribute — are methods on
///         <see cref="ISceneEditing" />, taking as a parameter what the dialog asked. These six stayed
///         the application's (<c>EditorSelectionVerbs.cs</c>) only because the drawn chooser they ask
///         through was a private method on it; it is <see cref="EditorShell.ChooseAsync{T}" /> now,
///         which a module reaches through <see cref="PluginContext.Shell" /> as a third party would.
///     </para>
/// </remarks>
/// <param name="context">What the module was activated with.</param>
/// <param name="editing">The verbs the answers are handed to.</param>
sealed class SceneQuestions(PluginContext context, ISceneEditing editing) {
    /// <summary>The three axes, which two of these verbs ask about in the same words.</summary>
    static readonly (string Label, int Index)[] Axes = [("X", 0), ("Y", 1), ("Z", 2)];

    EditorShell Shell => context.Shell;

    SceneDocument Scene => editing.Active;

    /// <summary>Registers Part D's verbs that ask a question first.</summary>
    public void Register() {
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
            enabled: () => Scene.Selection.Count > 0
        );

        Verb(
            "edit.recall-selection-set",
            EditorStrings.CommandEditRecallSelectionSet,
            EditorStrings.CategoryEdit,
            RecallSelectionSet,
            enabled: () => editing.SelectionSets.Count > 0
        );

        // ⚠ Two commands rather than six, and the axis is asked for rather than spelled into the id.
        // Align X, Align Y, Align Z, Distribute X … is six palette entries whose names differ by one
        // character, in a palette people search by typing — and the axis is the one thing about the
        // gesture that changes every time it is used.
        Verb(
            "entity.align",
            EditorStrings.CommandEntityAlign,
            EditorStrings.CategoryEntity,
            () => {
                // No question when the primary — the line everything aligns onto — has no place,
                // which is where the verb stopped before it moved into the service.
                if (Scene.World.IsAlive(Scene.Selection.Primary) && Scene.World.Has<WorldTransform>(Scene.Selection.Primary)) {
                    Axis("Align on which axis?", editing.Align);
                }
            },
            enabled: () => Scene.Selection.Count > 1
        );

        Verb(
            "entity.distribute",
            EditorStrings.CommandEntityDistribute,
            EditorStrings.CategoryEntity,
            () => Axis("Distribute along which axis?", editing.Distribute),
            enabled: () => Scene.Selection.Count > 2
        );

        // ⚠ The one of the nine that is not a command, which is why it is declared rather than
        // written. "Type +5 into Y and mean five more" is a property of the *field*, so it belongs to
        // the inspector's Vector3 drawer and to whatever parses what was typed — a verb that opened a
        // dialog asking for a delta would be a different feature wearing its name.
        context.AddCommand(
            new EditorCommand("entity.relative-transform", EditorStrings.CommandEntityRelativeTransform, () => { }) {
                Category = EditorStrings.CategoryEntity,
                Unavailable = EditorStrings.PlannedReasons["entity.relative-transform"]
            }
        );
    }

    /// <summary>Registers a command that does something, through the module's context.</summary>
    void Verb(string id, StringId title, StringId category, Action run, Func<bool>? enabled = null) =>
        context.AddCommand(
            new EditorCommand(id, title, run) {
                Category = category,
                Enablement = enabled
            }
        );

    /// <summary>Asks what to look for, and selects every entity whose name contains it.</summary>
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

            Chose(editing.SelectByName(text), $"Nothing is called '{text}'.");
        }
    }

    /// <summary>Asks which component, and selects everything carrying it.</summary>
    /// <remarks>The count is on the row, so a picker of dead ends is not what opens.</remarks>
    void SelectByType() {
        var present = editing.ComponentsInScene();

        if (present.Count == 0) {
            return;
        }

        _ = Ask();

        async Task Ask() {
            var chosen = await Shell.ChooseAsync(
                "Select everything with which component?",
                present.Select(static entry => new Row(entry.Component, entry.Count)),
                static row => row.Label,
                static row => row.Count == 1 ? "1 entity" : $"{row.Count} entities"
            ).ConfigureAwait(true);

            if (chosen is null) {
                return;
            }

            Chose(editing.SelectByType(chosen.Label), "Nothing carries that.");
        }
    }

    /// <summary>One row of a picker: something to choose, and how many it would take.</summary>
    /// <param name="Label">What the row says.</param>
    /// <param name="Count">How many entities it covers.</param>
    sealed record Row(string Label, int Count);

    /// <summary>Asks for a name, and saves the current selection under it.</summary>
    void SaveSelectionSet() {
        if (Scene.Selection.Count == 0) {
            return;
        }

        var count = Scene.Selection.Count;

        _ = Ask();

        async Task Ask() {
            var typed = await Shell.Dialogs.PromptAsync(
                "Save Selection Set",
                $"{count} entit{(count == 1 ? "y" : "ies")}, under what name?",
                confirm: "Save"
            ).ConfigureAwait(true);

            if (typed is { Length: > 0 } name && editing.SaveSelectionSet(name)) {
                Shell.Notifications.Success($"Saved '{name}'");
            }
        }
    }

    /// <summary>Asks which set, brings it back, and says so if it has lost members.</summary>
    /// <remarks>
    ///     ⚠ <b>A set that has lost members is recalled anyway and says so.</b> A set that silently
    ///     came back short would be worse than one that came back short and mentioned it.
    /// </remarks>
    void RecallSelectionSet() {
        if (editing.SelectionSets.Count == 0) {
            return;
        }

        var sets = editing.SelectionSets
            .Select(entry => new Row(entry.Key, entry.Value.Count))
            .OrderBy(static row => row.Label, StringComparer.Ordinal)
            .ToList();

        _ = Ask();

        async Task Ask() {
            var chosen = await Shell.ChooseAsync(
                "Which selection set?",
                sets,
                static row => row.Label,
                static row => row.Count == 1 ? "1 entity" : $"{row.Count} entities"
            ).ConfigureAwait(true);

            if (chosen is null || !editing.SelectionSets.TryGetValue(chosen.Label, out var members)) {
                return;
            }

            var alive = editing.RecallSelectionSet(chosen.Label);

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

    /// <summary>Says what a select verb found, and puts the focus on the scene if it found anything.</summary>
    void Chose(IReadOnlyList<Entity> found, string empty) {
        if (found.Count == 0) {
            Shell.Notifications.Show(empty, NotificationSeverity.Info);
            return;
        }

        Shell.Context = EditorApplication.SceneContext;
        Shell.Notifications.Success(found.Count == 1 ? "1 entity" : $"{found.Count} entities");
    }

    /// <summary>Asks which axis, and runs the verb along it.</summary>
    /// <remarks>Asked only when something selected can move, so a verb with nothing to do opens no dialog.</remarks>
    void Axis(string question, Action<int> work) {
        var world = Scene.World;

        if (!Scene.Selection.Any(entity => world.IsAlive(entity) && world.Has<LocalTransform>(entity) && world.Has<WorldTransform>(entity))) {
            return;
        }

        _ = Ask();

        async Task Ask() {
            var chosen = await Shell.ChooseAsync(
                question,
                Axes.Select(static axis => new Row(axis.Label, axis.Index)),
                static row => row.Label
            ).ConfigureAwait(true);

            if (chosen is not null) {
                work(chosen.Count);
            }
        }
    }
}
