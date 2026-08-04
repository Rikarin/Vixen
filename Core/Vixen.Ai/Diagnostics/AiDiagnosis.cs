// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core;
using Vixen.Ecs;

namespace Vixen.Ai.Diagnostics;

/// <summary>What is visibly wrong with an agent.</summary>
/// <remarks>
///     ⚠ <b>Five symptoms, and every one of them is a shape in the record stream rather than a fact
///     about a tree.</b> That is what makes the same reader work for all three planners, and it is
///     what makes doc 37 § P7's exit criterion — <i>diagnosed from the recorded log alone</i> —
///     possible at all: a reader that needed the tree would need the world, and the world is what is
///     not there when a headless test fails on a build machine at three in the morning.
/// </remarks>
public enum AiSymptom : byte {
    /// <summary>Nothing to report.</summary>
    None,

    /// <summary>
    ///     It keeps changing its mind. The visible failure of a utility agent, and of a tree whose
    ///     decorators disagree.
    /// </summary>
    Flapping,

    /// <summary>The same thing failed over and over and nothing else was ever tried.</summary>
    StuckFailing,

    /// <summary>It has been running one thing for the whole window and never finished it.</summary>
    NeverFinishes,

    /// <summary>
    ///     Its planner produced nothing: no action, or the same nothing every step. An unfinished
    ///     set, an unreachable goal, a tree whose root fails.
    /// </summary>
    Idle,

    /// <summary>Its steps are doing far more work than a settled agent's — a tree churning branches.</summary>
    Thrashing
}

/// <summary>One thing wrong with one agent, and the evidence for it.</summary>
/// <param name="Entity">Which agent.</param>
/// <param name="Symptom">What is wrong.</param>
/// <param name="Action">The action it is wrong about, or <see cref="Symbol.None" />.</param>
/// <param name="Planner">Which planner produced the records.</param>
/// <param name="Samples">How many records the finding is drawn from.</param>
/// <param name="Value">The number that crossed the threshold: switches, failures, transitions.</param>
/// <param name="First">The first tick in the window.</param>
/// <param name="Last">The last.</param>
public readonly record struct AiFinding(
    Entity Entity,
    AiSymptom Symptom,
    Symbol Action,
    AiPlanner Planner,
    int Samples,
    float Value,
    long First,
    long Last
) {
    /// <summary>The finding as a sentence, which is what a failing test prints.</summary>
    /// <returns>It.</returns>
    public override string ToString() {
        var what = Symptom switch {
            AiSymptom.Flapping => $"changed action {Value:0} times",
            AiSymptom.StuckFailing => $"failed {Action} {Value:0} times and tried nothing else",
            AiSymptom.NeverFinishes => $"has been running {Action} for {Samples} step(s) without finishing",
            AiSymptom.Idle => $"chose nothing for {Samples} step(s)",
            AiSymptom.Thrashing => $"averaged {Value:0.#} transitions a step",
            _ => "is fine"
        };

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Entity} ({Planner}) {what}, over ticks {First}–{Last}."
        );
    }
}

/// <summary>What counts as bad enough to report.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Thresholds rather than heuristics, and they are arguments rather than constants.</b> A
///         patrol that picks a new waypoint every second is flapping by the numbers and is working
///         perfectly; whether four switches in a window is a bug depends on the window and on the
///         game. What this can honestly do is count and say what it counted, which is why every
///         finding carries its evidence and its tick range.
///     </para>
///     <para>
///         ⚠ <b>Property initialisers and an explicit parameterless constructor, not
///         primary-constructor defaults.</b> A record struct's primary-constructor defaults do not run
///         for <c>default</c> <i>or</i> for <c>new()</c> — only when somebody names the constructor —
///         so writing them that way gives a "default" of all zeros, which here means every threshold
///         is met and every agent in the game is reported as flapping. It is the same trap
///         <c>BehaviorLayoutOptions</c> paid for in P2 and <c>ConstraintGizmoStyle</c> before that.
///         <c>default</c> is still all zeros, which <see cref="AiDiagnosis.Analyse" /> reads as
///         "nothing was passed".
///     </para>
/// </remarks>
public readonly record struct AiDiagnosisSettings {
    /// <summary>The shipped thresholds.</summary>
    public static AiDiagnosisSettings Default => new();

    /// <summary>How many action changes in the window is flapping.</summary>
    public int Switches { get; init; } = 4;

    /// <summary>How many failures of one action with nothing else tried is stuck.</summary>
    public int Failures { get; init; } = 4;

    /// <summary>How many records of one unfinished action is never finishing.</summary>
    public int Steps { get; init; } = 64;

    /// <summary>What average transitions a step counts as thrashing.</summary>
    public float Transitions { get; init; } = 8f;

    /// <summary>How many records an agent needs before it is judged at all.</summary>
    public int Minimum { get; init; } = 4;

    /// <summary>Creates the shipped thresholds.</summary>
    public AiDiagnosisSettings() {
    }
}

/// <summary>
///     Reads a recorded log and says what is wrong with the agents in it.
/// </summary>
/// <remarks>
///     <para>
///         <b>Doc 37 § P7's exit criterion, as a type.</b> "An agent misbehaving in a headless test is
///         diagnosed from the recorded log alone" is only a criterion if something can do the
///         diagnosing without the world — so this takes an <see cref="AgentDebugRecorder" /> and
///         nothing else. No <c>World</c>, no <c>AiSystem</c>, no template. Which means it works
///         against a log that arrived over doc 13's channel from a dedicated server, or out of a
///         crash dump, exactly as well as against one in the same process.
///     </para>
///     <para>
///         ⚠ <b>It reports symptoms and never causes.</b> "This agent changed action nine times in
///         forty ticks" is a fact; "its danger consideration is mis-tuned" is a guess, and a debugger
///         that guesses is one people learn to disbelieve. Every finding carries the count it is
///         built from and the ticks it spans, so the next question — <i>why</i> — is asked of the
///         records themselves.
///     </para>
/// </remarks>
public static class AiDiagnosis {
    /// <summary>Reads everything an agent recorded, oldest first.</summary>
    /// <param name="recorder">The log.</param>
    /// <param name="entity">Which agent, or <see cref="Entity.Null" /> for all of them.</param>
    /// <param name="into">Where to put them. Cleared first.</param>
    /// <returns>How many were written.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static int Read(AgentDebugRecorder recorder, Entity entity, List<AgentDebugRecord> into) {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();

