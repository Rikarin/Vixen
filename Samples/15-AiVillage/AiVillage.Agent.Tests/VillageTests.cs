// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai.Diagnostics;
using Vixen.Ai.Ecs;
using Vixen.Ai.Nodes;
using Vixen.Ai.Perception.Ecs;
using Vixen.Core;
using Vixen.Ecs.Systems;
using Vixen.Engine.Diagnostics;
using Xunit;

namespace Vixen.Samples.AiVillage.Tests;

/// <summary>The sample's claim, asserted: the decision is a function of the world.</summary>
/// <remarks>
///     <para>
///         <b>"The AI ran" is not the claim and would not be worth a suite.</b> A stack that steps
///         ten thousand agents and decides nothing passes every performance criterion doc 37 has.
///         What these assert is that each agent's <i>choice</i> changed when the intruder arrived
///         and changed back when it left — and, for the scavenger, that it did not, because three
///         agents that all reacted would prove nothing about there being three planners.
///     </para>
///     <para>
///         ⚠ <b>Through a real <see cref="Vixen.Engine.Frames.EngineLoop" />.</b>
///         <c>VillageSampleTests</c> in <c>Vixen.Ai.Nodes.Tests</c> asserts the same shape over a
///         hand-written stepping loop, so it cannot fail because the engine ordered the systems
///         differently. <see cref="The_engine_runs_perception_before_the_planners" /> is the half
///         that only exists here.
///     </para>
/// </remarks>
public class VillageTests {
    /// <summary>Long enough for the whole script and a little after it.</summary>
    const double WholeScript = 24.0;

    /// <summary>
    ///     ⚠ <b>The guard leaves its beat and goes back to it</b> — the abort in both directions.
    /// </summary>
    /// <remarks>
    ///     Going back is the half that fails when a lose-sight radius is missing or an abort scope
    ///     is wrong, and doc 37's perception README records that symptom as five changes of mind
    ///     against one.
    /// </remarks>
    [Fact]
    public void The_guard_leaves_its_beat_for_the_intruder_and_returns_to_it() {
        using var run = new VillageRun();

        run.Until(WholeScript);

        var chose = run.Decisions.For("guard").Select(change => change.To.ToString()).ToList();
        var report = string.Join(" → ", chose);

        Assert.Equal("patrol", chose[0]);
        Assert.Contains("chase", chose);
        Assert.True(chose.Count >= 3, $"the guard never changed its mind twice. {report}");

        // It went back to walking the beat after the intruder left, rather than standing where it
        // gave up. The last thing it chose is the thing it does when nothing is happening.
        Assert.Equal("patrol", chose[^1]);

        // And the chase began while the intruder was inside the sight radius rather than at some
        // arbitrary moment — a guard that "chased" from across the map would pass the line above.
        var chase = run.Decisions.For("guard").First(change => change.To == Symbol.Intern("chase"));

        Assert.True(chase.Distance <= 16f, $"the guard began chasing from {chase.Distance} m away.");
    }

    /// <summary>The villager scores rather than switches, and the refuge is a global sensor's.</summary>
    [Fact]
    public void The_villager_runs_for_the_refuge_and_settles_again() {
        using var run = new VillageRun();

        var start = run.Village.Where(run.Village.Villager);

        run.Until(12.0);

        var chose = run.Decisions.For("villager").Select(change => change.To.ToString()).ToList();

        Assert.Equal("pause", chose[0]);
        Assert.Contains("flee", chose);

        // ⚠ To the refuge, which it was told about by a *global* sensor cached once a pass — not to
        // wherever "away" happens to be. A zeroed key would send it to the world origin and still
        // look like fleeing, which is exactly the defect this sample found.
        var here = run.Village.Where(run.Village.Villager);

        Assert.True(
            AgentTarget.FlatDistance(here, Village.Refuge)
            < AgentTarget.FlatDistance(start, Village.Refuge),
            $"the villager is not heading for the refuge: {here}."
        );

        Assert.True(AgentTarget.FlatDistance(here, Village.Refuge) < 6f, $"it never arrived: {here}.");

        run.Until(WholeScript);

        // And it stopped running once the threat had gone.
        Assert.Equal("pause", run.Decisions.For("villager").Select(change => change.To.ToString()).Last());
    }

