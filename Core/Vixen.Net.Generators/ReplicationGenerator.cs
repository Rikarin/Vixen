// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Vixen.Net.Generators;

/// <summary>
///     Writes the replicator for every <c>[Replicated]</c> component, and the registration that
///     hands them to the runtime.
/// </summary>
/// <remarks>
///     <para>
///         This is the code a careful person would have written and would then have to keep in step
///         with the struct — the reason it is generated is that nobody does. A field added to a
///         component and forgotten in its serializer is a desync that reproduces on one machine in
///         ten; here the two cannot disagree, because there is only one declaration.
///     </para>
///     <para>
///         It also has to be generated rather than reflected. iOS is NativeAOT, so there is no
///         run-time code generation to fall back on and reflection over a struct's fields is exactly
///         what trimming removes. Everything about a component's layout is decided at build time and
///         becomes ordinary C# that the AOT compiler can see through.
///     </para>
///     <para>
///         ⚠ <b>A generator is judged by what it does not re-run.</b> The per-type step produces a
///         <see cref="string" /> of finished source, so the cache compares text: editing an unrelated
///         file re-runs nothing, and editing a component re-emits that component alone. Only the
///         registration file depends on the set of them, and only the set — adding a field to a
///         component does not invalidate it.
///     </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class ReplicationGenerator : IIncrementalGenerator {
    /// <summary>The attribute that says a component is replicated.</summary>
    public const string ReplicatedAttribute = "Vixen.Net.Replication.ReplicatedAttribute";

    /// <summary>The attribute that declares what a float is a float of.</summary>
    public const string QuantizeAttribute = "Vixen.Net.Replication.QuantizeAttribute";

    /// <summary>The namespace the generated replicators live in.</summary>
    public const string GeneratedNamespace = "Vixen.Net.Generated";

    /// <summary>The name of the step that turns one component declaration into source.</summary>
    /// <remarks>
    ///     Named so a test can ask what the pipeline did rather than how long it took: an incremental
    ///     generator's whole claim is about the second run, and the only way to check it is to look
    ///     at the reasons Roslyn recorded.
    /// </remarks>
    public const string DescribeStep = "Replication.Describe";

    static readonly DiagnosticDescriptor UnsupportedField = new(
        "VXNET1001",
        "A replicated field has a type that cannot be sent",
        "'{0}' is a {1}, which replication cannot put on the wire. Replicate a field of a supported type, or write the IComponentReplicator by hand.",
        "Vixen.Net",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static readonly DiagnosticDescriptor QuantizeNotFloat = new(
        "VXNET1002",
        "[Quantize] is on something that is not a float",
        "'{0}' is a {1}. [Quantize] declares the range of a float or a Vector3; an integer already knows what it is worth, and a rotation has no range to declare.",
        "Vixen.Net",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static readonly DiagnosticDescriptor QuantizeInvalid = new(
        "VXNET1003",
        "[Quantize] declares a range that cannot be encoded with",
        "'{0}' asks for {1} bits over [{2}, {3}]. A width is between 1 and 32 and the range has to go upwards.",
        "Vixen.Net",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static readonly DiagnosticDescriptor NothingToSend = new(
        "VXNET1004",
        "A replicated component has nothing in it",
        "'{0}' is replicated and has no public fields, so every snapshot of it is empty. Add a field, or drop the attribute.",
        "Vixen.Net",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var components = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                ReplicatedAttribute,
                static (node, _) => node is StructDeclarationSyntax,
                static (attributed, _) => Describe(attributed)
            )
            .WithTrackingName(DescribeStep);

        context.RegisterSourceOutput(
            components,
            static (production, model) => {
                foreach (var diagnostic in model.Diagnostics) {
                    production.ReportDiagnostic(diagnostic.ToDiagnostic());
                }

                if (model.Source.Length != 0) {
                    production.AddSource(model.HintName, SourceText.From(model.Source, Encoding.UTF8));
                }
            }
        );

        // Only the names, so that editing the inside of a component does not invalidate the
        // registration file. Adding or removing one does, which is exactly when it changes.
        var names = components.Select(static (model, _) => model.ClassName)
            .Where(static name => name.Length != 0)
            .Collect();

        context.RegisterSourceOutput(
            names,
            static (production, all) => {
                if (all.Length == 0) {
                    return;
                }

                production.AddSource(
                    "ReplicatedComponents.g.cs",
                    SourceText.From(EmitRegistration(all), Encoding.UTF8)
                );
            }
        );
    }

    static ReplicatorModel Describe(GeneratorAttributeSyntaxContext attributed) {
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();

        if (attributed.TargetSymbol is not INamedTypeSymbol type) {
            return new(string.Empty, string.Empty, string.Empty, new(diagnostics.ToImmutable()));
        }

        var fullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var wireName = type.ToDisplayString(
            new SymbolDisplayFormat(
                globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
                typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces
            )
        );

        var fields = ImmutableArray.CreateBuilder<WireValue>();

        foreach (var member in type.GetMembers()) {
            if (member is not IFieldSymbol field || field.IsStatic || field.IsConst || field.IsImplicitlyDeclared) {
                continue;
            }

            if (field.DeclaredAccessibility != Accessibility.Public) {
                continue;
            }

            var described = DescribeField(field, diagnostics);

            if (described is not null) {
                fields.Add(described.Value);
            }
        }

        if (fields.Count == 0 && diagnostics.Count == 0) {
            diagnostics.Add(Report(NothingToSend, type.Locations, wireName));
        }

        var className = Sanitize(wireName) + "Replicator";
        var settings = ReadSettings(attributed.Attributes);

        // An error emits nothing: the generated replicator would not compile, and a page of errors
        // inside code the author cannot see buries the one line that is wrong. A warning still
        // emits — an empty component is legal, and somebody may be part-way through writing it.
        var source = HasError(diagnostics)
            ? string.Empty
            : Emit(className, fullName, wireName, settings, fields.ToImmutable());

        return new(
            source.Length == 0 ? string.Empty : className,
            $"{className}.g.cs",
            source,
            new(diagnostics.ToImmutable())
        );
    }

    static WireValue? DescribeField(IFieldSymbol field, ImmutableArray<DiagnosticInfo>.Builder diagnostics) {
        var quantize = FindQuantize(field);
        var typeName = field.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        if (quantize is not null && !WireCodec.AcceptsQuantize(field.Type)) {
            diagnostics.Add(Report(QuantizeNotFloat, field.Locations, field.Name, typeName));

            return null;
        }

        if (quantize is { } range) {
            if (range.Bits is < 1 or > 32 || !(range.Max > range.Min)) {
                diagnostics.Add(
                    Report(
                        QuantizeInvalid,
                        field.Locations,
                        field.Name,
                        range.Bits.ToString(CultureInfo.InvariantCulture),
                        WireCodec.Literal(range.Min),
                        WireCodec.Literal(range.Max)
                    )
                );

                return null;
            }

            return new(field.Name, WireCodec.KindOf(field.Type, quantized: true), range.Min, range.Max, range.Bits);
        }

        var kind = WireCodec.KindOf(field.Type);

        if (kind == WireKind.Unsupported) {
            diagnostics.Add(Report(UnsupportedField, field.Locations, field.Name, typeName));

            return null;
        }

        return new(field.Name, kind, 0, 0, 0);
    }

    static Quantize? FindQuantize(IFieldSymbol field) {
        foreach (var attribute in field.GetAttributes()) {
            if (attribute.AttributeClass?.ToDisplayString() != QuantizeAttribute
                || attribute.ConstructorArguments.Length != 3) {
                continue;
            }

            var min = attribute.ConstructorArguments[0].Value;
            var max = attribute.ConstructorArguments[1].Value;
            var bits = attribute.ConstructorArguments[2].Value;

            if (min is float minimum && max is float maximum && bits is int width) {
                return new(minimum, maximum, width);
            }
        }

        return null;
    }

    static Settings ReadSettings(ImmutableArray<AttributeData> attributes) {
        var channel = "Unreliable";
        var priority = 0;

        foreach (var attribute in attributes) {
            foreach (var named in attribute.NamedArguments) {
                switch (named.Key) {
                    case "Channel" when named.Value.Value is int value:
                        channel = value switch {
                            0 => "Reliable",
                            1 => "ReliableUnordered",
                            3 => "Sequenced",
                            _ => "Unreliable"
                        };

                        break;

                    case "Priority" when named.Value.Value is int value:
                        priority = value;

                        break;

                    default:
                        break;
                }
            }
        }

        return new(channel, priority);
    }

    static string Emit(
        string className,
        string fullName,
        string wireName,
        Settings settings,
        ImmutableArray<WireValue> fields
    ) {
        var source = new StringBuilder();

        source.AppendLine("// <auto-generated/>");
        source.AppendLine("#nullable enable");
        source.AppendLine();
        source.AppendLine($"namespace {GeneratedNamespace};");
        source.AppendLine();
        source.AppendLine($"/// <summary>Replicates <see cref=\"{wireName}\" />. Written by Vixen.Net.Generators.</summary>");
        source.AppendLine($"internal sealed class {className} : global::Vixen.Net.Replication.IComponentReplicator {{");
        source.AppendLine($"    public static readonly {className} Instance = new();");
        source.AppendLine();

        foreach (var field in fields) {
            var range = WireCodec.RangeField(in field);

            if (range.Length != 0) {
                source.AppendLine(range);
            }
        }

        EmitLanes(source, fields);

        source.AppendLine();
        source.AppendLine("    static readonly global::Vixen.Ecs.QueryDescription Changed =");
        source.AppendLine("        new global::Vixen.Ecs.QueryDescription().RequireChanged(");
        source.AppendLine($"            new[] {{ global::Vixen.Ecs.ComponentType<{fullName}>.Id }}");
        source.AppendLine("        );");
        source.AppendLine();
        source.AppendLine("    public global::Vixen.Core.ComponentTypeId ComponentType =>");
        source.AppendLine($"        global::Vixen.Ecs.ComponentType<{fullName}>.Id;");
        source.AppendLine();
        source.AppendLine($"    public uint TypeId => {HashTypeName(wireName)}u;");
        source.AppendLine();
        source.AppendLine($"    public string TypeName => \"{wireName}\";");
        source.AppendLine();
        source.AppendLine($"    public global::Vixen.Net.Channel Channel => global::Vixen.Net.Channel.{settings.Channel};");
        source.AppendLine();
        source.AppendLine($"    public int Priority => {settings.Priority.ToString(CultureInfo.InvariantCulture)};");
        source.AppendLine();
        source.AppendLine("    public global::Vixen.Ecs.QueryDescription ChangedQuery => Changed;");
        source.AppendLine();
        source.AppendLine("    public global::System.ReadOnlySpan<global::Vixen.Net.Messaging.WireLane> Lanes => Layout;");
        source.AppendLine();
        source.AppendLine("    public bool Has(global::Vixen.Ecs.World world, global::Vixen.Core.Entity entity) =>");
        source.AppendLine($"        world.Has<{fullName}>(entity);");
        source.AppendLine();
        source.AppendLine("    public void Write(");
        source.AppendLine("        global::Vixen.Ecs.World world,");
        source.AppendLine("        global::Vixen.Core.Entity entity,");
        source.AppendLine("        ref global::Vixen.Net.Messaging.BitWriter writer");
        source.AppendLine("    ) {");
        source.AppendLine($"        ref readonly var value = ref world.Read<{fullName}>(entity);");

        foreach (var field in fields) {
            source.AppendLine($"        {WireCodec.Write(in field, $"value.{field.Name}")}");
        }

        source.AppendLine("    }");
        source.AppendLine();
        source.AppendLine("    public bool Apply(");
        source.AppendLine("        global::Vixen.Ecs.World world,");
        source.AppendLine("        global::Vixen.Core.Entity entity,");
        source.AppendLine("        ref global::Vixen.Net.Messaging.BitReader reader");
        source.AppendLine("    ) {");
        source.AppendLine($"        var value = default({fullName});");
        source.AppendLine();

        for (var i = 0; i < fields.Length; i++) {
            var field = fields[i];
            source.AppendLine($"        if (!{WireCodec.Read(in field, $"read{i}")}) {{");
            source.AppendLine("            return false;");
            source.AppendLine("        }");
            source.AppendLine();
            source.AppendLine($"        value.{field.Name} = {WireCodec.Convert(in field, $"read{i}")};");
            source.AppendLine();
        }

        source.AppendLine($"        if (world.Has<{fullName}>(entity)) {{");
        source.AppendLine("            world.Set(entity, value);");
        source.AppendLine("        } else {");
        source.AppendLine("            world.Add(entity, value);");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        return true;");
        source.AppendLine("    }");
        source.AppendLine("}");

        return source.ToString();
    }

    /// <summary>Emits the wire layout the delta codec reads this component's encoding through.</summary>
    /// <remarks>
    ///     An empty layout means "always send this component whole", which is the correct answer for
    ///     anything whose encoding is not a fixed run of fixed-width fields. Nothing is generated to
    ///     do the differencing itself: the runtime has one implementation of that, and this is the
    ///     only thing it needs to be told about a type.
    /// </remarks>
    static void EmitLanes(StringBuilder source, ImmutableArray<WireValue> fields) {
        var lanes = new List<string>();

        foreach (var field in fields) {
            if (WireCodec.TryLanes(in field, lanes)) {
                continue;
            }

            lanes.Clear();

            break;
        }

        source.AppendLine();
        source.AppendLine("    /// <summary>The fixed-width fields Write produces, in the order it produces them.</summary>");
        source.Append("    static readonly global::Vixen.Net.Messaging.WireLane[] Layout = ");

        if (lanes.Count == 0) {
            source.AppendLine("[];");

            return;
        }

        source.AppendLine("[");

        foreach (var lane in lanes) {
            source.AppendLine($"        {lane},");
        }

        source.AppendLine("    ];");
    }

    static string EmitRegistration(ImmutableArray<string> classNames) {
        var source = new StringBuilder();
        var sorted = classNames.Sort(StringComparer.Ordinal);

        source.AppendLine("// <auto-generated/>");
        source.AppendLine("#nullable enable");
        source.AppendLine();
        source.AppendLine($"namespace {GeneratedNamespace};");
        source.AppendLine();
        source.AppendLine("/// <summary>Every replicated component in this assembly.</summary>");
        source.AppendLine("/// <remarks>");
        source.AppendLine("///     The closed set the registry is built from. Nothing is ever deserialized into a type a");
        source.AppendLine("///     packet named — a packet names a position in this list, and a position that is not here");
        source.AppendLine("///     is a packet that is refused.");
        source.AppendLine("/// </remarks>");
        source.AppendLine("internal static class ReplicatedComponents {");
        source.AppendLine("    /// <summary>Hands every replicator in this assembly to a registry.</summary>");
        source.AppendLine("    /// <param name=\"registry\">The registry.</param>");
        source.AppendLine("    public static void RegisterAll(global::Vixen.Net.Replication.ReplicationRegistry registry) {");

        foreach (var className in sorted) {
            source.AppendLine($"        registry.Register({className}.Instance);");
        }

        source.AppendLine("    }");
        source.AppendLine("}");

        return source.ToString();
    }

    static DiagnosticInfo Report(
        DiagnosticDescriptor descriptor,
        ImmutableArray<Location> locations,
        params string[] arguments
    ) {
        var location = locations.Length == 0 ? Location.None : locations[0];

        return DiagnosticInfo.At(
            location,
            descriptor.Id,
            descriptor.Title.ToString(CultureInfo.InvariantCulture),
            string.Format(CultureInfo.InvariantCulture, descriptor.MessageFormat.ToString(CultureInfo.InvariantCulture), arguments),
            descriptor.DefaultSeverity
        );
    }

    static bool HasError(ImmutableArray<DiagnosticInfo>.Builder diagnostics) {
        foreach (var diagnostic in diagnostics) {
            if (diagnostic.Severity == DiagnosticSeverity.Error) {
                return true;
            }
        }

        return false;
    }

    static string Sanitize(string name) {
        var builder = new StringBuilder(name.Length);

        foreach (var character in name) {
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        }

        return builder.ToString();
    }

    /// <summary>
    ///     The wire id of a type name: 32-bit FNV-1a, the same function
    ///     <c>ReplicationRegistry.HashTypeName</c> computes at run time.
    /// </summary>
    /// <param name="fullName">The namespace-qualified type name.</param>
    /// <returns>The id.</returns>
    /// <remarks>
    ///     Two implementations of one function, which is normally a smell. It is deliberate here: the
    ///     generator cannot reference the runtime — it targets netstandard2.1 and runs inside the
    ///     compiler — and a test asserts the two agree, which is a cheaper guarantee than a shared
    ///     source file that both would have to compile.
    /// </remarks>
    public static uint HashTypeName(string fullName) => WireCodec.Hash(fullName);

    readonly record struct Quantize(float Min, float Max, int Bits);

    readonly record struct Settings(string Channel, int Priority);
}
