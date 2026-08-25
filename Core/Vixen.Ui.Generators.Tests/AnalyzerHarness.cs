// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Vixen.Ui.Generators.Tests;

/// <summary>Runs a Vixen.Ui analyzer over a string of C#, the way the compiler would.</summary>
/// <remarks>
///     ⚠ <b>The source under test brings its own <c>Vixen.Ui.StringId</c></b>, declared by
///     <see cref="StringId" /> below. The analyzer resolves a metadata name, so a declaration in the
///     compilation under test is the same thing to it as one in a referenced assembly — and a test
///     that referenced the real <c>Vixen.Ui</c> would be asserting that a restore happened.
/// </remarks>
public static class AnalyzerHarness {
    /// <summary>A <c>StringId</c> good enough to bind against, in the namespace the analyzer looks in.</summary>
    /// <remarks>
    ///     The record's two positional members are the id and the source text, which is
    ///     <c>Vixen.Ui.StringId</c>'s whole shape as far as anything here is concerned. Doc 46 § A3 is
    ///     why it is copied rather than referenced: the declaration shape is the contract, and a copy
    ///     of it in a test is the same statement about the shape that a second generator makes.
    /// </remarks>
    public const string StringId = """
        namespace Vixen.Ui {
            public readonly record struct StringId(string Id, string Source);
        }
        """;

    static readonly ImmutableArray<MetadataReference> References = CollectReferences();

    /// <summary>Compiles source and runs the analyzer over it.</summary>
    /// <param name="source">The C# to compile, without the <see cref="StringId" /> declaration —
    ///     which is appended. It has to compile: a snippet with an error in it binds to nothing, and
    ///     an analyzer that reported nothing about nothing would pass.</param>
    /// <returns>What the analyzer reported, in file order.</returns>
    /// <remarks>
    ///     Appended rather than prepended, because a <c>using</c> has to be the first thing in a file
    ///     and every snippet starts with one.
    /// </remarks>
    public static async Task<ImmutableArray<Diagnostic>> RunAsync(string source) {
        var tree = CSharpSyntaxTree.ParseText(source + Environment.NewLine + StringId, path: "Declarations.cs");

        var compilation = CSharpCompilation.Create(
            "ApplicationUnderTest",
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

        var reported = await compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new StringDeclarationAnalyzer()))
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);

        return [.. reported.OrderBy(diagnostic => diagnostic.Location.SourceSpan.Start)];
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

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
            if (!assembly.IsDynamic && assembly.Location.Length != 0) {
                references.Add(MetadataReference.CreateFromFile(assembly.Location));
            }
        }

        return references.ToImmutable();
    }
}
