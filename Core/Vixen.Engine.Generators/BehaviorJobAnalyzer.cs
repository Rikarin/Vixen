// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Vixen.Engine.Generators;

/// <summary>Reports work inside a <c>[BehaviorJob]</c> batch that several threads cannot do at once.</summary>
/// <remarks>
///     <para>
///         <b>The half that makes the dispatch safe.</b>
///         <c>docs/plan/04-ecs-and-scripting.md</c> § Making it fast, item 3 asks for behaviour
///         batches dispatched across the job system. What that section spends its length on is the
///         blocker: a behaviour declares no access set, and — more immediately — its own convenience
///         API reaches state the whole store shares. <c>Enabled</c> queues into a plain
///         <c>List&lt;Behavior?&gt;</c>, <c>Destroy()</c> and <c>Run(coroutine)</c> do the same to
///         their own queues, and none of it is synchronised, deliberately, because the lifecycle is
///         drained on one thread. Inside a dispatched <c>Update</c> that is a data race rather than a
///         slow path.
///     </para>
///     <para>
///         ⚠ <b>And the least visible member of the set is <c>Get&lt;T&gt;</c> of a <em>managed</em>
///         component.</b> Nothing at the call site says so: the world resolves a managed cell through
///         <c>StoreFor&lt;T&gt;()</c>, which can resize the world's table and allocate a row's slot,
///         so two behaviours reading the same managed component for the first time race each other.
///         The <em>read</em> half of that was fixed — <c>Read&lt;T&gt;</c> and <c>TryGet&lt;T&gt;</c>
///         resolve without writing — which is why this rule names <c>Get&lt;T&gt;</c> and not
///         <c>Read&lt;T&gt;</c>: what is left is a write to world-wide state wearing a getter's face.
///     </para>
///     <para>
///         <b>What it reads is the two bodies that are dispatched</b> — the marked type's
///         <c>Update</c> and <c>LateUpdate</c>, and the lambdas and local functions inside them — and
///         nothing further down. ⚠ <b>That bound is stated because it is the same one
///         <c>VXHP0001</c> states about itself</b>: a helper method two calls away can queue a
///         lifecycle change and this will not see it. Widening it to every method of a marked type
///         would be worse than the gap, because <c>Awake</c> and <c>OnEnable</c> run on the drain
///         thread where every one of these calls is correct.
///     </para>
///     <para>
///         <b>An error, not a warning, and unlike its siblings it has no <c>#pragma</c> story.</b>
///         The escape from this rule is to take <c>[BehaviorJob]</c> off the type, which costs a batch
///         its parallelism and nothing else. A suppressed race is a build that runs ten thousand
///         instances across eight threads into an unsynchronised list.
///     </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BehaviorJobAnalyzer : DiagnosticAnalyzer {
    /// <summary>The id reported for shared state touched inside a dispatched batch.</summary>
    public const string RaceDiagnosticId = "VXS0417";

    /// <summary>The id reported for <c>[BehaviorJob]</c> on a type no bucket will ever hold.</summary>
    public const string TargetDiagnosticId = "VXS0418";

    const string BehaviorMetadataName = "Vixen.Engine.Behaviors.Behavior";
    const string AttributeMetadataName = "Vixen.Engine.Behaviors.BehaviorJobAttribute";
    const string WorldMetadataName = "Vixen.Ecs.World";
    const string CoroutineSchedulerMetadataName = "Vixen.Engine.Coroutines.CoroutineScheduler";

    const string Lifecycle =
        "it queues into one of the store's lifecycle lists, which is a plain List<Behavior?> drained "
        + "on one thread";

    const string Coroutines =
        "it reaches the store's coroutine scheduler, which is shared by every behaviour in the world "
        + "and is not synchronised";

    const string Managed =
        "a managed component's cell is resolved by growing the world's own table and allocating the "
        + "row's slot, so two indices reading it for the first time race";

    const string Structure =
        "structural change moves an entity between archetypes, which no two threads may do to one "
        + "world at the same time";

    static readonly ImmutableHashSet<string> StructuralCalls = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "Create",
        "CreateMany",
        "Destroy",
        "Add",
        "AddDefault",
        "Remove"
    );

    static readonly DiagnosticDescriptor Race = new(
        RaceDiagnosticId,
        "A [BehaviorJob] batch touches state the whole store shares",
        "'{0}' runs on several threads at once here, because {1} carries [BehaviorJob]. Take the "
        + "attribute off the type, or move this out of the dispatched pass: {2}.",
        "Vixen.Engine",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        "[BehaviorJob] promises that a type's Update may be run for every instance at once. The "
        + "behaviour API's convenience members do not all keep that promise: Enabled, Destroy and Run "
        + "queue into unsynchronised lists, Get<T> of a managed component allocates in a table the "
        + "world shares, and structural change moves entities between archetypes. There is no pragma "
        + "for this one — the way out is to stop dispatching the type."
    );

    static readonly DiagnosticDescriptor Target = new(
        TargetDiagnosticId,
        "A [BehaviorJob] type is not a behaviour",
        "'{0}' carries [BehaviorJob] and does not derive from Behavior, so nothing dispatches it and "
        + "the attribute claims a parallelism that never happens",
        "Vixen.Engine",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        "The attribute is read by the behaviour bucket, which only exists for a type attached through "
        + "BehaviorStore.Add<T>. On anything else it is a mark that reads from a call site exactly "
        + "like an enforced one and does nothing."
    );

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Race, Target];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context) {
        if (context is null) {
            throw new ArgumentNullException(nameof(context));
        }

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(
            start => {
                var behavior = start.Compilation.GetTypeByMetadataName(BehaviorMetadataName);
                var attribute = start.Compilation.GetTypeByMetadataName(AttributeMetadataName);

                if (behavior is null || attribute is null) {
                    return;
                }

                var world = start.Compilation.GetTypeByMetadataName(WorldMetadataName);
                var coroutines = start.Compilation.GetTypeByMetadataName(CoroutineSchedulerMetadataName);

                start.RegisterSymbolAction(symbol => Declaration(symbol, behavior, attribute), SymbolKind.NamedType);

                start.RegisterOperationAction(
                    operation => Invocation(operation, behavior, attribute, world, coroutines),
                    OperationKind.Invocation
                );

                start.RegisterOperationAction(
                    operation => Assignment(operation, behavior, attribute),
                    OperationKind.SimpleAssignment
                );
            }
        );
    }

    static void Declaration(SymbolAnalysisContext context, INamedTypeSymbol behavior, INamedTypeSymbol attribute) {
        var type = (INamedTypeSymbol)context.Symbol;

        if (!Marked(type, attribute) || Derives(type, behavior)) {
            return;
        }

        context.ReportDiagnostic(
            Diagnostic.Create(Target, type.Locations.FirstOrDefault() ?? Location.None, type.Name)
        );
    }

    static void Invocation(
        OperationAnalysisContext context,
        INamedTypeSymbol behavior,
        INamedTypeSymbol attribute,
        INamedTypeSymbol? world,
        INamedTypeSymbol? coroutines
    ) {
        var invocation = (IInvocationOperation)context.Operation;
        var reason = Reason(invocation.TargetMethod, behavior, world, coroutines);

        if (reason is null) {
            return;
        }

        var pass = Dispatched(context.ContainingSymbol, behavior, attribute);

        if (pass is null) {
            return;
        }

        context.ReportDiagnostic(
            Diagnostic.Create(
                Race,
                invocation.Syntax.GetLocation(),
                invocation.TargetMethod.Name,
                pass.ContainingType.Name,
                reason
            )
        );
    }

    /// <summary>Why this call may not run on several threads at once, or null if it may.</summary>
    static string? Reason(
        IMethodSymbol method,
        INamedTypeSymbol behavior,
        INamedTypeSymbol? world,
        INamedTypeSymbol? coroutines
    ) {
        var container = method.ContainingType;

        if (coroutines is not null && SymbolEqualityComparer.Default.Equals(container, coroutines)) {
            return Coroutines;
        }

        if (SymbolEqualityComparer.Default.Equals(container, behavior)) {
            return method.Name switch {
                "Destroy" => Lifecycle,
                "Run" or "StopCoroutines" => Coroutines,
                "Get" when ManagedComponent(method) => Managed,
                _ => null
            };
        }

        if (world is not null && SymbolEqualityComparer.Default.Equals(container, world)) {
            if (StructuralCalls.Contains(method.Name)) {
                return Structure;
            }

            return method.Name == "Get" && ManagedComponent(method) ? Managed : null;
        }

        return null;
    }

    /// <summary>Whether the call's one type argument is a class, which is what makes a component managed.</summary>
    /// <remarks>
    ///     An unresolved type parameter — a helper generic over its own <c>T</c> — answers
    ///     <see langword="false" />, because a rule that reported a call whose component type it does
    ///     not know would fire on the ordinary case.
    /// </remarks>
    static bool ManagedComponent(IMethodSymbol method) =>
        method.TypeArguments.Length == 1
        && method.TypeArguments[0] is { IsReferenceType: true, TypeKind: not TypeKind.TypeParameter };

    static void Assignment(OperationAnalysisContext context, INamedTypeSymbol behavior, INamedTypeSymbol attribute) {
        var assignment = (IAssignmentOperation)context.Operation;

        if (assignment.Target is not IPropertyReferenceOperation { Property: { Name: "Enabled" } property }
            || !SymbolEqualityComparer.Default.Equals(property.ContainingType, behavior)) {
            return;
        }

        var pass = Dispatched(context.ContainingSymbol, behavior, attribute);

        if (pass is null) {
            return;
        }

        context.ReportDiagnostic(
            Diagnostic.Create(
                Race,
                assignment.Syntax.GetLocation(),
                "Enabled",
                pass.ContainingType.Name,
                Lifecycle
            )
        );
    }

    /// <summary>
    ///     The dispatched pass this operation is inside — <c>Update</c> or <c>LateUpdate</c> of a
    ///     marked behaviour — or null if it is not inside one.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Up the symbol chain, not the syntax tree.</b> A lambda and a local function are
    ///     methods in their own right whose containing symbol is the pass that declared them, and a
    ///     lambda handed to something else still runs inside the batch that made it.
    /// </remarks>
    static IMethodSymbol? Dispatched(ISymbol? symbol, INamedTypeSymbol behavior, INamedTypeSymbol attribute) {
        for (var current = symbol; current is not null; current = current.ContainingSymbol) {
            if (current is not IMethodSymbol method) {
                return null;
            }

            if (method.Name is not ("Update" or "LateUpdate") || !method.IsOverride || method.Parameters.Length != 0) {
                continue;
            }

            return Marked(method.ContainingType, attribute) && Derives(method.ContainingType, behavior) ? method : null;
        }

        return null;
    }

    /// <summary>
    ///     Whether the type itself carries the attribute. ⚠ Itself, because
    ///     <c>[BehaviorJob]</c> is not inherited: the bucket that reads it is closed over the static
    ///     type at the <c>Add&lt;T&gt;</c> call site, and a subclass is a different bucket with a
    ///     different body.
    /// </summary>
    static bool Marked(INamedTypeSymbol type, INamedTypeSymbol attribute) =>
        type.GetAttributes()
            .Any(data => SymbolEqualityComparer.Default.Equals(data.AttributeClass, attribute));

    static bool Derives(INamedTypeSymbol type, INamedTypeSymbol behavior) {
        for (var current = type.BaseType; current is not null; current = current.BaseType) {
            if (SymbolEqualityComparer.Default.Equals(current, behavior)) {
                return true;
            }
        }

        return false;
    }
}
