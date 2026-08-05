// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Gameplay.Quests;

namespace Vixen.Editor.Gameplay.Quests;

/// <summary>Something wrong with a quest, said the way a row can be highlighted.</summary>
/// <param name="Stage">Which stage, or −1 for the quest itself.</param>
/// <param name="Objective">Which objective of it, or −1 for the stage itself.</param>
/// <param name="Message">What is wrong, in a sentence.</param>
public readonly record struct QuestProblem(int Stage, int Objective, string Message);

/// <summary>One quest, open for editing.</summary>
/// <remarks>
///     <para>
///         <b>The document <em>is</em> a <see cref="QuestDefinition" />.</b> The same bargain
///         <c>Vixen.Editor.Gameplay.Loot</c> and <c>Vixen.Editor.Ai</c> make: a second representation
///         is a second thing to migrate and a second place for the rules to differ from the ones the
///         realm runs.
///     </para>
///     <para>
///         Every gesture is one operation and one <see cref="Changed" />, so an undo stack is
///         snapshot-in, snapshot-out rather than a per-field diff.
///     </para>
/// </remarks>
public sealed class QuestModel {
    /// <summary>Opens a quest.</summary>
    /// <param name="quest">The definition, which this edits in place.</param>
    public QuestModel(QuestDefinition quest) {
        ArgumentNullException.ThrowIfNull(quest);

        Quest = quest;
    }

    /// <summary>What is being edited.</summary>
    public QuestDefinition Quest { get; private set; }

    /// <summary>How many stages it has.</summary>
    public int Count => Quest.Stages.Count;

    /// <summary>Raised after any operation.</summary>
    public event Action<QuestModel>? Changed;

    /// <summary>Adds a stage.</summary>
    /// <param name="stage">The stage, or null for an empty one.</param>
    /// <returns>Its index.</returns>
    public int AddStage(QuestStageDefinition? stage = null) {
        Quest.Stages.Add(stage ?? new QuestStageDefinition());
        Changed?.Invoke(this);

        return Quest.Stages.Count - 1;
    }

    /// <summary>Removes a stage.</summary>
    /// <param name="stage">Its index.</param>
    /// <returns>Whether there was one.</returns>
    public bool RemoveStage(int stage) {
        if ((uint)stage >= (uint)Quest.Stages.Count) {
            return false;
        }

        Quest.Stages.RemoveAt(stage);
        Changed?.Invoke(this);

        return true;
    }

    /// <summary>Moves a stage, which is what reordering a quest is.</summary>
    /// <param name="from">Where it is.</param>
    /// <param name="to">Where it goes.</param>
    /// <returns>Whether it moved.</returns>
    public bool MoveStage(int from, int to) {
        if ((uint)from >= (uint)Quest.Stages.Count || (uint)to >= (uint)Quest.Stages.Count || from == to) {
            return false;
        }

        var stage = Quest.Stages[from];

        Quest.Stages.RemoveAt(from);
        Quest.Stages.Insert(to, stage);
        Changed?.Invoke(this);

        return true;
    }

    /// <summary>Adds an objective to a stage.</summary>
    /// <param name="stage">Which stage.</param>
    /// <param name="objective">The objective, or null for an empty one.</param>
    /// <returns>Its index, or −1 when there is no such stage.</returns>
    public int AddObjective(int stage, QuestObjectiveDefinition? objective = null) {
        if ((uint)stage >= (uint)Quest.Stages.Count) {
            return -1;
        }

        Quest.Stages[stage].Objectives.Add(objective ?? new QuestObjectiveDefinition());
        Changed?.Invoke(this);

        return Quest.Stages[stage].Objectives.Count - 1;
    }

    /// <summary>Removes an objective.</summary>
    /// <param name="stage">Which stage.</param>
    /// <param name="objective">Which objective.</param>
    /// <returns>Whether there was one.</returns>
    public bool RemoveObjective(int stage, int objective) {
        if ((uint)stage >= (uint)Quest.Stages.Count) {
            return false;
        }

        var objectives = Quest.Stages[stage].Objectives;

        if ((uint)objective >= (uint)objectives.Count) {
            return false;
        }

        objectives.RemoveAt(objective);
        Changed?.Invoke(this);

        return true;
    }

