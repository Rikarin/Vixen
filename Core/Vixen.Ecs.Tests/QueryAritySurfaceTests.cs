// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Core;
using Xunit;

using Spy = Vixen.Ecs.Tests.AritySpy<
    Vixen.Ecs.Tests.Slot0, Vixen.Ecs.Tests.Slot1, Vixen.Ecs.Tests.Slot2, Vixen.Ecs.Tests.Slot3,
    Vixen.Ecs.Tests.Slot4, Vixen.Ecs.Tests.Slot5, Vixen.Ecs.Tests.Slot6, Vixen.Ecs.Tests.Slot7,
    Vixen.Ecs.Tests.Slot8, Vixen.Ecs.Tests.Slot9, Vixen.Ecs.Tests.Slot10, Vixen.Ecs.Tests.Slot11,
    Vixen.Ecs.Tests.Slot12, Vixen.Ecs.Tests.Slot13, Vixen.Ecs.Tests.Slot14, Vixen.Ecs.Tests.Slot15>;

namespace Vixen.Ecs.Tests;

/// <summary>
///     Says that every arity of the generated query surface is reached, which is the one place in
///     this assembly where "is this line ever executed" is a real question rather than a metric.
/// </summary>
/// <remarks>
///     <para>
///         <c>QueryArityGenerator</c> emits two hundred and fifty-six methods — four description
///         builders, four iteration families, sixteen arities each — and before this file the tree
///         called <b>ten</b> of them. Measured by grepping every call site in
///         <c>Core</c>, <c>Samples</c>, <c>Editor</c>, <c>Platform</c> and <c>Tools</c>:
///         <c>Query</c> at arities 1, 2 and 4, <c>QueryWithEntity</c> and <c>ForEach</c> at arity 1,
///         <c>WithAll</c> at 1, 2 and 4, <c>WithAny</c> at 2, <c>WithNone</c> at 1 — and
///         <c>ForEachWithEntity</c>, all sixteen arities of it, by nothing at all.
///     </para>
///     <para>
///         ⚠ <b>This is deliberately a drive and not a coverage floor.</b>
///         <c>docs/plan/12</c> § "Coverage, reported and not gated" refuses the percentage — a
///         collector that fails to attach reports 0 % or 100 % and in neither case says the
///         instrument is dead — and says what should stand in its place here: an executable claim
///         that a named path is exercised, which is a test rather than a threshold. This is that
///         claim, and it fails by naming the arity rather than by a number drifting.
///     </para>
///     <para>
///         ⚠ <b>What the compiler cannot catch here is why the drive has to run.</b> Most ways of
///         transposing a column in a generated loop are type errors, because the sixteen type
///         parameters are distinct types. What is not a type error is an index: the loop bound, the
///         <c>Unsafe.Add</c> offset, and the entity handle taken alongside the components are all
///         plain integers, and an arity nothing calls is an arity in which none of them has ever
///         been evaluated.
///     </para>
/// </remarks>
public sealed class QueryAritySurfaceTests {
    /// <summary>The value slot <c>index</c> is created with, before anything bumps it.</summary>
    const int StartsAt = 100;

    /// <summary>
    ///     ⚠ The instrument, and it is checked before anything else: the surface is reached by
    ///     reflection, so a renamed family or a namespace that moved would otherwise leave every
    ///     assertion below comparing two empty sequences and calling it agreement.
    /// </summary>
    [Fact]
    public void TheGeneratedSurfaceIsThereToBeCounted() {
        Assert.NotEmpty(IterationMethods("Query", genericParametersBeforeTheComponents: 0));
        Assert.NotEmpty(BuilderMethods("WithAll"));
        Assert.NotNull(GeneratedType("QueryAction", 1));
    }

    /// <summary>
    ///     Every one of the four iteration families is emitted at every arity from one to
    ///     <see cref="ArityDrive.MaxArity" />, and at no arity beyond it.
    /// </summary>
    /// <remarks>
    ///     The upper bound is the half that matters. <c>AritySurface.cs</c> is written by hand
    ///     against sixteen, so raising <c>QueryArityGenerator.MaxArity</c> without extending it
    ///     would leave the new arities driven by nothing and every assertion here still green. This
    ///     is what makes that impossible.
    /// </remarks>
    [Fact]
    public void EveryIterationFamilyIsEmittedAtEveryArityTheDriveCovers() {
        var expected = Enumerable.Range(1, ArityDrive.MaxArity).ToArray();

        Assert.Equal(expected, Arities("Query", genericParametersBeforeTheComponents: 0));
        Assert.Equal(expected, Arities("QueryWithEntity", genericParametersBeforeTheComponents: 0));
        Assert.Equal(expected, Arities("ForEach", genericParametersBeforeTheComponents: 1));
        Assert.Equal(expected, Arities("ForEachWithEntity", genericParametersBeforeTheComponents: 1));
    }

