// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Vixen.Engine.Generators;

/// <summary>One behaviour this assembly declares, as much of it as the emission needs.</summary>
/// <param name="QualifiedName">Its fully qualified name, which is what the generated call names.</param>
/// <param name="Warning">Why it cannot be registered, or <see langword="null" /> if it can.</param>
/// <inheritdoc cref="ComponentModel" select="remarks" />
sealed record BehaviorModel(string QualifiedName, string? Warning);

/// <summary>Declares every behaviour this assembly can put in a scene, before any of its code runs.</summary>
/// <remarks>
///     <para>
///         <b><c>ComponentRegistrationGenerator</c>'s twin, keyed off <c>[DataContract]</c> rather
///         than <c>[Component]</c>.</b> A behaviour is a class and carries no <c>[Component]</c> —
///         the ECS never attaches it to an entity, <c>BehaviorStore</c> does — so what declares one
///         authorable is that it is a <c>Behavior</c> and that it can be described. Both halves are
///         necessary: a behaviour with no contract is one a scene could name and not restore, which
///         is worse than one it cannot name.
///     </para>
///     <para>
///         ⚠ <b>Started from the attribute rather than from the base type, because that is what an
///         incremental generator can index.</b> <c>ForAttributeWithMetadataName</c> is driven by a
///         table the compiler already keeps; asking "does this class derive from <c>Behavior</c>"
///         over every type in the compilation is a walk per keystroke. The base-type check is then a
///         cheap filter on the handful of types that carry the attribute at all.
///     </para>
///     <para>
///         ⚠ <b>A behaviour needs a public parameterless constructor, and the diagnostic says so.</b>
///         The registry's <c>Declare&lt;T&gt;</c> is constrained <c>new()</c> because the editor
///         makes one when somebody adds it and the loader makes one before it fills it in. A
///         behaviour whose only constructor takes arguments is perfectly good code and is not
///         authorable, and the difference is worth a message rather than a build error somewhere
///         else.
///     </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class BehaviorRegistrationGenerator : IIncrementalGenerator {
    const string ContractAttribute = "Vixen.Core.DataContractAttribute";
    const string BehaviorType = "Vixen.Engine.Behaviors.Behavior";
    const string RegistryType = "Vixen.Engine.Behaviors.SceneBehaviorRegistry";
    const string GeneratedNamespace = "Vixen.Generated.Engine";

    static readonly DiagnosticDescriptor NeedsConstructor = new(
        "VXS0402",
        "An authorable behaviour needs a parameterless constructor",
        "'{0}' is a described behaviour with no public parameterless constructor, so a scene cannot restore it",
        "Vixen.Engine",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        "The editor constructs one when somebody adds it and the loader constructs one before filling "
        + "it in, so a behaviour a scene may name has to be constructible with no arguments. Give it a "
        + "public parameterless constructor, or drop [DataContract] if it is not meant to be authored."
    );

    static readonly DiagnosticDescriptor GenericNotRegistered = new(
        "VXS0403",
        "A generic behaviour cannot be named by a scene",
        "'{0}' is generic, so a scene cannot name it and it is not registered",
        "Vixen.Engine",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        "A scene carries a behaviour's [DataContract] alias and nothing else, so one name would have "
        + "to stand for every instantiation. Declare the closed types the scene needs."
    );

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var behaviors = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                ContractAttribute,
                static (node, _) => node is Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax,
                static (syntaxContext, _) => Describe((INamedTypeSymbol) syntaxContext.TargetSymbol)
            )
            .Where(static model => model is not null)
            .Collect();

        // Selected down to a bool, which is `ComponentRegistrationGenerator`'s note: the compilation
        // changes on every keystroke and a bool compares equal to the last one.
        var reachable = context.CompilationProvider.Select(
            static (compilation, _) => compilation.GetTypeByMetadataName(RegistryType) is not null
        );

        context.RegisterSourceOutput(
            behaviors.Combine(reachable),
            static (production, pair) => Emit(production, pair.Left!, pair.Right)
        );
    }

    /// <summary>Describes a described type, or nothing if it is not a behaviour.</summary>
    static BehaviorModel? Describe(INamedTypeSymbol type) {
        if (!DerivesFromBehavior(type)) {
            return null;
        }

        var qualified = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        if (type.IsGenericType) {
            return new(qualified, qualified);
        }

        // ⚠ Abstract is silent rather than a diagnostic. A base behaviour that is described so its
        // members are inherited by the concrete ones below it is a perfectly ordinary thing to write,
        // and is not something a scene was ever going to name.
        if (type.IsAbstract) {
            return null;
        }

        return new(qualified, HasParameterlessConstructor(type) ? null : qualified);
    }

    static bool DerivesFromBehavior(INamedTypeSymbol type) {
        for (var walk = type.BaseType; walk is not null; walk = walk.BaseType) {
            if (walk.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::" + BehaviorType) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether <c>new T()</c> would compile.</summary>
    /// <remarks>
    ///     A class with no declared constructor at all gets the implicit one, which the symbol model
    ///     reports as an ordinary parameterless instance constructor — so the two cases are one test.
    /// </remarks>
    static bool HasParameterlessConstructor(INamedTypeSymbol type) =>
        type.InstanceConstructors.Any(
            constructor => constructor.Parameters.Length == 0
                && constructor.DeclaredAccessibility == Accessibility.Public
        );

    static void Emit(SourceProductionContext context, ImmutableArray<BehaviorModel> models, bool reachable) {
        var valid = new List<string>();

        foreach (var model in models) {
            if (model.Warning is not null) {
                // Which of the two it is, is which check produced it — a generic type never reaches
                // the constructor test, so the warning it carries names the reason it stopped.
                var descriptor = model.QualifiedName.Contains('<') ? GenericNotRegistered : NeedsConstructor;

                context.ReportDiagnostic(Diagnostic.Create(descriptor, Location.None, model.Warning));
                continue;
            }

            valid.Add(model.QualifiedName);
        }

        if (!reachable || valid.Count == 0) {
            return;
        }

        // Ordered, because a generator whose output moves for no reason makes every build a diff.
        valid.Sort(StringComparer.Ordinal);

        var source = new StringBuilder();
        source.AppendLine("// <auto-generated/>");
        source.AppendLine("#nullable disable");
        source.AppendLine();
        source.AppendLine($"namespace {GeneratedNamespace} {{");
        source.AppendLine("    /// <summary>Declares this assembly's scene behaviours before any of its code runs.</summary>");
        source.AppendLine("    internal static class BehaviorRegistration {");
        source.AppendLine("        [global::System.Runtime.CompilerServices.ModuleInitializer]");
        source.AppendLine("        internal static void Initialize() {");

        foreach (var behavior in valid) {
            source.AppendLine($"            global::{RegistryType}.Declare<{behavior}>();");
        }

        source.AppendLine("        }");
        source.AppendLine("    }");
        source.AppendLine("}");

        context.AddSource("BehaviorRegistration.g.cs", SourceText.From(source.ToString(), Encoding.UTF8));
    }
}
