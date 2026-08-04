// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai.Nodes.Ecs;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Navigation.Ecs;
using Xunit;

namespace Vixen.Ai.Nodes.Tests;

public class MoveToTests {
    [Fact]
    public void ItWalksToAPositionAndSucceedsOnArrival() {
        var level = new Level();
        var walker = level.Walker(new(5f, 0f, 5f));
        var board = Board.Position(new(30f, 0f, 30f));
        var task = new MoveToTask(Board.Key, acceptance: 1.5f);
        var state = new byte[MoveToTask.StateSize];
        var status = ActionStatus.Running;

        for (var frame = 0; frame < 900 && status == ActionStatus.Running; frame++) {
            status = task.Tick(Board.Context(level, walker, board), state, 1f / 60f);
            level.Step();
        }

        Assert.Equal(ActionStatus.Succeeded, status);
        Assert.True(
            AgentTarget.FlatDistance(level.Where(walker), new(30f, 0f, 30f)) <= 1.5f,
            $"it stopped at {level.Where(walker)}."
        );
    }

    /// <summary>
    ///     ⚠ The whole reason a move task keeps state. Re-issuing the destination every tick bumps its
    ///     version every tick, which is a full path search per agent per frame — the exact cost the
    ///     path queue's budget exists to bound, paid unconditionally.
    /// </summary>
    [Fact]
    public void ItRepathsWhenAFollowedEntityMovesAndNotOtherwise() {
        var level = new Level();
        var walker = level.Walker(new(5f, 0f, 5f));
        var quarry = level.World.Create(LocalTransform.At(new(30f, 0f, 30f)));
        var board = Board.Target(quarry);
        var task = new MoveToTask(Board.Key, repath: 1f);
        var state = new byte[MoveToTask.StateSize];

        task.Tick(Board.Context(level, walker, board), state, 1f / 60f);

        var issued = level.World.Get<NavigationDestination>(walker).Version;

        for (var frame = 0; frame < 10; frame++) {
            task.Tick(Board.Context(level, walker, board), state, 1f / 60f);
        }

        Assert.Equal(issued, level.World.Get<NavigationDestination>(walker).Version);

        // A step of a tenth of a metre, ten times: it has moved a metre in total and the destination
        // is re-issued once, not ten times.
        for (var frame = 0; frame < 10; frame++) {
            level.Transform(quarry).Position += new Vector3(0.15f, 0f, 0f);
            task.Tick(Board.Context(level, walker, board), state, 1f / 60f);
        }

        Assert.Equal(issued + 1, level.World.Get<NavigationDestination>(walker).Version);
    }

    /// <summary>
    ///     ⚠ An agent that kept walking to a destination its tree has forgotten about is the classic
    ///     behaviour-tree bug: a guard that chases you while playing its idle.
    /// </summary>
    [Fact]
    public void AbortingStopsTheAgentWhereItStands() {
        var level = new Level();
        var walker = level.Walker(new(5f, 0f, 5f));
        var board = Board.Position(new(35f, 0f, 35f));
        var task = new MoveToTask(Board.Key);
        var state = new byte[MoveToTask.StateSize];

        task.Tick(Board.Context(level, walker, board), state, 1f / 60f);
        level.Step(60);

        var moved = level.Where(walker);

        task.Abort(Board.Context(level, walker, board), state);
        level.Step(120);

        Assert.True(
            AgentTarget.FlatDistance(moved, level.Where(walker)) < 1f,
            $"it carried on from {moved} to {level.Where(walker)} after being aborted."
        );
    }

    [Fact]
    public void ItFailsWhenTheKeyNamesNothingOrTheEntityCannotWalk() {
        var level = new Level();
        var walker = level.Walker(new(5f, 0f, 5f));
        var stranger = level.World.Create(LocalTransform.At(new(5f, 0f, 5f)));
        var task = new MoveToTask(Board.Key);
        var state = new byte[MoveToTask.StateSize];

        Assert.Equal(ActionStatus.Failed, task.Tick(Board.Context(level, walker, Board.Empty()), state, 0.1f));

        Assert.Equal(
            ActionStatus.Failed,
            task.Tick(Board.Context(level, stranger, Board.Position(new(9f, 0f, 9f))), state, 0.1f)
        );
    }
}

