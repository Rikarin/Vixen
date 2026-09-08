// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Engine.Behaviors;
using Xunit;

namespace Vixen.Engine.Generators.Tests;

/// <summary>Runs one of the engine's generators over a string of C#, the way the compiler would.</summary>
/// <remarks>
///     <para>
///         <c>Vixen.Net.Generators.Tests.GeneratorHarness</c>'s shape, with the generator passed in
///         rather than chosen by a bool: there are four of them here and they answer about four
///         different attributes.
///     </para>
///     <para>
///         ⚠ <b>The compilation under test references the real <c>Vixen.Ecs</c> and
///         <c>Vixen.Engine</c>.</b> Three of these generators decide whether to emit at all by
///         asking whether their registry type is reachable, so a compilation that could not see
///         <c>SceneComponentRegistry</c> would produce no source and every emission assertion would
///         pass vacuously against nothing.
///     </para>
/// </remarks>
public static class GeneratorHarness {
    /// <summary>The usings and namespace every fixture in this assembly starts from.</summary>
    /// <remarks>
    ///     A namespace rather than the global one, because <c>SystemAccessInferenceGenerator</c>
    ///     emits into the type's own namespace and the global case would test the other branch.
    /// </remarks>
    public const string Preamble = """
        using Vixen.Core;
        using Vixen.Ecs;
        using Vixen.Ecs.Systems;
        using Vixen.Core.Threading;
        using Vixen.Engine.Behaviors;
        using Vixen.Engine.Frames;

        namespace Subject;
        """;

    static readonly ImmutableArray<MetadataReference> References = CollectReferences();

    /// <summary>Compiles source and runs one generator over it.</summary>
    /// <param name="generator">The generator to drive.</param>
    /// <param name="source">The C# to compile.</param>
    /// <returns>What the generator produced and complained about.</returns>
    public static (ImmutableArray<Diagnostic> Diagnostics, ImmutableArray<string> Sources) Run(
        IIncrementalGenerator generator,
        string source
    ) {
        var driver = CSharpGeneratorDriver.Create(generator).RunGenerators(Compile(source));
        var result = driver.GetRunResult().Results[0];
        var generated = ImmutableArray.CreateBuilder<string>();

        foreach (var produced in result.GeneratedSources) {
            generated.Add(produced.SourceText.ToString());
        }

        return (result.Diagnostics, generated.ToImmutable());
    }

    /// <summary>Everything the compiler says about the source once the generator has added to it.</summary>
    /// <param name="generator">The generator to drive.</param>
    /// <param name="source">The C# to compile.</param>
    /// <returns>The diagnostics of the updated compilation, generated code included.</returns>
    /// <remarks>
    ///     ⚠ <b>An emission test that only reads the generated string is half a test.</b> Every one
    ///     of these generators writes a call it cannot type-check — a closed generic, a constraint,
    ///     a cast per constructor parameter — so what matters is that the result compiles, which
    ///     only the compiler can say.
    /// </remarks>
    public static ImmutableArray<Diagnostic> CompileWithGeneratedCode(IIncrementalGenerator generator, string source) {
        CSharpGeneratorDriver.Create(generator).RunGeneratorsAndUpdateCompilation(Compile(source), out var updated, out _);

        return updated.GetDiagnostics();
    }

    /// <summary>Compiles the fixture, and refuses one that does not bind.</summary>
    /// <remarks>
    ///     ⚠ <b>The check is the instrument, not a convenience.</b> Every rule under test is decided
    ///     from symbols: a fixture with a binding error names an error type, the generator infers
    ///     nothing from it, and a test asserting that some diagnostic did <em>not</em> fire passes
    ///     for a reason that has nothing to do with the rule. Writing this harness turned up exactly
    ///     that — <c>Vixen.Core.Threading</c> was absent from <see cref="References" /> because
    ///     nothing had loaded it, so every fixture's <c>using</c> was an error and four of these
    ///     tests were green over a compilation with four errors in it.
    /// </remarks>
    static CSharpCompilation Compile(string source) {
        var compilation = CSharpCompilation.Create(
            "GeneratedUnderTest",
            [CSharpSyntaxTree.ParseText(source)],
            References,
            new(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
        );

        Assert.Empty(
            compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
        );

        return compilation;
    }

    static ImmutableArray<MetadataReference> CollectReferences() {
        // Touch a type from each assembly the source under test names, so that it is loaded and
        // therefore in the list below. A harness that discovered its references by walking
        // directories would break the first time somebody moved one — Vixen.Net.Generators.Tests's
        // note, and the same arrangement.
        _ = typeof(World).Assembly;
        _ = typeof(Behavior).Assembly;
        _ = typeof(Core.Entity).Assembly;
        _ = typeof(JobHandle).Assembly;

        var references = ImmutableArray.CreateBuilder<MetadataReference>();

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
            if (!assembly.IsDynamic && assembly.Location.Length != 0) {
                references.Add(MetadataReference.CreateFromFile(assembly.Location));
            }
        }

        return references.ToImmutable();
    }
}
