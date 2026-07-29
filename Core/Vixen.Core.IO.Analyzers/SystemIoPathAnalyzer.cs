// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Vixen.Core.IO.Analyzers;

/// <summary>Reports engine code that reaches for <c>System.IO.Path</c> instead of a virtual path.</summary>
/// <remarks>
///     <para>
///         The rule is <c>docs/plan/10-platforms.md</c>'s: only virtual paths in engine code, and
///         <c>System.IO.Path</c> outside <c>Vixen.Platform.*</c>, the editor and the tools. A
///         separator, a drive letter or a case-insensitive comparison written into engine code is a
///         bug that compiles on the machine it was written on and is found on a device.
///     </para>
///     <para>
///         Where the rule does <i>not</i> apply is decided in <c>.editorconfig</c>, not here: the
///         analyzer runs over the projects that reference it and reports every use it can see, and the
///         host-filesystem layers that are allowed to translate — <c>PhysicalFileProvider</c>, the file
///         watcher, the disk caches — turn it off by name with a written reason. A scope that lives in
///         one file beside every other exclusion is reviewable; one spread across csproj properties is
///         not.
///     </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SystemIoPathAnalyzer : DiagnosticAnalyzer {
    /// <summary>The id reported for a use of <c>System.IO.Path</c>.</summary>
    public const string DiagnosticId = "VXIO0001";

    const string PathMetadataName = "System.IO.Path";

    static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "System.IO.Path in engine code",
        "{0} addresses the host filesystem. Engine code addresses files as a VirtualPath through "
        + "VirtualFileSystem; System.IO.Path belongs to Vixen.Platform.*, the editor and the tools.",
        "Vixen.IO",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        "Six platforms have six ideas about where files are, and a path assembled from System.IO.Path "
        + "carries the ideas of the one it was assembled on: a separator, a root, a case-insensitive "
        + "comparison. Engine code says /app/textures/x.ktx2 and lets the mounted provider decide what "
        + "that is."
    );

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context) {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(
            start => {
                var path = start.Compilation.GetTypeByMetadataName(PathMetadataName);

                if (path is null) {
                    return;
                }

                // Every identifier in the compilation runs through this action, so the name is the
                // filter and the semantic model is only asked about the few that could be a hit.
                // `Path` catches the type; its member names catch what a `using static` brings in
                // unqualified.
                var names = new HashSet<string>(path.MemberNames, StringComparer.Ordinal) { path.Name };

                start.RegisterSyntaxNodeAction(
                    node => Analyze(node, path, names),
                    SyntaxKind.IdentifierName
                );
            }
        );
    }

    static void Analyze(SyntaxNodeAnalysisContext context, INamedTypeSymbol path, HashSet<string> names) {
        var identifier = (IdentifierNameSyntax)context.Node;

        if (!names.Contains(identifier.Identifier.ValueText)) {
            return;
        }

        // A `<see cref="Path.GetExtension(string)" />` explaining what a VirtualPath does differently
        // is documentation about the BCL, not a use of it.
        if (identifier.FirstAncestorOrSelf<CrefSyntax>() is not null) {
            return;
        }

        var info = context.SemanticModel.GetSymbolInfo(identifier, context.CancellationToken);

        // A method group that failed overload resolution has candidates rather than a symbol, and it
        // is still a use: `Func<string, string, string> f = Path.Combine;` names the thing.
        var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();

        if (symbol is null) {
            return;
        }

        // The type itself, or one of its members reached without naming it.
        var used = symbol as INamedTypeSymbol ?? symbol.ContainingType;

        if (!SymbolEqualityComparer.Default.Equals(used, path)) {
            return;
        }

        var expression = Widen(identifier);

        // `Path.Combine` arrives twice — once as the type, once as the member — and both widen to
        // the same expression. The member is the one that can say which member, so the type reports
        // only where it stands alone: an import, an alias, a `typeof`.
        if (symbol is INamedTypeSymbol && expression is MemberAccessExpressionSyntax) {
            return;
        }

        var name = symbol is INamedTypeSymbol ? PathMetadataName : $"{PathMetadataName}.{symbol.Name}";

        context.ReportDiagnostic(Diagnostic.Create(Rule, expression.GetLocation(), name));
    }

    /// <summary>Grows an identifier into the whole expression it was written as.</summary>
    /// <remarks>
    ///     <c>System.IO.Path.Combine</c> reaches the analyzer as the identifier <c>Path</c> in the
    ///     middle of it. Reporting there underlines one segment of an expression and leaves the reader
    ///     to work out which; reporting the expression underlines what they wrote.
    /// </remarks>
    static SyntaxNode Widen(SyntaxNode node) {
        while (true) {
            if (node.Parent is QualifiedNameSyntax qualified && qualified.Right == node) {
                node = qualified;
            } else if (node.Parent is MemberAccessExpressionSyntax qualifying && qualifying.Name == node) {
                node = qualifying;
            } else {
                break;
            }
        }

        // Having taken the qualification, take the member: `Path.Combine`, not `Path`.
        return node.Parent is MemberAccessExpressionSyntax access && access.Expression == node ? access : node;
    }
}
