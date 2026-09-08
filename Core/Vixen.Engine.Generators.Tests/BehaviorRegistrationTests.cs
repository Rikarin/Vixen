// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.CodeAnalysis;
using Xunit;

namespace Vixen.Engine.Generators.Tests;

/// <summary>VXS0402 and VXS0403, and the two silences the behaviour generator keeps deliberately.</summary>
public sealed class BehaviorRegistrationTests {
    [Fact]
    public void ABehaviorWithNoParameterlessConstructorIsVXS0402() {
        var (diagnostics, sources) = GeneratorHarness.Run(
            new BehaviorRegistrationGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            [DataContract]
            public class Spinner : Behavior {
                public Spinner(float speed) { }
            }
            """
        );

        Assert.Single(diagnostics, diagnostic => diagnostic.Id == "VXS0402");
        Assert.Empty(sources);
    }

    /// <summary>The VXS0402 negative: a behaviour with the implicit constructor is registered.</summary>
    /// <remarks>
    ///     ⚠ <b>A class with no declared constructor is the case the rule most easily gets wrong.</b>
    ///     Widened by adding <c>&amp;&amp; !constructor.IsImplicitlyDeclared</c> to
    ///     <c>HasParameterlessConstructor</c> — the implicit constructor stops counting, this goes
    ///     red, and the positive above stays green. Reverted.
    /// </remarks>
    [Fact]
    public void ABehaviorWithTheImplicitConstructorIsNotVXS0402_AndIsRegistered() {
        var (diagnostics, sources) = GeneratorHarness.Run(
            new BehaviorRegistrationGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            [DataContract]
            public class Spinner : Behavior {
                public float Speed;
            }
            """
        );

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "VXS0402");
        Assert.Contains("Declare<global::Subject.Spinner>()", Assert.Single(sources));
    }

    [Fact]
    public void ABehaviorWhoseParameterlessConstructorIsNotPublicIsVXS0402() {
        // `Declare<T>` is constrained `new()`, which a private constructor does not satisfy — so
        // accessibility is part of the question and not a detail.
        var (diagnostics, _) = GeneratorHarness.Run(
            new BehaviorRegistrationGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            [DataContract]
            public class Hidden : Behavior {
                Hidden() { }
            }
            """
        );

        Assert.Single(diagnostics, diagnostic => diagnostic.Id == "VXS0402");
    }

    [Fact]
    public void AGenericBehaviorIsVXS0403() {
        var (diagnostics, sources) = GeneratorHarness.Run(
            new BehaviorRegistrationGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            [DataContract]
            public class Follow<T> : Behavior {
            }
            """
        );

        Assert.Single(diagnostics, diagnostic => diagnostic.Id == "VXS0403");
        Assert.Empty(sources);
    }

    /// <summary>A behaviour nested in a generic type is VXS0403 too.</summary>
    /// <remarks>
    ///     ⚠ <b>Refuted, and worth keeping as the record of it.</b> This was written expecting a
    ///     hole: <c>Describe</c> asks <c>type.IsGenericType</c>, which reads like arity and would be
    ///     0 for <c>Inner</c> — so a behaviour a scene can no more name than <c>Follow&lt;T&gt;</c>
    ///     looked like it would be registered as
    ///     <c>Declare&lt;global::Subject.Outer&lt;T&gt;.Inner&gt;()</c>, which does not compile.
    ///     It is not: Roslyn's <c>INamedTypeSymbol.IsGenericType</c> walks the containing types, so
    ///     nesting inside an open type is already generic to it. The same answer covers the
    ///     <c>Contains('&lt;')</c> that picks which of the two ids to report — a name is exactly as
    ///     bracketed as the check is generic, so the two cannot disagree.
    /// </remarks>
    [Fact]
    public void ABehaviorNestedInAGenericTypeIsVXS0403() {
        var (diagnostics, sources) = GeneratorHarness.Run(
            new BehaviorRegistrationGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            public static class Outer<T> {
                [DataContract]
                public class Inner : Behavior { }
            }
            """
        );

        Assert.Single(diagnostics, diagnostic => diagnostic.Id == "VXS0403");
        Assert.Empty(sources);
    }

    /// <summary>The VXS0403 negative: a behaviour nested in a closed type is registered.</summary>
    /// <remarks>
    ///     The other side of the fixture above — a nested behaviour is perfectly nameable when
    ///     nothing around it is generic, so the refusal is about the containing types and not about
    ///     nesting. Widened by replacing <c>type.IsGenericType</c> with
    ///     <c>type.IsGenericType || type.ContainingType is not null</c>: this goes red and the
    ///     positive above stays green. Reverted.
    /// </remarks>
    [Fact]
    public void ABehaviorNestedInAClosedTypeIsNotVXS0403_AndIsRegistered() {
        var (diagnostics, sources) = GeneratorHarness.Run(
            new BehaviorRegistrationGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            public static class Outer {
                [DataContract]
                public class Inner : Behavior { }
            }
            """
        );

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "VXS0403");
        Assert.Contains("Declare<global::Subject.Outer.Inner>()", Assert.Single(sources));
    }

    [Fact]
    public void AnAbstractBehaviorIsSilent() {
        // Deliberately not a diagnostic: a described base whose members the concrete behaviours
        // below it inherit is ordinary, and was never something a scene would name.
        var (diagnostics, sources) = GeneratorHarness.Run(
            new BehaviorRegistrationGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            [DataContract]
            public abstract class Weapon : Behavior {
                protected Weapon(int damage) { }
            }
            """
        );

        Assert.Empty(diagnostics);
        Assert.Empty(sources);
    }

    [Fact]
    public void ADescribedClassThatIsNotABehaviorIsSilent() {
        // [DataContract] is the whole tree's serialisation marker, so the base-type filter is what
        // keeps this generator from having an opinion about every described type in an assembly.
        var (diagnostics, sources) = GeneratorHarness.Run(
            new BehaviorRegistrationGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            [DataContract]
            public class Settings {
                public Settings(int value) { }
            }
            """
        );

        Assert.Empty(diagnostics);
        Assert.Empty(sources);
    }

    [Fact]
    public void ABehaviorSeveralLevelsBelowBehaviorIsStillRegistered() {
        var (_, sources) = GeneratorHarness.Run(
            new BehaviorRegistrationGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            public abstract class Weapon : Behavior { }

            [DataContract]
            public class Rifle : Weapon { }
            """
        );

        Assert.Contains("Declare<global::Subject.Rifle>()", Assert.Single(sources));
    }

    [Fact]
    public void WhatIsEmittedCompiles() {
        // `Declare<T>` is constrained `where T : Behavior, new()`, and both halves are decided here
        // by symbol inspection rather than by the compiler.
        var diagnostics = GeneratorHarness.CompileWithGeneratedCode(
            new BehaviorRegistrationGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            [DataContract]
            public class Spinner : Behavior {
                public float Speed;
            }
            """
        );

        Assert.Empty(diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }
}
