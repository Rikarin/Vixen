// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Vixen.Core.Analyzers;

/// <summary>Reports a <c>catch</c> that takes every exception and then never mentions it.</summary>
/// <remarks>
///     <para>
///         The rule is <c>docs/plan/13-diagnostics.md</c> § Discipline's: <i>every <c>catch</c> either
///         handles or logs with the exception object</i>. That bullet claimed an analyzer enforced it
///         for as long as it existed and no analyzer did (#344). This is the enforcement, and it is
///         narrower than the sentence — deliberately, and the boundary is in the table below.
///     </para>
///     <para>
///         <b>⚠ CA1031 is not this rule and could not be made into it.</b> The BCL's "do not catch
///         general exception types" fires on the <i>shape</i> of the clause and says nothing about the
///         body, so it reports the very form doc 13 asks for — <c>catch (Exception exception)</c>
///         followed by a log carrying <c>exception</c> — and stays silent on nothing this rule reports.
///         It is also off in this repository: <c>AnalysisLevel</c> is <c>latest-recommended</c>, which
///         does not enable it, and sixteen unfiltered broad catches compile clean in <c>Core/</c> today
///         to prove it. The seven <c>#pragma warning disable CA1031</c> comments in the tree suppress a
///         rule that is not running.
///     </para>
///     <para>
///         What is reported is one shape: the clause takes <see cref="Exception" /> (or is bare, which
///         is the same reach), has no <c>when</c> filter to narrow it, never rethrows, and never names
///         the exception it caught. Such a clause cannot have handled the failure it was handed,
///         because it never looked at it — every failure the <c>try</c> can produce, including the ones
///         nobody predicted, takes the same silent path. That is the shape this repository keeps being
///         bitten by from the other side: fifteen renderers degraded silently until they were made to
///         log, and <c>PageResidency</c>'s own log events had never fired.
///     </para>
///     <para>
///         Four silences, each with a reason and a named negative test:
///     </para>
///     <list type="table">
///         <item>
///             <term>The body names the exception</term>
///             <description>
///                 It reached a log, a message, a condition, or a field. ⚠ Naming it inside an
///                 interpolated string counts, and counts through a nested lambda — the check is the
///                 symbol the identifier binds to, not text, because a text scan gets exactly those two
///                 wrong.
///             </description>
///         </item>
///         <item>
///             <term>The body throws</term>
///             <description>A <c>throw;</c> or a wrapped rethrow propagates the failure, which is the
///                 other half of "handles or logs".</description>
///         </item>
///         <item>
///             <term>A <c>when</c> filter</term>
///             <description>A filter is a written predicate about which failures this clause is for,
///                 which is the thing an untyped catch is missing.</description>
///         </item>
///         <item>
///             <term>A narrower type</term>
///             <description>
///                 ⚠ <c>catch (OperationCanceledException) { }</c> is not reported and that is a scope
///                 decision rather than an oversight. Naming a type is a decision about a named failure
///                 — cancellation, a socket closed under an accept, a codec's own error — and the
///                 twelve such clauses in <c>Core/</c> are each written with the reason above them.
///                 Reporting them would make the rule something people learn to switch off, which is
///                 how the widest and most damaging form gets through with it.
///             </description>
///         </item>
///     </list>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SilentCatchAnalyzer : DiagnosticAnalyzer {
    /// <summary>The id reported for a catch that discards the exception.</summary>
    public const string DiagnosticId = "VXLG0001";

    const string ExceptionMetadataName = "System.Exception";

    static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "A catch that discards the exception",
        "This catch takes every exception and never names it, so it neither handles nor reports what "
        + "failed. Log it with the exception object, rethrow, catch the type you meant, or say when (…).",
        "Vixen.Diagnostics",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        "Doc 13 § Discipline: every catch either handles or logs with the exception object. A clause "
        + "that catches Exception without a filter and never mentions what it caught cannot have done "
        + "either — it turns every failure the try can produce into the same silent one, which is how a "
        + "subsystem degrades without saying so. The exceptions are turned off by name in .editorconfig, "
        + "each carrying a written reason."
    );

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context) {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(
            start => {
                var exception = start.Compilation.GetTypeByMetadataName(ExceptionMetadataName);

                // A compilation that cannot name System.Exception is not one this rule can read.
                if (exception is null) {
                    return;
                }

                start.RegisterSyntaxNodeAction(node => Analyze(node, exception), SyntaxKind.CatchClause);
            }
        );
    }

    static void Analyze(SyntaxNodeAnalysisContext context, INamedTypeSymbol exception) {
        var clause = (CatchClauseSyntax)context.Node;

        // A filter is the decision about which failures this clause is for.
        if (clause.Filter is not null) {
            return;
        }

        // A bare `catch` reaches as wide as `catch (Exception)` and names nothing by construction.
        if (clause.Declaration is { } declaration) {
            var caught = context.SemanticModel.GetTypeInfo(declaration.Type, context.CancellationToken).Type;

            if (!SymbolEqualityComparer.Default.Equals(caught, exception)) {
                return;
            }
        }

        if (Throws(clause.Block)) {
            return;
        }

        if (Names(context, clause)) {
            return;
        }

        // `catch (Exception)`, not the whole block: the defect is the clause, and underlining a body
        // that may be fifty lines says nothing about which of them is wrong.
        var end = clause.Declaration?.Span.End ?? clause.CatchKeyword.Span.End;
        var span = TextSpan.FromBounds(clause.CatchKeyword.SpanStart, end);

        context.ReportDiagnostic(
            Diagnostic.Create(Rule, Location.Create(clause.SyntaxTree, span))
        );
    }

    /// <summary>Whether the body propagates the failure rather than ending it.</summary>
    /// <param name="body">The catch block.</param>
    /// <returns><c>true</c> when anything in it throws.</returns>
    /// <remarks>
    ///     A <c>throw</c> written inside a lambda in the body does not propagate out of the clause, so
    ///     counting it is a false negative. It is the safe direction to be wrong in — the rule stays
    ///     quiet about a shape nobody writes — and the alternative is a walk that has to know which
    ///     nested bodies run later, which is a second rule's worth of code for that shape alone.
    /// </remarks>
    static bool Throws(SyntaxNode body) =>
        body.DescendantNodes().Any(node => node is ThrowStatementSyntax or ThrowExpressionSyntax);

    /// <summary>Whether the body mentions the exception the clause caught.</summary>
    /// <param name="context">The analysis context, for the semantic model.</param>
    /// <param name="clause">The catch clause.</param>
    /// <returns><c>true</c> when an identifier in the body binds to the caught local.</returns>
    /// <remarks>
    ///     ⚠ Asking the semantic model rather than the text is what makes an exception mentioned only
    ///     inside <c>$"… {exception.Message}"</c>, or only inside a lambda the body hands to something
    ///     else, count as mentioned. Both forms occur in <c>Core/</c> today, and a scan that missed them
    ///     would report five correct call sites as defects.
    /// </remarks>
    static bool Names(SyntaxNodeAnalysisContext context, CatchClauseSyntax clause) {
        if (clause.Declaration is not { Identifier.ValueText.Length: > 0 } declaration) {
            return false;
        }

        var caught = context.SemanticModel.GetDeclaredSymbol(declaration, context.CancellationToken);

        if (caught is null) {
            return false;
        }

        foreach (var identifier in clause.Block.DescendantNodes().OfType<IdentifierNameSyntax>()) {
            var symbol = context.SemanticModel.GetSymbolInfo(identifier, context.CancellationToken).Symbol;

            if (SymbolEqualityComparer.Default.Equals(symbol, caught)) {
                return true;
            }
        }

        return false;
    }
}
