// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Vixen.Core;
using Xunit;

namespace Vixen.Ecs.Tests;

/// <summary>
///     Every arity of the generated query surface, run rather than counted.
/// </summary>
/// <remarks>
///     <para>
///         <c>docs/plan/12</c> § "Coverage, reported and not gated" refuses a percentage floor and
///         says what should stand in its place: <i>an executable claim that a named path is
///         exercised, which is a test and not a threshold</i>. This is that claim for the first of
///         the three places [#338](https://github.com/Rikarin/Vixen/issues/338) names — the query
///         surface — and it is two halves that need each other.
///     </para>
///     <para>
///         ⚠️ <b>The surface is about six hundred generated members and the suite was exercising one
///         arity of one family.</b> <c>QueryArityGenerator</c> emits sixteen arities of four
///         iteration families and four description builders — "roughly two thousand lines of code
///         whose only variable is a number", as its own remarks put it — and before this file the
///         whole of <c>Vixen.Ecs.Tests</c> called exactly one of them,
///         <c>ForEach&lt;SumHealth, Health&gt;</c>. A transposed index at arity nine would have been
///         found by a game, months later.
///     </para>
///     <para>
///         So <see cref="EveryArityOfEveryFamilyRunsOverTheRightColumns" /> runs all of them, and
///         <see cref="TheSuiteExercisesEveryArityTheGeneratorEmits" /> reads this assembly's own IL
///         back to check that it did. The second is what keeps the first honest when
///         <c>MaxArity</c> moves or a fifth family arrives: it enumerates the surface from the
///         compiled type rather than from a list somebody has to remember to extend, and names the
///         members nothing calls.
///     </para>
///     <para>
///         ⚠️ <b>What the pair does not prove.</b> The IL check sees a call, not an assertion — it
///         cannot tell a body that checks something from a body that merely runs. That is why it is
///         paired with a sweep whose every arity has a closed-form expectation rather than being
///         wired to a coverage percentage: the number of touches each component must have received
///         is fixed by the shape of the sweep and nothing else, so a loop that visits the wrong
///         entity, visits it twice or stops early lands somewhere the arithmetic says it should not.
///     </para>
/// </remarks>
public sealed class QueryAritySweepTests {
    const int Entities = 3;
    const int MaxArity = 16;

    /// <summary>
    ///     Runs every arity of all four iteration families and checks the arithmetic they leave.
    /// </summary>
    /// <remarks>
    ///     Each family visits components <c>A0..A(n-1)</c> at arity <c>n</c>, so <c>Ai</c> is visited
    ///     by the arities above it — <c>MaxArity - i</c> of them — once per family and once per
    ///     entity. ⚠️ That is the whole point of counting rather than asserting a flag: a loop that
    ///     runs over a chunk twice, or over the first entity <c>count</c> times, passes any "was it
    ///     called" check and fails this one.
    /// </remarks>
    [Fact]
    public void EveryArityOfEveryFamilyRunsOverTheRightColumns() {
        using var world = new World();
        var entities = new Entity[Entities];

        for (var index = 0; index < Entities; index++) {
            entities[index] = world.Create();
            Attach(world, entities[index], index);
        }

        Sweep(world, entities);

        // Four families, and Ai is in every arity above i.
        for (var index = 0; index < MaxArity; index++) {
            var expected = 4 * (MaxArity - index);

            foreach (var entity in entities) {
                Assert.Equal(expected, TouchesOf(world, entity, index));
            }
        }
    }

    /// <summary>
    ///     Every generated arity of every generated family is called by this assembly.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Read out of this assembly's metadata rather than recorded by the sweep, because a
    ///         list the sweep maintains by hand is a list that can say a call happened that did not.
    ///         Every generic call site leaves a <c>MethodSpecification</c> whose instantiation blob
    ///         begins with the number of type arguments, which is the arity — plus one for the
    ///         visitor families, whose first type parameter is the visitor.
    ///     </para>
    ///     <para>
    ///         ⚠️ <b>It fails closed.</b> An assembly it cannot open, a surface that reflects as
    ///         empty and a call set that comes back empty are each a failure naming itself, because
    ///         all three are the shapes in which this check would otherwise pass by measuring
    ///         nothing.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheSuiteExercisesEveryArityTheGeneratorEmits() {
        var surface = Surface();
        var called = CalledHere();

        Assert.NotEmpty(surface);
        Assert.NotEmpty(called);

