// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai.Ecs;
using Vixen.Ai.Perception.Ecs;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Xunit;

namespace Vixen.Ai.Perception.Tests;

public class SightTests {
    [Fact]
    public void ASourceInsideTheRadiusIsSeenAndOneOutsideIsNot() {
        var fleet = new Fleet();
        var listener = fleet.Listener(Vector3.Zero);
        var near = fleet.Source(new(0f, 0f, -5f));

        fleet.Source(new(0f, 0f, -40f));
        fleet.Step();

        var perceived = fleet.Perceived(listener);

        Assert.Equal(1, perceived.Count);
        Assert.True(perceived.TryGet(near, out var target));
        Assert.Equal(AiSense.Sight, target.Sense);
        Assert.True(target.Current);
    }

    /// <summary>
    ///     ⚠ The one doc 37 § D15 says every implementation gets wrong. A target walking the boundary
    ///     with one radius is found and lost several times a second; with the second, larger one it is
    ///     found once and lost once.
    /// </summary>
    [Fact]
    public void TheLoseSightRadiusIsWhatStopsTheFlicker() {
        // Five changes of mind against one, over the same six positions. Every one of the five is a
        // decorator observing the target key, and every one of them aborts a branch.
        Assert.Equal(5, Transitions(20f));
        Assert.Equal(1, Transitions(25f));

        static int Transitions(float loseSight) {
            var fleet = new Fleet(
                Fleet.Everything() with {
                    Sight = new() { Radius = 20f, LoseSightRadius = loseSight, ConeDegrees = 360f, Occlusion = false }
                }
            );

            var listener = fleet.Listener(Vector3.Zero);
            var target = fleet.Source(new(0f, 0f, -19f));

            fleet.Step();

            var seen = fleet.Perceived(listener).IsPerceiving(SenseMask.Sight);
            var changes = 0;

            // Loitering on the boundary — 21, 19, 21, 19, 21, which is a target standing still and an
            // agent breathing — and then leaving for good.
            foreach (var distance in new[] { 21f, 19f, 21f, 19f, 21f, 30f }) {
                fleet.MoveTo(target, new(0f, 0f, -distance));
                fleet.Step();

                var now = fleet.Perceived(listener).IsPerceiving(SenseMask.Sight);

                if (now != seen) {
                    changes++;
                }

                seen = now;
            }

            return changes;
        }
    }

    [Fact]
    public void TheConeIsWhatTheAgentIsFacing() {
        var fleet = new Fleet(
            Fleet.Everything() with {
                Sight = new() { Radius = 20f, LoseSightRadius = 20f, ConeDegrees = 90f, Occlusion = false }
            }
        );

        var listener = fleet.Listener(Vector3.Zero);

        fleet.Source(new(0f, 0f, -5f));
        fleet.Step();

        Assert.True(fleet.Perceived(listener).IsPerceiving(SenseMask.Sight));

        // Turned right round. The source has not moved and is well inside the radius.
        fleet.Face(listener, Quaternion.FromAxisAngle(Vector3.Up, MathF.PI));
        fleet.Step();

        Assert.False(fleet.Perceived(listener).IsPerceiving(SenseMask.Sight));
    }

    /// <summary>
    ///     A zeroed <c>LocalTransform</c> has a zero quaternion, which rotates every vector to nothing.
    ///     An agent built with <c>new()</c> would face nowhere and see nothing, silently.
    /// </summary>
    [Fact]
    public void AZeroedRotationFacesForwardRatherThanNowhere() {
        var fleet = new Fleet(
            Fleet.Everything() with {
                Sight = new() { Radius = 20f, LoseSightRadius = 20f, ConeDegrees = 90f, Occlusion = false }
            }
        );

        var listener = fleet.World.Create(AiPerception.Sensing(fleet.Config), default(Engine.Transforms.LocalTransform));

        fleet.Source(new(0f, 0f, -5f));
        fleet.Step();

        Assert.True(fleet.Perceived(listener).IsPerceiving(SenseMask.Sight));
    }

    [Fact]
    public void ALostTargetKeepsWhereItWasAndItsAgeGrows() {
        var fleet = new Fleet();
        var listener = fleet.Listener(Vector3.Zero);
        var target = fleet.Source(new(0f, 0f, -5f));

        fleet.Step();
        fleet.MoveTo(target, new(0f, 0f, -100f));
        fleet.Step(4);

        Assert.True(fleet.Perceived(listener).TryGet(target, out var remembered));
        Assert.False(remembered.Current);
        Assert.Equal(-5f, remembered.LastKnownLocation.Z, 3);
        Assert.Equal(0.4f, remembered.AgeAt(fleet.System.Clock), 2);
    }

