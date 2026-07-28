// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Vixen.Editor.NodeGraph.Generator;

/// <summary>
///     Turns <c>[Node]</c>-attributed classes into a node library.
/// </summary>
/// <remarks>
///     <para>
///         <b>What it writes.</b> For each node class: a <c>Definition</c> describing its ports, and
///         the <c>Bind</c> override that fills its port fields from what the compiler resolved. For
///         each assembly: one <c>NodeTypes.Register</c> listing every definition in it.
///     </para>
///     <para>
///         <b>Why generated rather than reflected.</b> The obvious implementation walks the loaded
///         assemblies looking for the attribute, and costs a reflection pass at startup, an AOT
///         hazard, and a library whose contents depend on what happened to be loaded. Generating it
///         costs a build step and makes the library a compile-time fact — which is also what lets
///         <c>Create</c> be a <c>new</c> rather than an <c>Activator</c> call.
///     </para>
///     <para>
///         <b>Why the port metadata comes from the same text as the fields.</b> A node's ports could
///         have been declared in the attribute and its fields written by hand to match. They would
///         then be two lists that have to agree, and a port renamed in one place is an edge that
///         silently stops arriving. Reading both off one declaration is the only arrangement where
///         that cannot happen.
///     </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class NodeTypeGenerator : IIncrementalGenerator {
    const string NodeAttribute = "Vixen.Editor.NodeGraph.NodeAttribute";
    const string InputAttribute = "Vixen.Editor.NodeGraph.InputAttribute";
    const string OutputAttribute = "Vixen.Editor.NodeGraph.OutputAttribute";

    static readonly DiagnosticDescriptor MustBePartial = new(
        "VXN0101",
        "A [Node] class has to be partial",
        "{0}",
        "Vixen.NodeGraph",
        DiagnosticSeverity.Error,
        true
    );

    static readonly DiagnosticDescriptor NotAPortType = new(
        "VXN0102",
        "A port field's type is not a port type",
        "{0}",
        "Vixen.NodeGraph",
        DiagnosticSeverity.Error,
        true
    );

    static readonly DiagnosticDescriptor MustDeriveFromNode = new(
        "VXN0103",
        "A [Node] class has to derive from Node",
        "{0}",
        "Vixen.NodeGraph",
        DiagnosticSeverity.Error,
        true
    );

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var nodes = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                NodeAttribute,
                static (node, _) => node is ClassDeclarationSyntax,
                static (syntax, _) => Read(syntax)
            )
            .Where(static model => model is not null)
            .Select(static (model, _) => model!);

        context.RegisterSourceOutput(nodes, static (production, model) => Emit(production, model));

        var all = nodes.Collect().Combine(context.CompilationProvider.Select(static (c, _) => c.AssemblyName ?? "Nodes"));

        context.RegisterSourceOutput(all, static (production, pair) => Library(production, pair.Left, pair.Right));
    }

    /// <summary>Reads one node class into a model, or nothing when it is not one.</summary>
    static NodeModel? Read(GeneratorAttributeSyntaxContext syntax) {
        if (syntax.TargetSymbol is not INamedTypeSymbol type || syntax.TargetNode is not ClassDeclarationSyntax declaration) {
            return null;
        }

        var problems = ImmutableArray.CreateBuilder<DiagnosticModel>();
        var location = declaration.Identifier.GetLocation();

        if (!declaration.Modifiers.Any(modifier => modifier.ValueText == "partial")) {
            problems.Add(Problem(MustBePartial.Id, string.Format(CultureInfo.InvariantCulture, "'{0}' is a node type, so the generator writes its Bind method into it. Add 'partial'.", type.Name), location));
        }

        if (!DerivesFromNode(type)) {
            problems.Add(Problem(MustDeriveFromNode.Id, string.Format(CultureInfo.InvariantCulture, "'{0}' is a node type but does not derive from Vixen.Editor.NodeGraph.Node, so there is no Bind for the generator to override.", type.Name), location));
        }

        var attribute = syntax.Attributes[0];
        var path = attribute.ConstructorArguments.Length > 0 ? attribute.ConstructorArguments[0].Value as string ?? "" : "";
        var summary = Named(attribute, "Summary") as string ?? "";
        var preview = Named(attribute, "Preview") is bool flag && flag;

        var ports = ImmutableArray.CreateBuilder<PortModel>();

        foreach (var member in Fields(type)) {
            if (member is not IFieldSymbol field) {
                continue;
            }

            var input = Attribute(field, InputAttribute);
            var output = Attribute(field, OutputAttribute);

            if (input is null && output is null) {
                continue;
            }

            var marker = input ?? output!;
            var kind = KindOf(field.Type);

            if (kind is null) {
                problems.Add(Problem(
                    NotAPortType.Id,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "'{0}' is marked as a port and is a {1}. A port field's type says what the port carries, so it has to be one of the port value types — Scalar, Float2, Float3, Float4, DynamicVector, Bool, Int, Texture or Sampler.",
                        field.Name,
                        field.Type.Name
                    ),
                    field.Locations.Length > 0 ? field.Locations[0] : location
                ));

                continue;
            }

            ports.Add(new(
                field.Name,
                Named(marker, "Name") as string is { Length: > 0 } named ? named : field.Name,
                input is not null,
                kind,
                Default(marker, field, syntax.SemanticModel),
                field.Type.Name,
                Named(marker, "Summary") as string ?? ""
            ));
        }

        // Inputs before outputs, and declaration order within each. What a create menu draws and what
        // a golden test reads, so it is decided here rather than left to member order.
        var ordered = ports.Where(port => port.IsInput).Concat(ports.Where(port => !port.IsInput)).ToImmutableArray();

        return new(
            type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString(),
            type.Name,
            type.DeclaredAccessibility == Accessibility.Public ? "public" : "internal",
            path,
            summary,
            preview,
            ordered,
            problems.ToImmutable()
        );
    }

    /// <summary>Writes one node class's half.</summary>
    static void Emit(SourceProductionContext production, NodeModel model) {
        foreach (var problem in model.Problems) {
            production.ReportDiagnostic(Diagnostic.Create(Descriptor(problem.Id), problem.Where(), problem.Message));
        }

        if (model.Problems.Length > 0) {
            return;
        }

        var text = new StringBuilder();

        text.AppendLine("// <auto-generated/>")
            .AppendLine("#nullable enable")
            .AppendLine()
            .AppendLine("using System.Collections.Immutable;")
            .AppendLine("using Vixen.Editor.NodeGraph;")
            .AppendLine();

        if (model.Namespace.Length > 0) {
            text.AppendLine($"namespace {model.Namespace};").AppendLine();
        }

        text.AppendLine($"partial class {model.TypeName} {{")
            .AppendLine("    /// <summary>What this node type is, for the registry. Generated.</summary>")
            .AppendLine("    public static NodeTypeDefinition Definition { get; } = new(")
            .AppendLine($"        {Quote(model.Path)},")
            .AppendLine("        [");

        for (var index = 0; index < model.Ports.Length; index++) {
            var port = model.Ports[index];
            var comma = index == model.Ports.Length - 1 ? "" : ",";
            var direction = port.IsInput ? "Input" : "Output";
            var defaults = port.Default.IsEmpty
                ? "[]"
                : "[" + string.Join(", ", port.Default.Select(Float)) + "]";

            text.AppendLine(
                $"            new({Quote(port.Name)}, PortDirection.{direction}, PortKind.{port.Kind}, "
                + $"{defaults}, {Quote(port.Summary)}){comma}"
            );
        }

        text.AppendLine("        ],")
            .AppendLine($"        static () => new {model.TypeName}(),")
            .AppendLine($"        {Quote(model.Summary)},")
            .AppendLine($"        {(model.Preview ? "true" : "false")}")
            .AppendLine("    );")
            .AppendLine()
            .AppendLine("    /// <inheritdoc />")
            .AppendLine("    public override void Bind(NodeBinding binding) {");

        foreach (var port in model.Ports) {
            var side = port.IsInput ? "Input" : "Output";

            text.AppendLine($"        {port.Field} = new {port.Type}(binding.{side}({Quote(port.Name)}));");
        }

        text.AppendLine("    }")
            .AppendLine("}");

        production.AddSource($"{model.FullName}.Node.g.cs", text.ToString());
    }

    /// <summary>Writes the assembly's registration list.</summary>
    static void Library(SourceProductionContext production, ImmutableArray<NodeModel> models, string assembly) {
        if (models.IsEmpty) {
            return;
        }

        var text = new StringBuilder();

        text.AppendLine("// <auto-generated/>")
            .AppendLine("#nullable enable")
            .AppendLine()
            .AppendLine("using Vixen.Editor.NodeGraph;")
            .AppendLine()
            .AppendLine($"namespace {assembly};")
            .AppendLine()
            .AppendLine("/// <summary>Every node type this assembly declares. Generated.</summary>")
            .AppendLine("/// <remarks>")
            .AppendLine("///     A host registers the libraries it wants rather than everything that happens to be")
            .AppendLine("///     loaded, which is what makes a shader graph's create menu contain no spawner nodes.")
            .AppendLine("/// </remarks>")
            .AppendLine("public static class NodeTypes {")
            .AppendLine("    /// <summary>Adds them all to a registry.</summary>")
            .AppendLine("    /// <param name=\"registry\">The registry.</param>")
            .AppendLine("    public static void Register(NodeTypeRegistry registry) {")
            .AppendLine("        System.ArgumentNullException.ThrowIfNull(registry);")
            .AppendLine();

        foreach (var model in models.Where(model => model.Problems.IsEmpty).OrderBy(model => model.Path, StringComparer.Ordinal)) {
            // Fully qualified from the root, because the registration list is emitted into a
            // namespace derived from the assembly name and a node's own namespace may be a prefix of
            // it — `Tests.Thing` inside `Vixen.Editor.NodeGraph.Tests` resolves to the wrong thing,
            // or to nothing.
            text.AppendLine($"        registry.Add(global::{model.FullName}.Definition);");
        }

        text.AppendLine("    }")
            .AppendLine("}");

        production.AddSource("NodeTypes.g.cs", text.ToString());
    }

    static DiagnosticDescriptor Descriptor(string id) => id switch {
        "VXN0101" => MustBePartial,
        "VXN0102" => NotAPortType,
        _ => MustDeriveFromNode
    };

    static DiagnosticModel Problem(string id, string message, Location location) {
        var span = location.GetLineSpan();

        return new(
            id,
            message,
            span.Path,
            location.SourceSpan.Start,
            location.SourceSpan.Length,
            span.StartLinePosition.Line,
            span.StartLinePosition.Character,
            span.EndLinePosition.Line,
            span.EndLinePosition.Character
        );
    }

    /// <summary>
    ///     Every field of a node class, its base classes' first.
    /// </summary>
    /// <remarks>
    ///     Inherited ports are the point of a shared base: a VFX block declares an ordering wire in
    ///     and one out, and every block that derives from it has them without saying so. Roslyn's
    ///     <c>GetMembers</c> returns only what a type declares itself, so the walk is explicit — and
    ///     it goes base-first so an inherited port appears before the ones the leaf added, which is
    ///     the order a node is drawn in.
    /// </remarks>
    static IEnumerable<ISymbol> Fields(INamedTypeSymbol type) {
        List<INamedTypeSymbol> chain = [];

        for (var current = type; current is not null; current = current.BaseType) {
            if (current.ToDisplayString() == "Vixen.Editor.NodeGraph.Node") {
                break;
            }

            chain.Add(current);
        }

        chain.Reverse();

        foreach (var link in chain) {
            foreach (var member in link.GetMembers()) {
                yield return member;
            }
        }
    }

    static bool DerivesFromNode(INamedTypeSymbol type) {
        for (var current = type.BaseType; current is not null; current = current.BaseType) {
            if (current.ToDisplayString() == "Vixen.Editor.NodeGraph.Node") {
                return true;
            }
        }

        return false;
    }

    static AttributeData? Attribute(ISymbol symbol, string name) {
        foreach (var attribute in symbol.GetAttributes()) {
            if (attribute.AttributeClass?.ToDisplayString() == name) {
                return attribute;
            }
        }

        return null;
    }

    static object? Named(AttributeData attribute, string name) {
        foreach (var argument in attribute.NamedArguments) {
            if (argument.Key == name) {
                return argument.Value.Kind == TypedConstantKind.Array ? argument.Value.Values : argument.Value.Value;
            }
        }

        return null;
    }

    /// <summary>The port's default: the attribute's if it has one, else the field's initializer.</summary>
    static ImmutableArray<float> Default(AttributeData marker, IFieldSymbol field, SemanticModel model) {
        if (Named(marker, "Default") is ImmutableArray<TypedConstant> values && values.Length > 0) {
            var lanes = ImmutableArray.CreateBuilder<float>(values.Length);

            foreach (var value in values) {
                lanes.Add(System.Convert.ToSingle(value.Value ?? 0f, CultureInfo.InvariantCulture));
            }

            return lanes.ToImmutable();
        }

        foreach (var reference in field.DeclaringSyntaxReferences) {
            if (reference.GetSyntax() is not VariableDeclaratorSyntax { Initializer.Value: { } expression }) {
                continue;
            }

            // The initializer is read, not evaluated: the port value types hold an expression, so
            // `= 0.5f` is an implicit conversion whose result is overwritten before anything sees it.
            // The constant is what has to reach a create menu that never instantiates the node.
            //
            // A partial class may declare its fields in a different file from the one the attribute
            // was found in, and a semantic model only answers about its own tree.
            if (expression.SyntaxTree != model.SyntaxTree) {
                continue;
            }

            var constant = model.GetConstantValue(expression);

            if (constant.HasValue && constant.Value is not null) {
                return constant.Value switch {
                    bool flag => [flag ? 1f : 0f],
                    _ => [System.Convert.ToSingle(constant.Value, CultureInfo.InvariantCulture)]
                };
            }
        }

        return [];
    }

    static string? KindOf(ITypeSymbol type) => type.ToDisplayString() switch {
        "Vixen.Editor.NodeGraph.Scalar" => "Float",
        "Vixen.Editor.NodeGraph.Float2" => "Float2",
        "Vixen.Editor.NodeGraph.Float3" => "Float3",
        "Vixen.Editor.NodeGraph.Float4" => "Float4",
        "Vixen.Editor.NodeGraph.DynamicVector" => "Dynamic",
        "Vixen.Editor.NodeGraph.Bool" => "Bool",
        "Vixen.Editor.NodeGraph.Int" => "Int",
        "Vixen.Editor.NodeGraph.Texture" => "Texture",
        "Vixen.Editor.NodeGraph.Sampler" => "Sampler",
        "Vixen.Editor.NodeGraph.Flow" => "Flow",
        _ => null
    };

    static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    static string Float(float value) => value.ToString("R", CultureInfo.InvariantCulture) + "f";
}
