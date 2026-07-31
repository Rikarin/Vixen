// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Vixen.DocGen;

/// <summary>Who uses what — docs/plan/25 § 2.4's `used-by` edge.</summary>
/// <remarks>
///     <para>
///         <b>The most valuable edge and the only expensive one.</b>
///         <c>SymbolFinder.FindReferencesAsync</c> per symbol over 243 projects is quadratic and not
///         affordable; this is one pass over every syntax tree instead, recording
///         (enclosing declaration → referenced type) as it goes. One traversal, edges in both
///         directions.
///     </para>
///     <para>
///         ⚠ <b>References are collected from everywhere, including the projects the graph does not
///         document.</b> That is the point: "used by <c>Samples/03-PbrShowcase</c>" is worth ten
///         times "referenced 400 times", and a sample is exactly the project
///         <see cref="Scope.IsDocumented" /> leaves out. Documented by area, referenced from
///         everywhere.
///     </para>
/// </remarks>
sealed class ReferenceIndex {
    /// <summary>How many referencing types a node carries. The rest are a count.</summary>
    const int PerNodeLimit = 40;

    readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DocReference>> byTarget = new(StringComparer.Ordinal);

    /// <summary>Builds the index over every project, documented or not.</summary>
    /// <param name="projects">Every loaded project.</param>
    /// <param name="known">The documentation ids the graph has nodes for; anything else is noise.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<ReferenceIndex> BuildAsync(
        IReadOnlyList<LoadedProject> projects,
        IReadOnlySet<string> known,
        CancellationToken cancellationToken
    ) {
        var index = new ReferenceIndex();

        await Parallel.ForEachAsync(
            projects,
            new ParallelOptions {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Environment.ProcessorCount
            },
            async (project, token) => {
                foreach (var tree in project.Compilation.SyntaxTrees) {
                    index.Visit(project, tree, known, token);
                }

                await Task.CompletedTask;
            });

        return index;
    }

    void Visit(LoadedProject project, SyntaxTree tree, IReadOnlySet<string> known, CancellationToken cancellationToken) {
        var model = project.Compilation.GetSemanticModel(tree);
        var root = tree.GetRoot(cancellationToken);

        foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>()) {
            cancellationToken.ThrowIfCancellationRequested();

            // The right-hand side of a qualified name is visited on its own; taking both would
            // count `Vixen.Ecs.World` twice and, worse, count the namespace as a reference.
            if (name.Parent is QualifiedNameSyntax qualified && qualified.Left == name) {
                continue;
            }

            var symbol = model.GetSymbolInfo(name, cancellationToken).Symbol;

            // A reference to a member is a reference to the type that declares it: somebody reading
            // `World` wants to know that `MovementSystem` calls `World.Query`, not to see `Query`
            // listed on its own.
            var type = symbol as INamedTypeSymbol
                ?? (symbol is { Kind: not SymbolKind.Namespace } ? symbol.ContainingType : null);

            if (type is null) {
                continue;
            }

            var target = (type.OriginalDefinition ?? type).GetDocumentationCommentId();

            if (target is null || !known.Contains(target)) {
                continue;
            }

            var source = Enclosing(model, name, cancellationToken);

            // A type referring to itself is not a use of it, and neither is a member of it.
            if (source is null || string.Equals(source.Id, target, StringComparison.Ordinal)) {
                continue;
            }

            byTarget
                .GetOrAdd(target, _ => new ConcurrentDictionary<string, DocReference>(StringComparer.Ordinal))
                .TryAdd(source.Id, source with { Area = project.Area, Assembly = project.Name });
        }
    }

    /// <summary>The type declaration the reference sits inside, or null at file scope.</summary>
    static DocReference? Enclosing(SemanticModel model, SyntaxNode node, CancellationToken cancellationToken) {
        for (var current = node.Parent; current is not null; current = current.Parent) {
            if (current is not BaseTypeDeclarationSyntax declaration) {
                continue;
            }

            var symbol = model.GetDeclaredSymbol(declaration, cancellationToken);
            var id = symbol?.GetDocumentationCommentId();

            return id is null ? null : new DocReference(id, symbol!.Name, string.Empty, string.Empty);
        }

        return null;
    }

    /// <summary>
    ///     The references to one node, samples first — a use in a sample is a worked example, and a
    ///     use in the engine is an implementation detail.
    /// </summary>
    public (IReadOnlyList<DocReference> Shown, int Total) For(string id) {
        if (!byTarget.TryGetValue(id, out var references)) {
            return ([], 0);
        }

        var ordered = references.Values
            .OrderBy(reference => Rank(reference.Area))
            .ThenBy(reference => reference.Name, StringComparer.Ordinal)
            .ToList();

        return (ordered.Count <= PerNodeLimit ? ordered : ordered.Take(PerNodeLimit).ToList(), ordered.Count);
    }

    static int Rank(string area) => area switch {
        "Samples" => 0,
        "Benchmarks" => 1,
        "Core" or "Platform" => 2,
        "Editor" or "Tools" or "Raven" => 3,
        _ => 4
    };
}