    [Fact]
    public void AForgottenTargetLeavesTheList() {
        var fleet = new Fleet(Fleet.Everything() with { Memory = 0.25f });
        var listener = fleet.Listener(Vector3.Zero);
        var target = fleet.Source(new(0f, 0f, -5f));

        fleet.Step();
        fleet.MoveTo(target, new(0f, 0f, -100f));
        fleet.Step(2);

        Assert.Equal(1, fleet.Perceived(listener).Count);

        fleet.Step(3);

        Assert.Equal(0, fleet.Perceived(listener).Count);
    }

    [Fact]
    public void TheListIsBoundedAndAFullListOfLiveTargetsRefusesMore() {
        var fleet = new Fleet(Fleet.Everything() with { MaxPerceived = 2 });
        var listener = fleet.Listener(Vector3.Zero);

        fleet.Source(new(0f, 0f, -1f));
        fleet.Source(new(0f, 0f, -2f));
        fleet.Source(new(0f, 0f, -3f));
        fleet.Step();

        Assert.Equal(2, fleet.Perceived(listener).Count);
    }
}

public class FilterTests {
    [Fact]
    public void TheTeamFilterStopsAnAllyBeingNoticed() {
        var fleet = new Fleet(Fleet.Everything() with { Filter = PerceptionFilters.Hostiles });
        var listener = fleet.Listener(Vector3.Zero, team: 1);

        fleet.Source(new(0f, 0f, -5f), team: 1);

        var enemy = fleet.Source(new(0f, 0f, -6f), team: 2);

        fleet.Step();

        var perceived = fleet.Perceived(listener);

        Assert.Equal(1, perceived.Count);
        Assert.True(perceived.TryGet(enemy, out _));
    }

    /// <summary>
    ///     ⚠ Damage goes through the filter regardless. An agent shot by its own side has to notice,
    ///     or friendly fire is invisible to the AI and a squad walks through its own grenades.
    /// </summary>
    [Fact]
    public void DamageReachesItsVictimEvenFromItsOwnSide() {
        var fleet = new Fleet(Fleet.Everything() with { Filter = PerceptionFilters.Hostiles });
        var listener = fleet.Listener(Vector3.Zero, team: 1);
        var ally = fleet.Source(new(0f, 0f, -5f), team: 1);

        fleet.System.ReportDamage(ally, listener, new(0f, 0f, -5f), 10f);
        fleet.Step();

        Assert.True(fleet.Perceived(listener).TryGet(ally, out var hurt));
        Assert.Equal(AiSense.Damage, hurt.Sense);
    }

    [Fact]
    public void ADelegateFilterIsAsked() {
        var asked = 0;
        var fleet = new Fleet(
            Fleet.Everything() with {
                Filter = PerceptionFilters.Where(
                    (in PerceptionParticipant listener, in PerceptionParticipant source, AiSense sense) => {
                        asked++;

                        return false;
                    }
                )
            }
        );

        var listener = fleet.Listener(Vector3.Zero);

        fleet.Source(new(0f, 0f, -5f));
        fleet.Step();

        Assert.True(asked > 0);
        Assert.Equal(0, fleet.Perceived(listener).Count);
    }
}

public class EventSenseTests {
    [Fact]
    public void ANoiseIsHeardOnceAndThenRemembered() {
        var fleet = new Fleet(Fleet.Everything(SenseMask.Hearing));
        var listener = fleet.Listener(Vector3.Zero);
        var shooter = fleet.Source(new(0f, 0f, -25f));

        fleet.System.ReportNoise(shooter, new(0f, 0f, -25f));
        fleet.Step();

        Assert.True(fleet.Perceived(listener).TryGet(shooter, out var heard));
        Assert.Equal(AiSense.Hearing, heard.Sense);
        Assert.True(heard.Current);

        // ⚠ Not heard a second time. An event consumed on every pass until it expires would read as a
        // single gunshot still going off a second later.
        fleet.Step();

        Assert.True(fleet.Perceived(listener).TryGet(shooter, out var stale));
        Assert.False(stale.Current);
    }

    [Fact]
    public void ANoiseCarriesAsFarAsItIsLoud() {
        var fleet = new Fleet(Fleet.Everything(SenseMask.Hearing));
        var near = fleet.Listener(Vector3.Zero);
        var far = fleet.Listener(new(0f, 0f, -60f));
        var shooter = fleet.Source(new(0f, 0f, -25f));

        fleet.System.ReportNoise(shooter, new(0f, 0f, -25f));
        fleet.Step();

        Assert.True(fleet.Perceived(near).IsPerceiving(SenseMask.Hearing));
        Assert.False(fleet.Perceived(far).IsPerceiving(SenseMask.Hearing));

        fleet.System.ReportNoise(shooter, new(0f, 0f, -25f), loudness: 3f);
        fleet.Step();

        Assert.True(fleet.Perceived(far).IsPerceiving(SenseMask.Hearing));
    }

