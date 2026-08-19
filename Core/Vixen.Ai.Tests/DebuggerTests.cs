// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.Runtime.InteropServices;
using Vixen.Ai.Diagnostics;
using Vixen.Ai.Ecs;
using Vixen.Core;
using Vixen.Ecs;
using Xunit;

namespace Vixen.Ai.Tests;

/// <summary>
///     P7's first exit criterion: an agent misbehaving in a headless test is diagnosed from the
///     recorded log alone.
/// </summary>
/// <remarks>
///     ⚠ <b>"Alone" is the load-bearing word and every test here honours it.</b> The world, the
///     system and the set go out of scope before <see cref="AiDiagnosis" /> is handed anything, so
///     what these assert is exactly what is available to somebody reading a build machine's output at
///     three in the morning — or to an editor reading a log that arrived over a wire from a dedicated
///     server that has since restarted.
/// </remarks>
public class AiDiagnosisExitCriteriaTests {
    [Fact]
    public void AFlappingAgentIsNamedFromTheLogWithNothingElseToLookAt() {
        var recorder = Record(
            static (system, world, entity) => {
                var danger = 0f;
                var hide = system.Actions.Register("hide", new AlwaysRuns());
                var fight = system.Actions.Register("fight", new AlwaysRuns());
                var set = new UtilitySet(
                    Symbol.Intern("flapper"),
                    Scored("hide", hide, () => 0.5f),
                    Scored("fight", fight, () => danger)
                ) {
                    // Inertia off: the two mechanisms that exist to stop exactly this.
                    CommitmentBonus = 0f,
                    DecisionInterval = 0f
                };

                system.Sets.Add(set);
                world.Get<AiAgent>(entity) = AiAgent.Scoring(0);

                return frame => danger = frame % 2 == 0 ? 0.9f : 0.1f;
            }
        );

        var findings = new List<AiFinding>();

        Assert.True(AiDiagnosis.Analyse(recorder, findings) > 0, AiDiagnosis.Describe(recorder));

        var flapping = findings.Find(finding => finding.Symptom == AiSymptom.Flapping);

        Assert.Equal(AiSymptom.Flapping, flapping.Symptom);
        Assert.Equal(AiPlanner.Utility, flapping.Planner);
        Assert.True(flapping.Value >= 4f, flapping.ToString());
        Assert.True(flapping.Last > flapping.First, "the finding has no window.");
    }