public class MoveDirectlyTowardTests {
    [Fact]
    public void ItMovesAtItsSpeedAndDoesNotOvershoot() {
        var level = new Level();
        var flier = level.World.Create(LocalTransform.At(Vector3.Zero));
        var board = Board.Position(new(0f, 0f, -1f));
        var task = new MoveDirectlyTowardTask(Board.Key, speed: 2f, acceptance: 0.05f);
        var state = Array.Empty<byte>();

        Assert.Equal(ActionStatus.Running, task.Tick(Board.Context(level, flier, board), state, 0.25f));
        Assert.Equal(-0.5f, level.Where(flier).Z, 4);

        // ⚠ A governed agent is handed a large delta. Unclamped, this step would land at −2 and the
        // task would oscillate around the target for ever.
        Assert.Equal(ActionStatus.Succeeded, task.Tick(Board.Context(level, flier, board), state, 5f));
        Assert.Equal(-1f, level.Where(flier).Z, 4);
    }
}

public class PatrolTests {
    static readonly Vector3[] Route = [
        new(5f, 0f, 5f),
        new(30f, 0f, 5f),
        new(30f, 0f, 30f)
    ];

    [Fact]
    public void ForwardIsTheOnlyModeThatEverFinishes() {
        Assert.Equal(ActionStatus.Succeeded, Walk(PatrolMode.Forward));
        Assert.Equal(ActionStatus.Running, Walk(PatrolMode.Loop));
        Assert.Equal(ActionStatus.Running, Walk(PatrolMode.PingPong));
    }

    /// <summary>
    ///     ⚠ The nearest point, not the first: a guard that respawns mid-route otherwise walks back to
    ///     the start of it through whatever is in the way.
    /// </summary>
    [Fact]
    public void ItStartsFromTheNearestPointRatherThanTheFirst() {
        var level = new Level();

        // Two thirds of the way along the first leg: nearest to the *second* point, and far enough
        // from it that the first tick heads for it rather than arriving at it.
        var walker = level.Walker(new(25f, 0f, 6f));

        level.World.Add(walker, PatrolRoute.Of(PatrolMode.Forward, Route));

        var task = new PatrolTask();
        var state = new byte[PatrolTask.StateSize];

        task.Tick(Board.Context(level, walker, Board.Empty()), state, 1f / 60f);

        Assert.Equal(Route[1], level.World.Get<NavigationDestination>(walker).Value);
    }

    /// <summary>
    ///     ⚠ A ping-pong reflects rather than wrapping. Resetting the index instead would make a
    ///     two-point route stand still at one end of itself.
    /// </summary>
    [Fact]
    public void PingPongTurnsRoundAtTheEnd() {
        var level = new Level();
        var walker = level.Walker(Route[0]);

        level.World.Add(walker, PatrolRoute.Of(PatrolMode.PingPong, [Route[0], Route[1]]));

        var task = new PatrolTask();
        var state = new byte[PatrolTask.StateSize];
        var visited = new List<Vector3>();

        for (var frame = 0; frame < 2_400; frame++) {
            task.Tick(Board.Context(level, walker, Board.Empty()), state, 1f / 60f);

            var destination = level.World.Get<NavigationDestination>(walker).Value;

            if (visited.Count == 0 || visited[^1] != destination) {
                visited.Add(destination);
            }

            level.Step();
        }

        Assert.True(visited.Count >= 3, $"it only ever headed for {visited.Count} points.");
        Assert.Equal(Route[1], visited[0]);
        Assert.Equal(Route[0], visited[1]);
        Assert.Equal(Route[1], visited[2]);
    }