    [Fact]
    public void TouchIsCloseRangeAndDoesNotNeedALineOfSight() {
        var fleet = new Fleet(Fleet.Everything(SenseMask.Touch));
        var listener = fleet.Listener(Vector3.Zero);
        var touching = fleet.Source(new(0f, 0f, -1f));

        fleet.Source(new(0f, 0f, -5f));
        fleet.Step();

        var perceived = fleet.Perceived(listener);

        Assert.Equal(1, perceived.Count);
        Assert.True(perceived.TryGet(touching, out var felt));
        Assert.Equal(AiSense.Touch, felt.Sense);
    }

    /// <summary>Sight knows where something is; hearing knows where a noise was. Sight wins the tie.</summary>
    [Fact]
    public void SightBeatsHearingForTheSameTargetInOnePass() {
        var fleet = new Fleet();
        var listener = fleet.Listener(Vector3.Zero);
        var target = fleet.Source(new(0f, 0f, -5f));

        fleet.System.ReportNoise(target, new(0f, 0f, -500f));
        fleet.Step();

        Assert.True(fleet.Perceived(listener).TryGet(target, out var known));
        Assert.Equal(AiSense.Sight, known.Sense);
        Assert.Equal(-5f, known.LastKnownLocation.Z, 3);
    }
}

public class TeamRelayTests {
    /// <summary>An ally that sees something tells the agents near it, and that is all it tells them.</summary>
    [Fact]
    public void AnAllyRelaysWhatItSeesAndTheRelayIsNotRelayedOnwards() {
        var fleet = new Fleet();
        var scout = fleet.Both(Vector3.Zero, team: 0);
        var rear = fleet.Both(new(0f, 0f, 22f), team: 0);
        var deep = fleet.Both(new(0f, 0f, 44f), team: 0);
        var enemy = fleet.Source(new(0f, 0f, -5f), team: 1);

        fleet.Step(3);

        Assert.True(fleet.Perceived(scout).TryGet(enemy, out var seen));
        Assert.Equal(AiSense.Sight, seen.Sense);

        Assert.True(fleet.Perceived(rear).TryGet(enemy, out var told));
        Assert.Equal(AiSense.Team, told.Sense);

        // ⚠ `deep` is in range of `rear` and not of `scout`. A relay that relayed would reach it,
        // several passes late, with nobody in the chain having seen anything.
        Assert.False(fleet.Perceived(deep).TryGet(enemy, out _));
    }
}

public class BindingTests {
    static readonly BlackboardLayout Layout = new BlackboardLayoutBuilder()
        .Add("target", BlackboardValueType.Entity)
        .Add("where", BlackboardValueType.Vector3)
        .Add("age", BlackboardValueType.Float)
        .Add("alert", BlackboardValueType.Bool)
        .Add("count", BlackboardValueType.Int)
        .Build();

    [Fact]
    public void TheTripleIsWrittenAndTheTargetSurvivesBeingLost() {
        var (fleet, agents, listener, target) = Build(
            new TargetLocationAgeBinding(SenseMask.Sight, Key("target"), Key("where"), Key("age"))
        );

        Step(fleet, agents);

        var blackboard = agents.BlackboardOf(fleet.World.Get<AiAgent>(listener))!;

        Assert.Equal(target, blackboard.GetEntity(Key("target")));
        Assert.Equal(-5f, blackboard.GetVector3(Key("where")).Z, 3);
        Assert.Equal(0f, blackboard.GetFloat(Key("age")), 3);

        fleet.MoveTo(target, new(0f, 0f, -100f));
        Step(fleet, agents, 3);

        // ⚠ Still set. "Chase him" and "search where he was" read one key and branch on the age,
        // rather than being two branches over two keys with two copies of the position.
        Assert.Equal(target, blackboard.GetEntity(Key("target")));
        Assert.Equal(-5f, blackboard.GetVector3(Key("where")).Z, 3);
        Assert.True(blackboard.GetFloat(Key("age")) > 0.2f);
    }

    [Fact]
    public void TheCountBindingWritesAFlagAndANumberAndNamesNoTarget() {
        var (fleet, agents, listener, _) = Build(new PerceivedCountBinding(SenseMask.Sight, Key("alert"), Key("count")));

        fleet.Source(new(0f, 0f, -6f));
        Step(fleet, agents);

        var blackboard = agents.BlackboardOf(fleet.World.Get<AiAgent>(listener))!;

        Assert.True(blackboard.GetBool(Key("alert")));
        Assert.Equal(2, blackboard.GetInt(Key("count")));
        Assert.False(blackboard.IsSet(Key("target")));
    }