    [Fact]
    public void AnAgentStuckOnOneFailingActionIsNamedAndSoIsTheAction() {
        var recorder = Record(
            static (system, world, entity) => {
                var action = system.Actions.Register("open-the-locked-door", new AlwaysFails());

                world.Get<AiAgent>(entity) = AiAgent.Running(action);

                return _ => { };
            }
        );

        var findings = new List<AiFinding>();

        AiDiagnosis.Analyse(recorder, findings);

        var stuck = findings.Find(finding => finding.Symptom == AiSymptom.StuckFailing);

        Assert.Equal(AiSymptom.StuckFailing, stuck.Symptom);
        Assert.Equal(Symbol.Intern("open-the-locked-door"), stuck.Action);
        Assert.Contains("open-the-locked-door", stuck.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ The other half of the criterion, and the one that decides whether anybody trusts it: an
    ///     agent doing its job must produce <i>no</i> findings. A diagnosis that fires on healthy
    ///     agents is one people learn to ignore, which is worse than not having it.
    /// </summary>
    [Fact]
    public void AnAgentThatIsWorkingProducesNothingAtAll() {
        var recorder = Record(
            static (system, world, entity) => {
                var action = system.Actions.Register("patrol", new SucceedsEveryFewTicks(4), sizeof(int));

                world.Get<AiAgent>(entity) = AiAgent.Running(action);

                return _ => { };
            }
        );

        var findings = new List<AiFinding>();

        Assert.Equal(0, AiDiagnosis.Analyse(recorder, findings));
    }

    /// <summary>Runs an agent for forty steps and hands back nothing but its log.</summary>
    static AgentDebugRecorder Record(Func<AiSystem, World, Entity, Action<int>> arrange) {
        var registry = new AgentActionRegistry();
        var system = new AiSystem(registry, BlackboardLayout.Empty);

        system.Debug.Enabled = true;

        using var world = new World("diagnosis");
        var entity = world.Create(AiAgent.Running(0));

        registry.Register("idle", new AlwaysRuns(), 0);

        var drive = arrange(system, world, entity);

        for (var frame = 0; frame < 40; frame++) {
            drive(frame);
            system.Step(world, Frame(frame));
        }

        // ⚠ The system and the world are gone by the time anything is asked. That is the criterion.
        return system.Debug;
    }

    static UtilityAction Scored(string name, ushort action, Func<float> reading) =>
        new(
            Symbol.Intern(name),
            action,
            new UtilityConsideration(
                Symbol.Intern("axis"),
                UtilityInputs.From((in AgentContext context) => reading()),
                ResponseCurve.Identity
            )
        );

    static GameTime Frame(int index) =>
        new(TimeSpan.FromSeconds(index * 0.1), TimeSpan.FromSeconds(0.1), TimeSpan.FromSeconds(0.1), index, 1f);

    sealed class AlwaysRuns : IAgentAction {
        public void Start(in AgentContext context, Span<byte> state) { }

        public ActionStatus Tick(in AgentContext context, Span<byte> state, float delta) => ActionStatus.Running;

        public void Abort(in AgentContext context, Span<byte> state) { }
    }

    sealed class AlwaysFails : IAgentAction {
        public void Start(in AgentContext context, Span<byte> state) { }

        public ActionStatus Tick(in AgentContext context, Span<byte> state, float delta) => ActionStatus.Failed;

        public void Abort(in AgentContext context, Span<byte> state) { }
    }

    sealed class SucceedsEveryFewTicks(int period) : IAgentAction {
        public void Start(in AgentContext context, Span<byte> state) { }

        public ActionStatus Tick(in AgentContext context, Span<byte> state, float delta) {
            ref var ticks = ref MemoryMarshal.AsRef<int>(state);

            if (++ticks < period) {
                return ActionStatus.Running;
            }

            ticks = 0;

            return ActionStatus.Succeeded;
        }

        public void Abort(in AgentContext context, Span<byte> state) { }
    }
}

/// <summary>Breakpoints: an agent stopped at a node, with everything about it left where it was.</summary>
public class AiBreakpointTests {
    [Fact]
    public void AnAgentStopsAtTheNodeAndStaysThereUntilItIsResumed() {
        using var harness = TreeHarness.For(
            BehaviorTree.Sequence("root", BehaviorTree.Task("first", "running")),
            BlackboardLayout.Empty,
            actions => TreeHarness.Probes(actions)
        );

        var stops = new AiBreakpoints();

        // Node 1 is the task: pre-order, so the root is 0.
        stops.Add(harness.Tree.Template.Name, 1);
        harness.Tree.Breakpoints = stops;
        harness.Step();

        Assert.True(harness.Tree.Halted);
        Assert.Equal("first", harness.Active);
        Assert.Equal(1, stops.Hits);
        Assert.Equal(1, stops.LastHit.Node);
        Assert.Equal(harness.Entity, stops.LastHit.Entity);

        // ⚠ A stopped agent does *nothing*: the state somebody stopped to look at has to still be
        // there when they look, so not one further tick reaches the task.
        var ticks = harness.Probe(1).Ticks;

        harness.Steps(5);

        Assert.Equal(ticks, harness.Probe(1).Ticks);

        harness.Tree.Resume();
        harness.Steps(3);

        Assert.False(harness.Tree.Halted);
        Assert.True(harness.Probe(1).Ticks > ticks, "resuming did not let the agent go.");
    }

    /// <summary>
    ///     ⚠ The scope rule is the abort rule: a breakpoint on a composite catches anything inside it.
    ///     One containment test an author can already see shaded in the editor, rather than a second
    ///     one they have to remember apart.
    /// </summary>
    [Fact]
    public void ABreakpointOnACompositeCatchesTheTaskInsideIt() {
        using var harness = TreeHarness.For(
            BehaviorTree.Selector(
                "root",
                BehaviorTree.Sequence("branch", BehaviorTree.Task("leaf", "running"))
            ),
            BlackboardLayout.Empty,
            actions => TreeHarness.Probes(actions)
        );

        var stops = new AiBreakpoints();

        stops.Add(harness.Tree.Template.Name, 1);
        harness.Tree.Breakpoints = stops;
        harness.Step();

        Assert.True(harness.Tree.Halted);
        Assert.Equal(1, stops.LastHit.Breakpoint.Node);
        Assert.Equal(2, stops.LastHit.Node);
    }

    [Fact]
    public void ABreakpointSetOnNothingCostsNothingAndStopsNobody() {
        using var harness = TreeHarness.For(
            BehaviorTree.Sequence("root", BehaviorTree.Task("first", "running")),
            BlackboardLayout.Empty,
            actions => TreeHarness.Probes(actions)
        );

        harness.Tree.Breakpoints = new AiBreakpoints();
        harness.Steps(5);

        Assert.False(harness.Tree.Halted);
        Assert.True(harness.Probe(1).Ticks >= 5);
    }

    [Fact]
    public void ToggleSetsAndThenClears() {
        var stops = new AiBreakpoints();
        var tree = Symbol.Intern("guard");

        Assert.True(stops.Toggle(tree, 4));
        Assert.True(stops.Contains(tree, 4));
        Assert.False(stops.Toggle(tree, 4));
        Assert.Equal(0, stops.Count);
    }
}

/// <summary>The one shape all three planners fill, and what each of them puts in it.</summary>
public class AiSnapshotTests {
    [Fact]
    public void ATreeAgentReportsItsActivePathAndItsDecoratorsLastAnswer() {
        var registry = new AgentActionRegistry();
        var layout = new BlackboardLayoutBuilder().Add("alarmed", BlackboardValueType.Bool).Build();

        registry.Register("stand", new Standing(), 0);

        var system = new AiSystem(registry, layout);
        var asset = BehaviorTree.Asset(
            "guard",
            BehaviorTree.Sequence(
                "root",
                BehaviorTree.Task("stand-guard", "stand")
                    .With(BlackboardDecorator.Set(layout.Key("alarmed")))
            )
        );

        var index = system.Trees.Add(BehaviorTreeCompiler.Compile(asset, registry, layout));

        using var world = new World("snapshot-tree");
        var entity = world.Create(AiAgent.Thinking(index));

        system.Step(world, GameTime.Zero);
        system.BlackboardOf(in world.Read<AiAgent>(entity))!.SetBool(layout.Key("alarmed"), true);
        system.Step(world, GameTime.Zero);

        var snapshot = new AiAgentSnapshot();

        Assert.True(AiSnapshots.Take(system, world, entity, snapshot));
        Assert.Equal(AiPlanner.BehaviorTree, snapshot.Planner);
        Assert.Equal(Symbol.Intern("guard"), snapshot.Asset);

        var doing = snapshot.Section(AiDebugSection.Doing).ToList();

        Assert.Contains(doing, row => row.Name == "root");
        Assert.Contains(doing, row => row is { Name: "stand-guard", Active: true });
        Assert.Contains(snapshot.Section(AiDebugSection.Why), row => row.Value == "passes");

        // ⚠ Set keys only. An unset key and a key holding zero are different states, and the
        // commonest AI bug there is — a sensor that never ran — looks exactly like the second.
        Assert.Contains(snapshot.Section(AiDebugSection.Data), row => row is { Name: "alarmed", Value: "true" });
    }

    /// <summary>
    ///     ⚠ <b>A tree agent's headline action is the live leaf's, not <c>AiAgent.Action</c>'s.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>AiSystem.Advance</c> hands a behaviour-tree agent to
    ///         <c>BehaviorTreeInstance.Step</c> and returns before the <c>Action</c> field the other
    ///         two planners maintain — correctly, because the tree owns which task is running. But
    ///         <c>AiSnapshots.Take</c> filled <c>Snapshot.Action</c> from that field for every
    ///         planner, so the overlay's and the panel's "what is it doing" reported
    ///         <c>NameOf(0)</c> for every tree agent alive: whichever action happened to be
    ///         registered first, presented as a fact.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The decoy is the whole test.</b>
    ///         <see cref="ATreeAgentReportsItsActivePathAndItsDecoratorsLastAnswer" /> registers one
    ///         action, so index zero <i>is</i> the right answer there and the defect is invisible;
    ///         P7's overlay test reads its readout off a <i>utility</i> agent. Registering something
    ///         else first is what makes a wrong answer look wrong. Found by putting the stack in
    ///         <c>Samples/15-AiVillage</c>, where a guard that was visibly chasing an intruder
    ///         reported that it was waiting.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ATreeAgentReportsTheTaskItIsRunningAndNotWhateverWasRegisteredFirst() {
        var registry = new AgentActionRegistry();
        var layout = new BlackboardLayoutBuilder().Add("alarmed", BlackboardValueType.Bool).Build();

        // The decoy, registered first so that index zero is an answer nobody should ever get.
        registry.Register("idle-decoy", new Standing(), 0);
        registry.Register("stand", new Standing(), 0);

        var system = new AiSystem(registry, layout);
        var asset = BehaviorTree.Asset("guard", BehaviorTree.Task("stand-guard", "stand"));
        var index = system.Trees.Add(BehaviorTreeCompiler.Compile(asset, registry, layout));

        using var world = new World("snapshot-tree-action");
        var entity = world.Create(AiAgent.Thinking(index));

        system.Step(world, GameTime.Zero);

        var snapshot = new AiAgentSnapshot();

        Assert.True(AiSnapshots.Take(system, world, entity, snapshot));
        Assert.Equal(Symbol.Intern("stand"), snapshot.Action);
        Assert.NotEqual(Symbol.Intern("idle-decoy"), snapshot.Action);
    }

    [Fact]
    public void AUtilityAgentReportsEveryCandidateAndTheChosenOnesFactors() {
        var registry = new AgentActionRegistry();

        registry.Register("wander", new Standing(), 0);
        registry.Register("run", new Standing(), 0);

        var system = new AiSystem(registry, BlackboardLayout.Empty);

        system.Sets.Add(
            new UtilitySet(
                Symbol.Intern("villager"),
                Candidate("wander", 0, 0.2f),
                Candidate("run", 1, 0.9f)
            )
        );

        using var world = new World("snapshot-utility");
        var entity = world.Create(AiAgent.Scoring(0));

        system.Step(world, GameTime.Zero);

        var snapshot = new AiAgentSnapshot();

        Assert.True(AiSnapshots.Take(system, world, entity, snapshot));

        var doing = snapshot.Section(AiDebugSection.Doing).ToList();

        Assert.Equal(2, doing.Count);
        Assert.True(doing[1].Active, "the chosen candidate is not marked.");
        Assert.True(doing[1].Number > doing[0].Number);
        Assert.Contains("run", snapshot.Reason, StringComparison.Ordinal);
        Assert.NotEmpty(snapshot.Section(AiDebugSection.Why));
    }

    /// <summary>
    ///     ⚠ Taking a picture must not change what the agent does, or the bug moves the moment
    ///     somebody looks for it. A capture re-scores the set without advancing its decision clock.
    /// </summary>
    [Fact]
    public void TakingASnapshotDoesNotChangeWhatTheAgentDecides() {
        var registry = new AgentActionRegistry();

        registry.Register("wander", new Standing(), 0);
        registry.Register("run", new Standing(), 0);

        var system = new AiSystem(registry, BlackboardLayout.Empty);

        system.Sets.Add(
            new UtilitySet(Symbol.Intern("villager"), Candidate("wander", 0, 0.2f), Candidate("run", 1, 0.9f))
        );

        using var world = new World("snapshot-quiet");
        var entity = world.Create(AiAgent.Scoring(0));

        system.Step(world, GameTime.Zero);

        var decisions = system.ScoringOf(in world.Read<AiAgent>(entity))!.Decisions;
        var snapshot = new AiAgentSnapshot();

        for (var take = 0; take < 10; take++) {
            AiSnapshots.Take(system, world, entity, snapshot);
        }

        Assert.Equal(decisions, system.ScoringOf(in world.Read<AiAgent>(entity))!.Decisions);
    }

    static UtilityAction Candidate(string name, ushort action, float score) =>
        new(
            Symbol.Intern(name),
            action,
            new UtilityConsideration(
                Symbol.Intern("axis"),
                UtilityInputs.From((in AgentContext context) => score),
                ResponseCurve.Identity
            )
        );

    sealed class Standing : IAgentAction {
        public void Start(in AgentContext context, Span<byte> state) { }

        public ActionStatus Tick(in AgentContext context, Span<byte> state, float delta) => ActionStatus.Running;

        public void Abort(in AgentContext context, Span<byte> state) { }
    }
}

/// <summary>Doc 37 § D17's one exception: a debug channel, off by default, refusing anything odd.</summary>
public class AiDebugChannelTests {
    [Fact]
    public void AnAgentSurvivesTheRoundTripAsRowsRatherThanAsATree() {
        var (system, world, entity) = Standing();

        using (world) {
            var channel = new AiDebugChannel { Enabled = true };
            var request = new ArrayBufferWriter<byte>();
            var reply = new ArrayBufferWriter<byte>();

            AiDebugChannel.WriteRequest(request, entity);

            Assert.True(channel.TryHandle(request.WrittenSpan, system, world, reply));
            Assert.Equal(1, channel.Answered);

            var far = new AiAgentSnapshot();

            Assert.True(AiDebugChannel.TryReadAgent(reply.WrittenSpan, far));
            Assert.Equal(entity, far.Entity);
            Assert.Equal(AiPlanner.Utility, far.Planner);
            Assert.Equal(Symbol.Intern("villager"), far.Asset);
            Assert.Equal(2, far.CountOf(AiDebugSection.Doing));
            Assert.Contains(far.Section(AiDebugSection.Doing), row => row.Active);
        }
    }

