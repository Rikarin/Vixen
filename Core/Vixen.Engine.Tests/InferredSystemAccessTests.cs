// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Xunit;

namespace Vixen.Engine.Tests;

/// <summary>
///     What <c>[InferAccess]</c> reads out of a system's own body, and that the schedule reads it back.
/// </summary>
/// <remarks>
///     ⚠ <b>The declarations these assert were written by the generator, not by this file.</b> The
///     systems below are ordinary partial classes with no <c>Access</c> property in them; the other
///     half of each is <c>Vixen.Engine.Generators</c>'s output for this assembly, compiled in beside
///     it. A test that constructed a <c>SystemAccess</c> by hand would be proving
///     <c>SystemAccess</c> works and saying nothing about whether inference happens.
/// </remarks>
public sealed class InferredSystemAccessTests {
    [Fact]
    public void TheChunkFormSaysWhichWayEachComponentGoes() {
        var access = ((IDeclaredAccess) new InferredChunkSystem()).Access;

        // Values<T> is a write and ReadValues<T> is a read, exactly as Get and Read are on the
        // world — the direction is in the call, so nothing has to be assumed.
        Assert.Equal([ComponentType<Marked>.Id], access.Writes);
        Assert.Contains(ComponentType<Tracked>.Id, access.Reads);
        Assert.Contains(ComponentType<Marked>.Id, access.Reads);
    }

    [Fact]
    public void TheDelegateFormIsReadAsWritingEverythingItNames() {
        var access = ((IDeclaredAccess) new InferredDelegateSystem()).Access;

        // ⚠ Both, and Tracked is only read by the body. QueryAction takes every component by `ref`
        // whether or not the lambda assigns through it, so there is no direction to read — and the
        // documented choice is to over-declare, because the other error is a data race.
        //
        // ⚠ Both sides are ordered, and only one of them used to be. A component id is assigned when
        // the type is first named, so which of these two is the lower number depends on which test in
        // the assembly mentioned it first — this passed run alone and failed in the whole project.
        ComponentTypeId[] wanted = [ComponentType<Marked>.Id, ComponentType<Tracked>.Id];

        Assert.Equal(wanted.Order().ToArray(), access.Writes.Order().ToArray());
    }

    [Fact]
    public void AWithNoneFilterIsNotSomethingTheSystemTouches() {
        var access = ((IDeclaredAccess) new InferredFilteredSystem()).Access;

        // The filter says what the entity must not have. An entity that matched it does not have a
        // Frozen for anyone to race over.
        Assert.DoesNotContain(ComponentType<Frozen>.Id, access.Reads);
        Assert.Contains(ComponentType<Marked>.Id, access.Reads);
    }

    [Fact]
    public void TheGraphOrdersSystemsByWhatWasInferred() {
        // Naming the types generically is what assigns them ids, and `[Writes(typeof(Frozen))]` can
        // only look one up. Frozen appears in no inferred declaration — it is only ever a filter —
        // so nothing else here would have stored it.
        ComponentRegistry.Of<Marked>();
        ComponentRegistry.Of<Frozen>();

        var conflicting = SystemGraph.Build([new InferredChunkSystem(), new SecondMarkedWriterSystem()]);

        Assert.Equal([0], conflicting.InPhase(SystemPhase.Update)[1].DependsOn);

        // ⚠ The half that makes the assertion above mean anything, and it was missing at first. An
        // undeclared system conflicts with everything, so a graph that ignored the inferred
        // declaration entirely would still produce that edge — and only fails here, where the
        // declaration is what says the two are disjoint.
        var disjoint = SystemGraph.Build([new InferredChunkSystem(), new DisjointWriterSystem()]);

        Assert.Empty(disjoint.InPhase(SystemPhase.Update)[1].DependsOn);
    }

    [Fact]
    public void TheSafetySystemGetsTheSameDeclaration() {
        var access = ((IDeclaredAccess) new InferredChunkSystem()).Access;

        // SystemRunner hands this object to JobScheduler.DeclareAccess for the length of the
        // system's Update, so an inferred write is what the race detector polices too.
        Assert.Contains(ComponentType<Marked>.Id.Value, access.JobAccess.Writes);
        Assert.False(access.JobAccess.IsEverything);
    }

    [Fact]
    public void AnInferredSystemIsNotUndeclared() {
        // The failure mode worth naming: a generator that emitted an empty declaration would leave
        // the system reading as "conflicts with everything" — indistinguishable from one nobody
        // annotated, and silently costing the whole phase its parallelism.
        Assert.False(((IDeclaredAccess) new InferredChunkSystem()).Access.IsEmpty);
        Assert.False(((IDeclaredAccess) new InferredDelegateSystem()).Access.IsEmpty);
    }
}

#pragma warning disable CS0649 // Assigned by the ECS, not here: a component is storage, and the test is about what names it.

/// <summary>A component this file writes through the chunk form.</summary>
internal struct Marked {
    public int Value;
}

/// <summary>A component this file only reads.</summary>
internal struct Tracked {
    public int Value;
}

/// <summary>A component that only ever appears in a <c>WithNone</c>.</summary>
internal struct Frozen {
    public int Value;
}

#pragma warning restore CS0649

/// <summary>Reads one component and writes another, through chunks.</summary>
[InferAccess]
public partial class InferredChunkSystem : SystemBase {
    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        foreach (var chunk in context.World.Chunks(new QueryDescription().WithAll<Marked, Tracked>())) {
            var marked = chunk.Values<Marked>();
            var tracked = chunk.ReadValues<Tracked>();

            for (var index = 0; index < chunk.Count; index++) {
                marked[index].Value += tracked[index].Value;
            }
        }

        return dependency;
    }
}

/// <summary>The same work through the delegate form, where the direction is not visible.</summary>
[InferAccess]
public partial class InferredDelegateSystem : SystemBase {
    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        context.World.Query(
            new QueryDescription().WithAll<Marked, Tracked>(),
            static (ref Marked marked, ref Tracked tracked) => marked.Value += tracked.Value
        );

        return dependency;
    }
}

/// <summary>Names a component only to exclude it.</summary>
[InferAccess]
public partial class InferredFilteredSystem : SystemBase {
    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        var count = 0;

        foreach (var chunk in context.World.Chunks(
                     new QueryDescription().WithAll<Marked>().WithNone<Frozen>()
                 )) {
            count += chunk.Count;
        }

        Seen = count;
        return dependency;
    }

    /// <summary>How many the last run saw, so the loop is not optimised into nothing.</summary>
    public int Seen { get; private set; }
}

/// <summary>Writes what <see cref="InferredChunkSystem" /> writes, so the graph has to order them.</summary>
[Writes(typeof(Marked))]
public sealed class SecondMarkedWriterSystem : SystemBase {
    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) => dependency;
}

/// <summary>Writes something no inferred system names, so the graph must leave the two unordered.</summary>
[Writes(typeof(Frozen))]
public sealed class DisjointWriterSystem : SystemBase {
    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) => dependency;
}
