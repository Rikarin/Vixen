// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai;
using Vixen.Ai.Diagnostics;
using Vixen.Ai.Ecs;
using Vixen.Core;
using Vixen.Ecs;
using Vixen.Editor.Ai;
using Vixen.Editor.AssetEditors.Ai;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>doc 37 § P7's editor panels, over the same records the runtime overlay draws.</summary>
public class AgentDebugModelTests {
    [Fact]
    public void ThePanelFindsTheAgentsAndShowsWhatTheSelectedOneIsDoing() {
        using var fixture = new AgentFixture();

        fixture.Steps(3);

        var model = new AgentDebugModel();

        Assert.True(model.Refresh(fixture.System, fixture.World));
        Assert.Single(model.Agents);
        Assert.Equal(fixture.Entity, model.Selected);
        Assert.Equal(AgentDebugOrigin.Local, model.Origin);
        Assert.Equal(Symbol.Intern("villager"), model.Snapshot.Asset);
        Assert.Equal(2, model.Section(AiDebugSection.Doing).Count());
        Assert.Contains(model.Section(AiDebugSection.Doing), row => row.Active);
    }

    [Fact]
    public void TheLogIsTheAgentsOwnAndTheFindingsAreDrawnFromIt() {
        using var fixture = new AgentFixture(flapping: true);

        fixture.Steps(40);

        var model = new AgentDebugModel();

        model.Refresh(fixture.System, fixture.World);

        Assert.NotEmpty(model.Log);
        Assert.All(model.Log, record => Assert.Equal(fixture.Entity, record.Entity));
        Assert.Contains(model.SelectedFindings(), finding => finding.Symptom == AiSymptom.Flapping);
    }

    /// <summary>
    ///     ⚠ The panel owns the breakpoint set and installs it on the system. A panel whose toggles
    ///     silently did nothing would be the worst kind of debugger.
    /// </summary>
    [Fact]
    public void ABreakpointSetInThePanelStopsTheAgentTheSystemIsRunning() {
        using var fixture = new AgentFixture(tree: true);

        var model = new AgentDebugModel();

        model.Refresh(fixture.System, fixture.World);
        model.ToggleBreakpoint(Symbol.Intern("guard"), 1);

        fixture.Steps(3);
        model.Refresh(fixture.System, fixture.World);

        Assert.True(model.Halted);
        Assert.Equal(1, model.Breakpoints.LastHit.Node);
        Assert.True(model.Resume(fixture.System, fixture.World));
        Assert.False(model.Halted);
    }

    /// <summary>
    ///     ⚠ A picture that arrived over a wire and one taken in this process are the same picture,
    ///     which is what makes debugging a dedicated server the same tool rather than a second one.
    /// </summary>
    [Fact]
    public void ARemoteSnapshotShowsExactlyAsALocalOneDoes() {
        using var fixture = new AgentFixture();

        fixture.Steps(3);

        var local = new AiAgentSnapshot();

        Assert.True(AiSnapshots.Take(fixture.System, fixture.World, fixture.Entity, local));

        var model = new AgentDebugModel();

        model.Show(local);

        Assert.Equal(AgentDebugOrigin.Remote, model.Origin);
        Assert.Equal(local.Count, model.Snapshot.Count);
        Assert.Equal(local.Reason, model.Snapshot.Reason);
        Assert.Equal(fixture.Entity, model.Selected);
    }
}

/// <summary>The panel itself: five lists, and no document behind any of them.</summary>
public class AgentDebuggerViewTests {
    [Fact]
    public void EveryListIsBuiltFromTheModel() {
        using var fixture = new AgentFixture(flapping: true);

        fixture.Steps(40);

        var model = new AgentDebugModel();

        model.Refresh(fixture.System, fixture.World);

        using var ui = UiTest.Create();
        var view = ui.Document.Root.Add<AgentDebuggerView>();

        view.Show(model);

        // ⚠ A frame, for `QueryViewTests`' reason: eight lists that were `Empty`-then-refill are
        // eight signals now, and a signal write only queues.
        ui.Frame();

        Assert.Single(view.Agents.Children);
        Assert.NotEmpty(view.Header.Children);
        Assert.NotEmpty(view.Doing.Children);
        Assert.NotEmpty(view.Log.Children);
        Assert.NotEmpty(view.Findings.Children);
    }

    [Fact]
    public void ShowingAgainRebuildsRatherThanAppends() {
        using var fixture = new AgentFixture();

        fixture.Steps(3);

        var model = new AgentDebugModel();

        model.Refresh(fixture.System, fixture.World);

        using var ui = UiTest.Create();
        var view = ui.Document.Root.Add<AgentDebuggerView>();

        view.Show(model);
        ui.Frame();

        var rows = view.Doing.Children.Count;

        Assert.NotEqual(0, rows);

        view.Refresh();
        view.Refresh();
        ui.Frame();

        Assert.Equal(rows, view.Doing.Children.Count);
    }
}

/// <summary>A world with one agent, stepped by an <see cref="AiSystem" />, and nothing drawn.</summary>
sealed class AgentFixture : IDisposable {
    readonly AgentActionRegistry registry = new();

    public AgentFixture(bool flapping = false, bool tree = false) {
        registry.Register("wander", new Idle(), 0);
        registry.Register("run", new Idle(), 0);

        System = new(registry, Layout);
        System.Debug.Enabled = true;
        World = new("agent-debugger");

        if (tree) {
            var asset = BehaviorTree.Asset("guard", BehaviorTree.Sequence("root", BehaviorTree.Task("stand", "wander")));

            System.Trees.Add(BehaviorTreeCompiler.Compile(asset, registry, Layout));
            Entity = World.Create(AiAgent.Thinking(0));

            return;
        }

        System.Sets.Add(
            new UtilitySet(
                Symbol.Intern("villager"),
                Candidate("wander", 0, () => 0.5f),
                Candidate("run", 1, () => Danger)
            ) {
                CommitmentBonus = flapping ? 0f : 0.15f,
                DecisionInterval = flapping ? 0f : 0.2f
            }
        );

        Flapping = flapping;
        Entity = World.Create(AiAgent.Scoring(0));
    }

    public static BlackboardLayout Layout { get; } =
        new BlackboardLayoutBuilder().Add("alarm", BlackboardValueType.Float).Build();

    public AiSystem System { get; }

    public World World { get; }

    public Entity Entity { get; }

    public bool Flapping { get; }

    public float Danger { get; private set; } = 0.9f;

    public void Steps(int count) {
        for (var frame = 0; frame < count; frame++) {
            if (Flapping) {
                Danger = frame % 2 == 0 ? 0.9f : 0.1f;
            }

            System.Step(
                World,
                new(TimeSpan.FromSeconds(frame * 0.1), TimeSpan.FromSeconds(0.1), TimeSpan.FromSeconds(0.1), frame, 1f)
            );
        }
    }

    public void Dispose() => World.Dispose();

    static UtilityAction Candidate(string name, ushort action, Func<float> score) =>
        new(
            Symbol.Intern(name),
            action,
            new UtilityConsideration(
                Symbol.Intern("axis"),
                UtilityInputs.From((in AgentContext context) => score()),
                ResponseCurve.Identity
            )
        );

    sealed class Idle : IAgentAction {
        public void Start(in AgentContext context, Span<byte> state) { }

        public ActionStatus Tick(in AgentContext context, Span<byte> state, float delta) => ActionStatus.Running;

        public void Abort(in AgentContext context, Span<byte> state) { }
    }
}