        var missing = surface.Where(member => !called.Contains(member))
            .OrderBy(member => member.Type, StringComparer.Ordinal)
            .ThenBy(member => member.Name, StringComparer.Ordinal)
            .ThenBy(member => member.Arity)
            .Select(member => $"{member.Type}.{member.Name}<{member.Arity}>")
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"{missing.Count} generated query member(s) are called by nothing in this assembly, so "
            + $"nothing here would notice if the generator emitted them wrong: {string.Join(", ", missing)}"
        );
    }

    /// <summary>The generated members, taken from the compiled types rather than from a list.</summary>
    /// <returns>Each declaring type, member name and generic arity.</returns>
    static HashSet<(string Type, string Name, int Arity)> Surface() {
        var members = new HashSet<(string, string, int)>();

        foreach (var type in new[] { typeof(WorldQueryExtensions), typeof(QueryDescription) }) {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)) {
                if (method.IsGenericMethodDefinition) {
                    members.Add((type.Name, method.Name, method.GetGenericArguments().Length));
                }
            }
        }

        return members;
    }

    /// <summary>Every generic instantiation this assembly's IL names, as type, member and arity.</summary>
    /// <returns>The call set.</returns>
    static HashSet<(string Type, string Name, int Arity)> CalledHere() {
        var location = typeof(QueryAritySweepTests).Assembly.Location;

        Assert.True(File.Exists(location), $"this assembly is not a file on disk, so its IL cannot be read: {location}");

        var called = new HashSet<(string, string, int)>();

        using var stream = File.OpenRead(location);
        using var portable = new PEReader(stream);
        var metadata = portable.GetMetadataReader();

        // ⚠ MetadataReader exposes no MethodSpecifications collection, unlike almost every other
        // table, so the rows are walked by index. They are one-based, per ECMA-335.
        for (var row = 1; row <= metadata.GetTableRowCount(TableIndex.MethodSpec); row++) {
            var specification = metadata.GetMethodSpecification(MetadataTokens.MethodSpecificationHandle(row));

            if (specification.Method.Kind != HandleKind.MemberReference) {
                continue;
            }

            var member = metadata.GetMemberReference((MemberReferenceHandle)specification.Method);

            if (member.Parent.Kind != HandleKind.TypeReference) {
                continue;
            }

            var declaring = metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)member.Parent).Name);
            called.Add((declaring, metadata.GetString(member.Name), ArityOf(metadata, specification.Signature)));
        }

        return called;
    }

    /// <summary>How many type arguments an instantiation blob carries.</summary>
    /// <param name="metadata">The reader.</param>
    /// <param name="signature">The instantiation blob.</param>
    /// <returns>The generic arity.</returns>
    /// <remarks>
    ///     ECMA-335 II.23.2.15: the blob is the <c>GENERICINST</c> byte 0x0A and then a compressed
    ///     count. Reading only the count is deliberate — the type arguments themselves would have to
    ///     be resolved against the reference table to say anything, and the question here is how
    ///     many there were.
    /// </remarks>
    static int ArityOf(MetadataReader metadata, BlobHandle signature) {
        var blob = metadata.GetBlobReader(signature);
        blob.ReadSignatureHeader();

        return blob.ReadCompressedInteger();
    }

    /// <summary>How many times component <paramref name="index" /> on an entity has been visited.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="index">Which of the sweep's components.</param>
    /// <returns>The count the sweep left behind.</returns>
    static int TouchesOf(World world, Entity entity, int index) => index switch {
        0 => world.Get<A0>(entity).Touches,
        1 => world.Get<A1>(entity).Touches,
        2 => world.Get<A2>(entity).Touches,
        3 => world.Get<A3>(entity).Touches,
        4 => world.Get<A4>(entity).Touches,
        5 => world.Get<A5>(entity).Touches,
        6 => world.Get<A6>(entity).Touches,
        7 => world.Get<A7>(entity).Touches,
        8 => world.Get<A8>(entity).Touches,
        9 => world.Get<A9>(entity).Touches,
        10 => world.Get<A10>(entity).Touches,
        11 => world.Get<A11>(entity).Touches,
        12 => world.Get<A12>(entity).Touches,
        13 => world.Get<A13>(entity).Touches,
        14 => world.Get<A14>(entity).Touches,
        15 => world.Get<A15>(entity).Touches,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    /// <summary>Gives an entity every one of the sweep's components, each knowing whose it is.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="owner">The entity's index, which the entity-carrying families check against.</param>
    static void Attach(World world, Entity entity, int owner) {
        world.Add(entity, new A0 { Owner = owner });
        world.Add(entity, new A1 { Owner = owner });
        world.Add(entity, new A2 { Owner = owner });
        world.Add(entity, new A3 { Owner = owner });
        world.Add(entity, new A4 { Owner = owner });
        world.Add(entity, new A5 { Owner = owner });
        world.Add(entity, new A6 { Owner = owner });
        world.Add(entity, new A7 { Owner = owner });
        world.Add(entity, new A8 { Owner = owner });
        world.Add(entity, new A9 { Owner = owner });
        world.Add(entity, new A10 { Owner = owner });
        world.Add(entity, new A11 { Owner = owner });
        world.Add(entity, new A12 { Owner = owner });
        world.Add(entity, new A13 { Owner = owner });
        world.Add(entity, new A14 { Owner = owner });
        world.Add(entity, new A15 { Owner = owner });
    }

    /// <summary>One call into every arity of every family, and the description builders that aim them.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entities">The entities, in the order the entity-carrying families expect.</param>
    /// <remarks>
    ///     ⚠️ The four description builders are asserted rather than merely called: an arity whose
    ///     <c>WithAll</c> dropped a type argument would match more than it should, and one whose
    ///     <c>WithNone</c> did would match anything at all. Constructing a description and looking
    ///     at nothing would raise a coverage number and catch neither.
    /// </remarks>
    static void Sweep(World world, Entity[] entities) {
        var visitor = default(Visits);
        var visitorWithEntity = new VisitsWithEntity(entities);

        var all1 = new QueryDescription().WithAll<A0>();
        Assert.Equal(Entities, world.Query(all1).EntityCount);
        Assert.Equal(Entities, world.Query(new QueryDescription().WithAny<A0>()).EntityCount);
        Assert.Equal(0, world.Query(new QueryDescription().WithNone<A0>()).EntityCount);
        Assert.True(new QueryDescription().WithChanged<A0>().HasChangeFilter);
        world.Query(all1, (ref A0 a0) => { a0.Touches++; });
        world.QueryWithEntity(all1, (Entity entity, ref A0 a0) => { Assert.Equal(entities[a0.Owner], entity); a0.Touches++; });
        world.ForEach<Visits, A0>(all1, ref visitor);
        world.ForEachWithEntity<VisitsWithEntity, A0>(all1, ref visitorWithEntity);

        var all2 = new QueryDescription().WithAll<A0, A1>();
        Assert.Equal(Entities, world.Query(all2).EntityCount);
        Assert.Equal(Entities, world.Query(new QueryDescription().WithAny<A0, A1>()).EntityCount);
        Assert.Equal(0, world.Query(new QueryDescription().WithNone<A0, A1>()).EntityCount);
        Assert.True(new QueryDescription().WithChanged<A0, A1>().HasChangeFilter);
        world.Query(all2, (ref A0 a0, ref A1 a1) => { a0.Touches++; a1.Touches++; });
        world.QueryWithEntity(all2, (Entity entity, ref A0 a0, ref A1 a1) => { Assert.Equal(entities[a0.Owner], entity); a0.Touches++; a1.Touches++; });
        world.ForEach<Visits, A0, A1>(all2, ref visitor);
        world.ForEachWithEntity<VisitsWithEntity, A0, A1>(all2, ref visitorWithEntity);

        var all3 = new QueryDescription().WithAll<A0, A1, A2>();
        Assert.Equal(Entities, world.Query(all3).EntityCount);
        Assert.Equal(Entities, world.Query(new QueryDescription().WithAny<A0, A1, A2>()).EntityCount);
        Assert.Equal(0, world.Query(new QueryDescription().WithNone<A0, A1, A2>()).EntityCount);
        Assert.True(new QueryDescription().WithChanged<A0, A1, A2>().HasChangeFilter);
        world.Query(all3, (ref A0 a0, ref A1 a1, ref A2 a2) => { a0.Touches++; a1.Touches++; a2.Touches++; });
        world.QueryWithEntity(all3, (Entity entity, ref A0 a0, ref A1 a1, ref A2 a2) => { Assert.Equal(entities[a0.Owner], entity); a0.Touches++; a1.Touches++; a2.Touches++; });
        world.ForEach<Visits, A0, A1, A2>(all3, ref visitor);
        world.ForEachWithEntity<VisitsWithEntity, A0, A1, A2>(all3, ref visitorWithEntity);

        var all4 = new QueryDescription().WithAll<A0, A1, A2, A3>();
        Assert.Equal(Entities, world.Query(all4).EntityCount);
        Assert.Equal(Entities, world.Query(new QueryDescription().WithAny<A0, A1, A2, A3>()).EntityCount);
        Assert.Equal(0, world.Query(new QueryDescription().WithNone<A0, A1, A2, A3>()).EntityCount);
        Assert.True(new QueryDescription().WithChanged<A0, A1, A2, A3>().HasChangeFilter);
        world.Query(all4, (ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3) => { a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; });
        world.QueryWithEntity(all4, (Entity entity, ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3) => { Assert.Equal(entities[a0.Owner], entity); a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; });
        world.ForEach<Visits, A0, A1, A2, A3>(all4, ref visitor);
        world.ForEachWithEntity<VisitsWithEntity, A0, A1, A2, A3>(all4, ref visitorWithEntity);

        var all5 = new QueryDescription().WithAll<A0, A1, A2, A3, A4>();
        Assert.Equal(Entities, world.Query(all5).EntityCount);
        Assert.Equal(Entities, world.Query(new QueryDescription().WithAny<A0, A1, A2, A3, A4>()).EntityCount);
        Assert.Equal(0, world.Query(new QueryDescription().WithNone<A0, A1, A2, A3, A4>()).EntityCount);
        Assert.True(new QueryDescription().WithChanged<A0, A1, A2, A3, A4>().HasChangeFilter);
        world.Query(all5, (ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4) => { a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; });
        world.QueryWithEntity(all5, (Entity entity, ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4) => { Assert.Equal(entities[a0.Owner], entity); a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; });
        world.ForEach<Visits, A0, A1, A2, A3, A4>(all5, ref visitor);
        world.ForEachWithEntity<VisitsWithEntity, A0, A1, A2, A3, A4>(all5, ref visitorWithEntity);

        var all6 = new QueryDescription().WithAll<A0, A1, A2, A3, A4, A5>();
        Assert.Equal(Entities, world.Query(all6).EntityCount);
        Assert.Equal(Entities, world.Query(new QueryDescription().WithAny<A0, A1, A2, A3, A4, A5>()).EntityCount);
        Assert.Equal(0, world.Query(new QueryDescription().WithNone<A0, A1, A2, A3, A4, A5>()).EntityCount);
        Assert.True(new QueryDescription().WithChanged<A0, A1, A2, A3, A4, A5>().HasChangeFilter);
        world.Query(all6, (ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5) => { a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; });
        world.QueryWithEntity(all6, (Entity entity, ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5) => { Assert.Equal(entities[a0.Owner], entity); a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; });
        world.ForEach<Visits, A0, A1, A2, A3, A4, A5>(all6, ref visitor);
        world.ForEachWithEntity<VisitsWithEntity, A0, A1, A2, A3, A4, A5>(all6, ref visitorWithEntity);

        var all7 = new QueryDescription().WithAll<A0, A1, A2, A3, A4, A5, A6>();
        Assert.Equal(Entities, world.Query(all7).EntityCount);
        Assert.Equal(Entities, world.Query(new QueryDescription().WithAny<A0, A1, A2, A3, A4, A5, A6>()).EntityCount);
        Assert.Equal(0, world.Query(new QueryDescription().WithNone<A0, A1, A2, A3, A4, A5, A6>()).EntityCount);
        Assert.True(new QueryDescription().WithChanged<A0, A1, A2, A3, A4, A5, A6>().HasChangeFilter);
        world.Query(all7, (ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6) => { a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; });
        world.QueryWithEntity(all7, (Entity entity, ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6) => { Assert.Equal(entities[a0.Owner], entity); a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; });
        world.ForEach<Visits, A0, A1, A2, A3, A4, A5, A6>(all7, ref visitor);
        world.ForEachWithEntity<VisitsWithEntity, A0, A1, A2, A3, A4, A5, A6>(all7, ref visitorWithEntity);

        var all8 = new QueryDescription().WithAll<A0, A1, A2, A3, A4, A5, A6, A7>();
        Assert.Equal(Entities, world.Query(all8).EntityCount);
        Assert.Equal(Entities, world.Query(new QueryDescription().WithAny<A0, A1, A2, A3, A4, A5, A6, A7>()).EntityCount);
        Assert.Equal(0, world.Query(new QueryDescription().WithNone<A0, A1, A2, A3, A4, A5, A6, A7>()).EntityCount);
        Assert.True(new QueryDescription().WithChanged<A0, A1, A2, A3, A4, A5, A6, A7>().HasChangeFilter);
        world.Query(all8, (ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6, ref A7 a7) => { a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; a7.Touches++; });
        world.QueryWithEntity(all8, (Entity entity, ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6, ref A7 a7) => { Assert.Equal(entities[a0.Owner], entity); a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; a7.Touches++; });
        world.ForEach<Visits, A0, A1, A2, A3, A4, A5, A6, A7>(all8, ref visitor);
        world.ForEachWithEntity<VisitsWithEntity, A0, A1, A2, A3, A4, A5, A6, A7>(all8, ref visitorWithEntity);

        var all9 = new QueryDescription().WithAll<A0, A1, A2, A3, A4, A5, A6, A7, A8>();
        Assert.Equal(Entities, world.Query(all9).EntityCount);
        Assert.Equal(Entities, world.Query(new QueryDescription().WithAny<A0, A1, A2, A3, A4, A5, A6, A7, A8>()).EntityCount);
        Assert.Equal(0, world.Query(new QueryDescription().WithNone<A0, A1, A2, A3, A4, A5, A6, A7, A8>()).EntityCount);
        Assert.True(new QueryDescription().WithChanged<A0, A1, A2, A3, A4, A5, A6, A7, A8>().HasChangeFilter);
        world.Query(all9, (ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6, ref A7 a7, ref A8 a8) => { a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; a7.Touches++; a8.Touches++; });
        world.QueryWithEntity(all9, (Entity entity, ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6, ref A7 a7, ref A8 a8) => { Assert.Equal(entities[a0.Owner], entity); a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; a7.Touches++; a8.Touches++; });
        world.ForEach<Visits, A0, A1, A2, A3, A4, A5, A6, A7, A8>(all9, ref visitor);
        world.ForEachWithEntity<VisitsWithEntity, A0, A1, A2, A3, A4, A5, A6, A7, A8>(all9, ref visitorWithEntity);

        var all10 = new QueryDescription().WithAll<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9>();
        Assert.Equal(Entities, world.Query(all10).EntityCount);
        Assert.Equal(Entities, world.Query(new QueryDescription().WithAny<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9>()).EntityCount);
        Assert.Equal(0, world.Query(new QueryDescription().WithNone<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9>()).EntityCount);
        Assert.True(new QueryDescription().WithChanged<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9>().HasChangeFilter);
        world.Query(all10, (ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6, ref A7 a7, ref A8 a8, ref A9 a9) => { a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; a7.Touches++; a8.Touches++; a9.Touches++; });
        world.QueryWithEntity(all10, (Entity entity, ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6, ref A7 a7, ref A8 a8, ref A9 a9) => { Assert.Equal(entities[a0.Owner], entity); a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; a7.Touches++; a8.Touches++; a9.Touches++; });
        world.ForEach<Visits, A0, A1, A2, A3, A4, A5, A6, A7, A8, A9>(all10, ref visitor);
        world.ForEachWithEntity<VisitsWithEntity, A0, A1, A2, A3, A4, A5, A6, A7, A8, A9>(all10, ref visitorWithEntity);

        var all11 = new QueryDescription().WithAll<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10>();
        Assert.Equal(Entities, world.Query(all11).EntityCount);
        Assert.Equal(Entities, world.Query(new QueryDescription().WithAny<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10>()).EntityCount);
        Assert.Equal(0, world.Query(new QueryDescription().WithNone<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10>()).EntityCount);
        Assert.True(new QueryDescription().WithChanged<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10>().HasChangeFilter);
        world.Query(all11, (ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6, ref A7 a7, ref A8 a8, ref A9 a9, ref A10 a10) => { a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; a7.Touches++; a8.Touches++; a9.Touches++; a10.Touches++; });
        world.QueryWithEntity(all11, (Entity entity, ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6, ref A7 a7, ref A8 a8, ref A9 a9, ref A10 a10) => { Assert.Equal(entities[a0.Owner], entity); a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; a7.Touches++; a8.Touches++; a9.Touches++; a10.Touches++; });
        world.ForEach<Visits, A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10>(all11, ref visitor);
        world.ForEachWithEntity<VisitsWithEntity, A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10>(all11, ref visitorWithEntity);

        var all12 = new QueryDescription().WithAll<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11>();
        Assert.Equal(Entities, world.Query(all12).EntityCount);
        Assert.Equal(Entities, world.Query(new QueryDescription().WithAny<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11>()).EntityCount);
        Assert.Equal(0, world.Query(new QueryDescription().WithNone<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11>()).EntityCount);
        Assert.True(new QueryDescription().WithChanged<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11>().HasChangeFilter);
        world.Query(all12, (ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6, ref A7 a7, ref A8 a8, ref A9 a9, ref A10 a10, ref A11 a11) => { a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; a7.Touches++; a8.Touches++; a9.Touches++; a10.Touches++; a11.Touches++; });
        world.QueryWithEntity(all12, (Entity entity, ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6, ref A7 a7, ref A8 a8, ref A9 a9, ref A10 a10, ref A11 a11) => { Assert.Equal(entities[a0.Owner], entity); a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; a7.Touches++; a8.Touches++; a9.Touches++; a10.Touches++; a11.Touches++; });
        world.ForEach<Visits, A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11>(all12, ref visitor);
        world.ForEachWithEntity<VisitsWithEntity, A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11>(all12, ref visitorWithEntity);

        var all13 = new QueryDescription().WithAll<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12>();
        Assert.Equal(Entities, world.Query(all13).EntityCount);
        Assert.Equal(Entities, world.Query(new QueryDescription().WithAny<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12>()).EntityCount);
        Assert.Equal(0, world.Query(new QueryDescription().WithNone<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12>()).EntityCount);
        Assert.True(new QueryDescription().WithChanged<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12>().HasChangeFilter);
        world.Query(all13, (ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6, ref A7 a7, ref A8 a8, ref A9 a9, ref A10 a10, ref A11 a11, ref A12 a12) => { a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; a7.Touches++; a8.Touches++; a9.Touches++; a10.Touches++; a11.Touches++; a12.Touches++; });
        world.QueryWithEntity(all13, (Entity entity, ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6, ref A7 a7, ref A8 a8, ref A9 a9, ref A10 a10, ref A11 a11, ref A12 a12) => { Assert.Equal(entities[a0.Owner], entity); a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; a7.Touches++; a8.Touches++; a9.Touches++; a10.Touches++; a11.Touches++; a12.Touches++; });
        world.ForEach<Visits, A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12>(all13, ref visitor);
        world.ForEachWithEntity<VisitsWithEntity, A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12>(all13, ref visitorWithEntity);

        var all14 = new QueryDescription().WithAll<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13>();
        Assert.Equal(Entities, world.Query(all14).EntityCount);
        Assert.Equal(Entities, world.Query(new QueryDescription().WithAny<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13>()).EntityCount);
        Assert.Equal(0, world.Query(new QueryDescription().WithNone<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13>()).EntityCount);
        Assert.True(new QueryDescription().WithChanged<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13>().HasChangeFilter);
        world.Query(all14, (ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6, ref A7 a7, ref A8 a8, ref A9 a9, ref A10 a10, ref A11 a11, ref A12 a12, ref A13 a13) => { a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; a7.Touches++; a8.Touches++; a9.Touches++; a10.Touches++; a11.Touches++; a12.Touches++; a13.Touches++; });
        world.QueryWithEntity(all14, (Entity entity, ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6, ref A7 a7, ref A8 a8, ref A9 a9, ref A10 a10, ref A11 a11, ref A12 a12, ref A13 a13) => { Assert.Equal(entities[a0.Owner], entity); a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; a7.Touches++; a8.Touches++; a9.Touches++; a10.Touches++; a11.Touches++; a12.Touches++; a13.Touches++; });
        world.ForEach<Visits, A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13>(all14, ref visitor);
        world.ForEachWithEntity<VisitsWithEntity, A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13>(all14, ref visitorWithEntity);

        var all15 = new QueryDescription().WithAll<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13, A14>();
        Assert.Equal(Entities, world.Query(all15).EntityCount);
        Assert.Equal(Entities, world.Query(new QueryDescription().WithAny<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13, A14>()).EntityCount);
        Assert.Equal(0, world.Query(new QueryDescription().WithNone<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13, A14>()).EntityCount);
        Assert.True(new QueryDescription().WithChanged<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13, A14>().HasChangeFilter);
        world.Query(all15, (ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6, ref A7 a7, ref A8 a8, ref A9 a9, ref A10 a10, ref A11 a11, ref A12 a12, ref A13 a13, ref A14 a14) => { a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; a7.Touches++; a8.Touches++; a9.Touches++; a10.Touches++; a11.Touches++; a12.Touches++; a13.Touches++; a14.Touches++; });
        world.QueryWithEntity(all15, (Entity entity, ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6, ref A7 a7, ref A8 a8, ref A9 a9, ref A10 a10, ref A11 a11, ref A12 a12, ref A13 a13, ref A14 a14) => { Assert.Equal(entities[a0.Owner], entity); a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; a7.Touches++; a8.Touches++; a9.Touches++; a10.Touches++; a11.Touches++; a12.Touches++; a13.Touches++; a14.Touches++; });
        world.ForEach<Visits, A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13, A14>(all15, ref visitor);
        world.ForEachWithEntity<VisitsWithEntity, A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13, A14>(all15, ref visitorWithEntity);

        var all16 = new QueryDescription().WithAll<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13, A14, A15>();
        Assert.Equal(Entities, world.Query(all16).EntityCount);
        Assert.Equal(Entities, world.Query(new QueryDescription().WithAny<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13, A14, A15>()).EntityCount);
        Assert.Equal(0, world.Query(new QueryDescription().WithNone<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13, A14, A15>()).EntityCount);
        Assert.True(new QueryDescription().WithChanged<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13, A14, A15>().HasChangeFilter);
        world.Query(all16, (ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6, ref A7 a7, ref A8 a8, ref A9 a9, ref A10 a10, ref A11 a11, ref A12 a12, ref A13 a13, ref A14 a14, ref A15 a15) => { a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; a7.Touches++; a8.Touches++; a9.Touches++; a10.Touches++; a11.Touches++; a12.Touches++; a13.Touches++; a14.Touches++; a15.Touches++; });
        world.QueryWithEntity(all16, (Entity entity, ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6, ref A7 a7, ref A8 a8, ref A9 a9, ref A10 a10, ref A11 a11, ref A12 a12, ref A13 a13, ref A14 a14, ref A15 a15) => { Assert.Equal(entities[a0.Owner], entity); a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; a7.Touches++; a8.Touches++; a9.Touches++; a10.Touches++; a11.Touches++; a12.Touches++; a13.Touches++; a14.Touches++; a15.Touches++; });
        world.ForEach<Visits, A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13, A14, A15>(all16, ref visitor);
        world.ForEachWithEntity<VisitsWithEntity, A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13, A14, A15>(all16, ref visitorWithEntity);

    }
}