    /// <summary>
    ///     ⚠ <b>And the scavenger ignores the whole affair</b>, which is the half of "visibly
    ///     different" that is easy to forget.
    /// </summary>
    [Fact]
    public void The_scavenger_ignores_the_intruder_and_gets_on_with_its_plans() {
        using var run = new VillageRun();

        run.Until(WholeScript);

        var chose = run.Decisions.For("scavenger").Select(change => change.To.ToString()).ToList();

        Assert.DoesNotContain("chase", chose);
        Assert.DoesNotContain("flee", chose);
        Assert.Contains("collect", chose);
        Assert.Contains("deposit", chose);

        // A plan is a chain and only its head is committed, so "it planned" is a count rather than
        // a state — and a domain that never resolved would leave it at zero.
        var planning = run.Village.Agents.PlanningOf(in run.Village.World.Read<AiAgent>(run.Village.Scavenger));

        Assert.NotNull(planning);
        Assert.True(planning.Plans > 0, "the scavenger never planned anything.");

        // ⚠ And it was refused nothing: `Expanded` reaching the budget is how a badly authored
        // domain is rejected rather than hung, so a village whose plans exhausted it would be a
        // village whose domain is wrong.
        Assert.True(
            planning.Plan.Expanded < run.Village.Agents.Goap.NodeBudget,
            $"the scavenger's domain exhausted the node budget ({planning.Plan.Expanded})."
        );
    }

    /// <summary>Doc 37 § D2's payoff: what differs is the planner and nothing else.</summary>
    [Fact]
    public void Every_agent_shares_one_of_everything_but_its_planner() {
        using var run = new VillageRun();

        run.Frames(60);

        var village = run.Village;

        Assert.Equal(3, village.Agents.Population);

        Assert.Equal(
            [AiPlanner.BehaviorTree, AiPlanner.Utility, AiPlanner.Goap],
            new[] { village.Guard, village.Villager, village.Scavenger }
                .Select(agent => village.World.Read<AiAgent>(agent).Planner)
                .ToArray()
        );

        Assert.Same(village.Registry, village.Agents.Actions);
        Assert.Same(village.Layout, village.Agents.Layout);

        // One perception config, and all three listen through it.
        foreach (var agent in new[] { village.Guard, village.Villager, village.Scavenger }) {
            Assert.Equal(0, village.World.Read<AiPerception>(agent).Config);
        }

        Assert.Equal(1, village.Perception.Configs.Count);

        // ⚠ And one action index reached by two planners: the villager rests on the same registered
        // `Wait` the scavenger's domain waits on.
        Assert.Equal(village.Pause, village.Agents.Sets[0][1].Action);
        Assert.Equal(village.Pause, village.Agents.Domains[0][2].Action);
    }

    /// <summary>
    ///     ⚠ <b>The ordering is the engine's, taken off the declaration.</b>
    /// </summary>
    /// <remarks>
    ///     <c>PerceptionSystem</c> carries <c>[UpdateBefore(typeof(AiSystem))]</c> and the sample's
    ///     own <c>IntruderSystem</c> carries <c>[UpdateBefore(typeof(PerceptionSystem))]</c>. Every
    ///     other caller of this stack in the repository calls <c>Step</c> by hand in the order it
    ///     decided on, so until this sample existed no scheduler had ever been asked to honour
    ///     either — a declaration nothing reads is a comment.
    /// </remarks>
    [Fact]
    public void The_engine_runs_perception_before_the_planners() {
        using var run = new VillageRun();

        run.Frames(1);

        var order = run.Loop.Systems.Graph.InPhase(SystemPhase.Update)
            .Select(node => node.Name)
            .ToList();

        var script = order.IndexOf(nameof(IntruderSystem));
        var senses = order.IndexOf(nameof(Vixen.Ai.Perception.Ecs.PerceptionSystem));
        var think = order.IndexOf(nameof(AiSystem));

        var report = string.Join(", ", order);

        Assert.True(script >= 0 && senses >= 0 && think >= 0, $"a system is missing: {report}");
        Assert.True(script < senses, $"the intruder moves after it is perceived: {report}");
        Assert.True(senses < think, $"the agents think before they sense: {report}");
    }

    /// <summary>Two runs of one script decide identically, line for line.</summary>
    /// <remarks>
    ///     ⚠ <b>The whole transcript rather than a count.</b> Two runs that each changed their minds
    ///     fourteen times, at different moments and about different things, would pass a count —
    ///     and doc 37 § D18's determinism is about <i>the decision</i>, which is what a transcript
    ///     is and a total is not.
    /// </remarks>
    [Fact]
    public void Two_runs_of_one_script_decide_identically() {
        using var first = new VillageRun();
        using var second = new VillageRun();

        first.Until(WholeScript);
        second.Until(WholeScript);

        Assert.NotEmpty(first.Decisions.Changes);
        Assert.Equal(first.Decisions.Transcript(), second.Decisions.Transcript());
    }

