// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Vixen.Core.Reflection.Generator;
using Xunit;

namespace Vixen.Core.Reflection.Tests;

/// <summary>VXS0201, and the half of generics the refusal is not about.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Nothing asserted this rule until now.</b> <c>git grep VXS0201</c> found the
///         descriptor, its <c>AnalyzerReleases.Unshipped.md</c> line and three pieces of prose —
///         a refusal nobody had watched run, on a generator three subsystems depend on.
///     </para>
///     <para>
///         ⚠ <b>And it cannot be tested from <c>Described.cs</c>.</b> Warnings are errors repo-wide
///         (<c>Directory.Build.props</c>), so a <c>[DataContract] class Box&lt;T&gt;</c> next door
///         fails the build rather than being observed. The generator is constructed and driven over
///         a string instead, which is <c>Vixen.Net.Generators.Tests.GeneratorHarness</c>'s shape.
///     </para>
/// </remarks>
public sealed class TypeDescriptorGeneratorTests {
    const string Preamble = """
        using System.Collections.Generic;
        using Vixen.Core;

        namespace Subject;
        """;

    [Fact]
    public void AGenericAnnotatedTypeIsVXS0201_AndGetsNoDescriptor() {
        var (diagnostics, sources) = Run(
            $$"""
            {{Preamble}}

            [DataContract]
            public class Box<T> {
                public T Value { get; set; } = default!;
            }
            """
        );

        var warning = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "VXS0201");

        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("Box<T>", warning.GetMessage());

