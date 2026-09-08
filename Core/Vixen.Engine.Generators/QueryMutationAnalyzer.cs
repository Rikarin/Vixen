// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Vixen.Engine.Generators;

/// <summary>Reports a structural change made while a query is iterating.</summary>
/// <remarks>
///     <para>
///         <c>docs/plan/04-ecs-and-scripting.md</c> § Structural change safety states this as
///         something the tooling already does: <i>"Direct structural mutation on the main thread
///         outside iteration is allowed and fast; the analyzer flags it inside a query body."</i>
///         The two mechanisms that make it safe — <c>CommandBuffer</c> and its
///         <c>ParallelWriter</c> — are both opt-in, so an author who has not reached for one is not
///         told.
///     </para>
///     <para>
///         ⚠ <b>The failure is the worst shape there is: it is usually fine.</b> Adding or removing
///         a component moves the entity to another archetype and can split the chunk the loop is
///         standing in. That corrupts the walk only when it moves the entity the loop is on or the
///         chunk it is reading — so three entities in a test pass and three thousand in a level fail
///         intermittently. Nothing throws.
///     </para>
///     <para>
///         <b>What counts as a query body.</b> Three forms, because the ECS offers three: the
///         delegate passed to <c>world.Query(…)</c>/<c>QueryWithEntity(…)</c>, a
///         <c>foreach</c> over <c>world.Chunks(…)</c> or <c>query.Chunks(…)</c>, and the
///         <c>Update</c> of a struct visitor handed to <c>ForEach</c>. The visitor is the one that
///         cannot be seen from the call site at all — its body is a different file.
///     </para>
///     <para>
///         <b>Suppression is a <c>#pragma</c> and it is meant to be used.</b> Mutating the entity a
///         loop is about to leave is sometimes exactly right, and a rule with no way out becomes a
///         rule people turn off wholesale. <c>#pragma warning disable VXS0415</c> with a line saying
///         why is the intended escape; a <c>CommandBuffer</c> played back at the sync point is the
///         intended answer.
///     </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class QueryMutationAnalyzer : DiagnosticAnalyzer {
    /// <summary>The id reported for a structural change inside a query body.</summary>
    public const string DiagnosticId = "VXS0415";

    const string WorldMetadataName = "Vixen.Ecs.World";
    const string QueryMetadataName = "Vixen.Ecs.Query";
    const string QueryExtensionsMetadataName = "Vixen.Ecs.WorldQueryExtensions";

    static readonly ImmutableHashSet<string> Structural =
        ImmutableHashSet.Create(StringComparer.Ordinal, "Create", "CreateMany", "Destroy", "Add", "AddDefault", "Remove");

    static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "A structural change inside a query body",
        "'World.{0}' changes an entity's archetype while {1} is iterating. Record it into the "
        + "CommandBuffer and let the sync point play it back.",
        "Vixen.Engine",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        "Adding or removing a component moves the entity to another archetype, which invalidates the "
        + "chunk the loop is walking. It corrupts iteration only when it moves the entity the loop is "
        + "standing on or splits the chunk being read, so it passes with three entities and fails "
        + "intermittently with three thousand. SystemContext carries the phase's CommandBuffer for "
        + "this; #pragma warning disable VXS0415 with a reason is the way to say you meant it."
    );

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context) {
        if (context is null) {
            throw new ArgumentNullException(nameof(context));
        }

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(
            start => {
                var world = start.Compilation.GetTypeByMetadataName(WorldMetadataName);

                if (world is null) {
                    return;
                }

                var query = start.Compilation.GetTypeByMetadataName(QueryMetadataName);
                var extensions = start.Compilation.GetTypeByMetadataName(QueryExtensionsMetadataName);

                start.RegisterOperationAction(
                    operation => Analyze(operation, world, query, extensions),
                    OperationKind.Invocation
                );
            }
        );
    }

    static void Analyze(
        OperationAnalysisContext context,
        INamedTypeSymbol world,
        INamedTypeSymbol? query,
        INamedTypeSymbol? extensions
    ) {
        var invocation = (IInvocationOperation) context.Operation;
        var method = invocation.TargetMethod;

        if (!SymbolEqualityComparer.Default.Equals(method.ContainingType, world)) {
            return;
        }

        if (!Structural.Contains(method.Name)) {
            return;
        }

        var body = Enclosing(invocation, query, extensions) ?? Visitor(context, world);

        if (body is null) {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.Syntax.GetLocation(), method.Name, body));
    }

    /// <summary>What the call is inside, walking outwards, or null if it is not inside anything.</summary>
    /// <remarks>
    ///     ⚠ <b>Outwards through the operation tree rather than the syntax tree</b>, because a lambda
    ///     reaches its call site as an argument through a conversion and a delegate creation, and the
    ///     syntax between them says nothing about which method it was handed to.
    /// </remarks>
    static string? Enclosing(IOperation operation, INamedTypeSymbol? query, INamedTypeSymbol? extensions) {
        for (var current = operation.Parent; current is not null; current = current.Parent) {
            if (current is IForEachLoopOperation loop && IsChunkWalk(loop, query)) {
                return "a chunk walk";
            }

            if (current is IAnonymousFunctionOperation && HandedToAQuery(current, extensions)) {
                return "the query body";
            }
        }

        return null;
    }

    static bool IsChunkWalk(IForEachLoopOperation loop, INamedTypeSymbol? query) {
        var collection = loop.Collection;

        while (collection is IConversionOperation conversion) {
            collection = conversion.Operand;
        }

        if (collection is not IInvocationOperation { TargetMethod: { Name: "Chunks" } chunks }) {
            return false;
        }

        // `world.Chunks(description)` and `query.Chunks()` are the same walk — the first forwards to
        // the second — and both hand out a chunk whose rows a structural change can move.
        return chunks.ContainingType is { } owner
            && (owner.Name is "World" || (query is not null && SymbolEqualityComparer.Default.Equals(owner, query)));
    }

    static bool HandedToAQuery(IOperation lambda, INamedTypeSymbol? extensions) {
        if (extensions is null) {
            return false;
        }

        // Conversion → delegate creation → argument, and any of them may be absent depending on how
        // the lambda was written, so this walks rather than indexes.
        for (var current = lambda.Parent; current is not null; current = current.Parent) {
            switch (current) {
                case IConversionOperation or IDelegateCreationOperation:
                    continue;

                case IArgumentOperation { Parent: IInvocationOperation call }:
                    return SymbolEqualityComparer.Default.Equals(call.TargetMethod.ContainingType, extensions);

                default:
                    return false;
            }
        }

        return false;
    }

    /// <summary>Whether the call is in the <c>Update</c> of a struct visitor.</summary>
    /// <remarks>
    ///     The visitor form is the one a reader cannot see from the call site: <c>ForEach</c> takes a
    ///     <c>ref TVisitor</c> and the body being iterated is a method on another type, in another
    ///     file, that looks like an ordinary method until you know what implements it.
    /// </remarks>
    static string? Visitor(OperationAnalysisContext context, INamedTypeSymbol world) {
        if (context.ContainingSymbol is not IMethodSymbol { Name: "Update" } method) {
            return null;
        }

        if (method.ContainingType is not { IsValueType: true } visitor) {
            return null;
        }

        foreach (var implemented in visitor.AllInterfaces) {
            if (implemented.Name is not ("IForEach" or "IForEachWithEntity")) {
                continue;
            }

            // The generated interfaces live beside World, which is what tells them apart from
            // somebody else's IForEach.
            if (SymbolEqualityComparer.Default.Equals(implemented.ContainingNamespace, world.ContainingNamespace)) {
                return "a struct visitor";
            }
        }

        return null;
    }
}
