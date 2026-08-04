// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Ai.Nodes.Tests;

/// <summary>A component nothing else in the engine defines, so a generator can look for it.</summary>
[Component]
struct CoverSpot {
    public float Quality;
}

/// <summary>The half of P8 that needs the world: the entity generator and the navmesh test.</summary>
public class WorldQueryTests {
    [Fact]
    public void TheEntityGeneratorMakesAPointForEveryOneCarryingTheComponent() {
        var level = new Level();

        level.World.Create(LocalTransform.At(new(2f, 0f, 0f)), new CoverSpot { Quality = 1f });
        level.World.Create(LocalTransform.At(new(30f, 0f, 0f)), new CoverSpot { Quality = 0.2f });
        level.World.Create(LocalTransform.At(new(4f, 0f, 0f)));

        var context = Context(level, level.World.Create(LocalTransform.At(Vector3.Zero)));
        var points = new List<QueryPoint>();

        Vixen.Ai.Nodes.WorldQueryTests.Entities<CoverSpot>()
            .Generate(in context, new(Vector3.Zero, Vector3.Zero), points);

        Assert.Equal(2, points.Count);

        // ⚠ The entity travels with the position, which is what makes "shoot at the best target" the
        // same machine as "stand in the best spot".
        Assert.All(points, point => Assert.False(point.Entity.IsNull));
    }

    [Fact]
    public void ARadiusOnTheEntityGeneratorKeepsTheNearOnes() {
        var level = new Level();

        level.World.Create(LocalTransform.At(new(2f, 0f, 0f)), new CoverSpot());
        level.World.Create(LocalTransform.At(new(30f, 0f, 0f)), new CoverSpot());

        var context = Context(level, level.World.Create(LocalTransform.At(Vector3.Zero)));
        var points = new List<QueryPoint>();

        Vixen.Ai.Nodes.WorldQueryTests.Entities<CoverSpot>(10f)
            .Generate(in context, new(Vector3.Zero, Vector3.Zero), points);

        Assert.Single(points);
        Assert.Equal(new Vector3(2f, 0f, 0f), points[0].Position);
    }

    /// <summary>
    ///     ⚠ The cheapest world test there is, and the one that belongs at the top of most lists: a
    ///     grid around an agent puts most of its points off the mesh, and rejecting those before
    ///     anything traces is the difference between a query that is affordable and one that is not.
    /// </summary>
    [Fact]
    public void TheNavMeshTestRejectsPointsOffTheFloor() {
        var level = new Level(10f);
        var context = Context(level, level.World.Create(LocalTransform.At(Vector3.Zero)));

        var query = new EnvironmentQuery(
            Symbol.Intern("on-the-floor"),
            [QueryGenerators.Grid(30f, 5f)],
            new QueryTest(Vixen.Ai.Nodes.WorldQueryTests.OnNavMesh(level.Query)) {
                Purpose = QueryTestPurpose.Filter,
                Ceiling = 0.5f
            },
            new QueryTest(QueryTests.Distance()) { Maximum = 40f }
        );

        var results = new QueryResults();

        Assert.True(query.Run(in context, new(Vector3.Zero, Vector3.Zero), results));
        Assert.True(results.Surviving > 0, "nothing at all was on the mesh.");
        Assert.True(
            results.Surviving < results.Generated,
            $"every one of {results.Generated} points was on a ten-metre floor."
        );

        foreach (var point in results.Points) {
            if (!point.Filtered) {
                Assert.InRange(point.Position.X, -11f, 11f);
            }
        }
    }

    static AgentContext Context(Level level, Entity entity) =>
        new(level.World, entity, new(BlackboardLayout.Empty), null, GameTime.Zero, 0);
}