        if (recorder.Count == 0) {
            return 0;
        }

        var buffer = new AgentDebugRecord[recorder.Count];
        var written = recorder.CopyTo(buffer);

        for (var index = 0; index < written; index++) {
            if (entity.IsNull || buffer[index].Entity == entity) {
                into.Add(buffer[index]);
            }
        }

        return into.Count;
    }

    /// <summary>Finds what is wrong with every agent in a log.</summary>
    /// <param name="recorder">The log.</param>
    /// <param name="into">Where the findings go. Cleared first.</param>
    /// <param name="settings">What counts as bad, or the shipped thresholds.</param>
    /// <returns>How many findings there were.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static int Analyse(
        AgentDebugRecorder recorder,
        List<AiFinding> into,
        AiDiagnosisSettings settings = default
    ) {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();

        if (settings == default) {
            settings = AiDiagnosisSettings.Default;
        }

        var records = new List<AgentDebugRecord>();

        if (Read(recorder, Entity.Null, records) == 0) {
            return 0;
        }

        // One pass to find who is in the log, then one pass per agent. A log is thousands of records
        // and a handful of agents, so the alternative — a dictionary of lists — allocates a list per
        // agent to save a walk that is already the cheap half.
        var seen = new HashSet<Entity>();

        foreach (var record in records) {
            if (seen.Add(record.Entity)) {
                Judge(records, record.Entity, settings, into);
            }
        }

        return into.Count;
    }

    /// <summary>The whole log as text, oldest first — what a failing headless test prints.</summary>
    /// <param name="recorder">The log.</param>
    /// <param name="entity">Which agent, or <see cref="Entity.Null" /> for all of them.</param>
    /// <returns>It.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="recorder" /> is null.</exception>
    public static string Describe(AgentDebugRecorder recorder, Entity entity = default) {
        var records = new List<AgentDebugRecord>();

        Read(recorder, entity, records);

        var text = new System.Text.StringBuilder();
        var findings = new List<AiFinding>();

        Analyse(recorder, findings);

        foreach (var finding in findings) {
            if (entity.IsNull || finding.Entity == entity) {
                text.Append(finding).Append('\n');
            }
        }

        foreach (var record in records) {
            text.Append("  ").Append(record).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>Judges one agent's slice of the log.</summary>
    static void Judge(
        List<AgentDebugRecord> records,
        Entity entity,
        in AiDiagnosisSettings settings,
        List<AiFinding> into
    ) {
        var samples = 0;
        var switches = 0;
        var failures = 0;
        var successes = 0;
        var running = 0;
        var idle = 0;
        var transitions = 0f;
        var first = 0L;
        var last = 0L;
        var previous = Symbol.None;
        var busiest = Symbol.None;
        var planner = AiPlanner.None;
        var failing = Symbol.None;
        var failingKinds = 0;

        foreach (var record in records) {
            if (record.Entity != entity) {
                continue;
            }

            if (samples == 0) {
                first = record.Tick;
                previous = record.Action;
            }

            last = record.Tick;
            planner = record.Planner;
            samples++;
            transitions += record.Score;

            if (record.Action != previous) {
                switches++;
                previous = record.Action;
            }

            if (!record.Action.IsSome) {
                idle++;
            } else {
                busiest = record.Action;
            }

            switch (record.Status) {
                case ActionStatus.Failed:
                    failures++;

                    if (record.Action != failing) {
                        failing = record.Action;
                        failingKinds++;
                    }

                    break;

                case ActionStatus.Succeeded:
                    successes++;

                    break;

                default:
                    running++;

                    break;
            }
        }

        if (samples < settings.Minimum) {
            return;
        }

        if (switches >= settings.Switches) {
            into.Add(new(entity, AiSymptom.Flapping, busiest, planner, samples, switches, first, last));
        }

        // ⚠ "And tried nothing else" is the whole of the test. An action that fails sometimes is a
        // tree working — a selector's first child failing is how a selector chooses — and only an
        // agent that failed at one thing repeatedly with no success anywhere is actually stuck.
        if (failures >= settings.Failures && successes == 0 && failingKinds <= 1) {
            into.Add(new(entity, AiSymptom.StuckFailing, failing, planner, samples, failures, first, last));
        }

        if (idle >= settings.Minimum && idle == samples) {
            into.Add(new(entity, AiSymptom.Idle, Symbol.None, planner, samples, idle, first, last));
        } else if (running == samples && switches == 0 && samples >= settings.Steps) {
            into.Add(new(entity, AiSymptom.NeverFinishes, busiest, planner, samples, samples, first, last));
        }

        var average = transitions / samples;

        // ⚠ Trees only, because <see cref="AgentDebugRecord.Score" /> is the number behind the reason
        // and only a tree's reason is a transition count. A GOAP record carries a plan cost there,
        // and a domain whose actions cost ten would otherwise be reported as thrashing for ever.
        if (planner == AiPlanner.BehaviorTree && average >= settings.Transitions) {
            into.Add(new(entity, AiSymptom.Thrashing, busiest, planner, samples, average, first, last));
        }
    }
}