    [Fact]
    public void ARouteWithFewerThanTwoPointsIsNotARoute() {
        var level = new Level();
        var walker = level.Walker(Route[0]);

        level.World.Add(walker, PatrolRoute.Of(PatrolMode.Loop, Route[0]));

        var task = new PatrolTask();

        Assert.Equal(
            ActionStatus.Failed,
            task.Tick(Board.Context(level, walker, Board.Empty()), new byte[PatrolTask.StateSize], 0.1f)
        );
    }

    static ActionStatus Walk(PatrolMode mode) {
        var level = new Level();
        var walker = level.Walker(Route[0]);

        level.World.Add(walker, PatrolRoute.Of(mode, Route));

        var task = new PatrolTask();
        var state = new byte[PatrolTask.StateSize];
        var status = ActionStatus.Running;

        for (var frame = 0; frame < 2_400 && status == ActionStatus.Running; frame++) {
            status = task.Tick(Board.Context(level, walker, Board.Empty()), state, 1f / 60f);
            level.Step();
        }

        return status;
    }
}

public class RotateTowardTests {
    [Fact]
    public void ItTurnsTheShortWayAndStopsInsideTheTolerance() {
        var level = new Level();
        var guard = level.World.Create(LocalTransform.At(Vector3.Zero));
        var board = Board.Position(new(10f, 0f, 0f));
        var task = new RotateTowardTask(Board.Key, degreesPerSecond: 90f, tolerance: 1f);
        var state = Array.Empty<byte>();

        // Facing +X is a quarter turn clockwise from the default −Z.
        Assert.Equal(ActionStatus.Running, task.Tick(Board.Context(level, guard, board), state, 0.5f));

        var half = RotateTowardTask.Yaw(level.Transform(guard).Rotation);

        Assert.Equal(float.DegreesToRadians(-45f), half, 3);

        Assert.Equal(ActionStatus.Succeeded, task.Tick(Board.Context(level, guard, board), state, 1f));
        Assert.Equal(float.DegreesToRadians(-90f), RotateTowardTask.Yaw(level.Transform(guard).Rotation), 3);
    }

    [Fact]
    public void ATurnTakesTheShortWayRoundRatherThanTheLongOne() {
        Assert.Equal(-0.1f, RotateTowardTask.Wrap(MathF.Tau - 0.1f), 4);
        Assert.Equal(0.1f, RotateTowardTask.Wrap(-MathF.Tau + 0.1f), 4);
        Assert.Equal(0f, RotateTowardTask.Wrap(MathF.Tau * 3f), 4);
    }

    /// <summary>With no key, it falls through to the one place everything downstream reads.</summary>
    [Fact]
    public void ItFallsBackToTheFocusWhenNoKeyIsGiven() {
        var level = new Level();
        var guard = level.World.Create(LocalTransform.At(Vector3.Zero));
        var task = new RotateTowardTask(BlackboardKey.Invalid, degreesPerSecond: 3600f);
        var state = Array.Empty<byte>();

        Assert.Equal(ActionStatus.Failed, task.Tick(Board.Context(level, guard, Board.Empty()), state, 0.1f));

        level.World.Add(guard, AiFocus.At(new(10f, 0f, 0f)));

        Assert.Equal(ActionStatus.Succeeded, task.Tick(Board.Context(level, guard, Board.Empty()), state, 1f));
        Assert.Equal(float.DegreesToRadians(-90f), RotateTowardTask.Yaw(level.Transform(guard).Rotation), 3);
    }

    [Fact]
    public void StandingOnTheTargetIsNotASpin() {
        var level = new Level();
        var guard = level.World.Create(LocalTransform.At(Vector3.Zero));
        var task = new RotateTowardTask(Board.Key);

        Assert.Equal(
            ActionStatus.Succeeded,
            task.Tick(Board.Context(level, guard, Board.Position(Vector3.Zero)), Array.Empty<byte>(), 0.1f)
        );
    }
}

