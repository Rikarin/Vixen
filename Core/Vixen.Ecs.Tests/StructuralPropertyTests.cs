// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Vixen.Core;
using Xunit;

namespace Vixen.Ecs.Tests;

/// <summary>
///     Random create/destroy/add/remove/set sequences, checked against a dictionary.
/// </summary>
/// <remarks>
///     <para>
///         What [04](../../docs/plan/04-ecs-and-scripting.md) § Tests asks for. The oracle is
///         deliberately the dumbest possible model — a <c>Dictionary</c> per entity — because the
///         thing under test is an optimisation of exactly that, and any cleverness in the model
///         would be cleverness that could be wrong in the same direction.
///     </para>
///     <para>
///         What it is really checking is that a structural change moves the entity's row from one
///         chunk to another <em>without</em> disturbing the row that gets swapped into its place.
///         Every archetype move is two mutations of two chunks, and the failure mode is an entity
///         nobody touched quietly acquiring somebody else's data.
///     </para>
/// </remarks>
public sealed class StructuralPropertyTests {
    const int Kinds = 6;

    [Fact]
    public void RandomStructuralChangesLeaveEveryComponentValueIntact() =>
        Gen.Select(Gen.Int[0, int.MaxValue], Gen.Int[8, 120])
            .Sample(input => Replay(input.Item1, input.Item2), iter: 400);

    /// <summary>
    ///     The same sequence against an archetype whose chunk holds only a handful of entities, so
    ///     that chunk boundaries, the tail-chunk refill and chunk retirement are all in the path
    ///     rather than reached once in a thousand runs.
    /// </summary>
    [Fact]
    public void TheSameHoldsWhenEveryChunkHoldsOnlyAFewEntities() =>
        Gen.Select(Gen.Int[0, int.MaxValue], Gen.Int[8, 120])
            .Sample(input => Replay(input.Item1, input.Item2, bulky: true), iter: 400);

    static bool Replay(int seed, int operations, bool bulky = false) {
        var state = (uint)seed | 1u;

        uint Next() {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }

        using var world = new World();
        var model = new Dictionary<Entity, Dictionary<int, int>>();
        var live = new List<Entity>();
        var dead = new List<Entity>();
        var counter = 0;

        for (var step = 0; step < operations; step++) {
            var choice = Next() % 100;

            if (live.Count == 0 || choice < 30) {
                var entity = world.Create();
                var components = new Dictionary<int, int>();

                if (bulky) {
                    world.Add(entity, new Bulky());
                }

                var initial = (int)(Next() % (1 << Kinds));

                for (var kind = 0; kind < Kinds; kind++) {
                    if ((initial & (1 << kind)) != 0) {
                        components[kind] = ++counter;
                        Add(world, entity, kind, components[kind]);
                    }
                }

                model[entity] = components;
                live.Add(entity);
            } else if (choice < 45) {
                var index = (int)(Next() % (uint)live.Count);
                var entity = live[index];
                world.Destroy(entity);
                model.Remove(entity);
                live.RemoveAt(index);
                dead.Add(entity);
            } else if (choice < 70) {
                var entity = live[(int)(Next() % (uint)live.Count)];
                var kind = (int)(Next() % Kinds);

                if (!model[entity].ContainsKey(kind)) {
                    model[entity][kind] = ++counter;
                    Add(world, entity, kind, model[entity][kind]);
                }
            } else if (choice < 85) {
                var entity = live[(int)(Next() % (uint)live.Count)];
                var kind = (int)(Next() % Kinds);

                if (model[entity].Remove(kind)) {
                    Remove(world, entity, kind);
                }
            } else {
                var entity = live[(int)(Next() % (uint)live.Count)];
                var kind = (int)(Next() % Kinds);

                if (model[entity].ContainsKey(kind) && kind != 4) {
                    model[entity][kind] = ++counter;
                    Set(world, entity, kind, model[entity][kind]);
                }
            }

            if (!Matches(world, model, bulky)) {
                return false;
            }
        }

        // Every handle to a destroyed entity has to stay refused, however many slots were recycled
        // over it in the meantime.
        foreach (var entity in dead) {
            if (world.IsAlive(entity)) {
                return false;
            }
        }

        return true;
    }

    static bool Matches(World world, Dictionary<Entity, Dictionary<int, int>> model, bool bulky) {
        if (world.EntityCount != model.Count) {
            return false;
        }

        foreach (var (entity, components) in model) {
            if (!world.IsAlive(entity)) {
                return false;
            }

            var signature = world.ArchetypeOf(entity).Signature;
            var expected = components.Count + (bulky ? 1 : 0);

            if (signature.Count != expected) {
                return false;
            }

            for (var kind = 0; kind < Kinds; kind++) {
                var present = components.TryGetValue(kind, out var value);

                if (present != Has(world, entity, kind)) {
                    return false;
                }

                if (present && !Reads(world, entity, kind, value)) {
                    return false;
                }
            }
        }

        return true;
    }

    static void Add(World world, Entity entity, int kind, int value) {
        switch (kind) {
            case 0:
                world.Add(entity, new Position(value, value + 1, value + 2));
                break;

            case 1:
                world.Add(entity, new Velocity(value + 3, value + 4, value + 5));
                break;

            case 2:
                world.Add(entity, new Health(value));
                break;

            case 3:
                world.Add(entity, new Flags((byte)value));
                break;

            case 4:
                world.Add<Frozen>(entity);
                break;

            default:
                world.Add(entity, new Named($"n{value}"));
                break;
        }
    }

    static void Set(World world, Entity entity, int kind, int value) {
        switch (kind) {
            case 0:
                world.Set(entity, new Position(value, value + 1, value + 2));
                break;

            case 1:
                world.Set(entity, new Velocity(value + 3, value + 4, value + 5));
                break;

            case 2:
                world.Set(entity, new Health(value));
                break;

            case 3:
                world.Set(entity, new Flags((byte)value));
                break;

            default:
                world.Set(entity, new Named($"n{value}"));
                break;
        }
    }

    static void Remove(World world, Entity entity, int kind) {
        switch (kind) {
            case 0:
                world.Remove<Position>(entity);
                break;

            case 1:
                world.Remove<Velocity>(entity);
                break;

            case 2:
                world.Remove<Health>(entity);
                break;

            case 3:
                world.Remove<Flags>(entity);
                break;

            case 4:
                world.Remove<Frozen>(entity);
                break;

            default:
                world.Remove<Named>(entity);
                break;
        }
    }

    static bool Has(World world, Entity entity, int kind) => kind switch {
        0 => world.Has<Position>(entity),
        1 => world.Has<Velocity>(entity),
        2 => world.Has<Health>(entity),
        3 => world.Has<Flags>(entity),
        4 => world.Has<Frozen>(entity),
        _ => world.Has<Named>(entity)
    };

    static bool Reads(World world, Entity entity, int kind, int value) => kind switch {
        0 => world.Read<Position>(entity) is { X: var x, Y: var y, Z: var z }
            && x == value
            && y == value + 1
            && z == value + 2,
        1 => world.Read<Velocity>(entity) is { X: var x, Y: var y, Z: var z }
            && x == value + 3
            && y == value + 4
            && z == value + 5,
        2 => world.Read<Health>(entity).Value == value,
        3 => world.Read<Flags>(entity).Value == (byte)value,
        4 => true,
        _ => world.Read<Named>(entity).Name == $"n{value}"
    };
}