    /// <summary>
    ///     ⚠ A build that does not carry this must not be distinguishable from one that does by how it
    ///     fails, so the switch is tested before the request is even parsed.
    /// </summary>
    [Fact]
    public void ABuildWithTheChannelOffRefusesBeforeItLooksAtTheRequest() {
        var (system, world, entity) = Standing();

        using (world) {
            var channel = new AiDebugChannel();
            var request = new ArrayBufferWriter<byte>();
            var reply = new ArrayBufferWriter<byte>();

            AiDebugChannel.WriteRequest(request, entity);

            Assert.False(channel.TryHandle(request.WrittenSpan, system, world, reply));
            Assert.Equal(1, channel.Refused);
            Assert.Equal(0, channel.Answered);
            Assert.Equal((byte)AiDebugMessage.NoAgent, reply.WrittenSpan[0]);
        }
    }

    [Fact]
    public void ATruncatedReplyIsRefusedRatherThanReadPast() {
        var (system, world, entity) = Standing();

        using (world) {
            var channel = new AiDebugChannel { Enabled = true };
            var request = new ArrayBufferWriter<byte>();
            var reply = new ArrayBufferWriter<byte>();

            AiDebugChannel.WriteRequest(request, entity);
            channel.TryHandle(request.WrittenSpan, system, world, reply);

            var whole = reply.WrittenSpan.ToArray();
            var far = new AiAgentSnapshot();

            // Every prefix of a well-formed message. Not one of them may be half-read.
            for (var cut = 1; cut < whole.Length; cut++) {
                Assert.False(AiDebugChannel.TryReadAgent(whole.AsSpan(0, cut), far), $"a {cut}-byte reply was read.");
                Assert.Equal(0, far.Count);
            }

            Assert.True(AiDebugChannel.TryReadAgent(whole, far));
        }
    }