    static (Fleet Fleet, AiSystem Agents, Entity Listener, Entity Target) Build(IBlackboardBinding binding) {
        var fleet = new Fleet(Fleet.Everything() with { Binding = binding });
        var agents = new AiSystem(Registry(), Layout);
        var listener = fleet.World.Create(
            AiPerception.Sensing(fleet.Config),
            Engine.Transforms.LocalTransform.Identity,
            AiAgent.Running(0)
        );

        fleet.System.Agents = agents;

        return (fleet, agents, listener, fleet.Source(new(0f, 0f, -5f)));
    }

    static AgentActionRegistry Registry() {
        var registry = new AgentActionRegistry();

        registry.Register("idle", new FinishWithTask(ActionStatus.Running));

        return registry;
    }

    static void Step(Fleet fleet, AiSystem agents, int count = 1) {
        for (var index = 0; index < count; index++) {
            agents.Step(fleet.World, Fleet.Frame(index));
            fleet.Step();
        }
    }

    static BlackboardKey Key(string name) {
        Assert.True(Layout.TryGetKey(Symbol.Intern(name), out var key));

        return key;
    }
}

public class ScheduleTests {
    [Fact]
    public void DistanceLodStretchesTheIntervalInBands() {
        var config = new PerceptionConfig { Interval = 0.1f };
        var governor = new DistanceLodGovernor();

        Assert.Equal(0.1f, governor.IntervalFor(config, 0f), 4);
        Assert.Equal(0.15f, governor.IntervalFor(config, 30f), 4);

        // Four hertz behind the player, which is doc 37 § D15's figure.
        Assert.Equal(0.25f, governor.IntervalFor(config, 100f), 4);
        Assert.Equal(0.1f, FixedRateGovernor.Instance.IntervalFor(config, 100f), 4);
    }

    /// <summary>⚠ With no focus set every listener is at distance zero, which is full rate.</summary>
    [Fact]
    public void NoFocusMeansFullRateRatherThanTheSlowestBand() {
        var fleet = new Fleet(Fleet.Everything() with { Interval = 0.1f });

        fleet.System.Governor = new DistanceLodGovernor();
        fleet.Listener(new(0f, 0f, 500f));
        fleet.Source(new(0f, 0f, 500f));
        fleet.Step(4);

        Assert.Equal(1, fleet.System.LastStats.Passes);

        fleet.System.Focus = Vector3.Zero;
        fleet.Step(4);

        // Five hundred metres out, so a pass every 0.25 s against a 0.1 s frame.
        Assert.True(fleet.System.LastStats.Passes <= 1);
    }

    /// <summary>
    ///     ⚠ A wave of listeners spawned in one frame must not share a phase for ever. Without the
    ///     spread on join, every one of them senses on the same tick and the frame costs the whole
    ///     population.
    /// </summary>
    [Fact]
    public void ListenersSpawnedTogetherDoNotSenseTogether() {
        var fleet = new Fleet(Fleet.Everything() with { Interval = 1f, RandomDeviation = 0.1f });

        for (var index = 0; index < 40; index++) {
            fleet.Listener(new(index * 3f, 0f, 0f));
        }

        var worst = 0;

        for (var frame = 0; frame < 20; frame++) {
            fleet.Step();
            worst = Math.Max(worst, fleet.System.LastStats.Passes);
        }

        Assert.True(worst < 40, $"the worst frame sensed for {worst} of 40 listeners.");
    }

    [Fact]
    public void AListenerThatIsDestroyedGivesItsSlotBack() {
        var fleet = new Fleet();
        var first = fleet.Listener(Vector3.Zero);

        fleet.Step();
        Assert.Equal(1, fleet.System.Population);

        fleet.World.Destroy(first);
        fleet.Step();
        Assert.Equal(0, fleet.System.Population);

        var second = fleet.Listener(Vector3.Zero);

        fleet.Step();
        Assert.Equal(1, fleet.System.Population);
        Assert.Equal(0, fleet.World.Get<AiPerception>(second).ListenerIndex);
    }

    [Fact]
    public void TheSystemDeclaresWhatItTouches() {
        var system = new PerceptionSystem();

        Assert.Contains(ComponentType<AiPerception>.Id, system.Access.Writes);
        Assert.Contains(ComponentType<AiStimuliSource>.Id, system.Access.Reads);
    }
}
