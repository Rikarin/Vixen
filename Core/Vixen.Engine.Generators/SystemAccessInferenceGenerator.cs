// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Vixen.Engine.Generators;

/// <summary>One system's inferred access, reduced to what the emission needs.</summary>
/// <param name="QualifiedName">Its fully qualified name, for the diagnostic.</param>
/// <param name="Namespace">The namespace to reopen, or empty for the global one.</param>
/// <param name="TypeName">The bare type name to reopen as a partial.</param>
/// <param name="Reads">Fully qualified component types read, sorted.</param>
/// <param name="Writes">Fully qualified component types written, sorted.</param>
/// <param name="Problem">Why nothing can be emitted, or <see langword="null" /> if something can.</param>
sealed record AccessModel(
    string QualifiedName,
    string Namespace,
    string TypeName,
    ImmutableArray<string> Reads,
    ImmutableArray<string> Writes,
    DiagnosticDescriptor? Problem
);

/// <summary>
///     Reads a system's component access out of its own query bodies and emits the declaration.
/// </summary>
/// <remarks>
///     <para>
///         [04](../../docs/plan/04-ecs-and-scripting.md) § Layer 2 asks for this and names what it
///         must emit into: <c>IDeclaredAccess</c>, not <c>[Reads]</c>/<c>[Writes]</c>. The reason is
///         that <c>SystemAccess.Declare().Write&lt;Position&gt;()</c> closes a generic and therefore
///         <em>assigns</em> <c>Position</c> a component id, where an attribute names a
///         <c>System.Type</c> and can only look one up — and there is nothing to look up until
///         something in the process has stored one.
///     </para>
///     <para>
///         ⚠ <b>Emitting a declaration nothing reads would be worse than emitting none.</b>
///         <c>SystemGraph.Build</c> prefers <c>IDeclaredAccess</c> over the attributes, and
///         <c>SystemRunner</c> hands the same object to the job scheduler's safety system — so what
///         is inferred here decides both the frame's parallelism and what the race detector believes.
///         That is why it is opt-in: a wrong inference is a data race, and a class that has asked for
///         one is a class whose author says its access is visible in its own body.
///     </para>
///     <para>
///         ⚠ <b>The delegate and visitor forms cannot say read from write, so they are read as
///         writes.</b> <c>QueryAction&lt;T0, T1&gt;</c> takes every component by <c>ref</c> whether or
///         not the body assigns through one, so there is no signal to read. Over-declaring costs
///         parallelism and under-declaring is a race, so the direction is chosen and written down
///         rather than guessed at per call. The chunk form is exact, because
///         <c>Values&lt;T&gt;</c> and <c>ReadValues&lt;T&gt;</c> are different calls — which is the
///         same distinction <c>Get</c> and <c>Read</c> make on the world.
///     </para>
///     <para>
///         <b>Here rather than in <c>Vixen.Ecs.Generators</c>.</b> That project is referenced by
///         <c>Vixen.Ecs</c> and travels in no package, because its output does not depend on the
///         compilation and a second referencing assembly would emit a second copy of it. This one has
///         to run in a game's own compilation, which is what this assembly is packed for.
///     </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class SystemAccessInferenceGenerator : IIncrementalGenerator {
    const string InferAttribute = "Vixen.Ecs.Systems.InferAccessAttribute";
    const string SystemInterface = "global::Vixen.Ecs.Systems.ISystem";
    const string DeclaredAccessInterface = "global::Vixen.Ecs.Systems.IDeclaredAccess";
    const string ReadsAttribute = "Vixen.Ecs.Systems.ReadsAttribute";
    const string WritesAttribute = "Vixen.Ecs.Systems.WritesAttribute";

    const string QueryDescriptionType = "global::Vixen.Ecs.QueryDescription";
    const string WorldType = "global::Vixen.Ecs.World";
    const string ChunkType = "global::Vixen.Ecs.Chunk";
    const string CommandBufferType = "global::Vixen.Ecs.CommandBuffer";
    const string ParallelWriterType = "global::Vixen.Ecs.CommandBuffer.ParallelWriter";
    const string QueryExtensionsType = "global::Vixen.Ecs.WorldQueryExtensions";

    static readonly DiagnosticDescriptor NotASystem = new(
        "VXS0407",
        "An inferred access has to be on a system",
        "'{0}' is marked [InferAccess] but does not implement ISystem, so nothing would ever read the declaration",
        "Vixen.Engine",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        "SystemGraph reads IDeclaredAccess off the systems a runner was given. On anything else the "
        + "generated property is dead code. Implement ISystem — deriving from SystemBase is the "
        + "usual way — or drop the attribute."
    );

    static readonly DiagnosticDescriptor NotPartial = new(
        "VXS0408",
        "An inferred access needs a partial, top-level, non-generic class",
        "'{0}' is marked [InferAccess] but is not a partial top-level non-generic class, so the declaration has nowhere to go",
        "Vixen.Engine",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        "The declaration is emitted as the other half of the class. A nested type would need every "
        + "type around it to be partial too, and a generic one would need a declaration per "
        + "instantiation — both are better refused than half-supported."
    );

    static readonly DiagnosticDescriptor AlreadyDeclared = new(
        "VXS0409",
        "A system that declares its own access does not need one inferred",
        "'{0}' is marked [InferAccess] and already implements IDeclaredAccess, so the inferred declaration is dropped",
        "Vixen.Engine",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        "A system whose access is computed at construction has one place that says what it is, which "
        + "is the whole point of the interface. Emitting a second Access property there would not "
        + "compile; dropping it silently would leave the attribute looking like it did something."
    );

    static readonly DiagnosticDescriptor AttributesOverride = new(
        "VXS0410",
        "An explicit access attribute overrides the inferred one",
        "'{0}' is marked [InferAccess] and also carries [Reads] or [Writes], so the attributes win and nothing is inferred",
        "Vixen.Engine",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        "Explicit attributes override inference, so the inferred declaration would never be read. "
        + "Two declarations that agree everywhere except where the schedule is wrong is the shape "
        + "this refuses; keep one of them."
    );

    static readonly DiagnosticDescriptor NothingInferred = new(
        "VXS0411",
        "Nothing could be inferred from this system's body",
        "'{0}' is marked [InferAccess] but no component access was found in its body, so it stays undeclared and conflicts with everything",
        "Vixen.Engine",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        "An empty declaration would be indistinguishable from a system nobody annotated — the "
        + "runner reads both as 'conflicts with everything' and serialises them. Either the queries "
        + "are somewhere this cannot see, in which case declare them with [Reads]/[Writes] or "
        + "IDeclaredAccess, or the attribute is on the wrong class."
    );

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var systems = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                InferAttribute,
                static (node, _) => node is ClassDeclarationSyntax,
                static (syntaxContext, token) => Describe(syntaxContext, token)
            )
            .Collect();

        context.RegisterSourceOutput(systems, static (production, models) => Emit(production, models));
    }

    static AccessModel Describe(GeneratorAttributeSyntaxContext context, CancellationToken token) {
        var type = (INamedTypeSymbol) context.TargetSymbol;
        var qualified = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var space = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString();

        AccessModel Refused(DiagnosticDescriptor problem) =>
            new(qualified, space, type.Name, [], [], problem);

        // Before the shape checks, because "this is not a system" is the mistake and "it is not
        // partial" is only how it would have been fixed. A class that is neither should be told the
        // first thing.
        if (!Implements(type, SystemInterface)) {
            return Refused(NotASystem);
        }

        if (type.ContainingType is not null || type.IsGenericType || !IsPartial(type)) {
            return Refused(NotPartial);
        }

        if (Implements(type, DeclaredAccessInterface)) {
            return Refused(AlreadyDeclared);
        }

        foreach (var attribute in type.GetAttributes()) {
            var name = attribute.AttributeClass?.ToDisplayString();

            if (name is ReadsAttribute or WritesAttribute) {
                return Refused(AttributesOverride);
            }
        }

        var reads = new SortedSet<string>(StringComparer.Ordinal);
        var writes = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var reference in type.DeclaringSyntaxReferences) {
            token.ThrowIfCancellationRequested();
            var node = reference.GetSyntax(token);
            var model = context.SemanticModel.Compilation.GetSemanticModel(node.SyntaxTree);

            foreach (var invocation in node.DescendantNodes().OfType<InvocationExpressionSyntax>()) {
                token.ThrowIfCancellationRequested();

                if (model.GetSymbolInfo(invocation, token).Symbol is not IMethodSymbol method) {
                    continue;
                }

                Collect(method, reads, writes);
            }
        }

        // A write implies a read, and SystemAccess says so too — but the emitted call chain says
        // `Read<T>()` for the ones that are only read, so the two sets are kept apart here.
        foreach (var written in writes) {
            reads.Remove(written);
        }

        if (reads.Count == 0 && writes.Count == 0) {
            return Refused(NothingInferred);
        }

        return new(qualified, space, type.Name, [.. reads], [.. writes], null);
    }

    static void Collect(IMethodSymbol method, SortedSet<string> reads, SortedSet<string> writes) {
        if (method.TypeArguments.Length == 0) {
            return;
        }

        var owner = (method.ReducedFrom ?? method).ContainingType
            .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        var name = method.Name;

        var into = owner switch {
            // A filter says what the entity must not have, which is not something the system reads.
            QueryDescriptionType when name is "WithNone" => null,
            QueryDescriptionType when name is "WithAll" or "WithAny" or "WithChanged" => reads,

            // Handing out a `ref` counts as a write whether or not one happens, which is the same
            // rule the world itself applies when it marks the chunk's column changed.
            WorldType when name is "Read" or "TryGet" or "Has" => reads,
            WorldType when name is "Get" or "Set" or "Add" or "AddDefault" or "Remove" or "Create" => writes,

            ChunkType when name is "ReadValues" => reads,
            ChunkType when name is "Values" => writes,

            CommandBufferType or ParallelWriterType when name is "Add" or "Set" or "Remove" => writes,

            // ⚠ Every component the delegate and visitor forms name is read as a write. The generated
            // delegates take all of them by `ref` whether or not the body assigns through one, so
            // there is nothing to read the direction from — and the safe direction is the wide one.
            QueryExtensionsType when name is "Query" or "QueryWithEntity" or "ForEach" => writes,

            _ => null
        };

        if (into is null) {
            return;
        }

        // `ForEach<TVisitor, T0, …>` names the visitor first, and a visitor is not a component.
        var first = name == "ForEach" ? 1 : 0;

        for (var index = first; index < method.TypeArguments.Length; index++) {
            var argument = method.TypeArguments[index];

            // A system that is itself generic, or a compilation that is mid-edit, names a component
            // this cannot resolve. Declaring nothing for it is what makes the diagnostic below fire
            // rather than a wrong declaration land.
            if (argument.TypeKind is TypeKind.TypeParameter or TypeKind.Error) {
                continue;
            }

            into.Add(argument.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        }
    }

    static bool IsPartial(INamedTypeSymbol type) =>
        type.DeclaringSyntaxReferences
            .Select(reference => reference.GetSyntax())
            .OfType<ClassDeclarationSyntax>()
            .Any(declaration => declaration.Modifiers.Any(modifier => modifier.ValueText == "partial"));

    static bool Implements(INamedTypeSymbol type, string qualifiedInterface) =>
        type.AllInterfaces.Any(
            contract => contract.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == qualifiedInterface
        );

    static void Emit(SourceProductionContext context, ImmutableArray<AccessModel> models) {
        var valid = new List<AccessModel>();

        foreach (var model in models) {
            if (model.Problem is not null) {
                context.ReportDiagnostic(Diagnostic.Create(model.Problem, Location.None, model.QualifiedName));
                continue;
            }

            valid.Add(model);
        }

        if (valid.Count == 0) {
            return;
        }

        // Ordered, because a generator whose output moves for no reason makes every build a diff.
        valid.Sort(static (left, right) => string.CompareOrdinal(left.QualifiedName, right.QualifiedName));

        foreach (var model in valid) {
            var source = new StringBuilder();
            source.AppendLine("// <auto-generated/>");
            source.AppendLine("#nullable enable");
            source.AppendLine();

            var indent = "";

            if (model.Namespace.Length > 0) {
                source.AppendLine($"namespace {model.Namespace} {{");
                indent = "    ";
            }

            source.AppendLine($"{indent}partial class {model.TypeName} : {DeclaredAccessInterface} {{");
            source.AppendLine($"{indent}    /// <summary>What this system's own body reads and writes.</summary>");
            source.AppendLine($"{indent}    public global::Vixen.Ecs.Systems.SystemAccess Access {{ get; }} =");
            source.AppendLine($"{indent}        global::Vixen.Ecs.Systems.SystemAccess.Declare()");

            foreach (var component in model.Reads) {
                source.AppendLine($"{indent}            .Read<{component}>()");
            }

            foreach (var component in model.Writes) {
                source.AppendLine($"{indent}            .Write<{component}>()");
            }

            source.AppendLine($"{indent}            .Build();");
            source.AppendLine($"{indent}}}");

            if (model.Namespace.Length > 0) {
                source.AppendLine("}");
            }

            var hint = model.QualifiedName.Replace("global::", "").Replace('.', '_');
            context.AddSource($"{hint}.Access.g.cs", SourceText.From(source.ToString(), Encoding.UTF8));
        }
    }
}
