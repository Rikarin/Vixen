// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Engine.Generators.Tests;

/// <summary>VXS0401, and what the component registration generator emits when it says nothing.</summary>
/// <remarks>
///     ⚠ <b>Every refusal here is paired with a fixture one step short of it.</b> A test that only
///     asserts a diagnostic fires is satisfied by a generator that reports it about everything, which
///     is a shape this repository has shipped more than once. The negative is what says the rule has
///     an edge, and per <c>Raven/README.md</c> it is proved by widening the rule in the generator
///     until the negative goes red and then reverting — which is recorded per rule below.
/// </remarks>
public sealed class ComponentRegistrationTests {
    [Fact]
    public void AGenericComponentIsVXS0401() {
        var (diagnostics, _) = GeneratorHarness.Run(
            new ComponentRegistrationGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            [Component]
            [DataContract]
            public struct Box<T> {
                public T Value;
            }
            """
        );

        var warning = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "VXS0401");

        Assert.Contains("Box<T>", warning.GetMessage());
    }

    /// <summary>The VXS0401 negative: a closed component is registered and not complained about.</summary>
    /// <remarks>
    ///     Widened by removing <c>type.IsGenericType ? qualified : null</c>'s condition in
    ///     <c>ComponentRegistrationGenerator.Describe</c> — every component then carries a warning,
    ///     this goes red on both halves, and the positive above stays green. Reverted.
    ///     ⚠ That widening also fails <c>Vixen.Engine</c>'s own build outright, with twenty
    ///     <c>error VXS0401</c> against real components — warnings are errors and the generator runs
    ///     over the assembly this project references. It has to be run with
    ///     <c>-p:TreatWarningsAsErrors=false</c> to reach the test at all, which is the same fact
    ///     that puts these tests in their own project.
    /// </remarks>
    [Fact]
    public void AClosedComponentIsNotVXS0401_AndIsRegistered() {
        var (diagnostics, sources) = GeneratorHarness.Run(
            new ComponentRegistrationGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            [Component]
            [DataContract]
            public struct Position {
                public float X;
            }
            """
        );

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "VXS0401");
        Assert.Contains("Declare<global::Subject.Position>()", Assert.Single(sources));
    }

    [Fact]
    public void AGenericComponentIsStillReportedWhenNothingWouldBeEmittedAnyway() {
        // The generic component carries no [DataContract], so it was never going to be registered.
        // Reported all the same: the generator's own remark says suppressing it there would make the
        // warning come and go with a project reference.
        var (diagnostics, _) = GeneratorHarness.Run(
            new ComponentRegistrationGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            [Component]
            public struct Handle<T> {
                public int Id;
            }
            """
        );

        Assert.Single(diagnostics, diagnostic => diagnostic.Id == "VXS0401");
    }

    [Fact]
    public void AComponentWithoutADataContractIsSilentAndNotRegistered() {
        // The handle case: a component the bridge that owns it writes. Not a diagnostic, and the
        // conjunction is the whole reason there is no denylist.
        var (diagnostics, sources) = GeneratorHarness.Run(
            new ComponentRegistrationGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            [Component]
            public struct PhysicsHandle {
                public int Index;
            }
            """
        );

        Assert.Empty(diagnostics);
        Assert.Empty(sources);
    }

    [Fact]
    public void AComponentDeclaringItsOwnDefaultGetsTheOtherCall() {
        var (_, sources) = GeneratorHarness.Run(
            new ComponentRegistrationGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            [Component]
            [DataContract]
            public struct Health : IDefaultComponent<Health> {
                public float Value;

                public static Health DefaultValue => new() { Value = 100f };
            }
            """
        );

        var emitted = Assert.Single(sources);

        Assert.Contains("DeclareWithDefault<global::Subject.Health>()", emitted);

        // And not both: `DeclareWithDefault` is where `T.DefaultValue` resolves statically, so a
        // component that declares one must not also take the path that ignores it.
        Assert.DoesNotContain("Declare<global::Subject.Health>()", emitted);
    }

    [Fact]
    public void WhatIsEmittedCompiles() {
        // ⚠ The one assertion the generated string cannot make about itself. `DeclareWithDefault<T>`
        // is constrained `where T : struct, IDefaultComponent<T>`, and the generator decides by name
        // whether a type satisfies it — so a wrong answer here is a constraint violation inside code
        // the author never wrote.
        var diagnostics = GeneratorHarness.CompileWithGeneratedCode(
            new ComponentRegistrationGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            [Component]
            [DataContract]
            public struct Health : IDefaultComponent<Health> {
                public float Value;

                public static Health DefaultValue => new() { Value = 100f };
            }

            [Component]
            [DataContract]
            public struct Position {
                public float X;
            }
            """
        );

        Assert.Empty(diagnostics.Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
    }

    [Fact]
    public void RegistrationIsOrdered() {
        // A generator whose output moves for no reason makes every build a diff, so the order is
        // part of the contract rather than an accident of the syntax provider.
        var (_, sources) = GeneratorHarness.Run(
            new ComponentRegistrationGenerator(),
            $$"""
            {{GeneratorHarness.Preamble}}

            [Component]
            [DataContract]
            public struct Zeta { public int Value; }

            [Component]
            [DataContract]
            public struct Alpha { public int Value; }
            """
        );

        var emitted = Assert.Single(sources);

        Assert.True(emitted.IndexOf("Subject.Alpha", StringComparison.Ordinal) < emitted.IndexOf("Subject.Zeta", StringComparison.Ordinal));
    }
}
