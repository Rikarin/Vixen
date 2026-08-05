// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Vixen.Engine.Generators;

/// <summary>One component this assembly declares, as much of it as the emission needs.</summary>
/// <param name="QualifiedName">Its fully qualified name, which is what the generated call names.</param>
/// <param name="IsContract">Whether it also carries <c>[DataContract]</c>.</param>
/// <param name="HasDefault">Whether it implements <c>IDefaultComponent&lt;itself&gt;</c>.</param>
/// <param name="Warning">Why it cannot be registered, or <see langword="null" /> if it can.</param>
/// <remarks>
///     A record of strings rather than the symbol, because an incremental generator's pipeline
///     compares its intermediate values and a <see cref="ISymbol" /> keeps a whole compilation alive
///     to do it.
/// </remarks>
sealed record ComponentModel(string QualifiedName, bool IsContract, bool HasDefault, string? Warning);

/// <summary>Declares every component this assembly can put in a scene, before any of its code runs.</summary>
/// <remarks>
///     <para>
///         <b>Two attributes are two different claims, and both have to be made.</b>
///         <c>[Component]</c> says the ECS may attach it to an entity; <c>[DataContract]</c> says it
///         can be described and turned into bytes. A type carrying both is by construction something
///         a compiled scene can carry, and a type carrying only the first is a handle the bridge that
///         owns it writes — <c>PhysicsBody</c>, and the renderer's equivalent. Those are excluded
///         with no denylist and no opt-out, which is the whole reason the rule is a conjunction
///         rather than a marker of its own.
///     </para>
///     <para>
///         ⚠ <b>Compile-time discovery, which is the opposite of an assembly scan.</b> What gets
///         registered is what this generator saw in the source, so it survives trimming and
///         NativeAOT and it is the same set in the editor, in a build worker and in a shipped game.
///         The registry's own remarks used to argue for hand-written calls on exactly those grounds;
///         a scan would indeed have been wrong, and this is not one. It is the bargain
///         <c>SerializerRegistration</c> and <c>TypeRegistration</c> already struck.
///     </para>
///     <para>
///         ⚠ <b>Nothing is emitted for an assembly that cannot see the registry.</b>
///         <c>Vixen.Net</c> declares components and does not reference <c>Vixen.Engine</c>, so
///         naming the registry there would not compile. Wiring this generator into such an assembly
///         is deliberately a no-op rather than an error: the day it grows a reference, its components
///         register with nothing else asked of anybody.
///     </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class ComponentRegistrationGenerator : IIncrementalGenerator {
    const string ComponentAttribute = "Vixen.Core.ComponentAttribute";
    const string RegistryType = "Vixen.Engine.Scenes.SceneComponentRegistry";
    const string GeneratedNamespace = "Vixen.Generated.Engine";

    static readonly DiagnosticDescriptor GenericNotRegistered = new(
        "VXS0401",
        "A generic component cannot be named by a scene",
        "'{0}' is generic, so a compiled scene cannot name it and it is not registered",
        "Vixen.Engine",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        "A compiled scene carries a component's [DataContract] alias and nothing else, so one name "
        + "would have to stand for every instantiation. Declare the closed types the scene needs."
    );

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var components = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                ComponentAttribute,
                static (node, _) => node is Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax,
                static (syntaxContext, _) => Describe((INamedTypeSymbol) syntaxContext.TargetSymbol)
            )
            .Collect();

        // Selected down to a bool on purpose. The compilation itself changes on every keystroke, so
        // combining it directly would rerun the emission for every edit; a bool compares equal to
        // the last one and the pipeline stops there.
        var reachable = context.CompilationProvider.Select(
            static (compilation, _) => compilation.GetTypeByMetadataName(RegistryType) is not null
        );

        context.RegisterSourceOutput(
            components.Combine(reachable),
            static (production, pair) => Emit(production, pair.Left, pair.Right)
        );
    }

    static ComponentModel Describe(INamedTypeSymbol type) {
        var qualified = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var isContract = type.GetAttributes()
            .Any(attribute => attribute.AttributeClass?.Name == "DataContractAttribute");

        return new(qualified, isContract, HasDefault(type), type.IsGenericType ? qualified : null);
    }

    /// <summary>Whether the type declares a default, by implementing the interface closed over itself.</summary>
    /// <param name="type">The component.</param>
    /// <returns>Whether it does.</returns>
    /// <remarks>
    ///     ⚠ <b>Closed over itself specifically, not merely implementing the interface.</b> The
    ///     constraint on <c>IDefaultComponent&lt;TSelf&gt;</c> already makes anything else
    ///     uncompilable, so a type reaching here with a different argument does not exist — but the
    ///     generated call is constrained the same way, and matching the argument is what makes it
    ///     impossible for this to emit source that does not compile.
    /// </remarks>
    static bool HasDefault(INamedTypeSymbol type) =>
        type.AllInterfaces.Any(
            candidate => candidate.Name == "IDefaultComponent"
                && candidate.TypeArguments.Length == 1
                && SymbolEqualityComparer.Default.Equals(candidate.TypeArguments[0], type)
        );

    static void Emit(SourceProductionContext context, ImmutableArray<ComponentModel> models, bool reachable) {
        var valid = new List<ComponentModel>();

        foreach (var model in models) {
            // ⚠ Reported even where nothing would be emitted anyway. A generic component is a
            // mistake in the source and stays one whether or not this assembly can see the registry;
            // suppressing it there would make the warning come and go with a project reference.
            if (model.Warning is not null) {
                context.ReportDiagnostic(Diagnostic.Create(GenericNotRegistered, Location.None, model.Warning));
                continue;
            }

            // Silent, and not a diagnostic: a component with no [DataContract] is the handle case,
            // which is correct and common. A handle declaring a default is silent for the same
            // reason and loses nothing — it has no binder for a panel to reach it through either
            // way, and `World.AddDefault` resolves against the type rather than against this.
            if (model.IsContract) {
                valid.Add(model);
            }
        }

        if (!reachable || valid.Count == 0) {
            return;
        }

        // Ordered, because a generator whose output moves for no reason makes every build a diff.
        valid.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.QualifiedName, right.QualifiedName));

        var source = new StringBuilder();
        source.AppendLine("// <auto-generated/>");
        source.AppendLine("#nullable disable");
        source.AppendLine();
        source.AppendLine($"namespace {GeneratedNamespace} {{");
        source.AppendLine("    /// <summary>Declares this assembly's scene components before any of its code runs.</summary>");
        source.AppendLine("    internal static class ComponentRegistration {");
        source.AppendLine("        [global::System.Runtime.CompilerServices.ModuleInitializer]");
        source.AppendLine("        internal static void Initialize() {");

        foreach (var component in valid) {
            // ⚠ Two methods rather than one with an optional factory, because the factory can only be
            // written where the type argument carries the constraint. `DeclareWithDefault` is where
            // `T.Default` resolves, statically — which is why a component that declares one costs no
            // reflection and one that does not costs nothing at all.
            var method = component.HasDefault ? "DeclareWithDefault" : "Declare";

            source.AppendLine($"            global::{RegistryType}.{method}<{component.QualifiedName}>();");
        }

        source.AppendLine("        }");
        source.AppendLine("    }");
        source.AppendLine("}");

        context.AddSource("ComponentRegistration.g.cs", SourceText.From(source.ToString(), Encoding.UTF8));
    }
}