    /// <summary>Edits an objective and raises one change for it.</summary>
    /// <param name="stage">Which stage.</param>
    /// <param name="objective">Which objective.</param>
    /// <param name="edit">What to do to it.</param>
    /// <returns>Whether there was one.</returns>
    public bool Edit(int stage, int objective, Action<QuestObjectiveDefinition> edit) {
        ArgumentNullException.ThrowIfNull(edit);

        if ((uint)stage >= (uint)Quest.Stages.Count) {
            return false;
        }

        var objectives = Quest.Stages[stage].Objectives;

        if ((uint)objective >= (uint)objectives.Count) {
            return false;
        }

        edit(objectives[objective]);
        Changed?.Invoke(this);

        return true;
    }

    /// <summary>A copy deep enough to restore, for an undo stack.</summary>
    /// <returns>The copy.</returns>
    public QuestDefinition Snapshot() =>
        Quest with {
            Requirements = [.. Quest.Requirements],
            GrantsTags = [.. Quest.GrantsTags],
            Stages = [
                .. Quest.Stages.Select(
                    stage => new QuestStageDefinition {
                        Id = stage.Id,
                        DisplayName = stage.DisplayName,
                        Description = stage.Description,
                        TimeLimit = stage.TimeLimit,
                        Objectives = [
                            .. stage.Objectives.Select(
                                objective => new QuestObjectiveDefinition {
                                    Type = objective.Type,
                                    DisplayName = objective.DisplayName,
                                    Count = objective.Count,
                                    Target = objective.Target,
                                    TargetTags = [.. objective.TargetTags],
                                    ExcludeTags = [.. objective.ExcludeTags],
                                    Scene = objective.Scene,
                                    Optional = objective.Optional,
                                    Hidden = objective.Hidden
                                }
                            )
                        ]
                    }
                )
            ]
        };

    /// <summary>Puts a snapshot back.</summary>
    /// <param name="snapshot">What <see cref="Snapshot" /> returned.</param>
    public void Restore(QuestDefinition snapshot) {
        ArgumentNullException.ThrowIfNull(snapshot);

        Quest = snapshot;
        Changed?.Invoke(this);
    }

    /// <summary>What is wrong with it, as an inspector shows it.</summary>
    /// <param name="objectives">Which objective types exist, or the shipped ten.</param>
    /// <returns>The problems, quest-level ones first.</returns>
    /// <remarks>
    ///     ⚠ <b>This checks what an editor can check without a catalog, and no more.</b> Whether a
    ///     target address exists, whether a tag is in the build and whether a verb resolves are all
    ///     questions about the whole content set, which is <see cref="QuestLibrary.Problems" />' job —
    ///     and answering half of them here in a second implementation is how the two come to disagree.
    /// </remarks>
    public IReadOnlyList<QuestProblem> Validate(QuestObjectiveRegistry? objectives = null) {
        var registry = objectives ?? QuestObjectiveRegistry.Default;
        var problems = new List<QuestProblem>();

        if (Quest.Stages.Count == 0) {
            problems.Add(new(-1, -1, "This quest has no stages, so accepting it would finish it."));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);

        for (var stage = 0; stage < Quest.Stages.Count; stage++) {
            var current = Quest.Stages[stage];

            if (current.Id.Length > 0 && !ids.Add(current.Id)) {
                problems.Add(new(stage, -1, $"Two stages are called '{current.Id}'."));
            }

            if (current.Objectives.Count == 0) {
                problems.Add(new(stage, -1, "This stage has no objectives, so it finishes as it starts."));
            } else if (current.Objectives.TrueForAll(objective => objective.Optional)) {
                problems.Add(new(stage, -1, "Every objective here is optional, so the stage finishes as it starts."));
            }

            for (var index = 0; index < current.Objectives.Count; index++) {
                var objective = current.Objectives[index];

                if (registry.Find(objective.Type) is null) {
                    problems.Add(
                        new(
                            stage,
                            index,
                            objective.Type.Length == 0
                                ? "This objective has no type."
                                : $"'{objective.Type}' is not an objective type this build has."
                        )
                    );
                }

                if (objective.Count < 1) {
                    problems.Add(new(stage, index, "An objective needs at least one of whatever it counts."));
                }

                if (objective.Hidden && objective.Optional) {
                    problems.Add(
                        new(stage, index, "A hidden optional objective is one nobody can know to do.")
                    );
                }
            }
        }

        if (Quest.Rewards.Choices.Count == 1) {
            problems.Add(new(-1, -1, "A reward choice of one is not a choice; make it an ordinary item."));
        }

        return problems;
    }
}
