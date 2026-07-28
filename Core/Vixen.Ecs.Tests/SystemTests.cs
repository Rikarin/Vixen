// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Threading;
using Vixen.Ecs.Systems;
using Xunit;

namespace Vixen.Ecs.Tests;

public sealed class SystemTests {
    /// <summary>
    ///     The systems below declare their access with <c>[Reads]</c> and <c>[Writes]</c>, and an
    ///     attribute can only look a component id up — it names a <see cref="Type" />, which is not
    ///     enough to close a generic and assign one. So the ids have to exist before any of these
    ///     tests builds a graph. Left to chance they only exist when some earlier test in the class
    ///     happened to name the type generically first, which makes every test here pass in a full
    ///     run and fail on its own. xUnit builds a fresh instance per test, so this runs before each
    ///     of them.
    /// </summary>
    public SystemTests() {
        ComponentRegistry.Of<Position>();
        ComponentRegistry.Of<Velocity>();
        ComponentRegistry.Of<Health>();
    }

    [Fact]
    public void SystemsRunInRegistrationOrderWhenNothingSaysOtherwise() {
        var log = new List<string>();
        using var world = new World();
        using var runner = new SystemRunner(world);

        runner.Add(new RecordingSystem("first", log)).Add(new RecordingSystem("second", log));
        runner.RunPhase(SystemPhase.Update, default);

        Assert.Equal(["first", "second"], log);
    }

    [Fact]
    public void UpdateAfterMovesASystemLaterEvenIfItWasRegisteredFirst() {
        var log = new List<string>();
        using var world = new World();
        using var runner = new SystemRunner(world);

        runner.Add(new LateSystem(log)).Add(new EarlySystem(log));
        runner.RunPhase(SystemPhase.Update, default);

        Assert.Equal(["early", "late"], log);
    }

    [Fact]
    public void PhasesRunInTheirDeclaredOrderWhateverOrderSystemsWereAddedIn() {
        var log = new List<string>();
        using var world = new World();
        using var runner = new SystemRunner(world);

        runner.Add(new RenderPhaseSystem(log)).Add(new InputPhaseSystem(log));
        runner.RunFrame(default);

        Assert.Equal(["input", "render"], log);
    }