        // A descriptor names one closed type, so there is no `Describe_Subject_Box` to emit — and
        // with nothing else annotated there is no registration file at all.
        Assert.Empty(sources);
    }

    /// <summary>⚠ The control the refusal is worthless without.</summary>
    /// <remarks>
    ///     A refusal test on its own is satisfied by a generator that refuses everything, and
    ///     equally by one that emitted nothing at all — which is the shape this repository keeps
    ///     finding. The non-generic type is in the <em>same</em> compilation as the generic one, so
    ///     one run says both that the rule fired and that it has an edge. Widened by deleting
    ///     <c>Describe</c>'s <c>if (type.IsGenericType)</c> arm's condition, so every annotated type
    ///     is refused: this goes red and the positive above stays green. Reverted.
    /// </remarks>
    [Fact]
    public void ANonGenericTypeInTheSameCompilationStillGetsADescriptor() {
        var (diagnostics, sources) = Run(
            $$"""
            {{Preamble}}

            [DataContract]
            public class Box<T> {
                public T Value { get; set; } = default!;
            }

            [DataContract]
            public class Settings {
                public int Width { get; set; }
            }
            """
        );

        Assert.Single(diagnostics, diagnostic => diagnostic.Id == "VXS0201");

        var emitted = Assert.Single(sources);

        Assert.Contains("Describe_Subject_Settings()", emitted);
        Assert.DoesNotContain("Describe_Subject_Box", emitted);
    }

    /// <summary>⚠ Where the refusal lands, which used to be nowhere.</summary>
    /// <remarks>
    ///     It was reported at <c>Location.None</c>, so with <c>TreatWarningsAsErrors</c> a user got
    ///     a build error with no file and no line on it — on a generator whose whole job is to run
    ///     inside somebody else's compilation. Every other Vixen generator that can resolve a
    ///     location does. Widened by restoring <c>Location.None</c>: this goes red and every other
    ///     test in this class stays green, which is exactly why the defect survived.
    /// </remarks>
    [Fact]
    public void TheRefusalPointsAtTheTypesOwnName() {
        var (diagnostics, _) = Run(
            $$"""
            {{Preamble}}

            [DataContract]
            public class Box<T> {
                public T Value { get; set; } = default!;
            }
            """
        );

        var warning = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "VXS0201");
        var span = warning.Location.GetLineSpan();

        Assert.NotEqual(Location.None, warning.Location);
        Assert.Equal("Subject.cs", span.Path);

        // The identifier, not the whole declaration: line 6 zero-based is `public class Box<T> {`,
        // and column 13 is where `Box` starts rather than where `public` or `[DataContract]` does.
        Assert.Equal(6, span.StartLinePosition.Line);
        Assert.Equal(13, span.StartLinePosition.Character);
        Assert.Equal(span.StartLinePosition.Line, span.EndLinePosition.Line);
    }

    [Fact]
    public void ATypeNestedInAGenericTypeIsRefusedToo() {
        // ⚠ `INamedTypeSymbol.IsGenericType` is not arity — it walks the containing types — so
        // `Outer<T>.Inner` is refused without the rule mentioning nesting. Asserted because the day
        // somebody writes `Arity > 0` there, the generator starts emitting
        // `Describe_Subject_Outer<T>_Inner`, which does not compile.
        var (diagnostics, _) = Run(
            $$"""
            {{Preamble}}

            public static class Outer<T> {
                [DataContract]
                public class Inner {
                    public int Value { get; set; }
                }
            }
            """
        );

        Assert.Single(diagnostics, diagnostic => diagnostic.Id == "VXS0201");
    }

    /// <summary>The other half of generics, which the refusal is not about and nothing checked.</summary>
    /// <remarks>
    ///     ⚠ <b><c>Described.cs</c> declared no generic member of any kind</b>, so
    ///     <c>CollectFactories</c> and the <c>CollectionFactory.Register</c> call it feeds were
    ///     uncovered — despite being the half of generics that is claimed to work. A described
    ///     <em>member</em> of type <c>List&lt;int&gt;</c> is fine; only a generic <em>declaring</em>
    ///     type is refused.
    /// </remarks>
    [Fact]
    public void ADescribedListMemberReachesCollectionFactory() {
        var (diagnostics, sources) = Run(
            $$"""
            {{Preamble}}

            [DataContract]
            public class Level {
                public List<int> Scores { get; set; } = new();
            }
            """
        );

        Assert.Empty(diagnostics);

        var emitted = Assert.Single(sources);

        Assert.Contains(
            "CollectionFactory.Register(typeof(global::System.Collections.Generic.List<int>), "
            + "static count => new global::System.Collections.Generic.List<int>(count))",
            emitted
        );
    }

    [Fact]
    public void ADescribedDictionaryMemberIsRegisteredBackedByADictionary() {
        var (_, sources) = Run(
            $$"""
            {{Preamble}}

            [DataContract]
            public class Level {
                public Dictionary<string, int> Scores { get; set; } = new();
            }
            """
        );

        Assert.Contains(
            "CollectionFactory.Register(typeof(global::System.Collections.Generic.Dictionary<string, int>), "
            + "static count => new global::System.Collections.Generic.Dictionary<string, int>(count))",
            Assert.Single(sources)
        );
    }

    [Fact]
    public void ADescribedListInterfaceMemberIsRegisteredBackedByAnArray() {
        // An interface is satisfied by an array with no copy, which is the whole reason the two
        // cases emit different factories.
        var (_, sources) = Run(
            $$"""
            {{Preamble}}

            [DataContract]
            public class Level {
                public IList<int> Scores { get; set; } = new List<int>();
            }
            """
        );

        Assert.Contains(
            "CollectionFactory.Register(typeof(global::System.Collections.Generic.IList<int>), "
            + "static count => new int[count])",
            Assert.Single(sources)
        );
    }

    [Fact]
    public void WhatIsEmittedCompiles() {
        // The registration names closed generics and a factory lambda per collection; whether that
        // binds is not something reading the emitted string can say.
        var compilation = Compile(
            $$"""
            {{Preamble}}

            [DataContract]
            public class Box<T> {
                public T Value { get; set; } = default!;
            }

            [DataContract]
            public class Level {
                public List<int> Scores { get; set; } = new();

                public Dictionary<string, int> Best { get; set; } = new();
            }
            """
        );

        var token = TestContext.Current.CancellationToken;

        CSharpGeneratorDriver
            .Create(new TypeDescriptorGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _, token);

        Assert.Empty(
            updated.GetDiagnostics(token).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
        );
    }

    static (ImmutableArray<Diagnostic> Diagnostics, ImmutableArray<string> Sources) Run(string source) {
        var driver = CSharpGeneratorDriver.Create(new TypeDescriptorGenerator()).RunGenerators(Compile(source));
        var result = driver.GetRunResult().Results[0];
        var generated = ImmutableArray.CreateBuilder<string>();

        foreach (var produced in result.GeneratedSources) {
            generated.Add(produced.SourceText.ToString());
        }

        return (result.Diagnostics, generated.ToImmutable());
    }

    /// <summary>Compiles the fixture, and refuses one that does not bind.</summary>
    /// <remarks>
    ///     ⚠ <b>The check is the instrument.</b> Every fact this generator emits is read off a
    ///     symbol, so a fixture with a binding error names an error type and the generator quietly
    ///     describes nothing — which would make "no VXS0201 here" true for a reason that has
    ///     nothing to do with the rule.
    /// </remarks>
    static CSharpCompilation Compile(string source) {
        var token = TestContext.Current.CancellationToken;

        var compilation = CSharpCompilation.Create(
            "DescribedUnderTest",
            [CSharpSyntaxTree.ParseText(source, path: "Subject.cs", cancellationToken: token)],
            References,
            new(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
        );

        Assert.Empty(
            compilation.GetDiagnostics(token).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
        );

        return compilation;
    }

    static ImmutableArray<MetadataReference> CollectReferences() {
        // Touch a type from each assembly the fixtures name, so that it is loaded and therefore in
        // the list below — Vixen.Net.Generators.Tests's arrangement, and its reason: a harness that
        // discovered its references by walking directories would break the first time somebody
        // moved one.
        _ = typeof(DataContractAttribute).Assembly;
        _ = typeof(TypeRegistry).Assembly;

        var references = ImmutableArray.CreateBuilder<MetadataReference>();

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
            if (!assembly.IsDynamic && assembly.Location.Length != 0) {
                references.Add(MetadataReference.CreateFromFile(assembly.Location));
            }
        }

        return references.ToImmutable();
    }

    static readonly ImmutableArray<MetadataReference> References = CollectReferences();
}