    /// <summary>
    ///     ⚠ <b>Wall time passing does not advance the script</b>, which is the test
    ///     <c>Samples/13</c>'s <c>ScriptedWalk</c> records as the one that caught a <c>Stopwatch</c>
    ///     a determinism test had happily passed against.
    /// </summary>
    [Fact]
    public void Wall_time_passing_does_not_advance_the_intruder() {
        using var run = new VillageRun();

        run.Until(10.0);

        var where = run.Village.Where(run.Village.Intruder);
        var elapsed = run.Elapsed;

        Thread.Sleep(50);

        for (var index = 0; index < 30; index++) {
            run.Loop.Frame(TimeSpan.Zero);
        }

        Assert.Equal(elapsed, run.Elapsed);
        Assert.Equal(where, run.Village.Where(run.Village.Intruder));
    }

    /// <summary>
    ///     doc 37 § P7's overlay, drawing a running game's agents — with no device anywhere.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the part that had no consumer at all.</b> <c>AiGameplayDebugger</c> is
    ///     constructed by its own tests; <c>AiOverlaySystem</c> was constructed by <i>nothing</i>,
    ///     in the whole repository, until the sample registered one.
    /// </remarks>
    [Fact]
    public void The_overlay_draws_every_agent_in_the_running_village() {
        using var run = new VillageRun();

        var draw = new DebugDraw();
        var debugger = new AiGameplayDebugger {
            Style = AiOverlayStyle.Everything,
            Perception = run.Village.Perception
        };

        run.Loop.Add(new AiOverlaySystem(debugger, run.Village.Agents, draw));
        run.Until(10.0);

        Assert.Equal(3, debugger.DrawnAgents);
        Assert.True(draw.Count > 0, "the overlay drew no geometry.");
        Assert.True(draw.TextCount > 0, "the overlay drew no labels.");
    }

    /// <summary>
    ///     Neither reacting agent is diagnosed as misbehaving — and the one that is, is a threshold
    ///     rather than a defect.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A diagnosis that fires on a working village is one people learn to ignore</b>,
    ///         and doc 37 § P9 deleted a whole symptom for exactly this reason:
    ///         <c>AiSymptom.NeverFinishes</c> reported two of the fixture's three perfectly-behaved
    ///         agents, because the log has no notion of progress and a patrol is one long action.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Running it for twenty-four seconds finds the same shape again, in
    ///         <c>Flapping</c>.</b> <c>AiDiagnosis</c> counts action changes over whatever the
    ///         recorder's ring is holding and compares that count to
    ///         <c>AiDiagnosisSettings.Switches</c> — an absolute number, not a rate. The scavenger
    ///         alternates <c>collect</c> and <c>deposit</c> roughly every seventy frames, which is
    ///         its domain working exactly as authored, and it crosses the shipped threshold of four
    ///         after about five seconds. A longer run always trips it.
    ///     </para>
    ///     <para>
    ///         So this asserts what is true rather than tuning the threshold until zero comes back:
    ///         the two agents whose decisions turn on the intruder are clean, and the only finding
    ///         in the village is the scavenger's own cycle. Doc 37 § P7 says the thresholds are
    ///         <i>"arguments rather than constants, because whether four switches in a window is a
    ///         bug depends on the window and on the game"</i> — but there is no window here, and a
    ///         game cannot supply one.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Neither_agent_that_reacts_to_the_intruder_is_diagnosed_as_misbehaving() {
        using var run = new VillageRun();

        run.Until(WholeScript);

        var findings = new List<AiFinding>();

        AiDiagnosis.Analyse(run.Village.Agents.Debug, findings);

        var report = string.Join(" | ", findings.Select(finding => $"{finding.Entity} {finding.Symptom}"));

        Assert.DoesNotContain(findings, finding => finding.Entity == run.Village.Guard);
        Assert.DoesNotContain(findings, finding => finding.Entity == run.Village.Villager);

        // And nothing is *stuck* or *failing* anywhere — those are the ones that would mean the
        // village is broken rather than that a counter is absolute.
        Assert.All(findings, finding => Assert.Equal(AiSymptom.Flapping, finding.Symptom));
        Assert.All(findings, finding => Assert.Equal(run.Village.Scavenger, finding.Entity));

        Assert.True(findings.Count <= 1, report);
    }

    /// <summary>And the whole point, in one line: the stack ran and it decided things.</summary>
    [Fact]
    public void The_village_decides_something_about_every_agent() {
        using var run = new VillageRun();

        run.Until(WholeScript);

        Assert.NotEmpty(run.Decisions.For("guard"));
        Assert.NotEmpty(run.Decisions.For("villager"));
        Assert.NotEmpty(run.Decisions.For("scavenger"));

        // ⚠ A stack that runs and decides nothing is the failure to expect, and it is the one a
        // frame counter cannot see.
        Assert.True(run.Decisions.Changes.Count >= 8, run.Decisions.Transcript());
    }
}
