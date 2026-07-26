// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Vixen.Core;
using Xunit;

namespace Vixen.Ecs.Tests;

/// <summary>
///     Random queries over random worlds, compared with a linear scan.
/// </summary>
/// <remarks>
///     <para>
///         What [04](../../docs/plan/04-ecs-and-scripting.md) § Tests asks for. The oracle walks
///         every entity and tests three set memberships by hand — the thing the archetype mask exists
///         to avoid doing. That is exactly why it is a good oracle: it shares no code and no idea
///         with the implementation, so the two can only agree by both being right.
///     </para>
///     <para>
///         The interesting cases are the empty ones. An empty <c>any</c> set means "no such
///         requirement" and an empty <c>all</c> set means "everything" — two places where a natural
///         implementation of the mask test does the opposite of what the words say, and where a
///         hand-written test is unlikely to think to look.
///     </para>
/// </remarks>
public sealed class QueryOracleTests {
    const int Kinds = 7;

    [Fact]
    public void EveryQueryAgreesWithALinearScan() =>
        Gen.Select(Gen.Int[0, int.MaxValue], Gen.Int[1, 60])
            .Sample(input => Check(input.Item1, input.Item2), iter: 500);

    static bool Check(int seed, int entityCount) {
        var state = (uint)seed | 1u;

        uint Next() {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }

        using var world = new World();
        var model = new List<(Entity Entity, int Mask)>();

        for (var index = 0; index < entityCount; index++) {
            var mask = (int)(Next() % (1 << Kinds));
            var entity = world.Create();

            for (var kind = 0; kind < Kinds; kind++) {
                if ((mask & (1 << kind)) != 0) {
                    Add(world, entity, kind);
                }
            }

            model.Add((entity, mask));
        }

        // Three independent random sets, each often empty, which is where the interesting cases are.
        for (var attempt = 0; attempt < 8; attempt++) {
            var all = (int)(Next() % (1 << Kinds));
            var any = (int)(Next() % (1 << Kinds));
            var none = (int)(Next() % (1 << Kinds));

            var description = Describe(all, any, none);
            var expected = new HashSet<Entity>();

            foreach (var (entity, mask) in model) {
                if ((mask & all) == all && (any == 0 || (mask & any) != 0) && (mask & none) == 0) {
                    expected.Add(entity);
                }
            }

            var found = new HashSet<Entity>();

            foreach (var chunk in world.Chunks(description)) {
                foreach (var entity in chunk.Entities) {
                    if (!found.Add(entity)) {
                        // The same entity twice means a chunk is being iterated twice, or an
                        // archetype matched under two signatures.
                        return false;
                    }
                }
            }

            if (!expected.SetEquals(found) || world.Query(description).EntityCount != expected.Count) {
                return false;
            }
        }

        return true;
    }

    static QueryDescription Describe(int all, int any, int none) {
        var description = new QueryDescription();
        description.RequireAll(Ids(all));
        description.RequireAny(Ids(any));
        description.Exclude(Ids(none));
        return description;
    }

    static ComponentTypeId[] Ids(int mask) {
        var ids = new List<ComponentTypeId>();

        for (var kind = 0; kind < Kinds; kind++) {
            if ((mask & (1 << kind)) != 0) {
                ids.Add(IdOf(kind));
            }
        }

        return [.. ids];
    }

    static ComponentTypeId IdOf(int kind) => kind switch {
        0 => ComponentType<Position>.Id,
        1 => ComponentType<Velocity>.Id,
        2 => ComponentType<Health>.Id,
        3 => ComponentType<Flags>.Id,
        4 => ComponentType<Frozen>.Id,
        5 => ComponentType<Player>.Id,
        _ => ComponentType<Npc>.Id
    };

    static void Add(World world, Entity entity, int kind) {
        switch (kind) {
            case 0:
                world.Add(entity, new Position());
                break;

            case 1:
                world.Add(entity, new Velocity());
                break;

            case 2:
                world.Add(entity, new Health());
                break;

            case 3:
                world.Add(entity, new Flags());
                break;

            case 4:
                world.Add<Frozen>(entity);
                break;

            case 5:
                world.Add<Player>(entity);
                break;

            default:
                world.Add<Npc>(entity);
                break;
        }
    }
}