    [Fact]
    public void AFarEndSpeakingAnotherVersionIsRefusedRatherThanHalfUnderstood() {
        var (system, world, entity) = Standing();

        using (world) {
            var channel = new AiDebugChannel { Enabled = true };
            var request = new ArrayBufferWriter<byte>();
            var reply = new ArrayBufferWriter<byte>();

            AiDebugChannel.WriteRequest(request, entity);

            var bytes = request.WrittenSpan.ToArray();

            bytes[1] = 0xFF;

            Assert.False(channel.TryHandle(bytes, system, world, reply));
        }
    }

    static (AiSystem System, World World, Entity Entity) Standing() {
        var registry = new AgentActionRegistry();

        registry.Register("wander", new Idle(), 0);
        registry.Register("run", new Idle(), 0);

        var system = new AiSystem(registry, BlackboardLayout.Empty);

        system.Sets.Add(
            new UtilitySet(
                Symbol.Intern("villager"),
                Candidate("wander", 0, 0.2f),
                Candidate("run", 1, 0.9f)
            )
        );

        var world = new World("channel");
        var entity = world.Create(AiAgent.Scoring(0));

        system.Step(world, GameTime.Zero);

        return (system, world, entity);
    }

    static UtilityAction Candidate(string name, ushort action, float score) =>
        new(
            Symbol.Intern(name),
            action,
            new UtilityConsideration(
                Symbol.Intern("axis"),
                UtilityInputs.From((in AgentContext context) => score),
                ResponseCurve.Identity
            )
        );

    sealed class Idle : IAgentAction {
        public void Start(in AgentContext context, Span<byte> state) { }

        public ActionStatus Tick(in AgentContext context, Span<byte> state, float delta) => ActionStatus.Running;

        public void Abort(in AgentContext context, Span<byte> state) { }
    }
}
