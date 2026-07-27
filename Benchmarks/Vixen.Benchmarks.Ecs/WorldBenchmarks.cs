// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using BenchmarkDotNet.Attributes;
using Vixen.Core;
using Vixen.Ecs;

namespace Vixen.Benchmarks.Ecs;

/// <summary>A three-float component, the shape every ECS benchmark in the world uses.</summary>
public struct Position {
    public float X;
    public float Y;
    public float Z;
}

/// <summary>The other one.</summary>
public struct Velocity {
    public float X;
    public float Y;
    public float Z;
}

/// <summary>A third, so an archetype can be wider than two.</summary>
public struct Health {
    public int Value;
}

/// <summary>
///     The operations [04](../../docs/plan/04-ecs-and-scripting.md) § Tests names: create, destroy,
///     get, set and iterate, at the scale the Phase 2 exit criterion sets.
/// </summary>
/// <remarks>
///     <para>
///         Ported from Arch's benchmark set rather than invented, because the exit criterion is
///         "match or beat Arch 2.1" (ADR-004) and a benchmark that measures something else cannot
///         answer that. Same entity counts, same component shapes, same operations.
///     </para>
///     <para>
///         <b>Iteration is measured three ways</b>, because the three are genuinely different code
///         and the difference is the whole reason the query surface has three shapes: spans over a
///         chunk, a delegate per entity, and a struct visitor per entity. A number for only the
///         first would flatter the design and a number for only the second would libel it.
///     </para>
/// </remarks>
[MemoryDiagnoser]
public class WorldBenchmarks {
    World world = null!;
    Entity[] entities = [];
    QueryDescription moving = null!;

    /// <summary>How many entities the world holds.</summary>
    [Params(1_000, 100_000)]
    public int Count { get; set; }

    [GlobalSetup]
    public void Setup() {
        world = new("benchmark");
        moving = new QueryDescription().WithAll<Position, Velocity>();
        entities = new Entity[Count];

        for (var index = 0; index < Count; index++) {
            entities[index] = world.Create(new Position(), new Velocity { X = 1 }, new Health { Value = index });
        }
    }

    [GlobalCleanup]
    public void Cleanup() => world.Dispose();

    [Benchmark]
    public void Create() {
        using var fresh = new World("create");

        for (var index = 0; index < Count; index++) {
            fresh.Create(new Position(), new Velocity { X = 1 }, new Health { Value = index });
        }
    }

    [Benchmark]
    public void CreateThenDestroy() {
        using var fresh = new World("destroy");
        var created = new Entity[Count];

        for (var index = 0; index < Count; index++) {
            created[index] = fresh.Create(new Position(), new Velocity { X = 1 });
        }

        for (var index = 0; index < Count; index++) {
            fresh.Destroy(created[index]);
        }
    }

    [Benchmark]
    public float Get() {
        var total = 0f;

        for (var index = 0; index < entities.Length; index++) {
            total += world.Read<Position>(entities[index]).X;
        }

        return total;
    }

    [Benchmark]
    public void Set() {
        for (var index = 0; index < entities.Length; index++) {
            world.Get<Position>(entities[index]).X = index;
        }
    }

    /// <summary>The obvious way to write a chunk loop: index both spans, bound by the chunk's count.</summary>
    [Benchmark(Baseline = true)]
    public void IterateChunksByCount() {
        foreach (var chunk in world.Chunks(moving)) {
            var positions = chunk.Values<Position>();
            var velocities = chunk.ReadValues<Velocity>();

            for (var index = 0; index < chunk.Count; index++) {
                positions[index].X += velocities[index].X;
            }
        }
    }

    /// <summary>The same loop bounded by a span's own length, which is what elides the bounds check.</summary>
    /// <remarks>
    ///     The pair exists because the difference is not obvious and it is large. A loop bounded by
    ///     <c>chunk.Count</c> is bounded by a number the JIT cannot connect to either span, so both
    ///     indexers keep their bounds check; bounding by <c>positions.Length</c> removes one, and
    ///     slicing the other to match removes the second.
    /// </remarks>
    [Benchmark]
    public void IterateChunksBySpan() {
        foreach (var chunk in world.Chunks(moving)) {
            var positions = chunk.Values<Position>();
            var velocities = chunk.ReadValues<Velocity>()[..positions.Length];

            for (var index = 0; index < positions.Length; index++) {
                positions[index].X += velocities[index].X;
            }
        }
    }

    [Benchmark]
    public void IterateDelegate() =>
        world.Query(
            moving,
            static (ref Position position, ref Velocity velocity) => position.X += velocity.X
        );

    [Benchmark]
    public void IterateVisitor() {
        var visitor = default(Integrate);
        world.ForEach<Integrate, Position, Velocity>(moving, ref visitor);
    }

    /// <summary>Adding and removing a component, which is an archetype move each way.</summary>
    [Benchmark]
    public void AddThenRemove() {
        for (var index = 0; index < entities.Length; index++) {
            world.Add(entities[index], new Frozen());
            world.Remove<Frozen>(entities[index]);
        }
    }

    struct Frozen : ITagComponent;

    struct Integrate : IForEach<Position, Velocity> {
        public void Execute(ref Position position, ref Velocity velocity) => position.X += velocity.X;
    }
}
