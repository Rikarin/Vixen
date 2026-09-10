// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Vixen.Core.Diagnostics.Analyzers.Tests;

/// <summary>Runs the analyzer over a string of C#, the way the compiler would.</summary>
public static class AnalyzerHarness {
    static readonly ImmutableArray<MetadataReference> References = CollectReferences();

    /// <summary>Compiles source and runs the analyzer over it.</summary>
    /// <param name="source">The C# to compile. It has to compile: a snippet with an error in it
    ///     binds to nothing, and an analyzer that reports nothing about nothing would pass.</param>
    /// <returns>What the analyzer reported.</returns>
    public static async Task<ImmutableArray<Diagnostic>> RunAsync(string source) {
        var compilation = Compile(source);

        return await compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new SilentCatchAnalyzer()))
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Compiles source, failing loudly rather than quietly analysing broken code.</summary>
    /// <param name="source">The C# to compile.</param>
    /// <returns>The compilation.</returns>
    public static CSharpCompilation Compile(string source) {
        var tree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(documentationMode: DocumentationMode.Diagnose),
            "Subsystem.cs"
        );

        var compilation = CSharpCompilation.Create(
            "EngineUnderTest",
            [tree],
            References,
            new(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
        );

        var broken = compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();

        if (broken.Length != 0) {
            throw new InvalidOperationException(
                $"The source under test does not compile: {string.Join("; ", broken)}"
            );
        }

        return compilation;
    }

    /// <summary>The source a diagnostic underlined.</summary>
    /// <param name="diagnostic">The diagnostic.</param>
    /// <returns>The text of its span.</returns>
    /// <remarks>
    ///     Where a diagnostic points is half of what it says. This rule underlines the clause and not
    ///     the body, so the span is part of what the tests assert rather than incidental.
    /// </remarks>
    public static string Underlined(Diagnostic diagnostic) {
        ArgumentNullException.ThrowIfNull(diagnostic);

        return diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan);
    }

    static ImmutableArray<MetadataReference> CollectReferences() {
        var references = ImmutableArray.CreateBuilder<MetadataReference>();

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
            if (!assembly.IsDynamic && assembly.Location.Length != 0) {
                references.Add(MetadataReference.CreateFromFile(assembly.Location));
            }
        }

        return references.ToImmutable();
    }
}
