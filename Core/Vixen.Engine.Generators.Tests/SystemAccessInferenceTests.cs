// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.CodeAnalysis;
using Xunit;

namespace Vixen.Engine.Generators.Tests;

/// <summary>VXS0407 to VXS0411, and what the inference actually reads out of a body.</summary>
/// <remarks>
///     ⚠ <b>These five were proved once, by hand, with a throwaway file.</b> A <c>DiagFixture.cs</c>
///     was added to <c>Vixen.Engine.Tests</c>, the build run, the ids read out of the compiler
///     output, and the file deleted — which found an ordering bug (a non-partial non-system reported
///     VXS0408 where VXS0407 is the thing to say) that nothing would have caught coming back. The
///     ordering is asserted below for that reason.
/// </remarks>
public sealed class SystemAccessInferenceTests {
    const string Components = """
        public struct Position { public float X; }
        public struct Velocity { public float X; }
        public struct Frozen { public bool Value; }
        """;

    const string Body = """
            public override JobHandle Update(in SystemContext context, JobHandle dependency) => dependency;
        """;

    [Fact]
    public void AnInferredAccessOnSomethingThatIsNotASystemIsVXS0407() {
        var (diagnostics, sources) = GeneratorHarness.Run(
            new SystemAccessInferenceGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            [InferAccess]
            public partial class NotASystem {
            }
            """
        );

        Assert.Single(diagnostics, diagnostic => diagnostic.Id == "VXS0407");
        Assert.Empty(sources);
    }

    /// <summary>⚠ The ordering the hand-run found: not-a-system is said before not-partial.</summary>
    /// <remarks>
    ///     A class that is neither should be told the thing that is the mistake, not the thing that
    ///     is how it would have been fixed. Widened by moving the shape check above the interface
    ///     check in <c>Describe</c>: this goes red on both assertions, every other fixture here stays
    ///     green — which is precisely why the bug survived the hand-run's first pass. Reverted.
    /// </remarks>
    [Fact]
    public void AClassThatIsNeitherASystemNorPartialIsVXS0407_AndNotVXS0408() {
        var (diagnostics, _) = GeneratorHarness.Run(
            new SystemAccessInferenceGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            [InferAccess]
            public class NeitherOne {
            }
            """
        );

        Assert.Single(diagnostics, diagnostic => diagnostic.Id == "VXS0407");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "VXS0408");
    }

    [Theory]
    [InlineData("public class NotPartial : SystemBase {")]
    [InlineData("public partial class Open<T> : SystemBase {")]
    public void ASystemThatIsNotAPartialTopLevelNonGenericClassIsVXS0408(string declaration) {
        var (diagnostics, sources) = GeneratorHarness.Run(
            new SystemAccessInferenceGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            [InferAccess]
            {{declaration}}
            {{Body}}
            }
            """
        );

        Assert.Single(diagnostics, diagnostic => diagnostic.Id == "VXS0408");
        Assert.Empty(sources);
    }

    [Fact]
    public void ANestedSystemIsVXS0408() {
        // The declaration is emitted as the other half of the class, and a nested type would need
        // every type around it to be partial too.
        var (diagnostics, _) = GeneratorHarness.Run(
            new SystemAccessInferenceGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            public static partial class Outer {
                [InferAccess]
                public partial class Inner : SystemBase {
                {{Body}}
                }
            }
            """
        );

        Assert.Single(diagnostics, diagnostic => diagnostic.Id == "VXS0408");
    }

    /// <summary>The VXS0408 negative: a partial top-level class is not refused.</summary>
    /// <remarks>
    ///     Widened by making <c>IsPartial</c> return <see langword="false" /> unconditionally: this
    ///     goes red, the three positives above stay green. Reverted.
    /// </remarks>
    [Fact]
    public void APartialTopLevelSystemIsNotVXS0408() {
        var (diagnostics, sources) = GeneratorHarness.Run(
            new SystemAccessInferenceGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            {{Components}}

            [InferAccess]
            public partial class MoveSystem : SystemBase {
                public override JobHandle Update(in SystemContext context, JobHandle dependency) {
                    foreach (var chunk in context.World.Chunks(new QueryDescription().WithAll<Position>())) {
                        var positions = chunk.Values<Position>();
                    }

                    return dependency;
                }
            }
            """
        );

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "VXS0408");
        Assert.NotEmpty(sources);
    }

    [Fact]
    public void ASystemThatAlreadyDeclaresItsAccessIsVXS0409() {
        var (diagnostics, sources) = GeneratorHarness.Run(
            new SystemAccessInferenceGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            {{Components}}

            [InferAccess]
            public partial class MoveSystem : SystemBase, IDeclaredAccess {
                public SystemAccess Access { get; } = SystemAccess.Declare().Write<Position>().Build();

            {{Body}}
            }
            """
        );

        Assert.Single(diagnostics, diagnostic => diagnostic.Id == "VXS0409");

        // ⚠ And nothing emitted, which is the point: a second Access property would not compile, and
        // dropping it silently would leave the attribute looking like it did something.
        Assert.Empty(sources);
    }

    [Theory]
    [InlineData("[Reads(typeof(Position))]")]
    [InlineData("[Writes(typeof(Position))]")]
    public void ASystemThatAlsoCarriesAnAccessAttributeIsVXS0410(string attribute) {
        var (diagnostics, sources) = GeneratorHarness.Run(
            new SystemAccessInferenceGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            {{Components}}

            [InferAccess]
            {{attribute}}
            public partial class MoveSystem : SystemBase {
                public override JobHandle Update(in SystemContext context, JobHandle dependency) {
                    foreach (var chunk in context.World.Chunks(new QueryDescription().WithAll<Position>())) {
                        var positions = chunk.Values<Position>();
                    }

                    return dependency;
                }
            }
            """
        );

        Assert.Single(diagnostics, diagnostic => diagnostic.Id == "VXS0410");
        Assert.Empty(sources);
    }

    /// <summary>The VXS0410 negative: an unrelated attribute does not count as a declaration.</summary>
    /// <remarks>
    ///     ⚠ <b>The refusal is about two <em>access</em> declarations, not about attributes.</b>
    ///     Widened by returning <c>AttributesOverride</c> for any attribute other than
    ///     <c>InferAccess</c>: this goes red and both positives above stay green. Reverted.
    /// </remarks>
    [Fact]
    public void ASystemCarryingAnUnrelatedAttributeIsNotVXS0410() {
        var (diagnostics, sources) = GeneratorHarness.Run(
            new SystemAccessInferenceGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            {{Components}}

            [InferAccess]
            [UpdateInGroup(SystemPhase.FixedUpdate)]
            public partial class MoveSystem : SystemBase {
                public override JobHandle Update(in SystemContext context, JobHandle dependency) {
                    foreach (var chunk in context.World.Chunks(new QueryDescription().WithAll<Position>())) {
                        var positions = chunk.Values<Position>();
                    }

                    return dependency;
                }
            }
            """
        );

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "VXS0410");
        Assert.NotEmpty(sources);
    }

    [Fact]
    public void ASystemWhoseBodyNamesNoComponentIsVXS0411() {
        var (diagnostics, sources) = GeneratorHarness.Run(
            new SystemAccessInferenceGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            [InferAccess]
            public partial class EmptySystem : SystemBase {
            {{Body}}
            }
            """
        );

        Assert.Single(diagnostics, diagnostic => diagnostic.Id == "VXS0411");

        // An empty declaration is indistinguishable from an unannotated system — the runner reads
        // both as "conflicts with everything" — so emitting one would be worse than emitting none.
        Assert.Empty(sources);
    }

    /// <summary>The VXS0411 negative: one visible query is enough, and it lands as a read.</summary>
    /// <remarks>
    ///     ⚠ <b>The widening that matters here is not deleting the rule but widening the
    ///     collection.</b> Adding <c>QueryDescriptionType when name is "WithNone" =&gt; reads</c> to
    ///     <c>Collect</c> makes a filter count as access — VXS0411 stops firing on
    ///     <see cref="AFilterOnlySystemIsVXS0411" /> below, which goes red while this one stays
    ///     green. Deleting the whole <c>Chunk</c> arm instead turns this one red. Both reverted.
    /// </remarks>
    [Fact]
    public void ASystemWithOneVisibleQueryIsNotVXS0411() {
        var (diagnostics, sources) = GeneratorHarness.Run(
            new SystemAccessInferenceGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            {{Components}}

            [InferAccess]
            public partial class ReadSystem : SystemBase {
                public override JobHandle Update(in SystemContext context, JobHandle dependency) {
                    foreach (var chunk in context.World.Chunks(new QueryDescription().WithAll<Position>())) {
                        var positions = chunk.ReadValues<Position>();
                    }

                    return dependency;
                }
            }
            """
        );

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "VXS0411");

        var emitted = Assert.Single(sources);

        Assert.Contains(".Read<global::Subject.Position>()", emitted);
        Assert.DoesNotContain(".Write<", emitted);
    }

    [Fact]
    public void AFilterOnlySystemIsVXS0411() {
        // ⚠ `WithNone` says what the entity must not have, which is not something the system reads —
        // so a system whose only type argument is a filter has inferred nothing, and says so.
        var (diagnostics, _) = GeneratorHarness.Run(
            new SystemAccessInferenceGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            {{Components}}

            [InferAccess]
            public partial class FilterSystem : SystemBase {
                public override JobHandle Update(in SystemContext context, JobHandle dependency) {
                    var query = new QueryDescription().WithNone<Frozen>();

                    return dependency;
                }
            }
            """
        );

        Assert.Single(diagnostics, diagnostic => diagnostic.Id == "VXS0411");
    }

    [Fact]
    public void ChunkValuesIsAWriteAndReadValuesIsARead() {
        // The chunk form is the exact one, because `Values<T>` and `ReadValues<T>` are two calls.
        var (_, sources) = GeneratorHarness.Run(
            new SystemAccessInferenceGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            {{Components}}

            [InferAccess]
            public partial class MoveSystem : SystemBase {
                public override JobHandle Update(in SystemContext context, JobHandle dependency) {
                    foreach (var chunk in context.World.Chunks(new QueryDescription().WithAll<Position, Velocity>())) {
                        var positions = chunk.Values<Position>();
                        var velocities = chunk.ReadValues<Velocity>();
                    }

                    return dependency;
                }
            }
            """
        );

        var emitted = Assert.Single(sources);

        Assert.Contains(".Read<global::Subject.Velocity>()", emitted);
        Assert.Contains(".Write<global::Subject.Position>()", emitted);

        // ⚠ And Position is not also declared as a read, even though `WithAll<Position, Velocity>`
        // named it: a write implies a read and the emitted chain says one or the other.
        Assert.DoesNotContain(".Read<global::Subject.Position>()", emitted);
    }

    [Fact]
    public void TheDelegateFormIsInferredAsAWrite() {
        // ⚠ `QueryAction<T0, T1>` takes every component by `ref` whether or not the body assigns
        // through one, so there is no direction to read and the safe one is the wide one.
        var (_, sources) = GeneratorHarness.Run(
            new SystemAccessInferenceGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            {{Components}}

            [InferAccess]
            public partial class MoveSystem : SystemBase {
                public override JobHandle Update(in SystemContext context, JobHandle dependency) {
                    context.World.Query(
                        new QueryDescription().WithAll<Position, Velocity>(),
                        (ref Position position, ref Velocity velocity) => { }
                    );

                    return dependency;
                }
            }
            """
        );

        var emitted = Assert.Single(sources);

        Assert.Contains(".Write<global::Subject.Position>()", emitted);
        Assert.Contains(".Write<global::Subject.Velocity>()", emitted);
        Assert.DoesNotContain(".Read<", emitted);
    }

    [Fact]
    public void TheDeclarationIsEmittedIntoTheSystemsOwnNamespace() {
        var (_, sources) = GeneratorHarness.Run(
            new SystemAccessInferenceGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            {{Components}}

            [InferAccess]
            public partial class ReadSystem : SystemBase {
                public override JobHandle Update(in SystemContext context, JobHandle dependency) {
                    foreach (var chunk in context.World.Chunks(new QueryDescription().WithAll<Position>())) {
                        var positions = chunk.ReadValues<Position>();
                    }

                    return dependency;
                }
            }
            """
        );

        var emitted = Assert.Single(sources);

        Assert.Contains("namespace Subject {", emitted);
        Assert.Contains("partial class ReadSystem : global::Vixen.Ecs.Systems.IDeclaredAccess {", emitted);
    }

    [Fact]
    public void WhatIsEmittedCompiles() {
        // ⚠ `Write<T>` closes a generic and so assigns the component an id — which means the
        // emitted chain has to name types that satisfy the constraint, in a partial half the author
        // never wrote. Only the compiler can say whether it does.
        var diagnostics = GeneratorHarness.CompileWithGeneratedCode(
            new SystemAccessInferenceGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            {{Components}}

            [InferAccess]
            public partial class MoveSystem : SystemBase {
                public override JobHandle Update(in SystemContext context, JobHandle dependency) {
                    foreach (var chunk in context.World.Chunks(new QueryDescription().WithAll<Position, Velocity>())) {
                        var positions = chunk.Values<Position>();
                        var velocities = chunk.ReadValues<Velocity>();
                    }

                    return dependency;
                }
            }
            """
        );

        Assert.Empty(diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }
}
