// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Vixen.Core.Analyzers.Tests;

/// <summary>Runs one of this project's analyzers over a string of C#, the way the compiler would.</summary>
/// <remarks>
///     The references come from this assembly's own load set, which is what puts the real
///     <c>Vixen.Core.HotPathAttribute</c> in front of the analyzer rather than a fixture's copy of it.
///     A rule keyed on <c>GetTypeByMetadataName</c> is silent when the name resolves to nothing, so a
///     harness that forgot the reference would pass every negative test and fail every positive one —
///     which is why <see cref="HotPathAllocationAnalyzerTests.TheHarnessCompilationCanNameTheRealAttribute" />
///     asks the compilation whether it can see the type before any rule test trusts it.
/// </remarks>
public static class AnalyzerHarness {
    static readonly ImmutableArray<MetadataReference> References = CollectReferences();

    /// <summary>Compiles source and runs <see cref="HotPathAllocationAnalyzer" /> over it.</summary>
    /// <param name="source">The C# to compile. It has to compile: a snippet with an error in it binds
    ///     to nothing, and an analyzer that reports nothing about nothing would pass.</param>
    /// <returns>What the analyzer reported.</returns>
    public static Task<ImmutableArray<Diagnostic>> RunAsync(string source) =>
        RunAsync(new HotPathAllocationAnalyzer(), source);

    /// <summary>Compiles source and runs one analyzer over it.</summary>
    /// <param name="analyzer">The rule under test.</param>
    /// <param name="source">The C# to compile, which has to compile for the same reason.</param>
    /// <returns>What that analyzer reported, and nothing another one would have.</returns>
    public static async Task<ImmutableArray<Diagnostic>> RunAsync(DiagnosticAnalyzer analyzer, string source) {
        var compilation = Compile(source);

        return await compilation
            .WithAnalyzers(ImmutableArray.Create(analyzer))
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Compiles source, failing loudly rather than quietly analysing broken code.</summary>
    /// <param name="source">The C# to compile.</param>
    /// <returns>The compilation.</returns>
    public static CSharpCompilation Compile(string source) {
        var tree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(documentationMode: DocumentationMode.Diagnose),
            "Frame.cs"
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
    public static string Underlined(Diagnostic diagnostic) {
        ArgumentNullException.ThrowIfNull(diagnostic);

        return diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan);
    }

    static ImmutableArray<MetadataReference> CollectReferences() {
        var references = ImmutableArray.CreateBuilder<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // ⚠ GetAssemblies() lists what is LOADED, and a ProjectReference nothing has touched yet is
        // not loaded — so Vixen.Core goes in by name rather than by hoping. `_ = typeof(...)` is not
        // enough: a discard of a side-effect-free expression compiles to no IL at all, the token is
        // never resolved, and the assembly is never loaded. That mistake cost sixteen red tests.
        foreach (var location in AppDomain.CurrentDomain.GetAssemblies()
                     .Where(assembly => !assembly.IsDynamic && assembly.Location.Length != 0)
                     .Select(assembly => assembly.Location)
                     .Append(typeof(HotPathAttribute).Assembly.Location)) {
            if (seen.Add(location)) {
                references.Add(MetadataReference.CreateFromFile(location));
            }
        }

        return references.ToImmutable();
    }
}