/// <summary>The two nodes: run it now, or keep a key on the answer.</summary>
public class RunQueryNodeTests {
    [Fact]
    public void TheTaskWritesTheBestPointAndSucceeds() {
        var level = new Level();
        var layout = new BlackboardLayoutBuilder()
            .Add("target", BlackboardValueType.Vector3)
            .Add("spot", BlackboardValueType.Vector3)
            .Build();

        var board = new Blackboard(layout);
        var entity = level.World.Create(LocalTransform.At(Vector3.Zero));
        var context = new AgentContext(level.World, entity, board, null, GameTime.Zero, 0);

        board.SetVector3(layout.Key("target"), new(0f, 0f, 20f));

        var task = new RunQueryTask(
            new(Nearest(), layout.Key("target"), layout.Key("spot"))
        );

        var state = new byte[RunQueryTask.StateSize];

        task.Start(in context, state);

        Assert.Equal(ActionStatus.Succeeded, task.Tick(in context, state, 0f));
        Assert.True(board.IsSet(layout.Key("spot")));

        // The query prefers points near the target, and the ring nearest it is at z = +5.
        Assert.True(board.GetVector3(layout.Key("spot")).Z > 0f, board.GetVector3(layout.Key("spot")).ToString());
    }

    /// <summary>
    ///     ⚠ Failing rather than staying <c>Running</c> is what lets a selector fall through to the
    ///     branch that does not need an answer — take cover, or if there is none, run.
    /// </summary>
    [Fact]
    public void TheTaskFailsWhenNothingSurvived() {
        var level = new Level();
        var entity = level.World.Create(LocalTransform.At(Vector3.Zero));
        var context = new AgentContext(level.World, entity, new(BlackboardLayout.Empty), null, GameTime.Zero, 0);

        var query = new EnvironmentQuery(
            Symbol.Intern("impossible"),
            [QueryGenerators.Circle(5f, 8)],
            new QueryTest(QueryTests.Distance()) { Purpose = QueryTestPurpose.Filter, Ceiling = 1f }
        );

        var task = new RunQueryTask(new(query));
        var state = new byte[RunQueryTask.StateSize];

        task.Start(in context, state);

        Assert.Equal(ActionStatus.Failed, task.Tick(in context, state, 0f));
    }

    /// <summary>
    ///     ⚠ A service that finds nothing clears the key rather than leaving the last answer there. A
    ///     stale destination is the bug that walks an agent confidently to a spot that stopped being
    ///     cover two seconds ago, and it is invisible because the key still looks reasonable.
    /// </summary>
    [Fact]
    public void TheServiceClearsTheKeyWhenTheQueryFindsNothing() {
        var level = new Level();
        var layout = new BlackboardLayoutBuilder().Add("spot", BlackboardValueType.Vector3).Build();
        var board = new Blackboard(layout);
        var entity = level.World.Create(LocalTransform.At(Vector3.Zero));
        var agent = new AgentContext(level.World, entity, board, null, GameTime.Zero, 0);

        var reachable = true;

        var query = new EnvironmentQuery(
            Symbol.Intern("sometimes"),
            [QueryGenerators.Circle(5f, 8)],
            new QueryTest(
                QueryTests.From(
                    "mood",
                    (in AgentContext c, in QueryOrigin o, in QueryPoint p) => reachable ? 1f : float.NaN
                )
            )
        );

        var service = new RunQueryService(new(query, Result: layout.Key("spot")));

        var template = BehaviorTreeCompiler.Compile(
            BehaviorTree.Asset("holder", BehaviorTree.Sequence("root", BehaviorTree.Task("wait", "wait"))),
            Registry(),
            layout
        );

        var instance = new BehaviorTreeInstance(template, new AgentMemoryPool());
        var context = new BehaviorContext(agent, instance, 0);

        service.Tick(in context, [], 0f);
        Assert.True(board.IsSet(layout.Key("spot")));

        reachable = false;
        service.Tick(in context, [], 0f);
        Assert.False(board.IsSet(layout.Key("spot")));
    }

    static AgentActionRegistry Registry() {
        var registry = new AgentActionRegistry();

        registry.Register("wait", new WaitTask(1f), WaitTask.StateSize);

        return registry;
    }

    /// <summary>A ring around the agent, scored by how near each point is to the context.</summary>
    static EnvironmentQuery Nearest() =>
        new(
            Symbol.Intern("toward"),
            [QueryGenerators.Circle(5f, 8, aroundQuerier: true)],
            new QueryTest(
                QueryTests.Distance(QueryDistanceFrom.Context),
                new ResponseCurve { Slope = -1f, Shift = 1f }
            ) { Maximum = 40f }
        );
}
