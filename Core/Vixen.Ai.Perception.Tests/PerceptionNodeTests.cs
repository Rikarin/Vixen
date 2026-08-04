// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai.Ecs;
using Vixen.Ai.Perception.Ecs;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Xunit;

namespace Vixen.Ai.Perception.Tests;

/// <summary>
///     The three nodes doc 37 § Part 3 files under this assembly, authored as a <c>.vxbt</c> and
///     compiled by a resolver that was taught about them.
/// </summary>
/// <remarks>
///     ⚠ <b>This is the test that says the extension point works at all.</b> The node library lives in
///     <c>Vixen.Ai</c> so that a game loading a tree and an editor authoring one read the same
///     declarations — but these three types are not in <c>Vixen.Ai</c> and it cannot construct them.
///     Without <c>BehaviorTreeResolver.AddDecorator</c> and its two siblings the schema could describe
///     a node the compiler would refuse, which is a type the editor offers and the file rejects.
/// </remarks>
public class PerceptionNodeTests {
    [Fact]
    public void ATreeThatNamesThePerceptionNodesCompilesAndRuns() {
        var fleet = new Fleet();
        var enemy = fleet.Source(new(0f, 0f, -5f), team: 1);
        var content = Tree();
        var resolver = new BehaviorTreeResolver { Schema = new BehaviorNodeSchema() };

        PerceptionNodes.Register(resolver, fleet.System);

        var layout = content.BuildLayout(null);

        Assert.True(BehaviorTreeContentCompiler.TryCompile(content, resolver, out var diagnostics, out var template));
        Assert.Empty(diagnostics);

        var agents = new AiSystem(resolver.Actions, layout);
        var tree = agents.Trees.Add(template!);
        var listener = fleet.World.Create(
            AiPerception.Sensing(fleet.Config),
            Engine.Transforms.LocalTransform.Identity,
            AiAgent.Thinking(tree)
        );

        fleet.System.Agents = agents;
        agents.Step(fleet.World, Fleet.Frame(0));
        fleet.Step();
        agents.Step(fleet.World, Fleet.Frame(1));

        var blackboard = agents.BlackboardOf(fleet.World.Get<AiAgent>(listener))!;

        // NearestPerceived wrote the target, and the PerceivedTarget decorator let the branch under it
        // run — which is the whole loop: a sense, a key, a service and a condition.
        Assert.Equal(enemy, blackboard.GetEntity(Key(layout, "target")));
        Assert.Equal(ActionStatus.Running, fleet.World.Get<AiAgent>(listener).Status);
    }

    /// <summary>A noise the tree made, heard by somebody else.</summary>
    [Fact]
    public void MakeNoiseEmitsExactlyOneStimulusAndOtherAgentsHearIt() {
        var fleet = new Fleet(Fleet.Everything(SenseMask.Hearing));
        var noisy = fleet.World.Create(Engine.Transforms.LocalTransform.At(new(0f, 0f, -5f)));
        var listener = fleet.Listener(Vector3.Zero);
        var task = new MakeNoiseTask(fleet.System, 1f);
        var state = new byte[MakeNoiseTask.StateSize];
        var context = new AgentContext(
            fleet.World,
            noisy,
            new(BlackboardLayout.Empty),
            null,
            Fleet.Frame(0),
            0
        );

        Assert.Equal(ActionStatus.Succeeded, task.Tick(in context, state, 0.1f));

        // ⚠ Ticked again on the same span. A task kept running for a frame or two would otherwise make
        // one noise a frame — a footstep that reads as a stampede.
        Assert.Equal(ActionStatus.Succeeded, task.Tick(in context, state, 0.1f));

        fleet.Step();

        Assert.True(fleet.Perceived(listener).TryGet(noisy, out var heard));
        Assert.Equal(AiSense.Hearing, heard.Sense);
        Assert.Equal(1, fleet.Perceived(listener).Count);
    }

    /// <summary>A type in the schema with no factory behind it is a diagnostic, not a crash.</summary>
    [Fact]
    public void ANodeTheResolverWasNotTaughtIsReportedRatherThanBuilt() {
        var content = Tree();
        var resolver = new BehaviorTreeResolver { Schema = PerceptionNodes.Register(new BehaviorNodeSchema()) };

        BehaviorTreeContentCompiler.TryCompile(content, resolver, out var diagnostics, out _);

        Assert.Contains(diagnostics, problem => problem.Message.Contains("no factory registered", StringComparison.Ordinal));
    }

    /// <summary>Registering into a schema twice is what a game does when two systems both do it.</summary>
    [Fact]
    public void RegisteringTwiceIsHarmless() {
        var schema = PerceptionNodes.Register(new BehaviorNodeSchema());
        var before = schema.Types.Count;

        PerceptionNodes.Register(schema);

        Assert.Equal(before, schema.Types.Count);
        Assert.True(schema.TryGet("PerceivedTarget", out _));
        Assert.True(schema.TryGet("NearestPerceived", out _));
        Assert.True(schema.TryGet("MakeNoise", out _));
    }

    static BehaviorTreeContent Tree() {
        var root = new BehaviorNodeContent { Name = "Brain", Type = "Selector" };
        var chase = new BehaviorNodeContent { Name = "Chase", Type = "Wait" };
        var perceived = new BehaviorAttachmentContent { Type = "PerceivedTarget" };
        var nearest = new BehaviorAttachmentContent { Type = "NearestPerceived", Interval = 0.1f };

        chase.Fields["Seconds"] = "5";
        perceived.Fields["Senses"] = nameof(AiSense.Sight);
        perceived.Fields["Key"] = "target";
        perceived.Fields["Aborts"] = nameof(ObserverAborts.Both);
        nearest.Fields["Senses"] = nameof(AiSense.Sight);
        nearest.Fields["Key"] = "target";

        chase.Decorators.Add(perceived);
        root.Services.Add(nearest);
        root.Children.Add(chase);
        root.Children.Add(new() { Name = "Idle", Type = "Wait", Fields = { ["Seconds"] = "1" } });

        return new BehaviorTreeContent {
            Name = "guard",
            Root = root,
            Keys = { new() { Name = "target", Type = BlackboardValueType.Entity } }
        };
    }

    static BlackboardKey Key(BlackboardLayout layout, string name) {
        Assert.True(layout.TryGetKey(Symbol.Intern(name), out var key));

        return key;
    }
}
