// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.CodeAnalysis;
using Xunit;

namespace Vixen.Engine.Generators.Tests;

/// <summary>VXS0404, VXS0405 and VXS0406, and the factory the generator writes when it says nothing.</summary>
public sealed class GameSystemRegistrationTests {
    /// <summary>A system that does nothing, which every fixture here derives from or ignores.</summary>
    const string Body = """
            public override JobHandle Update(in SystemContext context, JobHandle dependency) => dependency;
        """;

    [Fact]
    public void ADeclaredTypeThatIsNotASystemIsVXS0404() {
        var (diagnostics, sources) = GeneratorHarness.Run(
            new GameSystemGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            [GameSystem]
            public class NotASystem {
            }
            """
        );

        Assert.Single(diagnostics, diagnostic => diagnostic.Id == "VXS0404");
        Assert.Empty(sources);
    }

    /// <summary>The VXS0404 negative: an ISystem is declared and not complained about.</summary>
    /// <remarks>
    ///     Widened by making <c>ImplementsSystem</c> return <see langword="false" /> unconditionally
    ///     — this goes red, the positive above stays green. Reverted.
    /// </remarks>
    [Fact]
    public void ASystemIsNotVXS0404_AndIsDeclared() {
        var (diagnostics, sources) = GeneratorHarness.Run(
            new GameSystemGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            [GameSystem]
            public class MoveSystem : SystemBase {
            {{Body}}
            }
            """
        );

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "VXS0404");
        Assert.Contains("typeof(global::Subject.MoveSystem)", Assert.Single(sources));
    }

    [Theory]
    [InlineData("public Two(int a) { } public Two(float b) { }")]
    [InlineData("Two() { }")]
    public void ASystemWithoutExactlyOnePublicConstructorIsVXS0405(string constructors) {
        var (diagnostics, sources) = GeneratorHarness.Run(
            new GameSystemGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            [GameSystem]
            public class Two : SystemBase {
                {{constructors}}
            {{Body}}
            }
            """
        );

        Assert.Single(diagnostics, diagnostic => diagnostic.Id == "VXS0405");
        Assert.Empty(sources);
    }

    /// <summary>The VXS0405 negative: one public constructor, however many private ones there are.</summary>
    /// <remarks>
    ///     ⚠ <b>"Exactly one" is about the public ones only</b>, which is the half a narrower rule
    ///     would get wrong. Widened by dropping the <c>DeclaredAccessibility == Public</c> filter in
    ///     <c>Describe</c>: the private constructor starts counting, this goes red, and both
    ///     positives above stay green. Reverted.
    /// </remarks>
    [Fact]
    public void ASystemWithOnePublicAndOnePrivateConstructorIsNotVXS0405() {
        var (diagnostics, sources) = GeneratorHarness.Run(
            new GameSystemGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            [GameSystem]
            public class One : SystemBase {
                public One() { }
                One(int internalUse) { }
            {{Body}}
            }
            """
        );

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "VXS0405");
        Assert.NotEmpty(sources);
    }

    [Theory]
    [InlineData("public abstract class Half : SystemBase {")]
    [InlineData("public class Open<T> : SystemBase {")]
    public void AnAbstractOrGenericSystemIsVXS0406(string declaration) {
        var (diagnostics, sources) = GeneratorHarness.Run(
            new GameSystemGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            [GameSystem]
            {{declaration}}
            {{Body}}
            }
            """
        );

        Assert.Single(diagnostics, diagnostic => diagnostic.Id == "VXS0406");
        Assert.Empty(sources);
    }

    /// <summary>A system nested in a generic type is VXS0406 too, for the same reason.</summary>
    /// <remarks>
    ///     ⚠ Written expecting a hole and it is not one, for the reason
    ///     <c>BehaviorRegistrationTests.ABehaviorNestedInAGenericTypeIsVXS0403</c> records: Roslyn's
    ///     <c>IsGenericType</c> is not arity, it walks the containing types. Kept because the day
    ///     somebody replaces it with <c>Arity &gt; 0</c> the generator starts emitting
    ///     <c>typeof(global::Subject.Outer&lt;T&gt;.Inner)</c> and a factory that constructs it,
    ///     neither of which compiles, and nothing else here would say so.
    /// </remarks>
    [Fact]
    public void ASystemNestedInAGenericTypeIsVXS0406() {
        var (diagnostics, sources) = GeneratorHarness.Run(
            new GameSystemGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            public static class Outer<T> {
                [GameSystem]
                public class Inner : SystemBase {
                {{Body}}
                }
            }
            """
        );

        Assert.Single(diagnostics, diagnostic => diagnostic.Id == "VXS0406");
        Assert.Empty(sources);
    }

    /// <summary>The VXS0406 negative: a concrete non-generic system is declared.</summary>
    /// <remarks>
    ///     Widened by adding <c>|| type.ContainingType is not null</c> to the abstract-or-generic
    ///     test: this goes red and the three positives above stay green. Reverted.
    /// </remarks>
    [Fact]
    public void AConcreteSystemNestedInAClosedTypeIsNotVXS0406() {
        var (diagnostics, sources) = GeneratorHarness.Run(
            new GameSystemGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            public static class Outer {
                [GameSystem]
                public class Inner : SystemBase {
                {{Body}}
                }
            }
            """
        );

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "VXS0406");
        Assert.Contains("typeof(global::Subject.Outer.Inner)", Assert.Single(sources));
    }

    [Fact]
    public void TheConstructorIsTheServiceList() {
        var (_, sources) = GeneratorHarness.Run(
            new GameSystemGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            public sealed class Clock { }

            [GameSystem]
            public class MoveSystem : SystemBase {
                public MoveSystem(Clock clock, World world) { }
            {{Body}}
            }
            """
        );

        var emitted = Assert.Single(sources);

        // Both the declaration of what it needs and the cast per parameter, in order — the pair is
        // what makes this a compile-time factory rather than a small DI container.
        Assert.Contains("typeof(global::Subject.Clock), typeof(global::Vixen.Ecs.World)", emitted);
        Assert.Contains("(global::Subject.Clock) services[0], (global::Vixen.Ecs.World) services[1]", emitted);
    }

    [Fact]
    public void ASystemThatAsksForNothingGetsAnEmptyServiceList() {
        // Not a missing declaration: a system with no constructor parameters has said what it needs.
        var (diagnostics, sources) = GeneratorHarness.Run(
            new GameSystemGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            [GameSystem]
            public class MoveSystem : SystemBase {
            {{Body}}
            }
            """
        );

        Assert.Empty(diagnostics);
        Assert.Contains("new global::System.Type[] { }", Assert.Single(sources));
    }

    [Fact]
    public void WhatIsEmittedCompiles() {
        // ⚠ The factory casts every service and calls a constructor the generator never saw the
        // compiler check. Reading the emitted string cannot say whether that binds.
        var diagnostics = GeneratorHarness.CompileWithGeneratedCode(
            new GameSystemGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            public sealed class Clock { }

            [GameSystem]
            public class MoveSystem : SystemBase {
                public MoveSystem(Clock clock, World world) { }
            {{Body}}
            }
            """
        );

        Assert.Empty(diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }
}
