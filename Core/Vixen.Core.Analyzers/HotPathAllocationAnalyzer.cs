// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Vixen.Core.Analyzers;

/// <summary>Reports a managed-heap allocation written inside a <c>[HotPath]</c> member.</summary>
/// <remarks>
///     <para>
///         <c>HotPathAttribute</c>'s own summary said it was "a contract for the allocation analyzer",
///         and there was no allocation analyzer (#1161). An attribute that names an enforcement nobody
///         wrote reads from a call site exactly like one that is checked, which is worse than no
///         attribute at all. This is the enforcement; the attribute's summary now names this rule.
///     </para>
///     <para>
///         <b>⚠ What it can see is one method body and nothing further.</b> It reports the allocations
///         that are <i>written</i> in the marked member — a <c>new</c>, an array, a boxed value, a
///         capturing lambda, a built string — and it cannot see one inside a method that member calls.
///         That blind spot is deliberate rather than pending: an interprocedural allocation analysis
///         over the BCL is not something an analyzer can do at compile time, and this repository
///         already owns the instrument that <i>can</i> see through a call — <c>Vixen.Testing.Measured</c>
///         counts <see cref="GC.GetAllocatedBytesForCurrentThread" /> across real work and asserts
///         exactly zero. The two are complements: the counter catches a callee, the rule catches the
///         edit, and the rule is the half that runs on every build over methods no test measures.
///     </para>
///     <para>
///         A <c>throw</c> is exempt. A frame that throws has already lost, and demanding a cached
///         exception instance is how a rule teaches people to swallow errors instead.
///     </para>
///     <para>
///         Two shapes that look like allocations and are not, both silent on purpose: a
///         <c>new</c> of a struct is bytes on the stack or in the enclosing object, and a lambda that
///         captures nothing is cached in a static field by the compiler — including a method group over
///         a static method, since C# 11. Reporting either would make the rule something people learn to
///         suppress.
///     </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HotPathAllocationAnalyzer : DiagnosticAnalyzer {
    /// <summary>The id reported for an allocation inside a <c>[HotPath]</c> member.</summary>
    public const string DiagnosticId = "VXHP0001";

    const string HotPathMetadataName = "Vixen.Core.HotPathAttribute";

    static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Allocation in a [HotPath] member",
        "{0} allocates on the managed heap, and '{1}' is marked [HotPath], which says it must not",
        "Vixen.Performance",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        "[HotPath] declares that a member runs inside the frame loop and must not allocate. A "
        + "per-frame allocation is not a leak and never shows up as one: it shows up as a collection "
        + "somebody else's frame pays for, at a moment that depends on what the rest of the process "
        + "did. The rule reads the marked body only — a callee's allocation is Vixen.Testing.Measured's "
        + "half of the same question."
    );

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context) {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(
            start => {
                var hotPath = start.Compilation.GetTypeByMetadataName(HotPathMetadataName);

                // A compilation that cannot name the attribute cannot have marked anything with it.
                if (hotPath is null) {
                    return;
                }

                start.RegisterOperationBlockStartAction(
                    block => {
                        if (!IsHotPath(block.OwningSymbol, hotPath)) {
                            return;
                        }

                        var owner = block.OwningSymbol.Name;

                        block.RegisterOperationAction(
                            operation => Analyze(operation, owner),
                            OperationKind.ObjectCreation,
                            OperationKind.ArrayCreation,
                            OperationKind.AnonymousObjectCreation,
                            OperationKind.Conversion,
                            OperationKind.DelegateCreation,
                            OperationKind.InterpolatedString,
                            OperationKind.Binary
                        );
                    }
                );
            }
        );
    }

    /// <summary>Whether the attribute reaches this member — on it, on its property, or on its type.</summary>
    /// <param name="symbol">The member owning the operation block.</param>
    /// <param name="hotPath">The attribute type.</param>
    /// <returns><c>true</c> when the member is under the contract.</returns>
    /// <remarks>
    ///     The attribute's <c>AttributeUsage</c> allows a class or a struct, so a marked type marks
    ///     every member in it; and an accessor carries no attributes of its own, so a marked property
    ///     has to be found through <see cref="IMethodSymbol.AssociatedSymbol" />.
    /// </remarks>
    static bool IsHotPath(ISymbol symbol, INamedTypeSymbol hotPath) {
        if (Marked(symbol, hotPath)) {
            return true;
        }

        if (symbol is IMethodSymbol { AssociatedSymbol: { } associated } && Marked(associated, hotPath)) {
            return true;
        }

        for (var type = symbol.ContainingType; type is not null; type = type.ContainingType) {
            if (Marked(type, hotPath)) {
                return true;
            }
        }

        return false;
    }

    static bool Marked(ISymbol symbol, INamedTypeSymbol hotPath) {
        foreach (var attribute in symbol.GetAttributes()) {
            if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, hotPath)) {
                return true;
            }
        }

        return false;
    }

    static void Analyze(OperationAnalysisContext context, string owner) {
        var operation = context.Operation;

        // A compile-time constant is in the metadata, not on the heap, and a string literal is
        // interned once for the assembly.
        if (operation.ConstantValue.HasValue) {
            return;
        }

        if (Exempt(operation)) {
            return;
        }

        var what = Describe(operation);

        if (what is null) {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, operation.Syntax.GetLocation(), what, owner));
    }

    /// <summary>The two places an allocation inside a marked member is not a per-call cost.</summary>
    /// <param name="operation">The operation.</param>
    /// <returns><c>true</c> when it should not be reported.</returns>
    /// <remarks>
    ///     ⚠ The attribute half is not a nicety. A member's <em>own attribute list</em> is an operation
    ///     block whose owning symbol is that member, so the first thing this rule saw on every marked
    ///     method was <c>new HotPathAttribute</c> — the rule reporting the mark that turned it on.
    ///     Thirteen of the eighteen fixtures below caught it, including every negative, which is the
    ///     argument for writing the negatives at all: an attribute is metadata written once by the
    ///     compiler and never constructed in a frame.
    /// </remarks>
    static bool Exempt(IOperation operation) {
        for (var node = operation; node is not null; node = node.Parent) {
            // A frame that throws has already lost.
            if (node is IThrowOperation or IAttributeOperation) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Names the allocation, or returns <c>null</c> when the operation does not make one.</summary>
    /// <param name="operation">The operation to classify.</param>
    /// <returns>What to put in the message, or <c>null</c>.</returns>
    static string? Describe(IOperation operation) {
        switch (operation) {
            case IObjectCreationOperation creation:
                // `new Vector3(...)` is bytes where they already are. Only a class costs a heap slot.
                return creation.Type is { IsReferenceType: true }
                    ? $"'new {creation.Type.Name}'"
                    : null;

            case IArrayCreationOperation:
                // Implicit ones are the params arrays, which allocate exactly as much as written ones.
                return "an array";

            case IAnonymousObjectCreationOperation:
                return "an anonymous object";

            case IConversionOperation conversion:
                return Boxes(conversion) ? $"boxing '{conversion.Operand.Type!.Name}'" : null;

            case IDelegateCreationOperation delegated:
                return DescribeDelegate(delegated);

            case IInterpolatedStringOperation interpolated:
                return interpolated.Type?.SpecialType == SpecialType.System_String
                    ? "an interpolated string"
                    : null;

            case IBinaryOperation {
                OperatorKind: BinaryOperatorKind.Add, Type.SpecialType: SpecialType.System_String
            } concatenation:
                // `a + b + c` is a tree of adds and one report is the useful number of reports.
                return concatenation.Parent is IBinaryOperation {
                    OperatorKind: BinaryOperatorKind.Add, Type.SpecialType: SpecialType.System_String
                }
                    ? null
                    : "a concatenated string";

            default:
                return null;
        }
    }

    static bool Boxes(IConversionOperation conversion) =>
        conversion is { Type.IsReferenceType: true, Operand.Type.IsValueType: true }
        && !conversion.Conversion.IsUserDefined;

    static string? DescribeDelegate(IDelegateCreationOperation delegated) {
        switch (delegated.Target) {
            case IAnonymousFunctionOperation lambda:
                return Captures(lambda) ? "a closure" : null;

            // A method group over a static method is cached in a static field (C# 11); one over an
            // instance is a new delegate every time, because it has to carry the receiver.
            case IMethodReferenceOperation { Instance: not null } reference:
                return $"a delegate over '{reference.Method.Name}'";

            default:
                return null;
        }
    }

    /// <summary>Whether a lambda reads anything from outside itself.</summary>
    /// <param name="lambda">The lambda.</param>
    /// <returns><c>true</c> when it closes over state and so allocates a display class.</returns>
    /// <remarks>
    ///     ⚠ This is the whole reason the rule cannot just report every lambda. A capture-free lambda
    ///     is allocated once and cached in a static field, so reporting it would be reporting a cost
    ///     that is not paid per call — and the fix a reader would reach for, <c>static</c>, changes
    ///     nothing. What the rule wants to catch is doc 00's "no implicit closure in a [HotPath]
    ///     method": the display class, and the <c>this</c> capture that hides inside an instance
    ///     method's lambda without a single <c>this.</c> written anywhere.
    /// </remarks>
    static bool Captures(IAnonymousFunctionOperation lambda) {
        foreach (var descendant in lambda.Descendants()) {
            var referenced = descendant switch {
                // Only a real `this`. The implicit receiver of an object initializer or a `with`
                // expression is also an instance reference and captures nothing.
                IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance } =>
                    lambda.Symbol.ContainingSymbol,
                ILocalReferenceOperation local => local.Local.ContainingSymbol,
                IParameterReferenceOperation parameter => parameter.Parameter.ContainingSymbol,
                _ => null
            };

            if (referenced is not null && !Within(referenced, lambda.Symbol)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="symbol" /> is the lambda or something nested inside it.</summary>
    /// <param name="symbol">The symbol that owns the local, the parameter or the <c>this</c>.</param>
    /// <param name="lambda">The lambda under test.</param>
    /// <returns><c>true</c> when the reference stays inside the lambda.</returns>
    static bool Within(ISymbol symbol, ISymbol lambda) {
        for (var owner = symbol; owner is not null; owner = owner.ContainingSymbol) {
            if (SymbolEqualityComparer.Default.Equals(owner, lambda)) {
                return true;
            }
        }

        return false;
    }
}
