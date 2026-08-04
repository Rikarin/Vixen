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

/// <summary>
///     doc 37 § Part 5's <i>"Live, in play mode"</i> — the half of each editor that follows a running
///     agent rather than an authored file.
/// </summary>
/// <remarks>
///     ⚠ <b>Every one of these was claimed and unbuilt until the plan was read against the code.</b>
///     The agent debugger listed an active path; the canvas never tinted anything, the utility bars
///     were always the typed readings, and the GOAP viewer drew conditions with no verdict. What they
///     have in common is that each looked finished from the panel that <i>was</i> built.
/// </remarks>
public class LiveTreeCanvasTests {
    [Fact]
    public void TheCanvasTintsTheActivePathAndWhatEachNodeLastReturned() {
        using var fixture = new EditorFixture();
        using var live = new LiveAgent();

        var path = fixture.Write("Assets/Guard.vxbt", LiveAgent.Yaml);
        var document = new BehaviorTreeDocument(fixture.Project, AssetId.Empty, path);

        using var ui = UiTest.Create();
        var view = ui.Document.Root.Add<BehaviorTreeView>();

        view.Show(document);
        live.Steps(20);

        var model = new AgentDebugModel();

        model.Refresh(live.System, live.World);
        view.Follow(model);

        // Stepped again *after* tracing was turned on, so a node has finished since.
        live.Steps(20);
        model.Refresh(live.System, live.World);

        Assert.True(view.RefreshLive() > 0, "nothing on the canvas was tinted.");

        var accents = view.Canvas.Graph.Nodes.Select(node => node.Accent).ToList();

        Assert.Contains("active", accents);
        Assert.Contains("path", accents);

        // ⚠ And the last result is a separate fact from the live path: the first child failed, which
        // is *why* the second is running, and a picture that only lit the live branch would hide it.
        Assert.Contains(accents, accent => accent is "failed" or "succeeded");
    }

    [Fact]
    public void FollowingNothingClearsTheTinting() {
        using var fixture = new EditorFixture();
        using var live = new LiveAgent();

        var path = fixture.Write("Assets/Guard.vxbt", LiveAgent.Yaml);
        var document = new BehaviorTreeDocument(fixture.Project, AssetId.Empty, path);

        using var ui = UiTest.Create();
        var view = ui.Document.Root.Add<BehaviorTreeView>();

        view.Show(document);

        var model = new AgentDebugModel();

        model.Refresh(live.System, live.World);
        live.Steps(20);
        model.Refresh(live.System, live.World);
        view.Follow(model);
        view.Follow(null);

        Assert.All(view.Canvas.Graph.Nodes, node => Assert.NotEqual("active", node.Accent));
    }

    /// <summary>⚠ Tracing is off until a panel asks, because it is a per-agent cost.</summary>
    [Fact]
    public void TracingIsOffUntilThePanelAsksForIt() {
        using var live = new LiveAgent();

        live.Steps(10);

        var instance = live.System.TreeOf(in live.World.Read<AiAgent>(live.Agent))!;

        Assert.False(instance.Trace);
        Assert.Null(instance.LastResultOf(0));

        var model = new AgentDebugModel();

        model.Refresh(live.System, live.World);

        Assert.True(instance.Trace);
    }
}

/// <summary>The utility table, following an agent instead of the readings an author typed.</summary>
public class LiveUtilityTests {
    [Fact]
    public void TheBarsAreTheAgentsScoresWhenOneIsBeingFollowed() {
        using var fixture = new EditorFixture();
        using var live = new LiveAgent(utility: true);

        var path = fixture.Write("Assets/Mood.vxutility", LiveAgent.SetYaml);
        var document = new UtilitySetDocument(fixture.Project, AssetId.Empty, path);

        using var ui = UiTest.Create();
        var view = ui.Document.Root.Add<UtilitySetView>();

        view.Show(document);

        // Authoring: nothing has been typed, so every input reads zero and every bar is empty.
        Assert.Null(view.LiveScore("Flee"));

        live.Steps(20);

        var model = new AgentDebugModel();

        model.Refresh(live.System, live.World);
        view.Follow(model);

        // ⚠ The agent's own score, not the panel's arithmetic over typed numbers — which is what
        // "the bars are live for the selected agent" has to mean to be worth anything.
        var score = view.LiveScore("Flee");

        Assert.NotNull(score);
        Assert.True(score > 0.5f, $"the agent scored {score}.");
    }
}

