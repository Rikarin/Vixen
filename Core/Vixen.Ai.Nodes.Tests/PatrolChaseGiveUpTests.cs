// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai.Ecs;
using Vixen.Ai.Nodes.Ecs;
using Vixen.Ai.Perception;
using Vixen.Ai.Perception.Ecs;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Navigation.Ecs;
using Xunit;

namespace Vixen.Ai.Nodes.Tests;

/// <summary>
///     P4's exit criterion: an agent patrols a baked navmesh, notices the player, chases and gives up
///     — with no window, asserting positions.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every part of it is authored rather than driven.</b> The tree is a <c>.vxbt</c>
///         compiled through <c>BehaviorTreeContentCompiler</c>, the route is a component on the guard,
///         the sight is a <c>PerceptionConfig</c>, and the only thing the test does each frame is move
///         the player and step the three systems. Nothing here calls a task directly, so a failure is
///         a failure of the whole chain — sense, key, abort, node, crowd — which is the chain P0 to P4
///         exist to make work together.
///     </para>
///     <para>
///         The three phases are three assertions about <i>where the guard is</i>: it walks its route,
///         it closes on the player, and it goes back to the route. "Gives up" is the age of the
///         stimulus crossing half a second, which is one <c>Blackboard</c> decorator observing one
///         float key — no timer, no state, and nothing in the tree that knows what perception is.
///     </para>
/// </remarks>
public class PatrolChaseGiveUpTests {
    static readonly Vector3[] Route = [
        new(5f, 0f, 5f),
        new(32f, 0f, 5f),
        new(32f, 0f, 32f)
    ];

    [Fact]
    public void AGuardPatrolsNoticesThePlayerChasesAndGivesUp() {
        var level = new Level();
        var content = Tree();
        var layout = content.BuildLayout(null);
        var perception = Sight(layout);
        var resolver = new BehaviorTreeResolver { Schema = new BehaviorNodeSchema() };

        WorldNodes.Register(resolver, level.Query);

        Assert.True(BehaviorTreeContentCompiler.TryCompile(content, resolver, out var diagnostics, out var template));
        Assert.Empty(diagnostics);

        var agents = new AiSystem(resolver.Actions, layout);
        var tree = agents.Trees.Add(template!);

        level.Agents = agents;
        perception.Agents = agents;

        var guard = Guard(level, tree, perception);
        var player = level.World.Create(
            LocalTransform.At(new(39f, 0f, 39f)),
            AiStimuliSource.Perceivable(team: 2, senses: SenseMask.Sight)
        );

        // ── Patrolling ────────────────────────────────────────────────────────────────────────
        Run(level, perception, 240);

        var patrolled = level.Where(guard);

        Assert.True(
            patrolled.X > 12f,
            $"three seconds in, the guard was at {patrolled} rather than well along the first leg."
        );

        Assert.False(
            Blackboard(level, agents, guard).IsSet(Key(layout, "target")),
            "the guard noticed a player nearly forty metres away."
        );

        // ── Noticing, and chasing ─────────────────────────────────────────────────────────────
        var ambush = patrolled + new Vector3(0f, 0f, 6f);

        level.Transform(player).Position = ambush;
        Run(level, perception, 30);

        Assert.Equal(player, Blackboard(level, agents, guard).GetEntity(Key(layout, "target")));

        var closing = AgentTarget.FlatDistance(level.Where(guard), ambush);

        Run(level, perception, 150);

        var closed = AgentTarget.FlatDistance(level.Where(guard), ambush);

        Assert.True(closed < closing, $"the guard was {closing:0.0} m away and is now {closed:0.0} m away.");
        Assert.True(closed <= 2.5f, $"the guard stopped {closed:0.0} m short of the player.");

        // ── Giving up ─────────────────────────────────────────────────────────────────────────
        level.Transform(player).Position = new(39f, 0f, 39f);
        Run(level, perception, 240);

        var abandoned = level.Where(guard);

        Assert.True(
            AgentTarget.FlatDistance(abandoned, ambush) > 3f,
            $"the guard is still standing at {abandoned}, where it last saw the player."
        );

        // Back on the route: the destination is one of its own points again, and the guard is walking
        // to it rather than to where the player was.
        var destination = level.World.Get<NavigationDestination>(guard).Value;

        Assert.Contains(destination, Route);
        Assert.True(
            AgentTarget.FlatDistance(abandoned, destination) < AgentTarget.FlatDistance(patrolled, destination) + 30f,
            $"the guard at {abandoned} is not on its way to {destination}."
        );
    }

