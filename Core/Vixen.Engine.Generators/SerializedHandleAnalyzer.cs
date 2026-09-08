// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Vixen.Engine.Generators;

/// <summary>Reports a component that would write an entity handle into a file.</summary>
/// <remarks>
///     <para>
///         <c>docs/plan/04-ecs-and-scripting.md</c> § Determinism and persistence, in three words:
///         <b>never serialise a raw <c>Entity</c></b>. Ids are dense and reused, so a handle is a slot
///         in a running process; written down and read back into another world it either names a
///         different entity or fails a version check, and nothing says which.
///     </para>
///     <para>
///         <b>The engine already keeps this rule by hand, in comments.</b>
///         <c>CameraTargets</c>, <c>Possessing</c>, <c>PossessedBy</c>, <c>ViewTarget</c> and
///         <c>PredictionSmoothing</c> each carry <c>[Component]</c> without <c>[DataContract]</c> and
///         each says why in its own remarks — "not [DataContract], because it names an entity". This
///         is that convention with a compiler behind it. ⚠ It found one component that had drifted
///         out of it.
///     </para>
///     <para>
///         <b>Both attributes are the trigger, because both together are what makes a component
///         reach a file.</b> <c>SceneComponentRegistry</c> declares a type carrying the pair, and
///         <c>WorldSerializer</c> writes a column for exactly those; a component with only
///         <c>[Component]</c> lives entirely in a running world and may hold whatever it likes.
///     </para>
///     <para>
///         ⚠ <b>What this is not is a fix.</b> A component that genuinely needs to point at another
///         entity across a save has nothing to hold instead — there is no persistent identity, no
///         <c>GuidComponent</c> and no <c>Guid → Entity</c> map — which is
///         <a href="https://github.com/Rikarin/Vixen/issues/296">#296</a>. This makes the gap loud
///         instead of silent, which is the half of that issue that does not need the design decided.
///     </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SerializedHandleAnalyzer : DiagnosticAnalyzer {
    /// <summary>The id reported for a serialised component that holds an entity handle.</summary>
    public const string DiagnosticId = "VXS0415";

    const string EntityMetadataName = "Vixen.Core.Entity";
    const string ComponentAttributeMetadataName = "Vixen.Core.ComponentAttribute";
    const string DataContractAttributeMetadataName = "Vixen.Core.DataContractAttribute";

    /// <summary>How far into an array or a generic's arguments a handle is looked for.</summary>
    const int MaximumDepth = 4;

    static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "A serialised component holds an entity handle",
        "'{0}.{1}' is an Entity on a component carrying both [Component] and [DataContract], so it is "
        + "written into a scene or a captured world as a slot number that means nothing in the world "
        + "it is read back into. Drop [DataContract], or hold something that survives the trip.",
        "Vixen.Engine",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        "docs/plan/04-ecs-and-scripting.md § Determinism and persistence: entity ids are dense and "
        + "reused, so never serialise a raw Entity. WorldSerializer keeps this for the hierarchy by "
        + "refusing to write Parent, Child and Sibling and rebuilding the links from a table of "
        + "indices — and says out loud that it cannot do the same for a game's own component, because "
        + "nothing generic knows which of a component's fields are handles. The engine's own "
        + "entity-naming components carry [Component] alone for this reason."
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
                var entity = start.Compilation.GetTypeByMetadataName(EntityMetadataName);
                var component = start.Compilation.GetTypeByMetadataName(ComponentAttributeMetadataName);
                var contract = start.Compilation.GetTypeByMetadataName(DataContractAttributeMetadataName);

                if (entity is null || component is null || contract is null) {
                    return;
                }

                start.RegisterSymbolAction(
                    symbol => Analyze(symbol, entity, component, contract),
                    SymbolKind.NamedType
                );
            }
        );
    }

    static void Analyze(
        SymbolAnalysisContext context,
        INamedTypeSymbol entity,
        INamedTypeSymbol component,
        INamedTypeSymbol contract
    ) {
        var type = (INamedTypeSymbol) context.Symbol;

        // A component that is not written down is not this rule's business, and the pair is what
        // decides that: SceneComponentRegistry declares the types carrying both.
        if (!Carries(type, component) || !Carries(type, contract)) {
            return;
        }

        foreach (var member in type.GetMembers()) {
            if (member is not IFieldSymbol { IsConst: false, IsStatic: false } field) {
                continue;
            }

            if (!Holds(field.Type, entity, MaximumDepth)) {
                continue;
            }

            var named = field.AssociatedSymbol ?? field;

            context.ReportDiagnostic(
                Diagnostic.Create(
                    Rule,
                    named.Locations.FirstOrDefault() ?? Location.None,
                    type.Name,
                    named.Name
                )
            );
        }
    }

    static bool Carries(INamedTypeSymbol type, INamedTypeSymbol attribute) {
        foreach (var applied in type.GetAttributes()) {
            if (SymbolEqualityComparer.Default.Equals(applied.AttributeClass, attribute)) {
                return true;
            }
        }

        return false;
    }

    static bool Holds(ITypeSymbol held, INamedTypeSymbol entity, int depth) {
        if (depth <= 0) {
            return false;
        }

        if (held is IArrayTypeSymbol array) {
            return Holds(array.ElementType, entity, depth - 1);
        }

        if (held is not INamedTypeSymbol named) {
            return false;
        }

        if (SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, entity)) {
            return true;
        }

        foreach (var argument in named.TypeArguments) {
            if (Holds(argument, entity, depth - 1)) {
                return true;
            }
        }

        return false;
    }
}