public class DoesPathExistTests {
    [Fact]
    public void AnOpenFloorHasAPathAndSomewhereOffTheMeshDoesNot() {
        var level = new Level();
        var walker = level.Walker(new(5f, 0f, 5f));

        foreach (var test in Enum.GetValues<PathTest>()) {
            Assert.True(Ask(level, walker, new(30f, 0f, 30f), test), $"{test} could not find a path across a floor.");
            Assert.False(Ask(level, walker, new(400f, 0f, 400f), test), $"{test} found a path off the mesh.");
        }
    }

    /// <summary>A budget too small to reach the far corner says no, which is the conservative answer.</summary>
    [Fact]
    public void ABudgetedSearchThatRunsOutSaysNo() {
        var level = new Level(200f);
        var walker = level.Walker(new(5f, 0f, 5f));

        Assert.False(Ask(level, walker, new(190f, 0f, 190f), PathTest.Budgeted, budget: 1));
        Assert.True(Ask(level, walker, new(190f, 0f, 190f), PathTest.Full));
    }

    static bool Ask(Level level, Entity walker, Vector3 goal, PathTest test, int budget = 256) {
        var decorator = new DoesPathExistDecorator(level.Query, Board.Key, test, budget);
        var context = new BehaviorContext(Board.Context(level, walker, Board.Position(goal)), null!, 0);

        return decorator.Evaluate(in context, []);
    }
}

public class FocusTests {
    /// <summary>
    ///     ⚠ The half people leave out. A focus nobody cleared is a guard that keeps staring at where
    ///     an enemy was after it has forgotten about it.
    /// </summary>
    [Fact]
    public void TheFocusIsClearedWhenTheKeyIsUnset() {
        var level = new Level();
        var guard = level.World.Create(LocalTransform.At(Vector3.Zero), new AiFocus());
        var quarry = level.World.Create(LocalTransform.At(new(4f, 0f, 4f)));
        var board = Board.Target(quarry);
        var service = new DefaultFocusService(Board.Key);

        service.Tick(new(Board.Context(level, guard, board), null!, 0), [], 0.1f);

        Assert.True(level.World.Get<AiFocus>(guard).HasFocus);
        Assert.Equal(quarry, level.World.Get<AiFocus>(guard).Target);
        Assert.True(level.World.Get<AiFocus>(guard).TryResolve(level.World, out var where));
        Assert.Equal(new Vector3(4f, 0f, 4f), where);

        board.Clear(Board.Key);
        service.Tick(new(Board.Context(level, guard, board), null!, 0), [], 0.1f);

        Assert.False(level.World.Get<AiFocus>(guard).HasFocus);
    }

    [Fact]
    public void AFocusOnADestroyedEntityResolvesToNothing() {
        var level = new Level();
        var quarry = level.World.Create(LocalTransform.At(new(4f, 0f, 4f)));
        var focus = AiFocus.On(quarry);

        Assert.True(focus.TryResolve(level.World, out _));

        level.World.Destroy(quarry);

        Assert.False(focus.TryResolve(level.World, out _));
    }
}

/// <summary>One blackboard with one key, in each of the two types a target may be.</summary>
static class Board {
    static readonly BlackboardLayout Positions = new BlackboardLayoutBuilder()
        .Add("target", BlackboardValueType.Vector3)
        .Build();

    static readonly BlackboardLayout Targets = new BlackboardLayoutBuilder()
        .Add("target", BlackboardValueType.Entity)
        .Build();

    public static BlackboardKey Key => new(0);

    public static Blackboard Position(Vector3 value) {
        var board = new Blackboard(Positions);

        board.SetVector3(Key, value);

        return board;
    }

    public static Blackboard Target(Entity value) {
        var board = new Blackboard(Targets);

        board.SetEntity(Key, value);

        return board;
    }

    public static Blackboard Empty() => new(Positions);

    public static AgentContext Context(Level level, Entity entity, Blackboard board) =>
        new(level.World, entity, board, null, Level.Frame(0), 0);
}
