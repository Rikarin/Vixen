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
    public void AQueryHandedToAnotherTypeIsVXS0412() {
        // ⚠ The declaration is still emitted, and that is why this has to be said. Silence here is
        // a confidently under-declared IDeclaredAccess, which the runner hands to the job
        // scheduler's safety system — an under-declared system is a data race, not a slow one.
        var source = $$"""
            {{GeneratorHarness.Preamble}}

            {{Components}}

            public static class Helper {
                public static void Sweep(World world) {
                    foreach (var chunk in world.Chunks(new QueryDescription().WithAll<Velocity>())) {
                        var velocities = chunk.Values<Velocity>();
                    }
                }
            }

            [InferAccess]
            public partial class MoveSystem : SystemBase {
                public override JobHandle Update(in SystemContext context, JobHandle dependency) {
                    foreach (var chunk in context.World.Chunks(new QueryDescription().WithAll<Position>())) {
                        var positions = chunk.Values<Position>();
                    }

                    Helper.Sweep(context.World);

                    return dependency;
                }
            }
            """;

        var (diagnostics, sources) = GeneratorHarness.Run(new SystemAccessInferenceGenerator(), source);
        var warning = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "VXS0412");

        Assert.Contains("Sweep", warning.GetMessage());

        // ⚠ On the call, not on the project. The five rules above it report at Location.None because
        // their model carries only a name; this one lands on the line that causes it, which is the
        // only thing that makes it actionable in an editor.
        Assert.NotEqual(Location.None, warning.Location);

        var span = warning.Location.GetLineSpan();
        var lines = source.Replace("\r\n", "\n").Split('\n');

        Assert.Equal("Subject.cs", span.Path);
        Assert.Contains("Helper.Sweep(context.World);", lines[span.StartLinePosition.Line]);

        // The declaration is emitted, and it is exactly as wrong as the warning says: Velocity,
        // which Helper.Sweep writes, is not in it.
        var emitted = Assert.Single(sources);

        Assert.Contains(".Write<global::Subject.Position>()", emitted);
        Assert.DoesNotContain("Velocity", emitted);
    }

    [Fact]
    public void AContextHandedToABaseClassIsVXS0412() {
        // The other half of the issue's blind spot, and the one an inheritance-shaped codebase hits
        // first: a base class's Update is not in this class's declarations.
        var (diagnostics, _) = GeneratorHarness.Run(
            new SystemAccessInferenceGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            {{Components}}

            public abstract class Sweeping : SystemBase {
                protected void SweepAll(in SystemContext context) { }
            }

            [InferAccess]
            public partial class MoveSystem : Sweeping {
                public override JobHandle Update(in SystemContext context, JobHandle dependency) {
                    foreach (var chunk in context.World.Chunks(new QueryDescription().WithAll<Position>())) {
                        var positions = chunk.Values<Position>();
                    }

                    SweepAll(context);

                    return dependency;
                }
            }
            """
        );

        Assert.Single(diagnostics, diagnostic => diagnostic.Id == "VXS0412");
    }

    /// <summary>The VXS0412 negative: a private helper on the same class is not a blind spot.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the half that decides whether the rule is usable.</b> Inference walks every
    ///     invocation under the class's own <c>DeclaringSyntaxReferences</c>, so a private method, a
    ///     local function and the other half of a partial are all already read — a rule that warned
    ///     about those would fire on the ordinary way of writing a system and get suppressed
    ///     wholesale. Widened by dropping the
    ///     <c>SymbolEqualityComparer.Default.Equals(owner, system)</c> test in
    ///     <c>HandsOffAccess</c>: this goes red and both positives above stay green. Reverted.
    /// </remarks>
    [Fact]
    public void APrivateHelperOnTheSameClassIsNotVXS0412_AndItsQueryIsInferred() {
        var (diagnostics, sources) = GeneratorHarness.Run(
            new SystemAccessInferenceGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            {{Components}}

            [InferAccess]
            public partial class MoveSystem : SystemBase {
                public override JobHandle Update(in SystemContext context, JobHandle dependency) {
                    Sweep(context.World);

                    return dependency;
                }

                void Sweep(World world) {
                    foreach (var chunk in world.Chunks(new QueryDescription().WithAll<Velocity>())) {
                        var velocities = chunk.Values<Velocity>();
                    }
                }
            }
            """
        );

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "VXS0412");

        // And the proof that it is not a blind spot: what the helper queries is in the declaration.
        Assert.Contains(".Write<global::Subject.Velocity>()", Assert.Single(sources));
    }

    /// <summary>The other VXS0412 negative: a call that carries none of the four is not one.</summary>
    /// <remarks>
    ///     The rule is about a world (or a piece of it) leaving the class, not about calling out of
    ///     it at all — a system that calls <c>Math.Clamp</c> or a logger has handed nothing away.
    ///     Widened by returning <see langword="true" /> from <c>HandsOffAccess</c> without the
    ///     parameter test: this goes red and both positives stay green. Reverted.
    /// </remarks>
    [Fact]
    public void ACallThatCarriesNoWorldIsNotVXS0412() {
        var (diagnostics, _) = GeneratorHarness.Run(
            new SystemAccessInferenceGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            {{Components}}

            public static class Helper {
                public static float Scale(float value) => value * 2f;
            }

            [InferAccess]
            public partial class MoveSystem : SystemBase {
                public override JobHandle Update(in SystemContext context, JobHandle dependency) {
                    foreach (var chunk in context.World.Chunks(new QueryDescription().WithAll<Position>())) {
                        var positions = chunk.Values<Position>();

                        for (var index = 0; index < chunk.Count; index++) {
                            positions[index].X = Helper.Scale(positions[index].X);
                        }
                    }

                    return dependency;
                }
            }
            """
        );

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "VXS0412");
    }

    [Fact]
    public void ARefusedSystemIsNotAlsoToldItsAccessLeftTheClass() {
        // A class that already has VXS0407-VXS0411 has been told no declaration is being written,
        // so a second warning about what that declaration might be missing is noise.
        var (diagnostics, _) = GeneratorHarness.Run(
            new SystemAccessInferenceGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            {{Components}}

            public static class Helper {
                public static void Sweep(World world) { }
            }

            [InferAccess]
            public partial class EmptySystem : SystemBase {
                public override JobHandle Update(in SystemContext context, JobHandle dependency) {
                    Helper.Sweep(context.World);

                    return dependency;
                }
            }
            """
        );

        Assert.Single(diagnostics, diagnostic => diagnostic.Id == "VXS0411");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "VXS0412");
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
