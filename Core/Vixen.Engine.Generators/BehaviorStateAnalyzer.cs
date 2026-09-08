// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Vixen.Engine.Generators;

/// <summary>Reports a <c>Behavior</c> that keeps what the world already knows.</summary>
/// <remarks>
///     <para>
///         <b>The rule the layering rests on.</b>
///         <c>docs/plan/04-ecs-and-scripting.md</c> § The rule that keeps this coherent: component
///         data lives in ECS, and a behaviour holds no state that is not either a component or
///         private scratch. Without it the ECS below becomes decoration.
///     </para>
///     <para>
///         ⚠ <b>The rule was deliberately narrowed and this analyzer enforces the narrow one.</b> A
///         behaviour carrying <c>[DataContract]</c> serialises its own members into a
///         <c>.vxscene</c>, on purpose — a designer's <c>Speed</c> should not become a generated
///         component struct with an archetype nobody queries. So *data* on a behaviour is fine. What
///         is banned is the shape that makes the ECS decoration: a behaviour keeping a second copy
///         of something the world is already the authority on — an entity handle, or a component.
///     </para>
///     <para>
///         ⚠ <b>Why an entity handle in particular is an error rather than a style note.</b> An
///         <c>Entity</c> is a slot in a running process. A <c>[DataContract]</c> behaviour holding
///         one writes a stale slot number into the file and reads it back as a handle into a
///         different world — which resolves to the wrong entity or fails a version check, silently
///         either way. `WorldSerializer` refuses to write `Parent`, `Child` and `Sibling` for
///         exactly this reason and says out loud that it cannot do the same for a game's own
///         component.
///     </para>
///     <para>
///         <b>What it deliberately does not do is judge how hot the data is.</b> Doc 04 asks for a
///         third diagnostic — a warning about hot data, promoting a field into a component — and
///         there is no static predicate for "hot": the document itself says profiling is what
///         decides. A rule whose predicate cannot be false is worse than no rule.
///     </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BehaviorStateAnalyzer : DiagnosticAnalyzer {
    /// <summary>The id reported for a behaviour member that holds an entity handle.</summary>
    public const string HandleDiagnosticId = "VXS0413";

    /// <summary>The id reported for a behaviour member that holds a copy of a component.</summary>
    public const string ComponentDiagnosticId = "VXS0414";

    const string BehaviorMetadataName = "Vixen.Engine.Behaviors.Behavior";
    const string EntityMetadataName = "Vixen.Core.Entity";
    const string ComponentAttributeMetadataName = "Vixen.Core.ComponentAttribute";
    const string TagComponentMetadataName = "Vixen.Ecs.ITagComponent";
    const string WorldMetadataName = "Vixen.Ecs.World";

    /// <summary>How far into a generic's type arguments a held type is looked for.</summary>
    /// <remarks>
    ///     A <c>List&lt;Entity&gt;</c> is the case the document names and a
    ///     <c>Dictionary&lt;int, List&lt;Entity&gt;&gt;</c> is the same thing said twice. The bound
    ///     exists because a type argument graph can be cyclic — <c>Node&lt;Node&lt;…&gt;&gt;</c> —
    ///     and an analyzer that recursed forever would hang a build rather than fail it.
    /// </remarks>
    const int MaximumDepth = 4;

    static readonly DiagnosticDescriptor Handle = new(
        HandleDiagnosticId,
        "A behaviour holds an entity handle",
        "'{0}' holds an Entity. A behaviour asks the world or the hierarchy for an entity; it does "
        + "not keep one, because a handle is a slot in a running process and does not survive being "
        + "written down.",
        "Vixen.Engine",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        "docs/plan/04-ecs-and-scripting.md: a Behavior may not hold a List<Entity> of 'its children' "
        + "— it asks the hierarchy. A [DataContract] behaviour holding one writes a stale slot "
        + "number into the .vxscene and reads it back as a handle into a different world, which "
        + "either resolves to the wrong entity or fails a version check and says nothing either way."
    );

    static readonly DiagnosticDescriptor Copy = new(
        ComponentDiagnosticId,
        "A behaviour holds a copy of a component",
        "'{0}' holds '{1}', which is a component. Read it from the world through Get<{1}>() at the "
        + "point of use; a copy on the behaviour is a second answer to a question the chunk already "
        + "answers.",
        "Vixen.Engine",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        "docs/plan/04-ecs-and-scripting.md: a behaviour that keeps what the world already knows — a "
        + "cached transform, a copy of a component — is the shape that makes the ECS decoration. It "
        + "is also a copy nothing invalidates: a system writing the chunk leaves the behaviour's "
        + "field at whatever it was when it was read."
    );

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Handle, Copy];

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
                var entity = start.Compilation.GetTypeByMetadataName(EntityMetadataName);

                // Nothing to say in a compilation that has never heard of a behaviour. The engine's
                // own generator assembly is referenced by projects that are nowhere near it.
                if (behavior is null || entity is null) {
                    return;
                }

                var componentAttribute = start.Compilation.GetTypeByMetadataName(ComponentAttributeMetadataName);
                var tagComponent = start.Compilation.GetTypeByMetadataName(TagComponentMetadataName);

                start.RegisterSymbolAction(
                    symbol => Analyze(symbol, behavior, entity, componentAttribute, tagComponent),
                    SymbolKind.NamedType
                );

                var world = start.Compilation.GetTypeByMetadataName(WorldMetadataName);

                start.RegisterOperationAction(
                    operation => Cached(operation, behavior, world),
                    OperationKind.SimpleAssignment
                );
            }
        );
    }

    static void Analyze(
        SymbolAnalysisContext context,
        INamedTypeSymbol behavior,
        INamedTypeSymbol entity,
        INamedTypeSymbol? componentAttribute,
        INamedTypeSymbol? tagComponent
    ) {
        var type = (INamedTypeSymbol)context.Symbol;

        // The root itself is exempt and has to be: `Behavior.Entity` is the entity the behaviour is
        // on, which is the one handle the design does hand out.
        if (type.TypeKind != TypeKind.Class || SymbolEqualityComparer.Default.Equals(type, behavior)) {
            return;
        }

        if (!DerivesFrom(type, behavior)) {
            return;
        }

        // ⚠ Fields, and every field — the implicitly declared ones included. The rule is about what a
        // behaviour *holds*, and storage is the only honest reading of that: an auto-property has a
        // backing field and is caught through it, while a computed `Read<NetworkId>()` property has
        // none and is the correct pattern rather than a violation. Reading the property's own type
        // instead flagged `NetworkBehaviour.NetworkId`, which reaches through to the world on every
        // call and is exactly what the rule asks an author to write.
        foreach (var member in type.GetMembers()) {
            if (member is not IFieldSymbol { IsConst: false, IsStatic: false } field) {
                continue;
            }

            var located = Offender(field.Type, entity, componentAttribute, tagComponent, MaximumDepth);

            if (located is null) {
                continue;
            }

            // A backing field is named `<Target>k__BackingField` and sits on the property's line, so
            // the diagnostic is reported against the property an author actually wrote.
            var named = field.AssociatedSymbol ?? field;
            var location = named.Locations.FirstOrDefault() ?? Location.None;

            context.ReportDiagnostic(
                SymbolEqualityComparer.Default.Equals(located, entity)
                    ? Diagnostic.Create(Handle, location, named.Name)
                    : Diagnostic.Create(Copy, location, named.Name, located.Name)
            );
        }
    }

    /// <summary>Reports a behaviour member being filled with a component read out of the world.</summary>
    /// <remarks>
    ///     ⚠ <b>The type rule alone would miss the example the document names.</b> "A cached
    ///     transform" is a <c>LocalTransform</c>, and <c>LocalTransform</c> carries no
    ///     <c>[Component]</c> — the attribute is what makes a component <i>scene-placeable</i>, not
    ///     what makes it a component, so a rule that read only the annotation would be silent on the
    ///     one case doc 04 spells out. What is decidable instead is the shape of the read:
    ///     <c>Get&lt;T&gt;</c> and <c>Read&lt;T&gt;</c> return a component out of a chunk, and
    ///     assigning one into a member of the behaviour is the copy.
    /// </remarks>
    static void Cached(OperationAnalysisContext context, INamedTypeSymbol behavior, INamedTypeSymbol? world) {
        var assignment = (ISimpleAssignmentOperation)context.Operation;

        var (member, instance) = assignment.Target switch {
            IFieldReferenceOperation field => ((ISymbol)field.Field, field.Instance),
            IPropertyReferenceOperation property => (property.Property, property.Instance),
            _ => (null, null)
        };

        // Only a member of the behaviour itself. Writing a component into somebody else's field is
        // that type's business, and `this` is what makes it this behaviour's state.
        if (member is null || instance is not IInstanceReferenceOperation) {
            return;
        }

        if (member.ContainingType is not { } owner || !DerivesFrom(owner, behavior)) {
            return;
        }

        var read = Unwrap(assignment.Value);

        if (read is not IInvocationOperation { TargetMethod: { } method } invocation) {
            return;
        }

        if (method.Name is not ("Get" or "Read") || method.TypeArguments.Length != 1) {
            return;
        }

        // `Behavior.Get<T>` and `World.Get<T>`/`Read<T>`, and nothing else called Get.
        var declaring = method.ContainingType;

        var recognised = DerivesFrom(declaring, behavior)
            || SymbolEqualityComparer.Default.Equals(declaring, behavior)
            || (world is not null && SymbolEqualityComparer.Default.Equals(declaring, world));

        if (!recognised) {
            return;
        }

        context.ReportDiagnostic(
            Diagnostic.Create(Copy, invocation.Syntax.GetLocation(), member.Name, method.TypeArguments[0].Name)
        );
    }

    static IOperation Unwrap(IOperation value) =>
        value is IConversionOperation conversion ? Unwrap(conversion.Operand) : value;

    static bool DerivesFrom(INamedTypeSymbol type, INamedTypeSymbol root) {
        for (var current = type.BaseType; current is not null; current = current.BaseType) {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, root)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>The banned type a member's type reaches, or null.</summary>
    /// <remarks>
    ///     ⚠ <b>An entity beats a component when a member reaches both.</b> One member gets one
    ///     diagnostic, and the handle is the half that also corrupts a file.
    /// </remarks>
    static INamedTypeSymbol? Offender(
        ITypeSymbol held,
        INamedTypeSymbol entity,
        INamedTypeSymbol? componentAttribute,
        INamedTypeSymbol? tagComponent,
        int depth
    ) {
        if (depth <= 0) {
            return null;
        }

        if (held is IArrayTypeSymbol array) {
            return Offender(array.ElementType, entity, componentAttribute, tagComponent, depth - 1);
        }

        if (held is not INamedTypeSymbol named) {
            return null;
        }

        if (SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, entity)) {
            return entity;
        }

        INamedTypeSymbol? component = IsComponent(named, componentAttribute, tagComponent) ? named : null;

        foreach (var argument in named.TypeArguments) {
            var inside = Offender(argument, entity, componentAttribute, tagComponent, depth - 1);

            if (inside is null) {
                continue;
            }

            if (SymbolEqualityComparer.Default.Equals(inside, entity)) {
                return entity;
            }

            component ??= inside;
        }

        return component;
    }

    static bool IsComponent(
        INamedTypeSymbol type,
        INamedTypeSymbol? componentAttribute,
        INamedTypeSymbol? tagComponent
    ) {
        // ⚠ A component is a struct. `BehaviorRef` and the managed store exist, but a class a
        // behaviour holds is a reference to one object rather than a copy of a row — which is what
        // an asset or a service reference is, and those are allowed.
        if (!type.IsValueType) {
            return false;
        }

        if (componentAttribute is not null) {
            foreach (var attribute in type.GetAttributes()) {
                if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, componentAttribute)) {
                    return true;
                }
            }
        }

        if (tagComponent is null) {
            return false;
        }

        foreach (var implemented in type.AllInterfaces) {
            if (SymbolEqualityComparer.Default.Equals(implemented.OriginalDefinition, tagComponent)) {
                return true;
            }
        }

        return false;
    }
}