    /// <summary>Sense, then think, then walk — the order <c>PerceptionSystem</c> declares.</summary>
    static void Run(Level level, PerceptionSystem perception, int frames) =>
        level.Step(frames, frame => perception.Step(level.World, Level.Frame(frame)));

    static Entity Guard(Level level, int tree, PerceptionSystem perception) {
        var guard = level.Walker(Route[0]);

        level.World.Add(guard, AiAgent.Thinking(tree));
        level.World.Add(guard, AiPerception.Sensing(perception.Configs.Count - 1, team: 1));
        level.World.Add(guard, PatrolRoute.Of(PatrolMode.Loop, Route));

        return guard;
    }

    /// <summary>Sight only, at eight metres, with nothing in the way and no jitter.</summary>
    static PerceptionSystem Sight(BlackboardLayout layout) {
        var system = new PerceptionSystem();

        system.Configs.Add(
            new PerceptionConfig {
                Senses = SenseMask.Sight,
                Sight = new() { Radius = 8f, LoseSightRadius = 10f, ConeDegrees = 360f, Occlusion = false },
                RandomDeviation = 0f,
                Filter = PerceptionFilters.Hostiles,
                Binding = new TargetLocationAgeBinding(
                    SenseMask.Sight,
                    Key(layout, "target"),
                    Key(layout, "seen"),
                    Key(layout, "age")
                )
            }
        );

        return system;
    }

    /// <summary>Chase what was seen recently; otherwise patrol.</summary>
    static BehaviorTreeContent Tree() {
        var root = new BehaviorNodeContent { Name = "Brain", Type = "Selector" };
        var chase = new BehaviorNodeContent { Name = "Chase", Type = "MoveTo" };
        var recent = new BehaviorAttachmentContent { Type = "Blackboard" };

        chase.Fields["Key"] = "target";
        chase.Fields["Acceptance"] = "2";
        chase.Fields["Repath"] = "1";

        // ⚠ The whole of "gives up", and it is one decorator over one float. No timer, no second
        // branch holding a remembered position, and nothing in the tree that knows what a sense is.
        recent.Fields["Key"] = "age";
        recent.Fields["Test"] = nameof(BlackboardTest.Less);
        recent.Fields["Value"] = "0.5";
        recent.Fields["Aborts"] = nameof(ObserverAborts.Both);

        chase.Decorators.Add(recent);
        root.Children.Add(chase);
        root.Children.Add(new() { Name = "Walk", Type = "Patrol", Fields = { ["Acceptance"] = "1.5" } });

        return new BehaviorTreeContent {
            Name = "guard",
            Root = root,
            Keys = {
                new() { Name = "target", Type = BlackboardValueType.Entity },
                new() { Name = "seen", Type = BlackboardValueType.Vector3 },
                new() { Name = "age", Type = BlackboardValueType.Float }
            }
        };
    }

    static Blackboard Blackboard(Level level, AiSystem agents, Entity guard) =>
        agents.BlackboardOf(level.World.Get<AiAgent>(guard))!;

    static BlackboardKey Key(BlackboardLayout layout, string name) {
        Assert.True(layout.TryGetKey(Symbol.Intern(name), out var key));

        return key;
    }
}