    /// <summary>The same claim for the four description builders and the four generated type families.</summary>
    [Fact]
    public void EveryBuilderAndEveryGeneratedTypeIsEmittedAtEveryArityTheDriveCovers() {
        var expected = Enumerable.Range(1, ArityDrive.MaxArity).ToArray();

        foreach (var family in new[] { "WithAll", "WithAny", "WithNone", "WithChanged" }) {
            Assert.Equal(
                expected,
                BuilderMethods(family).Select(method => method.GetGenericArguments().Length).Order()
            );
        }

        foreach (var family in new[] { "QueryAction", "QueryEntityAction", "IForEach", "IForEachWithEntity" }) {
            foreach (var arity in expected) {
                Assert.NotNull(GeneratedType(family, arity));
            }

            Assert.Null(GeneratedType(family, ArityDrive.MaxArity + 1));
        }
    }

    /// <summary>
    ///     Runs all sixty-four generated iteration methods over one entity and checks the arithmetic
    ///     they leave behind.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Slot <c>index</c> is a column of every arity greater than <c>index</c> — sixteen of
    ///         them less <c>index</c> — in each of four families, so its value is a closed form
    ///         rather than a number read off a run. A column delivered twice, or not at all, leaves
    ///         exactly one slot off by exactly the number of times it was wrong.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Three entities rather than one, and that is not decoration.</b> With a single
    ///         row every offset into the chunk is zero, so a loop that walked the same row <c>n</c>
    ///         times would leave the arithmetic exactly right. Distinct seeds per entity are what
    ///         make the row index observable.
    ///     </para>
    /// </remarks>
    [Fact]
    public void EveryArityOfEveryIterationFamilyReachesEveryColumn() {
        using var world = new World();
        var seeds = new[] { 100, 1_000, 10_000 };
        var entities = seeds.Select(seed => Populate(world, seed)).ToArray();

        var seen = new List<Entity>();
        var spy = new Spy { Seen = seen };

        ArityDrive.RunEveryArity(world, ref spy);

        foreach (var (entity, seed) in entities.Zip(seeds)) {
            Assert.Equal(Expected(seed, 0), world.Read<Slot0>(entity).Value);
            Assert.Equal(Expected(seed, 1), world.Read<Slot1>(entity).Value);
            Assert.Equal(Expected(seed, 2), world.Read<Slot2>(entity).Value);
            Assert.Equal(Expected(seed, 3), world.Read<Slot3>(entity).Value);
            Assert.Equal(Expected(seed, 4), world.Read<Slot4>(entity).Value);
            Assert.Equal(Expected(seed, 5), world.Read<Slot5>(entity).Value);
            Assert.Equal(Expected(seed, 6), world.Read<Slot6>(entity).Value);
            Assert.Equal(Expected(seed, 7), world.Read<Slot7>(entity).Value);
            Assert.Equal(Expected(seed, 8), world.Read<Slot8>(entity).Value);
            Assert.Equal(Expected(seed, 9), world.Read<Slot9>(entity).Value);
            Assert.Equal(Expected(seed, 10), world.Read<Slot10>(entity).Value);
            Assert.Equal(Expected(seed, 11), world.Read<Slot11>(entity).Value);
            Assert.Equal(Expected(seed, 12), world.Read<Slot12>(entity).Value);
            Assert.Equal(Expected(seed, 13), world.Read<Slot13>(entity).Value);
            Assert.Equal(Expected(seed, 14), world.Read<Slot14>(entity).Value);
            Assert.Equal(Expected(seed, 15), world.Read<Slot15>(entity).Value);
        }

        // The two entity-carrying families, once per arity each, over every row — which is the one
        // column the components cannot vouch for, because nothing writes through it.
        Assert.Equal(2 * ArityDrive.MaxArity * entities.Length, seen.Count);

        foreach (var entity in entities) {
            Assert.Equal(2 * ArityDrive.MaxArity, seen.Count(handed => handed == entity));
        }
    }

