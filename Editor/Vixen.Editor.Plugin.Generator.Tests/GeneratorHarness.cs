// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Vixen.Editor.Plugin.Generator.Tests;

/// <summary>Runs <see cref="DeclaredRegistrationGenerator" /> over a string of C#, the way the compiler would.</summary>
/// <remarks>
///     ⚠ <b>Against the real editor assemblies</b>, every one the test process has loaded — the same
///     set <c>PluginFolder</c> compiles a plugin against. What the generator writes is a call into
///     <c>DeclaredMenus</c>, <c>IEditorRegistry</c> and five records, and only the shipped ones can say
///     whether it compiles.
/// </remarks>
public static class GeneratorHarness {
    static readonly ImmutableArray<MetadataReference> References = [
        .. ((string) AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .Select(path => (MetadataReference) MetadataReference.CreateFromFile(path))
    ];

    /// <summary>Compiles source, runs the generator, and hands back what it produced and said.</summary>
    /// <param name="source">The C#.</param>
    /// <param name="assembly">The compilation's assembly name, which is the generated class's namespace.</param>
    /// <returns>The generator's diagnostics, its sources, and the compiler's diagnostics over the result.</returns>
    public static Result Run(string source, string assembly = "Fixture") {
        var compilation = Compile(source, assembly);

        var driver = CSharpGeneratorDriver
            .Create(new DeclaredRegistrationGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);

        var run = driver.GetRunResult().Results[0];

        return new(
            run.Diagnostics,
            [.. run.GeneratedSources.Select(static produced => produced.SourceText.ToString())],
            updated.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToImmutableArray(),
            updated
        );
    }

    /// <summary>Compiles the fixture, and refuses one that does not bind.</summary>
    /// <remarks>
    ///     ⚠ <b>The instrument, not a convenience</b> — <c>Vixen.Engine.Generators.Tests</c> found four
    ///     green tests over a compilation with four errors in it. A fixture naming an error type gives
    ///     the generator nothing to read, and a test asserting a diagnostic did not fire would pass for
    ///     that reason.
    /// </remarks>
    static CSharpCompilation Compile(string source, string assembly) {
        var compilation = CSharpCompilation.Create(
            assembly,
            [CSharpSyntaxTree.ParseText(source, path: "Fixture.cs")],
            References,
            new(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
        );

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        return compilation;
    }

    /// <summary>What one run produced.</summary>
    /// <param name="Generator">What the generator reported.</param>
    /// <param name="Sources">What it wrote.</param>
    /// <param name="Errors">The compiler's errors over the source plus what it wrote.</param>
    /// <param name="Compilation">The updated compilation, for a test that wants to emit it.</param>
    public sealed record Result(
        ImmutableArray<Diagnostic> Generator,
        ImmutableArray<string> Sources,
        ImmutableArray<Diagnostic> Errors,
        Compilation Compilation
    );
}