/// <summary>The struct-visitor half of the sweep, at every arity the generator emits.</summary>
/// <remarks>
///     One struct implementing sixteen interfaces rather than sixteen structs: the <c>Execute</c>
///     overloads differ in parameter count, so they do not collide, and a visitor that accumulated
///     would then be one accumulator rather than sixteen.
/// </remarks>
public struct Visits :
    IForEach<A0>,
    IForEach<A0, A1>,
    IForEach<A0, A1, A2>,
    IForEach<A0, A1, A2, A3>,
    IForEach<A0, A1, A2, A3, A4>,
    IForEach<A0, A1, A2, A3, A4, A5>,
    IForEach<A0, A1, A2, A3, A4, A5, A6>,
    IForEach<A0, A1, A2, A3, A4, A5, A6, A7>,
    IForEach<A0, A1, A2, A3, A4, A5, A6, A7, A8>,
    IForEach<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9>,
    IForEach<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10>,
    IForEach<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11>,
    IForEach<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12>,
    IForEach<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13>,
    IForEach<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13, A14>,
    IForEach<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13, A14, A15> {
    /// <summary>Visits 1 component(s).</summary>
    /// <param name="a0">A reference into the chunk.</param>
    public void Execute(ref A0 a0) { a0.Touches++; }

    /// <summary>Visits 2 component(s).</summary>
    /// <param name="a0">A reference into the chunk.</param>
    /// <param name="a1">A reference into the chunk.</param>
    public void Execute(ref A0 a0, ref A1 a1) { a0.Touches++; a1.Touches++; }

    /// <summary>Visits 3 component(s).</summary>
    /// <param name="a0">A reference into the chunk.</param>
    /// <param name="a1">A reference into the chunk.</param>
    /// <param name="a2">A reference into the chunk.</param>
    public void Execute(ref A0 a0, ref A1 a1, ref A2 a2) { a0.Touches++; a1.Touches++; a2.Touches++; }

    /// <summary>Visits 4 component(s).</summary>
    /// <param name="a0">A reference into the chunk.</param>
    /// <param name="a1">A reference into the chunk.</param>
    /// <param name="a2">A reference into the chunk.</param>
    /// <param name="a3">A reference into the chunk.</param>
    public void Execute(ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3) { a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; }

    /// <summary>Visits 5 component(s).</summary>
    /// <param name="a0">A reference into the chunk.</param>
    /// <param name="a1">A reference into the chunk.</param>
    /// <param name="a2">A reference into the chunk.</param>
    /// <param name="a3">A reference into the chunk.</param>
    /// <param name="a4">A reference into the chunk.</param>
    public void Execute(ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4) { a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; }

    /// <summary>Visits 6 component(s).</summary>
    /// <param name="a0">A reference into the chunk.</param>
    /// <param name="a1">A reference into the chunk.</param>
    /// <param name="a2">A reference into the chunk.</param>
    /// <param name="a3">A reference into the chunk.</param>
    /// <param name="a4">A reference into the chunk.</param>
    /// <param name="a5">A reference into the chunk.</param>
    public void Execute(ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5) { a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; }

    /// <summary>Visits 7 component(s).</summary>
    /// <param name="a0">A reference into the chunk.</param>
    /// <param name="a1">A reference into the chunk.</param>
    /// <param name="a2">A reference into the chunk.</param>
    /// <param name="a3">A reference into the chunk.</param>
    /// <param name="a4">A reference into the chunk.</param>
    /// <param name="a5">A reference into the chunk.</param>
    /// <param name="a6">A reference into the chunk.</param>
    public void Execute(ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6) { a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; }

    /// <summary>Visits 8 component(s).</summary>
    /// <param name="a0">A reference into the chunk.</param>
    /// <param name="a1">A reference into the chunk.</param>
    /// <param name="a2">A reference into the chunk.</param>
    /// <param name="a3">A reference into the chunk.</param>
    /// <param name="a4">A reference into the chunk.</param>
    /// <param name="a5">A reference into the chunk.</param>
    /// <param name="a6">A reference into the chunk.</param>
    /// <param name="a7">A reference into the chunk.</param>
    public void Execute(ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6, ref A7 a7) { a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; a7.Touches++; }

    /// <summary>Visits 9 component(s).</summary>
    /// <param name="a0">A reference into the chunk.</param>
    /// <param name="a1">A reference into the chunk.</param>
    /// <param name="a2">A reference into the chunk.</param>
    /// <param name="a3">A reference into the chunk.</param>
    /// <param name="a4">A reference into the chunk.</param>
    /// <param name="a5">A reference into the chunk.</param>
    /// <param name="a6">A reference into the chunk.</param>
    /// <param name="a7">A reference into the chunk.</param>
    /// <param name="a8">A reference into the chunk.</param>
    public void Execute(ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6, ref A7 a7, ref A8 a8) { a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; a7.Touches++; a8.Touches++; }

    /// <summary>Visits 10 component(s).</summary>
    /// <param name="a0">A reference into the chunk.</param>
    /// <param name="a1">A reference into the chunk.</param>
    /// <param name="a2">A reference into the chunk.</param>
    /// <param name="a3">A reference into the chunk.</param>
    /// <param name="a4">A reference into the chunk.</param>
    /// <param name="a5">A reference into the chunk.</param>
    /// <param name="a6">A reference into the chunk.</param>
    /// <param name="a7">A reference into the chunk.</param>
    /// <param name="a8">A reference into the chunk.</param>
    /// <param name="a9">A reference into the chunk.</param>
    public void Execute(ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6, ref A7 a7, ref A8 a8, ref A9 a9) { a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; a7.Touches++; a8.Touches++; a9.Touches++; }

    /// <summary>Visits 11 component(s).</summary>
    /// <param name="a0">A reference into the chunk.</param>
    /// <param name="a1">A reference into the chunk.</param>
    /// <param name="a2">A reference into the chunk.</param>
    /// <param name="a3">A reference into the chunk.</param>
    /// <param name="a4">A reference into the chunk.</param>
    /// <param name="a5">A reference into the chunk.</param>
    /// <param name="a6">A reference into the chunk.</param>
    /// <param name="a7">A reference into the chunk.</param>
    /// <param name="a8">A reference into the chunk.</param>
    /// <param name="a9">A reference into the chunk.</param>
    /// <param name="a10">A reference into the chunk.</param>
    public void Execute(ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6, ref A7 a7, ref A8 a8, ref A9 a9, ref A10 a10) { a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; a7.Touches++; a8.Touches++; a9.Touches++; a10.Touches++; }

    /// <summary>Visits 12 component(s).</summary>
    /// <param name="a0">A reference into the chunk.</param>
    /// <param name="a1">A reference into the chunk.</param>
    /// <param name="a2">A reference into the chunk.</param>
    /// <param name="a3">A reference into the chunk.</param>
    /// <param name="a4">A reference into the chunk.</param>
    /// <param name="a5">A reference into the chunk.</param>
    /// <param name="a6">A reference into the chunk.</param>
    /// <param name="a7">A reference into the chunk.</param>
    /// <param name="a8">A reference into the chunk.</param>
    /// <param name="a9">A reference into the chunk.</param>
    /// <param name="a10">A reference into the chunk.</param>
    /// <param name="a11">A reference into the chunk.</param>
    public void Execute(ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6, ref A7 a7, ref A8 a8, ref A9 a9, ref A10 a10, ref A11 a11) { a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; a7.Touches++; a8.Touches++; a9.Touches++; a10.Touches++; a11.Touches++; }

    /// <summary>Visits 13 component(s).</summary>
    /// <param name="a0">A reference into the chunk.</param>
    /// <param name="a1">A reference into the chunk.</param>
    /// <param name="a2">A reference into the chunk.</param>
    /// <param name="a3">A reference into the chunk.</param>
    /// <param name="a4">A reference into the chunk.</param>
    /// <param name="a5">A reference into the chunk.</param>
    /// <param name="a6">A reference into the chunk.</param>
    /// <param name="a7">A reference into the chunk.</param>
    /// <param name="a8">A reference into the chunk.</param>
    /// <param name="a9">A reference into the chunk.</param>
    /// <param name="a10">A reference into the chunk.</param>
    /// <param name="a11">A reference into the chunk.</param>
    /// <param name="a12">A reference into the chunk.</param>
    public void Execute(ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6, ref A7 a7, ref A8 a8, ref A9 a9, ref A10 a10, ref A11 a11, ref A12 a12) { a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; a7.Touches++; a8.Touches++; a9.Touches++; a10.Touches++; a11.Touches++; a12.Touches++; }

    /// <summary>Visits 14 component(s).</summary>
    /// <param name="a0">A reference into the chunk.</param>
    /// <param name="a1">A reference into the chunk.</param>
    /// <param name="a2">A reference into the chunk.</param>
    /// <param name="a3">A reference into the chunk.</param>
    /// <param name="a4">A reference into the chunk.</param>
    /// <param name="a5">A reference into the chunk.</param>
    /// <param name="a6">A reference into the chunk.</param>
    /// <param name="a7">A reference into the chunk.</param>
    /// <param name="a8">A reference into the chunk.</param>
    /// <param name="a9">A reference into the chunk.</param>
    /// <param name="a10">A reference into the chunk.</param>
    /// <param name="a11">A reference into the chunk.</param>
    /// <param name="a12">A reference into the chunk.</param>
    /// <param name="a13">A reference into the chunk.</param>
    public void Execute(ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6, ref A7 a7, ref A8 a8, ref A9 a9, ref A10 a10, ref A11 a11, ref A12 a12, ref A13 a13) { a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; a7.Touches++; a8.Touches++; a9.Touches++; a10.Touches++; a11.Touches++; a12.Touches++; a13.Touches++; }

    /// <summary>Visits 15 component(s).</summary>
    /// <param name="a0">A reference into the chunk.</param>
    /// <param name="a1">A reference into the chunk.</param>
    /// <param name="a2">A reference into the chunk.</param>
    /// <param name="a3">A reference into the chunk.</param>
    /// <param name="a4">A reference into the chunk.</param>
    /// <param name="a5">A reference into the chunk.</param>
    /// <param name="a6">A reference into the chunk.</param>
    /// <param name="a7">A reference into the chunk.</param>
    /// <param name="a8">A reference into the chunk.</param>
    /// <param name="a9">A reference into the chunk.</param>
    /// <param name="a10">A reference into the chunk.</param>
    /// <param name="a11">A reference into the chunk.</param>
    /// <param name="a12">A reference into the chunk.</param>
    /// <param name="a13">A reference into the chunk.</param>
    /// <param name="a14">A reference into the chunk.</param>
    public void Execute(ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6, ref A7 a7, ref A8 a8, ref A9 a9, ref A10 a10, ref A11 a11, ref A12 a12, ref A13 a13, ref A14 a14) { a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; a7.Touches++; a8.Touches++; a9.Touches++; a10.Touches++; a11.Touches++; a12.Touches++; a13.Touches++; a14.Touches++; }

    /// <summary>Visits 16 component(s).</summary>
    /// <param name="a0">A reference into the chunk.</param>
    /// <param name="a1">A reference into the chunk.</param>
    /// <param name="a2">A reference into the chunk.</param>
    /// <param name="a3">A reference into the chunk.</param>
    /// <param name="a4">A reference into the chunk.</param>
    /// <param name="a5">A reference into the chunk.</param>
    /// <param name="a6">A reference into the chunk.</param>
    /// <param name="a7">A reference into the chunk.</param>
    /// <param name="a8">A reference into the chunk.</param>
    /// <param name="a9">A reference into the chunk.</param>
    /// <param name="a10">A reference into the chunk.</param>
    /// <param name="a11">A reference into the chunk.</param>
    /// <param name="a12">A reference into the chunk.</param>
    /// <param name="a13">A reference into the chunk.</param>
    /// <param name="a14">A reference into the chunk.</param>
    /// <param name="a15">A reference into the chunk.</param>
    public void Execute(ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6, ref A7 a7, ref A8 a8, ref A9 a9, ref A10 a10, ref A11 a11, ref A12 a12, ref A13 a13, ref A14 a14, ref A15 a15) { a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; a7.Touches++; a8.Touches++; a9.Touches++; a10.Touches++; a11.Touches++; a12.Touches++; a13.Touches++; a14.Touches++; a15.Touches++; }

}