    /// <summary>Every arity of every description builder builds a description that behaves like its name.</summary>
    [Fact]
    public void EveryArityOfEveryBuilderDescribesWhatItIsNamedAfter() {
        using var world = new World();
        Populate(world, StartsAt);

        var built = ArityDrive.EveryDescription();
        Assert.Equal(4 * ArityDrive.MaxArity, built.Count);

        foreach (var (family, arity, description) in built) {
            var matched = world.Query(description).EntityCount;
            var what = $"{family} at arity {arity}";

            switch (family) {
                case "WithNone":
                    // The entity has all sixteen, so any subset of them excludes it.
                    Assert.True(matched == 0, $"{what} matched {matched} entities and should have matched none.");
                    break;

                case "WithChanged":
                    Assert.True(description.HasChangeFilter, $"{what} set no change filter.");
                    Assert.Equal(arity, description.ChangedComponents.Count);
                    goto default;

                default:
                    Assert.True(matched == 1, $"{what} matched {matched} entities and should have matched one.");
                    break;
            }
        }
    }

    /// <summary>Creates one entity carrying all sixteen slots, each seeded to a value of its own.</summary>
    /// <param name="world">The world.</param>
    /// <param name="seed">What slot zero starts at; slot <c>index</c> starts <c>index</c> above it.</param>
    /// <returns>The entity.</returns>
    static Entity Populate(World world, int seed) {
        var entity = world.Create();

        world.Add(entity, new Slot0 { Value = seed + 0 });
        world.Add(entity, new Slot1 { Value = seed + 1 });
        world.Add(entity, new Slot2 { Value = seed + 2 });
        world.Add(entity, new Slot3 { Value = seed + 3 });
        world.Add(entity, new Slot4 { Value = seed + 4 });
        world.Add(entity, new Slot5 { Value = seed + 5 });
        world.Add(entity, new Slot6 { Value = seed + 6 });
        world.Add(entity, new Slot7 { Value = seed + 7 });
        world.Add(entity, new Slot8 { Value = seed + 8 });
        world.Add(entity, new Slot9 { Value = seed + 9 });
        world.Add(entity, new Slot10 { Value = seed + 10 });
        world.Add(entity, new Slot11 { Value = seed + 11 });
        world.Add(entity, new Slot12 { Value = seed + 12 });
        world.Add(entity, new Slot13 { Value = seed + 13 });
        world.Add(entity, new Slot14 { Value = seed + 14 });
        world.Add(entity, new Slot15 { Value = seed + 15 });

        return entity;
    }

    /// <summary>What slot <paramref name="index" /> is worth once the drive has run.</summary>
    /// <param name="seed">The entity's seed.</param>
    /// <param name="index">Which slot.</param>
    /// <returns>Its starting value plus one bump per family per arity that includes it.</returns>
    static int Expected(int seed, int index) => seed + index + (4 * (ArityDrive.MaxArity - index));

    /// <summary>The overloads of one generated iteration family, by name.</summary>
    /// <param name="family">The method name, e.g. <c>ForEachWithEntity</c>.</param>
    /// <param name="genericParametersBeforeTheComponents">One for the visitor families, none for the delegate ones.</param>
    /// <returns>The overloads.</returns>
    static MethodInfo[] IterationMethods(string family, int genericParametersBeforeTheComponents) =>
        [
            .. typeof(WorldQueryExtensions)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.Name == family
                    && method.GetGenericArguments().Length > genericParametersBeforeTheComponents
                )
        ];

    /// <summary>The arities one iteration family is emitted at, in order.</summary>
    /// <param name="family">The method name.</param>
    /// <param name="genericParametersBeforeTheComponents">One for the visitor families, none for the delegate ones.</param>
    /// <returns>The arities.</returns>
    static int[] Arities(string family, int genericParametersBeforeTheComponents) =>
        [
            .. IterationMethods(family, genericParametersBeforeTheComponents)
                .Select(method => method.GetGenericArguments().Length - genericParametersBeforeTheComponents)
                .Order()
        ];

    /// <summary>The generic overloads of one description builder, by name.</summary>
    /// <param name="family">The method name, e.g. <c>WithChanged</c>.</param>
    /// <returns>The overloads.</returns>
    static MethodInfo[] BuilderMethods(string family) =>
        [
            .. typeof(QueryDescription)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(method => method.Name == family && method.IsGenericMethodDefinition)
        ];

    /// <summary>One generated delegate or interface, by family and arity.</summary>
    /// <param name="family">The unmangled name, e.g. <c>IForEach</c>.</param>
    /// <param name="arity">How many component type parameters.</param>
    /// <returns>The type, or null when the generator does not emit it.</returns>
    static Type? GeneratedType(string family, int arity) =>
        typeof(QueryDescription).Assembly.GetType($"Vixen.Ecs.{family}`{arity}");
}
