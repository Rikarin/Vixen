// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

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
    ///     <para>
    ///         The one with the most references, <b>plus every other documented assembly as a
    ///         reference</b>. The widest single project is still only as wide as its own dependency
    ///         closure, and a guide page for anything outside that closure could never have a
    ///         compiled example — <c>Vixen.Physics</c> was the case that found this, with every fence
    ///         on its page failing to bind its own namespace.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Only the engine's own compilations are added, never framework assemblies.</b>
    ///         The workspace carries a hundred and twenty distinct instances of each of those, and a
    ///         compilation handed the surplus cannot bind a corlib — which surfaces as
    ///         <c>CS0518: Predefined type 'System.Void' is not defined</c>, an error that reads like a
    ///         missing reference rather than like too many. Starting from a compilation that already
    ///         works and adding only project references keeps that settled.
    ///     </para>
    ///     <para>
    ///         The options come with the host because a compilation's trees have to agree about
    ///         language version — a tree parsed with the defaults cannot join one parsed with the
    ///         project's, and Roslyn rejects the whole compilation rather than compiling the example
    ///         wrongly.
    ///     </para>
    /// </remarks>
    public static (Compilation? Host, CSharpParseOptions? ParseOptions) Host(IReadOnlyList<Compilation> engine) {
        var widest = engine.OrderByDescending(compilation => compilation.References.Count()).FirstOrDefault();

        if (widest is null) {
            return (null, null);
        }

        var absent = engine
            .Where(compilation => !ReferenceEquals(compilation, widest))
            .Where(compilation => compilation.AssemblyName is not null)
            .Where(compilation => !widest.ReferencedAssemblyNames.Any(name =>
                string.Equals(name.Name, compilation.AssemblyName, StringComparison.Ordinal)))
            .Select(compilation => compilation.ToMetadataReference())
            .ToArray();

        var host = absent.Length == 0 ? widest : widest.AddReferences(absent);
        var options = host.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions;

        // ⚠ The host's documentation mode is deliberately NOT inherited, and this is the one option
        // that has to be overruled rather than taken. A guide example is not public API surface: it
        // is eight lines showing how a type is used, and `/// <summary>` on every struct in it would
        // make every example longer than the idea it exists to convey. With `Diagnose` the compiler
        // raises CS1591 on each of them, and this repository treats warnings as errors — so the
        // examples would fail to "compile" for having no XML comments.
        //
        // What makes it worth a paragraph is HOW it was found. The mode arrived from whichever
        // project happened to have the most references, so the gate's strictness was a property of
        // the reference graph rather than a decision: it was off while the widest compilation was a
        // tooling project, and adding `Vixen.Live.Gate` — which carries the whole ASP.NET framework
        // reference — turned it on for eighty-nine examples in areas nobody had touched. `Parse`
        // keeps the doc-comment trivia the highlighter colours and stops the diagnostics.
        return (host, options?.WithDocumentationMode(DocumentationMode.Parse));
    }

    /// <summary>
    ///     The engine's own rules a compiled fence is held to, by diagnostic id.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Until <a href="https://github.com/Rikarin/Vixen/issues/1238">#1238</a> the guide
    ///         corpus was checked against the compiler and not against this repository's own
    ///         rules</b>, so every example a shipped analyzer would refuse compiled clean. The one
    ///         that found it is the page about world serialisation, whose remapping example declared
    ///         a <c>[Component] [DataContract]</c> struct holding an <c>Entity</c> — <c>VXS0416</c>,
    ///         an error, on the page about the operation the example exists to explain.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A named list of ids and not "every analyzer", which is a decision rather than
    ///         laziness.</b> The tree ships analyzers whose subject is a call site rather than a
    ///         shape — <c>CheckStrings</c>' declaration rules, the hot-path allocation warning — and
    ///         an eight-line fence is not the program they were written about. These three are the
    ///         rules about what a type may <em>hold</em>: they fire on the declaration a reader
    ///         copies into their own project, and are an error there. Widening the list is a corpus
    ///         run, not an edit.
    ///     </para>
    /// </remarks>
    public static readonly ImmutableArray<string> Enforced = ["VXS0413", "VXS0414", "VXS0416"];

    /// <summary>Roslyn's own id for an analyzer that threw, which is reported alongside the rules.</summary>
    /// <remarks>
    ///     An analyzer that threw is the shape of a gate that did not run — <c>CompilationWithAnalyzers</c>
    ///     turns the exception into this rather than propagating it — so it is let through the filter
    ///     below instead of being dropped with everything the list does not name.
    /// </remarks>
    const string AnalyzerFailed = "AD0001";

    /// <summary>
    ///     Every analyzer the workspace resolved, deduplicated, keeping the ones that report an
    ///     <see cref="Enforced" /> rule.
    /// </summary>
    /// <remarks>
    ///     By type name rather than by instance: an analyzer arrives once per project that names it,
    ///     and this repository names <c>Vixen.Engine.Generators</c> from a few dozen. Running the same
    ///     rule forty times would report each finding forty times.
    /// </remarks>
    /// <param name="analyzers">Every analyzer, from every project the workspace loaded.</param>
    /// <returns>The set to run over a fence.</returns>
    public static ImmutableArray<DiagnosticAnalyzer> Rules(IEnumerable<DiagnosticAnalyzer> analyzers) {
        ArgumentNullException.ThrowIfNull(analyzers);

        return [
            .. analyzers
                .GroupBy(analyzer => analyzer.GetType().FullName ?? string.Empty, StringComparer.Ordinal)
                .Select(group => group.First())
                .Where(analyzer => analyzer.SupportedDiagnostics
                    .Any(rule => Enforced.Contains(rule.Id, StringComparer.Ordinal)))
                .OrderBy(analyzer => analyzer.GetType().FullName, StringComparer.Ordinal)
        ];
    }

    /// <summary>
    ///     ⚠ The instrument, asked what it prints on the day it does not run: which
    ///     <see cref="Enforced" /> rules no loaded analyzer reports.
    /// </summary>
    /// <remarks>
    ///     A gate that resolved no analyzers checks nothing and looks identical to a clean corpus —
    ///     which is how this whole class of example survived until #1238. The analyzers arrive from
    ///     the workspace's own <c>@(Analyzer)</c> items, so they are absent for the same reasons the
    ///     generators are: the tree built in another configuration, or a load failure Roslyn reports
    ///     by handing back an empty list.
    /// </remarks>
    /// <param name="rules">The set from <see cref="Rules" />.</param>
    /// <returns>The unreported ids, in order; empty when the gate can do its job.</returns>
    public static IReadOnlyList<string> Unreported(ImmutableArray<DiagnosticAnalyzer> rules) => [
        .. Enforced.Where(id => rules.IsDefaultOrEmpty
            || !rules.Any(analyzer => analyzer.SupportedDiagnostics
                .Any(rule => string.Equals(rule.Id, id, StringComparison.Ordinal))))
    ];

    /// <summary>What the engine's own rules say about one fence's tree.</summary>
    /// <remarks>
    ///     ⚠ <b>Per tree rather than <c>GetAllDiagnosticsAsync</c>.</b> The host is a whole engine
    ///     project; analysing it two hundred times would be the gate's entire cost and would report
    ///     the engine's own diagnostics as the guide's. A semantic pass filtered to the fence's tree
    ///     runs the symbol actions for the symbols that tree declares, which is exactly the question.
    /// </remarks>
    /// <param name="compilation">The host with the fence's tree already added.</param>
    /// <param name="tree">The fence's tree.</param>
    /// <param name="rules">The analyzers, from <see cref="Rules" />.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The diagnostics an <see cref="Enforced" /> rule reported inside the fence.</returns>
    public static async Task<IReadOnlyList<Diagnostic>> AnalyzeAsync(
        Compilation compilation,
        SyntaxTree tree,
        ImmutableArray<DiagnosticAnalyzer> rules,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(tree);

        if (rules.IsDefaultOrEmpty) {
            return [];
        }

        var withAnalyzers = compilation.WithAnalyzers(
            rules,
            new CompilationWithAnalyzersOptions(
                new AnalyzerOptions([]),
                onAnalyzerException: null,
                concurrentAnalysis: false,
                logAnalyzerExecutionTime: false
            )
        );

        var reported = await withAnalyzers.GetAnalyzerSemanticDiagnosticsAsync(
            compilation.GetSemanticModel(tree),
            null,
            cancellationToken
        );

        reported = reported.AddRange(
            await withAnalyzers.GetAnalyzerSyntaxDiagnosticsAsync(tree, cancellationToken));

        return [
            .. reported
                // An AD0001 has no location inside the fence, so the tree filter would drop it and
                // an analyzer that threw would read as a clean run.
                .Where(diagnostic => string.Equals(diagnostic.Id, AnalyzerFailed, StringComparison.Ordinal)
                    || (Enforced.Contains(diagnostic.Id, StringComparer.Ordinal)
                        && diagnostic.Location.SourceTree == tree))
        ];
    }

    public static IReadOnlyList<Result> Compile(
        IReadOnlyList<Example> examples,
        IReadOnlyList<Compilation> engine,
        ImmutableArray<DiagnosticAnalyzer> rules,
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

            var withExample = host.AddSyntaxTrees(tree);

            var errors = withExample
                .GetDiagnostics(cancellationToken)
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Where(diagnostic => diagnostic.Location.SourceTree == tree)
                .Concat(AnalyzeAsync(withExample, tree, rules, cancellationToken).GetAwaiter().GetResult())
                .Select(diagnostic =>
                    $"{example.Page}:{example.Line}: {diagnostic.Id}: {diagnostic.GetMessage()}")
                .ToList();

            results.Add(new Result(example, errors));
        }

        return results;
    }
}
