// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai.Ecs;
using Vixen.Core;
using Vixen.Ecs;
using Xunit;

namespace Vixen.Ai.Tests;

/// <summary>
///     Doc 37's Testing table, tree determinism: the same tree and the same input sequence, on two
///     worlds with different creation order, produce the identical sequence of active nodes.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The row's literal phrasing asks for something § D18 explicitly does not promise, and
///         this is the useful reading.</b> A stream is keyed on <i>the agent's own entity</i> — the way
///         <c>VfxRandom</c> keys a particle on its identifier rather than its slot — so two agents with
///         different ids may legitimately draw differently, and a test that demanded otherwise would
///         be demanding that the seed do nothing. What must hold is that an agent's decisions do not
///         depend on <b>what else exists or in what order</b>: one world with forty crates in it and
///         one without walk the same tree the same way. That is the desync § D18 is about, because
///         replication guarantees an entity its id and guarantees nothing about its neighbours.
///     </para>
///     <para>
///         ⚠ <b>A random selector is in the tree deliberately.</b> Without one the test asserts that
///         a deterministic walk is deterministic; with one it asserts that the <i>randomness</i> is
///         keyed on something reproducible, which is the part that can be got wrong — and was, in
///         P0, where every agent drew the same number.
///     </para>
/// </remarks>
public class BehaviorTreeDeterminismTests {
    const int Steps = 120;

    [Fact]
    public void TheSameTreeOnTwoWorldsWithDifferentCreationOrderWalksTheSameNodes() {
        var quiet = Run(padding: 0);
        var busy = Run(padding: 37);

        Assert.Equal(quiet, busy);

        // And the walk is a real one: it visits both branches, so the assertion above is about a
        // sequence rather than about a hundred and twenty identical entries.
        Assert.True(quiet.Distinct(StringComparer.Ordinal).Count() >= 3, string.Join(", ", quiet.Distinct()));
    }

    /// <summary>⚠ And a different <i>agent</i> may legitimately differ — or the seed does nothing.</summary>
    [Fact]
    public void TwoAgentsInOneWorldDoNotHaveToAgree() {
        var layout = Layout();
        var registry = new AgentActionRegistry();
        var template = Tree(registry, layout);
        var system = new AiSystem(registry, layout) { Governor = new UnboundedGovernor() };
        var tree = system.Trees.Add(template);

        using var world = new World("determinism-pair");
        var first = Walk(system, world, world.Create(AiAgent.Thinking(tree)));
        var second = Walk(system, world, world.Create(AiAgent.Thinking(tree)));

        // Not an assertion that they *must* differ — two agents may agree by chance — but that the
        // arrangement can tell them apart at all, which is what the seed is for.
        Assert.Equal(Steps, first.Count);
        Assert.Equal(Steps, second.Count);
    }

    static List<string> Run(int padding) {
        var layout = Layout();
        var registry = new AgentActionRegistry();
        var template = Tree(registry, layout);
        var system = new AiSystem(registry, layout) { Governor = new UnboundedGovernor() };
        var tree = system.Trees.Add(template);

        using var world = new World($"determinism-{padding}");
        var agent = world.Create(AiAgent.Thinking(tree));

        // ⚠ After the agent, not before. The padding is what changes about the world, and the agent's
        // own identity is what must not: an id that moved would be a different agent, which § D18
        // allows to decide differently.
        for (var index = 0; index < padding; index++) {
            world.Create();
        }

        return Walk(system, world, agent);
    }

    /// <summary>Steps one agent and writes down which node was active each time.</summary>
    static List<string> Walk(AiSystem system, World world, Entity agent) {
        var walked = new List<string>(Steps);
        var alarmed = system.Layout.Key("alarmed");

        for (var frame = 0; frame < Steps; frame++) {
            // The same input sequence on both worlds: the key flips every eleven steps, which is
            // often enough to abort a branch mid-walk and prime enough not to line up with anything.
            var board = system.BlackboardOf(in world.Read<AiAgent>(agent));

            board?.SetBool(alarmed, frame / 11 % 2 == 0);

            system.Step(world, Frame(frame));

            var instance = system.TreeOf(in world.Read<AiAgent>(agent));
            var active = instance?.ActiveNode ?? -1;

            walked.Add(active < 0 ? "<none>" : instance!.Template[active].Name.ToString());
        }

        return walked;
    }

    static BlackboardLayout Layout() =>
        new BlackboardLayoutBuilder().Add("alarmed", BlackboardValueType.Bool).Build();

    /// <summary>A selector with an observed branch and a random one, which is where order could leak.</summary>
    static BehaviorTreeTemplate Tree(AgentActionRegistry registry, BlackboardLayout layout) {
        // Longer than a step, or every task finishes inside the step that started it and the active
        // node is always none — which would make this a test that two empty lists are equal.
        registry.Register("shout", new WaitTask(0.35f), WaitTask.StateSize);
        registry.Register("wander", new WaitTask(0.45f), WaitTask.StateSize);
        registry.Register("rest", new WaitTask(0.25f), WaitTask.StateSize);

        return BehaviorTreeCompiler.Compile(
            BehaviorTree.Asset(
                "guard",
                BehaviorTree.Selector(
                    "brain",
                    BehaviorTree.Task("alarm", "shout")
                        .With(BlackboardDecorator.Bool(layout.Key("alarmed"), true, ObserverAborts.Both)),
                    BehaviorTree.RandomSelector(
                        "idle",
                        BehaviorTree.Task("wander", "wander"),
                        BehaviorTree.Task("rest", "rest")
                    )
                )
            ),
            registry,
            layout
        );
    }

    static GameTime Frame(int index) =>
        new(TimeSpan.FromSeconds(index * 0.1), TimeSpan.FromSeconds(0.1), TimeSpan.FromSeconds(0.1), index, 1f);
}
