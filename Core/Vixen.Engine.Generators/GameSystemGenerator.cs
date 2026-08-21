// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Vixen.Engine.Generators;

/// <summary>One system this assembly declares, as much of it as the emission needs.</summary>
/// <param name="QualifiedName">Its fully qualified name, which is what the generated call names.</param>
/// <param name="Parameters">
///     Its constructor's parameter types, fully qualified, in order. Empty for a system that takes
///     no services — which is a declaration, not a missing one.
/// </param>
/// <param name="Problem">Why it cannot be declared, or <see langword="null" /> if it can.</param>
sealed record SystemModel(string QualifiedName, ImmutableArray<string> Parameters, DiagnosticDescriptor? Problem);

/// <summary>Declares every system this assembly puts in a game's frame, before any of its code runs.</summary>
/// <remarks>
///     <para>
///         <b>The other two generators here register something a <em>file</em> may name; this one
///         registers something a <em>frame</em> is made of.</b> A project's system set used to exist
///         only as the imperative body of its <c>Game.OnInitialise</c>, so the editor could list a
///         project's <c>ISystem</c> types by reflection and could not run one of them. What crosses
///         instead is a declaration plus a constructor call written at compile time.
///     </para>
///     <para>
///         ⚠ <b>The constructor is the service list, and the emitted factory is why there is no
///         reflection.</b> <c>ConstructorInfo.Invoke</c> would have made this a small DI container,
///         which is the thing <c>ServiceRegistry</c>'s remarks refuse on NativeAOT grounds. A
///         generator already knows every parameter's type, so it writes the <c>new</c> with the
///         casts in it and the runtime does a dictionary lookup per parameter and nothing else.
///     </para>
///     <para>
///         ⚠ <b>One public constructor, and the diagnostic says so.</b> Two would make "which
///         services does this system need" a question with two answers and no way to choose between
///         them; none would make it unconstructible. Both are perfectly good code that is not
///         declarable, which is worth a message rather than a compile error in generated source.
///     </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class GameSystemGenerator : IIncrementalGenerator {
    const string SystemAttribute = "Vixen.Engine.Frames.GameSystemAttribute";
    const string SystemInterface = "global::Vixen.Ecs.Systems.ISystem";
    const string RegistryType = "Vixen.Engine.Frames.GameSystemRegistry";
    const string GeneratedNamespace = "Vixen.Generated.Engine";

    static readonly DiagnosticDescriptor NotASystem = new(
        "VXS0404",
        "A declared game system has to be a system",
        "'{0}' is marked [GameSystem] but does not implement ISystem, so nothing could add it to a frame",
        "Vixen.Engine",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        "A frame is a graph of ISystem, and [GameSystem] is what puts one in it. Implement ISystem — "
        + "deriving from SystemBase is the usual way — or drop the attribute."
    );

    static readonly DiagnosticDescriptor NotConstructible = new(
        "VXS0405",
        "A declared game system needs exactly one public constructor",
        "'{0}' is marked [GameSystem] and does not have exactly one public constructor, so what it needs is ambiguous",
        "Vixen.Engine",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        "A declared system's constructor parameters are the services it asks for, which is the whole "
        + "of how it says what it needs. Two public constructors would be two different answers with "
        + "nothing to choose between them, and none would be a system nothing can build."
    );

    static readonly DiagnosticDescriptor NotConcrete = new(
        "VXS0406",
        "A declared game system cannot be abstract or generic",
        "'{0}' is marked [GameSystem] and is abstract or generic, so there is no one system to add",
        "Vixen.Engine",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        "A declaration names one type to construct. An abstract system has no instance and a generic "
        + "one would need a closed type per instantiation; declare the concrete systems instead."
    );

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var systems = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                SystemAttribute,
                static (node, _) => node is Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax,
                static (syntaxContext, _) => Describe((INamedTypeSymbol) syntaxContext.TargetSymbol)
            )
            .Collect();

        // Reduced to a bool for `ComponentRegistrationGenerator`'s reason: the compilation changes on
        // every keystroke and a bool compares equal to the last one.
        var reachable = context.CompilationProvider.Select(
            static (compilation, _) => compilation.GetTypeByMetadataName(RegistryType) is not null
        );

        context.RegisterSourceOutput(
            systems.Combine(reachable),
            static (production, pair) => Emit(production, pair.Left, pair.Right)
        );
    }

    static SystemModel Describe(INamedTypeSymbol type) {
        var qualified = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        if (type.IsAbstract || type.IsGenericType) {
            return new(qualified, ImmutableArray<string>.Empty, NotConcrete);
        }

        if (!ImplementsSystem(type)) {
            return new(qualified, ImmutableArray<string>.Empty, NotASystem);
        }

        var constructors = type.InstanceConstructors
            .Where(constructor => constructor.DeclaredAccessibility == Accessibility.Public)
            .ToArray();

        if (constructors.Length != 1) {
            return new(qualified, ImmutableArray<string>.Empty, NotConstructible);
        }

        var parameters = constructors[0]
            .Parameters
            .Select(parameter => parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
            .ToImmutableArray();

        return new(qualified, parameters, null);
    }

    static bool ImplementsSystem(INamedTypeSymbol type) =>
        type.AllInterfaces.Any(
            contract => contract.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == SystemInterface
        );

    static void Emit(SourceProductionContext context, ImmutableArray<SystemModel> models, bool reachable) {
        var valid = new List<SystemModel>();

        foreach (var model in models) {
            if (model.Problem is not null) {
                context.ReportDiagnostic(Diagnostic.Create(model.Problem, Location.None, model.QualifiedName));
                continue;
            }

            valid.Add(model);
        }

        if (!reachable || valid.Count == 0) {
            return;
        }

        // Ordered, because a generator whose output moves for no reason makes every build a diff.
        valid.Sort(static (left, right) => string.CompareOrdinal(left.QualifiedName, right.QualifiedName));

        var source = new StringBuilder();
        source.AppendLine("// <auto-generated/>");
        source.AppendLine("#nullable disable");
        source.AppendLine();
        source.AppendLine($"namespace {GeneratedNamespace} {{");
        source.AppendLine("    /// <summary>Declares this assembly's game systems before any of its code runs.</summary>");
        source.AppendLine("    internal static class GameSystemRegistration {");
        source.AppendLine("        [global::System.Runtime.CompilerServices.ModuleInitializer]");
        source.AppendLine("        internal static void Initialize() {");

        foreach (var system in valid) {
            var types = system.Parameters.Length == 0
                ? "global::System.Type[] { }"
                : "global::System.Type[] { " + string.Join(", ", system.Parameters.Select(name => $"typeof({name})")) + " }";

            var arguments = string.Join(
                ", ",
                system.Parameters.Select((name, index) => $"({name}) services[{index}]")
            );

            source.AppendLine($"            global::{RegistryType}.Declare(");
            source.AppendLine($"                typeof({system.QualifiedName}),");
            source.AppendLine($"                new {types},");
            source.AppendLine($"                static services => new {system.QualifiedName}({arguments})");
            source.AppendLine("            );");
        }

        source.AppendLine("        }");
        source.AppendLine("    }");
        source.AppendLine("}");

        context.AddSource("GameSystemRegistration.g.cs", SourceText.From(source.ToString(), Encoding.UTF8));
    }
}