/// <summary>The GOAP viewer, showing the live search rather than the authored picture.</summary>
public class LiveGoapTests {
    [Fact]
    public void ConditionsAreDrawnWithoutAVerdictUntilSomethingIsRunning() {
        var projection = new GoapGraphProjection();
        var domain = LiveAgent.Domain(new());

        projection.Project(domain);

        // ⚠ Three states, not two. "Nobody is running this domain" drawn as "false" would tell an
        // author every condition was failing when in fact nothing had asked.
        Assert.Empty(projection.World);
        Assert.All(
            projection.Graph.Nodes.SelectMany(node => node.Attachments),
            attachment => Assert.True(attachment.Detail is "" or null)
        );
    }

    [Fact]
    public void ALiveWorldGivesEveryConditionAVerdictAndTheSearchItsRejections() {
        // Carrying a pear already, so the plan is one step and picking one up is *not* in it — which
        // is what leaves an action for the "considered and rejected" accent to land on.
        var pantry = new LiveAgent.Pantry { OnGround = 1, Carried = 1, Hunger = 80 };
        var domain = LiveAgent.Domain(pantry);
        var planner = new GoapPlanner(domain);
        var considered = new List<GoapConsidered> { new(0, GoapRejection.ConditionsUnmet) };
        var plan = new GoapPlan();

        using var world = new World("goap-live");
        var context = new AgentContext(world, new(3, 1, world.Id), new(BlackboardLayout.Empty), null, GameTime.Zero, 0);

        planner.Resolve(in context, plan);

        Span<int> keys = stackalloc int[8];

        domain.Keys.Project(in context, keys);

        var projection = new GoapGraphProjection();

        projection.Project(domain, plan, keys[..domain.Keys.Count], considered);

        var details = projection.Graph.Nodes.SelectMany(node => node.Attachments).Select(row => row.Detail).ToList();

        Assert.Contains("holds", details);
        Assert.Contains("unmet", details);
        Assert.Equal(3, projection.World.Count);

        // The action the search looked at and turned down is marked as such rather than left blank.
        Assert.Contains(projection.Graph.Nodes, node => node.Accent == "considered");
    }

    /// <summary>⚠ The trace is off unless a tool hands the planner a list.</summary>
    [Fact]
    public void ASearchWritesNothingDownUnlessSomebodyAsked() {
        var domain = LiveAgent.Domain(new() { OnGround = 1, Hunger = 80 });
        var planner = new GoapPlanner(domain);
        var plan = new GoapPlan();

        using var world = new World("goap-untraced");
        var context = new AgentContext(world, new(4, 1, world.Id), new(BlackboardLayout.Empty), null, GameTime.Zero, 0);

        Assert.Null(planner.Traced);

        planner.Resolve(in context, plan);
        planner.Traced = [];
        planner.Resolve(in context, plan);

        Assert.NotEmpty(planner.Traced);
    }
}

/// <summary>The agent inspector's open-the-asset button.</summary>
public class OpenAssetTests {
    [Fact]
    public void ThePanelSaysWhatToOpenAndWhereRatherThanOpeningIt() {
        using var live = new LiveAgent();
        using var ui = UiTest.Create();

        var model = new AgentDebugModel();

        live.Steps(20);
        model.Refresh(live.System, live.World);

        var view = ui.Document.Root.Add<AgentDebuggerView>();
        var opened = (Asset: Symbol.None, Node: -1);

        view.Show(model);
        view.Opening += (asset, node) => opened = (asset, node);

        Assert.True(view.OpenAsset());
        Assert.Equal(Symbol.Intern("guard"), opened.Asset);

        // ⚠ The *live* node, which is the whole reason the button is on this panel.
        Assert.True(opened.Node >= 0);
        Assert.Equal(model.ActivePath[^1], opened.Node);
    }

    [Fact]
    public void ThereIsNothingToOpenWhenNothingIsSelected() {
        using var ui = UiTest.Create();
        var view = ui.Document.Root.Add<AgentDebuggerView>();

        view.Show(new AgentDebugModel());

        Assert.False(view.OpenAsset());
    }
}
