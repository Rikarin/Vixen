// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Vixen.DocGen.Guide;

/// <summary>One code fence pulled out of a page, with where it came from.</summary>
/// <param name="Page">The markdown file, repository-relative.</param>
/// <param name="Line">The line the fence opens on, so an error points at the page.</param>
/// <param name="Language">`csharp`, `rvn`, `vxml`, …</param>
/// <param name="Code">The fence's contents.</param>
/// <param name="Compile">Whether the build has to compile it.</param>
/// <param name="Fragment">Whether it needs wrapping in a method before it will.</param>
/// <param name="Reason">Why it is not compiled, when it is not.</param>
sealed record Example(
    string Page,
    int Line,
    string Language,
    string Code,
    bool Compile,
    bool Fragment,
    string? Reason
);

/// <summary>
///     Compiles the examples — docs/plan/25 § 4.3, "the single most valuable gate in the document".
/// </summary>
/// <remarks>
///     <para>
///         Documentation examples rot silently and are the first thing a new user copies. So every
///         fence marked <c>compile</c> is built against the real engine, and a fence that will not
///         build fails <c>CheckDocs</c> with the page and line it came from.
///     </para>
///     <para>
///         In process, and inside a compilation the workspace already produced: the example becomes
///         one more syntax tree in the engine assembly with the widest view of the engine, so it
///         compiles against exactly what the graph was read from. No second project, no
///         <c>dotnet build</c>, and no reference list to keep in step — which is the part that would
///         have rotted.
///     </para>
/// </remarks>
static class Examples {
    /// <summary>
    ///     The usings an example gets for free. A page teaching `World.Query` should show the query,
    ///     not six using directives — and a fence that needs something else says so itself.
    /// </summary>
    const string Preamble =
        """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Threading;
        using System.Threading.Tasks;
        """;

    /// <summary>What one example turned into.</summary>
    /// <param name="Example">The fence.</param>
    /// <param name="Errors">Compile errors, each already pointing at the page.</param>
    public sealed record Result(Example Example, IReadOnlyList<string> Errors);

    /// <summary>
    ///     The compilable source for a fence, and where the fence's own text starts inside it.
    /// </summary>
    /// <remarks>
    ///     Shared with <see cref="Highlighter" /> on purpose: the colours are computed from the same
    ///     tree the gate compiles, so a fence cannot be checked as one thing and coloured as another.
    ///     The offset is what maps a token's position back onto the text the reader sees.
    /// </remarks>
    public static (string Source, int Offset) Wrap(Example example, int index) {
        // A namespace of its own, because the host assembly has types of its own and an example is
        // allowed to declare a `Position` without colliding with one.
        var body = example.Fragment
            ? $"static class Example {{\n    static void Run() {{\n{example.Code}\n    }}\n}}"
            : example.Code;

        var source = $"{Preamble}\n\nnamespace DocExample{index} {{\n{body}\n}}";

        return (source, source.IndexOf(example.Code, StringComparison.Ordinal));
    }

    /// <summary>
    ///     The compilation an example is built inside, and the parse options its tree must share.
    /// </summary>
    /// <remarks>
    ///     The one with the most references: it is the assembly with the widest view of the engine,
    ///     so an example compiles against as much of it as any one project can see. The options come
    ///     with it because a compilation's trees have to agree about language version — a tree parsed
    ///     with the defaults cannot join one parsed with the project's, and Roslyn rejects the whole
    ///     compilation rather than compiling the example wrongly.
    /// </remarks>
    public static (Compilation? Host, CSharpParseOptions? ParseOptions) Host(IReadOnlyList<Compilation> engine) {
        var host = engine.OrderByDescending(compilation => compilation.References.Count()).FirstOrDefault();

        return (host, host?.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions);
    }

    public static IReadOnlyList<Result> Compile(
        IReadOnlyList<Example> examples,
        IReadOnlyList<Compilation> engine,
        CancellationToken cancellationToken
    ) {
        var compilable = examples.Where(example => example is { Compile: true, Language: "csharp" }).ToList();

        if (compilable.Count == 0) {
            return [];
        }

        // ⚠ The example is compiled *inside* an engine compilation rather than in a new one of
        // its own. Building a reference set by hand looks obvious and is not: the workspace's
        // compilations carry a hundred and twenty distinct instances of each framework assembly,
        // and a compilation handed those cannot bind a corlib — which surfaces as
        // `CS0518: Predefined type 'System.Void' is not defined`, an error that reads like a
        // missing reference rather than a surplus of them. Adding a tree to a compilation that
        // already works inherits its references and its options, and cannot be wrong about either.
        var (host, parseOptions) = Host(engine);

        if (host is null) {
            return [];
        }

        var results = new List<Result>(compilable.Count);

        for (var index = 0; index < compilable.Count; index++) {
            var example = compilable[index];

            var (source, _) = Wrap(example, index);
            var tree = CSharpSyntaxTree.ParseText(source, parseOptions, cancellationToken: cancellationToken);

            var errors = host.AddSyntaxTrees(tree)
                .GetDiagnostics(cancellationToken)
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Where(diagnostic => diagnostic.Location.SourceTree == tree)
                .Select(diagnostic =>
                    $"{example.Page}:{example.Line}: {diagnostic.Id}: {diagnostic.GetMessage()}")
                .ToList();

            results.Add(new Result(example, errors));
        }

        return results;
    }
}