    [Fact]
    public void AnOrderingCycleNamesEverySystemInIt() {
        using var world = new World();
        using var runner = new SystemRunner(world);

        runner.Add(new CycleA()).Add(new CycleB());

        var failure = Assert.Throws<InvalidOperationException>(() => runner.RunPhase(SystemPhase.Update, default));
        Assert.Contains(nameof(CycleA), failure.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(CycleB), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddingASystemAfterTheFirstPhaseIsRefused() {
        var log = new List<string>();
        using var world = new World();
        using var runner = new SystemRunner(world);

        runner.Add(new RecordingSystem("first", log));
        runner.RunPhase(SystemPhase.Update, default);

        Assert.Throws<InvalidOperationException>(() => runner.Add(new RecordingSystem("second", log)));
    }

    // ---------------------------------------------------------------- the dependency graph

    [Fact]
    public void SystemsWithDisjointWritesDoNotWaitForEachOther() {
        using var world = new World();
        using var runner = new SystemRunner(world);

        runner.Add(new MoveSystem()).Add(new HealSystem());

        var nodes = runner.Graph.InPhase(SystemPhase.Update);

        Assert.Equal(2, nodes.Count);
        Assert.Empty(nodes[0].DependsOn);
        Assert.Empty(nodes[1].DependsOn);
    }

    [Fact]
    public void AReaderWaitsForTheWriterThatCameBeforeIt() {
        using var world = new World();
        using var runner = new SystemRunner(world);

        runner.Add(new MoveSystem()).Add(new ReadPositionSystem());

        var nodes = runner.Graph.InPhase(SystemPhase.Update);

        Assert.Equal([0], nodes[1].DependsOn);
    }

    [Fact]
    public void TwoReadersOfTheSameComponentDoNotWaitForEachOther() {
        using var world = new World();
        using var runner = new SystemRunner(world);

        runner.Add(new ReadPositionSystem()).Add(new AlsoReadPositionSystem());

        var nodes = runner.Graph.InPhase(SystemPhase.Update);

        Assert.Empty(nodes[1].DependsOn);
    }

    /// <summary>
    ///     "I did not say what I touch" can only be read as "assume everything". The other reading —
    ///     assume nothing — is silently wrong exactly when it matters.
    /// </summary>
    [Fact]
    public void AnUndeclaredSystemConflictsWithEverything() {
        using var world = new World();
        using var runner = new SystemRunner(world);

        runner.Add(new MoveSystem()).Add(new UndeclaredSystem()).Add(new HealSystem());

        var nodes = runner.Graph.InPhase(SystemPhase.Update);

        Assert.Equal([0], nodes[1].DependsOn);
        Assert.Equal([1], nodes[2].DependsOn);
    }

    /// <summary>
    ///     A write implies a read, so a system that only writes X and one that only reads X are not
    ///     mistaken for disjoint — which is the one combination that is definitely a race.
    /// </summary>
    [Fact]
    public void AWriteImpliesARead() {
        var access = SystemAccess.Declare().Write<Position>().Build();

        Assert.Contains(ComponentType<Position>.Id, access.Reads);
        Assert.True(access.ConflictsWith(SystemAccess.Declare().Read<Position>().Build()));
    }

    [Fact]
    public void AccessCanBeDeclaredAtConstructionInsteadOfWithAttributes() {
        using var world = new World();
        using var runner = new SystemRunner(world);

        runner.Add(new ProgrammaticSystem()).Add(new ReadPositionSystem());

        Assert.Equal([0], runner.Graph.InPhase(SystemPhase.Update)[1].DependsOn);
    }

    [Fact]
    public void TheGraphDumpsAsDotAndMermaid() {
        using var world = new World();
        using var runner = new SystemRunner(world);

        runner.Add(new MoveSystem()).Add(new ReadPositionSystem());

        var dot = runner.Graph.ToDot();
        var mermaid = runner.Graph.ToMermaid();

        Assert.Contains("digraph systems", dot, StringComparison.Ordinal);
        Assert.Contains("\"Update.MoveSystem\" -> \"Update.ReadPositionSystem\"", dot, StringComparison.Ordinal);
        Assert.Contains("flowchart LR", mermaid, StringComparison.Ordinal);
        Assert.Contains("Update_MoveSystem --> Update_ReadPositionSystem", mermaid, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the phase boundary

    [Fact]
    public void StructuralChangeRecordedDuringAPhaseIsAppliedAtItsEnd() {
        using var world = new World();
        using var runner = new SystemRunner(world);

        runner.Add(new SpawnSystem(3));

        Assert.Equal(0, world.EntityCount);
        runner.RunPhase(SystemPhase.Update, default);
        Assert.Equal(3, world.EntityCount);
    }

    [Fact]
    public void WhatASystemWroteIsVisibleToAChangeFilterOnTheNextPhase() {
        using var world = new World();
        using var runner = new SystemRunner(world);
        var entity = world.Create(new Position());

        runner.Add(new MoveSystem());

        var lastSeen = world.Version;
        runner.RunPhase(SystemPhase.Update, default);

        var changed = 0;

        foreach (var chunk in world.Chunks(new QueryDescription().WithChanged<Position>(), lastSeen)) {
            changed += chunk.Count;
        }

        Assert.Equal(1, changed);
        Assert.Equal(1, world.Read<Position>(entity).X);
    }

    [Fact]
    public void SystemsThatScheduleWorkRunAgainstTheJobSystem() {
        using var scheduler = new JobScheduler(2);
        using var world = new World();
        using var runner = new SystemRunner(world, scheduler);

        for (var index = 0; index < 500; index++) {
            world.Create(new Position(index, 0, 0), new Velocity(1, 0, 0));
        }

        runner.Add(new IntegrateSystem());
        runner.RunPhase(SystemPhase.Update, default);

        var total = 0f;

        foreach (var chunk in world.Chunks(new QueryDescription().WithAll<Position>())) {
            foreach (var position in chunk.ReadValues<Position>()) {
                total += position.X;
            }
        }

        Assert.Equal((499 * 500 / 2) + 500, total);
    }

    [Fact]
    public void DisposingTheRunnerDisposesEverySystem() {
        var log = new List<string>();
        using var world = new World();
        var runner = new SystemRunner(world);

        runner.Add(new RecordingSystem("first", log)).Add(new RecordingSystem("second", log));
        runner.Dispose();

        Assert.Equal(["dispose second", "dispose first"], log);
    }

    // ---------------------------------------------------------------- systems under test

    sealed class RecordingSystem(string name, List<string> log) : SystemBase {
        public override JobHandle Update(in SystemContext context, JobHandle dependency) {
            log.Add(name);
            return dependency;
        }

        public override void Dispose() {
            log.Add($"dispose {name}");
            base.Dispose();
        }
    }

    sealed class EarlySystem(List<string> log) : SystemBase {
        public override JobHandle Update(in SystemContext context, JobHandle dependency) {
            log.Add("early");
            return dependency;
        }
    }

    [UpdateAfter(typeof(EarlySystem))]
    sealed class LateSystem(List<string> log) : SystemBase {
        public override JobHandle Update(in SystemContext context, JobHandle dependency) {
            log.Add("late");
            return dependency;
        }
    }

    [UpdateInGroup(SystemPhase.Input)]
    sealed class InputPhaseSystem(List<string> log) : SystemBase {
        public override JobHandle Update(in SystemContext context, JobHandle dependency) {
            log.Add("input");
            return dependency;
        }
    }

    [UpdateInGroup(SystemPhase.Render)]
    sealed class RenderPhaseSystem(List<string> log) : SystemBase {
        public override JobHandle Update(in SystemContext context, JobHandle dependency) {
            log.Add("render");
            return dependency;
        }
    }

    [UpdateAfter(typeof(CycleB))]
    sealed class CycleA : SystemBase {
        public override JobHandle Update(in SystemContext context, JobHandle dependency) => dependency;
    }

    [UpdateAfter(typeof(CycleA))]
    sealed class CycleB : SystemBase {
        public override JobHandle Update(in SystemContext context, JobHandle dependency) => dependency;
    }

    [Writes(typeof(Position))]
    sealed class MoveSystem : SystemBase {
        public override JobHandle Update(in SystemContext context, JobHandle dependency) {
            context.World.Query(
                new QueryDescription().WithAll<Position>(),
                static (ref Position position) => position.X += 1
            );

            return dependency;
        }
    }

    [Writes(typeof(Health))]
    sealed class HealSystem : SystemBase {
        public override JobHandle Update(in SystemContext context, JobHandle dependency) => dependency;
    }

    [Reads(typeof(Position))]
    sealed class ReadPositionSystem : SystemBase {
        public override JobHandle Update(in SystemContext context, JobHandle dependency) => dependency;
    }

    [Reads(typeof(Position))]
    sealed class AlsoReadPositionSystem : SystemBase {
        public override JobHandle Update(in SystemContext context, JobHandle dependency) => dependency;
    }

    sealed class UndeclaredSystem : SystemBase {
        public override JobHandle Update(in SystemContext context, JobHandle dependency) => dependency;
    }

    sealed class ProgrammaticSystem : SystemBase, IDeclaredAccess {
        public SystemAccess Access { get; } = SystemAccess.Declare().Write<Position>().Build();

        public override JobHandle Update(in SystemContext context, JobHandle dependency) => dependency;
    }

    [Writes(typeof(Health))]
    sealed class SpawnSystem(int count) : SystemBase {
        public override JobHandle Update(in SystemContext context, JobHandle dependency) {
            for (var index = 0; index < count; index++) {
                context.Commands.Add(context.Commands.Create(), new Health(index));
            }

            return dependency;
        }
    }

    [Reads(typeof(Velocity))]
    [Writes(typeof(Position))]
    sealed class IntegrateSystem : SystemBase {
        public override JobHandle Update(in SystemContext context, JobHandle dependency) {
            context.World.Query(
                new QueryDescription().WithAll<Position, Velocity>(),
                static (ref Position position, ref Velocity velocity) => position.X += velocity.X
            );

            return dependency;
        }
    }
}