/// <summary>The entity-carrying struct-visitor half, which also checks the entity it was handed.</summary>
/// <remarks>
///     ⚠️ The entity reference walks the chunk beside the columns, so a loop that advanced one and
///     not the other would hand out the wrong entity with the right components — a defect no count
///     of visits can see. Every component knows its owner's index, so the visitor can say so.
/// </remarks>
/// <param name="entities">The entities, in creation order.</param>
public struct VisitsWithEntity(Entity[] entities) :
    IForEachWithEntity<A0>,
    IForEachWithEntity<A0, A1>,
    IForEachWithEntity<A0, A1, A2>,
    IForEachWithEntity<A0, A1, A2, A3>,
    IForEachWithEntity<A0, A1, A2, A3, A4>,
    IForEachWithEntity<A0, A1, A2, A3, A4, A5>,
    IForEachWithEntity<A0, A1, A2, A3, A4, A5, A6>,
    IForEachWithEntity<A0, A1, A2, A3, A4, A5, A6, A7>,
    IForEachWithEntity<A0, A1, A2, A3, A4, A5, A6, A7, A8>,
    IForEachWithEntity<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9>,
    IForEachWithEntity<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10>,
    IForEachWithEntity<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11>,
    IForEachWithEntity<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12>,
    IForEachWithEntity<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13>,
    IForEachWithEntity<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13, A14>,
    IForEachWithEntity<A0, A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13, A14, A15> {
    /// <summary>Visits one entity's 1 component(s).</summary>
    /// <param name="entity">The entity the components belong to.</param>
    /// <param name="a0">A reference into the chunk.</param>
    public void Execute(Entity entity, ref A0 a0) {
        Assert.Equal(entities[a0.Owner], entity);
        a0.Touches++;
    }

    /// <summary>Visits one entity's 2 component(s).</summary>
    /// <param name="entity">The entity the components belong to.</param>
    /// <param name="a0">A reference into the chunk.</param>
    /// <param name="a1">A reference into the chunk.</param>
    public void Execute(Entity entity, ref A0 a0, ref A1 a1) {
        Assert.Equal(entities[a0.Owner], entity);
        a0.Touches++; a1.Touches++;
    }

    /// <summary>Visits one entity's 3 component(s).</summary>
    /// <param name="entity">The entity the components belong to.</param>
    /// <param name="a0">A reference into the chunk.</param>
    /// <param name="a1">A reference into the chunk.</param>
    /// <param name="a2">A reference into the chunk.</param>
    public void Execute(Entity entity, ref A0 a0, ref A1 a1, ref A2 a2) {
        Assert.Equal(entities[a0.Owner], entity);
        a0.Touches++; a1.Touches++; a2.Touches++;
    }

    /// <summary>Visits one entity's 4 component(s).</summary>
    /// <param name="entity">The entity the components belong to.</param>
    /// <param name="a0">A reference into the chunk.</param>
    /// <param name="a1">A reference into the chunk.</param>
    /// <param name="a2">A reference into the chunk.</param>
    /// <param name="a3">A reference into the chunk.</param>
    public void Execute(Entity entity, ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3) {
        Assert.Equal(entities[a0.Owner], entity);
        a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++;
    }

    /// <summary>Visits one entity's 5 component(s).</summary>
    /// <param name="entity">The entity the components belong to.</param>
    /// <param name="a0">A reference into the chunk.</param>
    /// <param name="a1">A reference into the chunk.</param>
    /// <param name="a2">A reference into the chunk.</param>
    /// <param name="a3">A reference into the chunk.</param>
    /// <param name="a4">A reference into the chunk.</param>
    public void Execute(Entity entity, ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4) {
        Assert.Equal(entities[a0.Owner], entity);
        a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++;
    }

    /// <summary>Visits one entity's 6 component(s).</summary>
    /// <param name="entity">The entity the components belong to.</param>
    /// <param name="a0">A reference into the chunk.</param>
    /// <param name="a1">A reference into the chunk.</param>
    /// <param name="a2">A reference into the chunk.</param>
    /// <param name="a3">A reference into the chunk.</param>
    /// <param name="a4">A reference into the chunk.</param>
    /// <param name="a5">A reference into the chunk.</param>
    public void Execute(Entity entity, ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5) {
        Assert.Equal(entities[a0.Owner], entity);
        a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++;
    }

    /// <summary>Visits one entity's 7 component(s).</summary>
    /// <param name="entity">The entity the components belong to.</param>
    /// <param name="a0">A reference into the chunk.</param>
    /// <param name="a1">A reference into the chunk.</param>
    /// <param name="a2">A reference into the chunk.</param>
    /// <param name="a3">A reference into the chunk.</param>
    /// <param name="a4">A reference into the chunk.</param>
    /// <param name="a5">A reference into the chunk.</param>
    /// <param name="a6">A reference into the chunk.</param>
    public void Execute(Entity entity, ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6) {
        Assert.Equal(entities[a0.Owner], entity);
        a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++;
    }

    /// <summary>Visits one entity's 8 component(s).</summary>
    /// <param name="entity">The entity the components belong to.</param>
    /// <param name="a0">A reference into the chunk.</param>
    /// <param name="a1">A reference into the chunk.</param>
    /// <param name="a2">A reference into the chunk.</param>
    /// <param name="a3">A reference into the chunk.</param>
    /// <param name="a4">A reference into the chunk.</param>
    /// <param name="a5">A reference into the chunk.</param>
    /// <param name="a6">A reference into the chunk.</param>
    /// <param name="a7">A reference into the chunk.</param>
    public void Execute(Entity entity, ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6, ref A7 a7) {
        Assert.Equal(entities[a0.Owner], entity);
        a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; a7.Touches++;
    }

    /// <summary>Visits one entity's 9 component(s).</summary>
    /// <param name="entity">The entity the components belong to.</param>
    /// <param name="a0">A reference into the chunk.</param>
    /// <param name="a1">A reference into the chunk.</param>
    /// <param name="a2">A reference into the chunk.</param>
    /// <param name="a3">A reference into the chunk.</param>
    /// <param name="a4">A reference into the chunk.</param>
    /// <param name="a5">A reference into the chunk.</param>
    /// <param name="a6">A reference into the chunk.</param>
    /// <param name="a7">A reference into the chunk.</param>
    /// <param name="a8">A reference into the chunk.</param>
    public void Execute(Entity entity, ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6, ref A7 a7, ref A8 a8) {
        Assert.Equal(entities[a0.Owner], entity);
        a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; a7.Touches++; a8.Touches++;
    }

    /// <summary>Visits one entity's 10 component(s).</summary>
    /// <param name="entity">The entity the components belong to.</param>
    /// <param name="a0">A reference into the chunk.</param>
    /// <param name="a1">A reference into the chunk.</param>
    /// <param name="a2">A reference into the chunk.</param>
    /// <param name="a3">A reference into the chunk.</param>
    /// <param name="a4">A reference into the chunk.</param>
    /// <param name="a5">A reference into the chunk.</param>
    /// <param name="a6">A reference into the chunk.</param>
    /// <param name="a7">A reference into the chunk.</param>
    /// <param name="a8">A reference into the chunk.</param>
    /// <param name="a9">A reference into the chunk.</param>
    public void Execute(Entity entity, ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6, ref A7 a7, ref A8 a8, ref A9 a9) {
        Assert.Equal(entities[a0.Owner], entity);
        a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; a7.Touches++; a8.Touches++; a9.Touches++;
    }

    /// <summary>Visits one entity's 11 component(s).</summary>
    /// <param name="entity">The entity the components belong to.</param>
    /// <param name="a0">A reference into the chunk.</param>
    /// <param name="a1">A reference into the chunk.</param>
    /// <param name="a2">A reference into the chunk.</param>
    /// <param name="a3">A reference into the chunk.</param>
    /// <param name="a4">A reference into the chunk.</param>
    /// <param name="a5">A reference into the chunk.</param>
    /// <param name="a6">A reference into the chunk.</param>
    /// <param name="a7">A reference into the chunk.</param>
    /// <param name="a8">A reference into the chunk.</param>
    /// <param name="a9">A reference into the chunk.</param>
    /// <param name="a10">A reference into the chunk.</param>
    public void Execute(Entity entity, ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6, ref A7 a7, ref A8 a8, ref A9 a9, ref A10 a10) {
        Assert.Equal(entities[a0.Owner], entity);
        a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; a7.Touches++; a8.Touches++; a9.Touches++; a10.Touches++;
    }

    /// <summary>Visits one entity's 12 component(s).</summary>
    /// <param name="entity">The entity the components belong to.</param>
    /// <param name="a0">A reference into the chunk.</param>
    /// <param name="a1">A reference into the chunk.</param>
    /// <param name="a2">A reference into the chunk.</param>
    /// <param name="a3">A reference into the chunk.</param>
    /// <param name="a4">A reference into the chunk.</param>
    /// <param name="a5">A reference into the chunk.</param>
    /// <param name="a6">A reference into the chunk.</param>
    /// <param name="a7">A reference into the chunk.</param>
    /// <param name="a8">A reference into the chunk.</param>
    /// <param name="a9">A reference into the chunk.</param>
    /// <param name="a10">A reference into the chunk.</param>
    /// <param name="a11">A reference into the chunk.</param>
    public void Execute(Entity entity, ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6, ref A7 a7, ref A8 a8, ref A9 a9, ref A10 a10, ref A11 a11) {
        Assert.Equal(entities[a0.Owner], entity);
        a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; a7.Touches++; a8.Touches++; a9.Touches++; a10.Touches++; a11.Touches++;
    }

    /// <summary>Visits one entity's 13 component(s).</summary>
    /// <param name="entity">The entity the components belong to.</param>
    /// <param name="a0">A reference into the chunk.</param>
    /// <param name="a1">A reference into the chunk.</param>
    /// <param name="a2">A reference into the chunk.</param>
    /// <param name="a3">A reference into the chunk.</param>
    /// <param name="a4">A reference into the chunk.</param>
    /// <param name="a5">A reference into the chunk.</param>
    /// <param name="a6">A reference into the chunk.</param>
    /// <param name="a7">A reference into the chunk.</param>
    /// <param name="a8">A reference into the chunk.</param>
    /// <param name="a9">A reference into the chunk.</param>
    /// <param name="a10">A reference into the chunk.</param>
    /// <param name="a11">A reference into the chunk.</param>
    /// <param name="a12">A reference into the chunk.</param>
    public void Execute(Entity entity, ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6, ref A7 a7, ref A8 a8, ref A9 a9, ref A10 a10, ref A11 a11, ref A12 a12) {
        Assert.Equal(entities[a0.Owner], entity);
        a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; a7.Touches++; a8.Touches++; a9.Touches++; a10.Touches++; a11.Touches++; a12.Touches++;
    }

    /// <summary>Visits one entity's 14 component(s).</summary>
    /// <param name="entity">The entity the components belong to.</param>
    /// <param name="a0">A reference into the chunk.</param>
    /// <param name="a1">A reference into the chunk.</param>
    /// <param name="a2">A reference into the chunk.</param>
    /// <param name="a3">A reference into the chunk.</param>
    /// <param name="a4">A reference into the chunk.</param>
    /// <param name="a5">A reference into the chunk.</param>
    /// <param name="a6">A reference into the chunk.</param>
    /// <param name="a7">A reference into the chunk.</param>
    /// <param name="a8">A reference into the chunk.</param>
    /// <param name="a9">A reference into the chunk.</param>
    /// <param name="a10">A reference into the chunk.</param>
    /// <param name="a11">A reference into the chunk.</param>
    /// <param name="a12">A reference into the chunk.</param>
    /// <param name="a13">A reference into the chunk.</param>
    public void Execute(Entity entity, ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6, ref A7 a7, ref A8 a8, ref A9 a9, ref A10 a10, ref A11 a11, ref A12 a12, ref A13 a13) {
        Assert.Equal(entities[a0.Owner], entity);
        a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; a7.Touches++; a8.Touches++; a9.Touches++; a10.Touches++; a11.Touches++; a12.Touches++; a13.Touches++;
    }

    /// <summary>Visits one entity's 15 component(s).</summary>
    /// <param name="entity">The entity the components belong to.</param>
    /// <param name="a0">A reference into the chunk.</param>
    /// <param name="a1">A reference into the chunk.</param>
    /// <param name="a2">A reference into the chunk.</param>
    /// <param name="a3">A reference into the chunk.</param>
    /// <param name="a4">A reference into the chunk.</param>
    /// <param name="a5">A reference into the chunk.</param>
    /// <param name="a6">A reference into the chunk.</param>
    /// <param name="a7">A reference into the chunk.</param>
    /// <param name="a8">A reference into the chunk.</param>
    /// <param name="a9">A reference into the chunk.</param>
    /// <param name="a10">A reference into the chunk.</param>
    /// <param name="a11">A reference into the chunk.</param>
    /// <param name="a12">A reference into the chunk.</param>
    /// <param name="a13">A reference into the chunk.</param>
    /// <param name="a14">A reference into the chunk.</param>
    public void Execute(Entity entity, ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6, ref A7 a7, ref A8 a8, ref A9 a9, ref A10 a10, ref A11 a11, ref A12 a12, ref A13 a13, ref A14 a14) {
        Assert.Equal(entities[a0.Owner], entity);
        a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; a7.Touches++; a8.Touches++; a9.Touches++; a10.Touches++; a11.Touches++; a12.Touches++; a13.Touches++; a14.Touches++;
    }

    /// <summary>Visits one entity's 16 component(s).</summary>
    /// <param name="entity">The entity the components belong to.</param>
    /// <param name="a0">A reference into the chunk.</param>
    /// <param name="a1">A reference into the chunk.</param>
    /// <param name="a2">A reference into the chunk.</param>
    /// <param name="a3">A reference into the chunk.</param>
    /// <param name="a4">A reference into the chunk.</param>
    /// <param name="a5">A reference into the chunk.</param>
    /// <param name="a6">A reference into the chunk.</param>
    /// <param name="a7">A reference into the chunk.</param>
    /// <param name="a8">A reference into the chunk.</param>
    /// <param name="a9">A reference into the chunk.</param>
    /// <param name="a10">A reference into the chunk.</param>
    /// <param name="a11">A reference into the chunk.</param>
    /// <param name="a12">A reference into the chunk.</param>
    /// <param name="a13">A reference into the chunk.</param>
    /// <param name="a14">A reference into the chunk.</param>
    /// <param name="a15">A reference into the chunk.</param>
    public void Execute(Entity entity, ref A0 a0, ref A1 a1, ref A2 a2, ref A3 a3, ref A4 a4, ref A5 a5, ref A6 a6, ref A7 a7, ref A8 a8, ref A9 a9, ref A10 a10, ref A11 a11, ref A12 a12, ref A13 a13, ref A14 a14, ref A15 a15) {
        Assert.Equal(entities[a0.Owner], entity);
        a0.Touches++; a1.Touches++; a2.Touches++; a3.Touches++; a4.Touches++; a5.Touches++; a6.Touches++; a7.Touches++; a8.Touches++; a9.Touches++; a10.Touches++; a11.Touches++; a12.Touches++; a13.Touches++; a14.Touches++; a15.Touches++;
    }

}
