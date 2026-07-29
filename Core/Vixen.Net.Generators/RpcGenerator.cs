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
///     Writes the sender, the dispatch table and the manifest entry for every <c>[ServerRpc]</c> and
///     <c>[ClientRpc]</c> handler.
/// </summary>
/// <remarks>
///     <para>
///         This is the piece the reference implementation gets by rewriting IL: there, one method
///         name means both "send this" and "run this", depending on where it is called. ADR-002 bans
///         the weaving and NativeAOT would not survive it, so the handler keeps its name and the
///         sender gets its own — reached through a nested <c>Rpc</c> accessor, so the call site reads
///         <c>Rpc.TakeDamage(dmg)</c> and says out loud that a packet is being sent.
///     </para>
///     <para>
///         That is one line more ceremony and materially better code. Transparent RPC hides latency
///         and bandwidth at the call site, which is a well-known readability trap; the constraint
///         pushed the design somewhere better.
///     </para>
///     <para>
///         The table is ordered by hashed method id at build time and the wire carries the position,
///         so adding a method cannot silently reroute an old one on a peer that has not been rebuilt:
///         the ordering changes, the manifest hash changes, and the handshake refuses the connection.
///     </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class RpcGenerator : IIncrementalGenerator {
    /// <summary>The attribute marking a handler a client calls and a server runs.</summary>
    public const string ServerRpcAttribute = "Vixen.Net.Rpc.ServerRpcAttribute";

    /// <summary>The attribute marking a handler a server calls and clients run.</summary>
    public const string ClientRpcAttribute = "Vixen.Net.Rpc.ClientRpcAttribute";

    /// <summary>The interface a type declaring calls has to implement.</summary>
    public const string RpcObjectInterface = "Vixen.Net.Rpc.IRpcObject";

    /// <summary>
    ///     The type a handler takes as its first parameter to be told who called it.
    /// </summary>
    /// <remarks>
    ///     It is not read from the wire — it is what the router knows about the connection the bytes
    ///     arrived on. That is the whole point: a handler that wanted the caller's id as an ordinary
    ///     argument would be asking the caller who they are.
    /// </remarks>
    public const string RpcContextType = "Vixen.Net.Rpc.RpcContext";

    /// <summary>The name of the step that turns the handlers into source.</summary>
    public const string DescribeStep = "Rpc.Describe";

    static readonly DiagnosticDescriptor UnsupportedParameter = new(
        "VXNET2001",
        "A remote call has an argument that cannot be sent",
        "'{0}' takes a {1}, which cannot be put on the wire. Take an argument of a supported type, or send an id and look the thing up.",
        "Vixen.Net",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static readonly DiagnosticDescriptor NotPartial = new(
        "VXNET2002",
        "A type declaring remote calls is not partial",
        "'{0}' declares remote calls, so its senders and dispatch table are generated into it. Make it partial.",
        "Vixen.Net",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static readonly DiagnosticDescriptor NotAnRpcObject = new(
        "VXNET2003",
        "A type declaring remote calls does not say what they are about",
        "'{0}' declares remote calls but does not implement IRpcObject. A generated sender needs a NetworkId to address and a router to send through.",
        "Vixen.Net",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static readonly DiagnosticDescriptor MustReturnVoid = new(
        "VXNET2004",
        "A remote call returns something",
        "'{0}' returns {1}. A handler is one way: it is sent, and it happens later or it does not. To await an answer, keep the handler void and call it through RpcRouter.CallAsync<T>.",
        "Vixen.Net",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static readonly DiagnosticDescriptor BothDirections = new(
        "VXNET2005",
        "A handler is marked as going both ways",
        "'{0}' is both a ServerRpc and a ClientRpc. One handler travels one way; declare two.",
        "Vixen.Net",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static readonly DiagnosticDescriptor CannotBeGeneratedInto = new(
        "VXNET2006",
        "A type declaring remote calls cannot have code generated into it",
        "'{0}' is nested, generic, or not a class. Generated senders are emitted as a partial class in a namespace.",
        "Vixen.Net",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    static readonly DiagnosticDescriptor QuantizeNotFloat = new(
        "VXNET2007",
        "[Quantize] is on an argument that is not a float",
        "'{0}' is a {1}. [Quantize] declares the range of a float or a Vector3; an integer already knows what it is worth, and a rotation has no range to declare.",
        "Vixen.Net",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var server = Handlers(context, ServerRpcAttribute, "Server");
        var client = Handlers(context, ClientRpcAttribute, "Client");

        // Both attributes feed one step, because a type's dispatch table has to hold all of its
        // calls whichever way each one travels — the switch is over one index space.
        var types = server.Collect()
            .Combine(client.Collect())
            .Select(static (both, _) => Group(both.Left, both.Right))
            .WithTrackingName(DescribeStep);

        context.RegisterSourceOutput(types, static (production, models) => Produce(production, models));
    }

    static IncrementalValuesProvider<HandlerModel> Handlers(
        IncrementalGeneratorInitializationContext context,
        string attribute,
        string kind
    ) =>
        context.SyntaxProvider.ForAttributeWithMetadataName(
            attribute,
            static (node, _) => node is MethodDeclarationSyntax,
            (attributed, _) => DescribeHandler(attributed, kind)
        );

    static void Produce(SourceProductionContext production, ImmutableArray<RpcTypeModel> models) {
        var named = ImmutableArray.CreateBuilder<string>();

        foreach (var model in models) {
            foreach (var diagnostic in model.Diagnostics) {
                production.ReportDiagnostic(diagnostic.ToDiagnostic());
            }

            if (model.Source.Length == 0) {
                continue;
            }

            production.AddSource(model.HintName, SourceText.From(model.Source, Encoding.UTF8));
            named.Add(model.DeclaringType);
        }

        if (named.Count != 0) {
            production.AddSource("RpcMethods.g.cs", SourceText.From(EmitRegistration(named.ToImmutable()), Encoding.UTF8));
        }
    }

    static HandlerModel DescribeHandler(GeneratorAttributeSyntaxContext attributed, string kind) {
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();
        var typeDiagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();

        if (attributed.TargetSymbol is not IMethodSymbol method || method.ContainingType is not { } type) {
            return HandlerModel.None;
        }

        var declaringType = type.ToDisplayString(
            new SymbolDisplayFormat(
                globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
                typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces
            )
        );

        if (type.TypeKind != TypeKind.Class || type.IsGenericType || type.ContainingType is not null) {
            typeDiagnostics.Add(Report(CannotBeGeneratedInto, type.Locations, declaringType));
        } else {
            if (!IsPartial(type)) {
                typeDiagnostics.Add(Report(NotPartial, type.Locations, declaringType));
            }

            if (!Implements(type, RpcObjectInterface)) {
                typeDiagnostics.Add(Report(NotAnRpcObject, type.Locations, declaringType));
            }
        }

        var opposite = kind == "Server" ? ClientRpcAttribute : ServerRpcAttribute;

        foreach (var candidate in method.GetAttributes()) {
            if (candidate.AttributeClass?.ToDisplayString() == opposite) {
                diagnostics.Add(Report(BothDirections, method.Locations, method.Name));
            }
        }

        if (!method.ReturnsVoid) {
            diagnostics.Add(
                Report(
                    MustReturnVoid,
                    method.Locations,
                    method.Name,
                    method.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
                )
            );
        }

        var arguments = ImmutableArray.CreateBuilder<WireValue>();
        var signature = new StringBuilder(method.Name).Append('(');
        var takesContext = method.Parameters.Length != 0
            && method.Parameters[0].Type.ToDisplayString() == RpcContextType;

        for (var i = 0; i < method.Parameters.Length; i++) {
            var parameter = method.Parameters[i];

            if (i > 0) {
                signature.Append(',');
            }

            signature.Append(parameter.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));

            if (i == 0 && takesContext) {
                continue;
            }

            var described = DescribeParameter(method, parameter, diagnostics);

            if (described is not null) {
                arguments.Add(described.Value);
            }
        }

        signature.Append(')');

        var settings = ReadSettings(attributed.Attributes[0], kind == "Server");
        var text = signature.ToString();

        return new(
            declaringType,
            method.Name,
            WireCodec.Hash($"{declaringType}.{text}"),
            text,
            kind,
            settings.RequireOwnership,
            settings.Channel,
            settings.Target,
            takesContext,
            new(arguments.ToImmutable()),
            new(diagnostics.ToImmutable()),
            new(typeDiagnostics.ToImmutable())
        );
    }

    static WireValue? DescribeParameter(
        IMethodSymbol method,
        IParameterSymbol parameter,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics
    ) {
        var quantize = FindQuantize(parameter);
        var typeName = parameter.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        if (quantize is not null && !WireCodec.AcceptsQuantize(parameter.Type)) {
            diagnostics.Add(Report(QuantizeNotFloat, parameter.Locations, parameter.Name, typeName));

            return null;
        }

        if (quantize is { } range && range.Bits is >= 1 and <= 32 && range.Max > range.Min) {
            return new(parameter.Name, WireCodec.KindOf(parameter.Type, quantized: true), range.Min, range.Max, range.Bits);
        }

        var kind = WireCodec.KindOf(parameter.Type);

        if (kind == WireKind.Unsupported || parameter.RefKind != RefKind.None) {
            diagnostics.Add(Report(UnsupportedParameter, method.Locations, method.Name, typeName));

            return null;
        }

        return new(parameter.Name, kind, 0, 0, 0);
    }

    static ImmutableArray<RpcTypeModel> Group(
        ImmutableArray<HandlerModel> server,
        ImmutableArray<HandlerModel> client
    ) {
        var byType = new Dictionary<string, List<HandlerModel>>(StringComparer.Ordinal);

        foreach (var handler in server) {
            Add(byType, handler);
        }

        foreach (var handler in client) {
            Add(byType, handler);
        }

        var models = ImmutableArray.CreateBuilder<RpcTypeModel>();

        foreach (var pair in byType) {
            var handlers = pair.Value;

            // Ordered by hashed id, which is what the manifest insists on: two builds then number
            // the calls the same without having to agree on declaration order.
            handlers.Sort(static (left, right) => left.MethodId.CompareTo(right.MethodId));
            models.Add(EmitType(pair.Key, handlers));
        }

        return models.ToImmutable().Sort(static (left, right) => string.CompareOrdinal(left.HintName, right.HintName));
    }

    static void Add(Dictionary<string, List<HandlerModel>> byType, HandlerModel handler) {
        if (handler.DeclaringType.Length == 0) {
            return;
        }

        if (!byType.TryGetValue(handler.DeclaringType, out var handlers)) {
            handlers = [];
            byType[handler.DeclaringType] = handlers;
        }

        handlers.Add(handler);
    }

    static RpcTypeModel EmitType(string declaringType, List<HandlerModel> handlers) {
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();
        var failed = false;

        // Type-level complaints are the same on every handler in the type, so they are reported once
        // — one "make it partial" per type, not one per call it declares.
        foreach (var diagnostic in handlers[0].TypeDiagnostics) {
            diagnostics.Add(diagnostic);
            failed |= diagnostic.Severity == DiagnosticSeverity.Error;
        }

        foreach (var handler in handlers) {
            foreach (var diagnostic in handler.Diagnostics) {
                diagnostics.Add(diagnostic);
                failed |= diagnostic.Severity == DiagnosticSeverity.Error;
            }
        }

        var hint = $"{Sanitize(declaringType)}.Rpc.g.cs";

        if (failed) {
            return new(declaringType, hint, string.Empty, new(diagnostics.ToImmutable()));
        }

        var separator = declaringType.LastIndexOf('.');
        var simpleName = separator < 0 ? declaringType : declaringType[(separator + 1)..];
        var @namespace = separator < 0 ? string.Empty : declaringType[..separator];
        var source = new StringBuilder();

        source.AppendLine("// <auto-generated/>");
        source.AppendLine("#nullable enable");
        source.AppendLine();

        if (@namespace.Length != 0) {
            source.AppendLine($"namespace {@namespace};");
            source.AppendLine();
        }

        source.AppendLine($"partial class {simpleName} : global::Vixen.Net.Rpc.IRpcInvoker {{");
        source.AppendLine("    /// <summary>Every remote call this type declares, ordered by id.</summary>");
        source.AppendLine("    internal static readonly global::Vixen.Net.Rpc.RpcMethod[] RpcMethodTable = {");

        foreach (var handler in handlers) {
            source.AppendLine(
                $"        new(\"{declaringType}\", \"{handler.Signature}\", "
                + $"global::Vixen.Net.Rpc.RpcKind.{handler.Kind}, "
                + $"{(handler.RequireOwnership ? "true" : "false")}, "
                + $"global::Vixen.Net.Channel.{handler.Channel}, "
                + $"global::Vixen.Net.Rpc.RpcTarget.{handler.Target}),"
            );
        }

        source.AppendLine("    };");
        source.AppendLine();

        foreach (var handler in handlers) {
            foreach (var argument in handler.Arguments) {
                if (argument.Kind is WireKind.QuantizedSingle or WireKind.QuantizedVector3) {
                    source.AppendLine(
                        $"    static readonly global::Vixen.Net.Messaging.QuantizeRange {RangeName(handler, argument)} = "
                        + $"new({WireCodec.Literal(argument.Min)}, {WireCodec.Literal(argument.Max)}, {argument.Bits});"
                    );
                }
            }
        }

        source.AppendLine();
        source.AppendLine("    /// <summary>The senders for this type's remote calls.</summary>");
        source.AppendLine("    public RpcSenders Rpc => new(this);");
        source.AppendLine();
        source.AppendLine("    uint global::Vixen.Net.Rpc.IRpcInvoker.RpcTypeId => RpcMethodTable[0].TypeId;");
        source.AppendLine();
        source.AppendLine("    bool global::Vixen.Net.Rpc.IRpcInvoker.Invoke(");
        source.AppendLine("        uint methodIndex,");
        source.AppendLine("        in global::Vixen.Net.Rpc.RpcContext context,");
        source.AppendLine("        ref global::Vixen.Net.Messaging.BitReader reader");
        source.AppendLine("    ) {");
        source.AppendLine("        switch (methodIndex) {");

        for (var i = 0; i < handlers.Count; i++) {
            EmitCase(source, handlers[i], i);
        }

        source.AppendLine("            default:");
        source.AppendLine("                return false;");
        source.AppendLine("        }");
        source.AppendLine("    }");
        source.AppendLine();
        source.AppendLine("    /// <summary>One method per handler, each of which sends a packet.</summary>");
        source.AppendLine($"    public readonly struct RpcSenders(global::{declaringType} target) {{");

        for (var i = 0; i < handlers.Count; i++) {
            EmitSender(source, handlers[i], i);
        }

        source.AppendLine("    }");
        source.AppendLine("}");

        return new(declaringType, hint, source.ToString(), new(diagnostics.ToImmutable()));
    }

    static void EmitCase(StringBuilder source, HandlerModel handler, int index) {
        source.AppendLine($"            case {index.ToString(CultureInfo.InvariantCulture)}: {{");

        for (var i = 0; i < handler.Arguments.Length; i++) {
            var argument = handler.Arguments[i];
            var local = $"argument{i.ToString(CultureInfo.InvariantCulture)}";

            source.AppendLine($"                if (!{Read(handler, argument, local)}) {{");
            source.AppendLine("                    return false;");
            source.AppendLine("                }");
            source.AppendLine();
        }

        source.Append($"                {handler.Name}(");

        if (handler.TakesContext) {
            source.Append("context");
        }

        for (var i = 0; i < handler.Arguments.Length; i++) {
            if (i > 0 || handler.TakesContext) {
                source.Append(", ");
            }

            source.Append(WireCodec.Convert(handler.Arguments[i], $"argument{i.ToString(CultureInfo.InvariantCulture)}"));
        }

        source.AppendLine(");");
        source.AppendLine();
        source.AppendLine("                return true;");
        source.AppendLine("            }");
        source.AppendLine();
    }

    static void EmitSender(StringBuilder source, HandlerModel handler, int index) {
        source.AppendLine($"        /// <summary>Sends <c>{handler.Signature}</c>.</summary>");
        source.Append($"        public void {handler.Name}(");

        for (var i = 0; i < handler.Arguments.Length; i++) {
            if (i > 0) {
                source.Append(", ");
            }

            source.Append($"{ParameterType(handler.Arguments[i])} {handler.Arguments[i].Name}");
        }

        source.AppendLine(") {");
        source.AppendLine("            var router = target.RpcRouter;");
        source.AppendLine();
        source.AppendLine("            if (router is null) {");
        source.AppendLine("                return;");
        source.AppendLine("            }");
        source.AppendLine();
        source.AppendLine($"            var method = RpcMethodTable[{index.ToString(CultureInfo.InvariantCulture)}];");
        source.AppendLine("            var writer = router.BeginCall(method, target.NetworkId);");

        foreach (var argument in handler.Arguments) {
            source.AppendLine($"            {Write(handler, argument)}");
        }

        source.AppendLine("            router.EndCall(method, target.NetworkId, ref writer);");
        source.AppendLine("        }");
        source.AppendLine();
    }

    static string Read(HandlerModel handler, in WireValue argument, string local) =>
        WireCodec.Read(in argument, local).Replace(argument.RangeName, RangeName(handler, argument));

    static string Write(HandlerModel handler, in WireValue argument) =>
        WireCodec.Write(in argument, argument.Name).Replace(argument.RangeName, RangeName(handler, argument));

    static string EmitRegistration(ImmutableArray<string> types) {
        var source = new StringBuilder();

        source.AppendLine("// <auto-generated/>");
        source.AppendLine("#nullable enable");
        source.AppendLine();
        source.AppendLine("namespace Vixen.Net.Generated;");
        source.AppendLine();
        source.AppendLine("/// <summary>Every remote call declared in this assembly.</summary>");
        source.AppendLine("/// <remarks>");
        source.AppendLine("///     The closed set a packet's indices are resolved against. An index outside it is a packet");
        source.AppendLine("///     that is refused, which is why nothing here is discovered at run time.");
        source.AppendLine("/// </remarks>");
        source.AppendLine("internal static class RpcMethods {");
        source.AppendLine("    /// <summary>Hands every call table in this assembly to a manifest.</summary>");
        source.AppendLine("    /// <param name=\"manifest\">The manifest.</param>");
        source.AppendLine("    public static void RegisterAll(global::Vixen.Net.Rpc.RpcManifest manifest) {");

        foreach (var type in types) {
            source.AppendLine($"        manifest.Register(global::{type}.RpcMethodTable);");
        }

        source.AppendLine("    }");
        source.AppendLine("}");

        return source.ToString();
    }

    static string ParameterType(in WireValue value) =>
        value.Kind switch {
            WireKind.Vector3 or WireKind.QuantizedVector3 => $"global::{WireCodec.Vector3Type}",
            WireKind.Rotation => $"global::{WireCodec.QuaternionType}",
            WireKind.Boolean => "bool",
            WireKind.Byte => "byte",
            WireKind.SByte => "sbyte",
            WireKind.Int16 => "short",
            WireKind.UInt16 => "ushort",
            WireKind.Int32 => "int",
            WireKind.UInt32 => "uint",
            _ => "float"
        };

    static string RangeName(HandlerModel handler, in WireValue value) => $"{handler.Name}_{value.Name}Range";

    static bool IsPartial(INamedTypeSymbol type) {
        foreach (var reference in type.DeclaringSyntaxReferences) {
            if (reference.GetSyntax() is TypeDeclarationSyntax declaration) {
                foreach (var modifier in declaration.Modifiers) {
                    if (modifier.ValueText == "partial") {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    static bool Implements(INamedTypeSymbol type, string interfaceName) {
        foreach (var candidate in type.AllInterfaces) {
            if (candidate.ToDisplayString() == interfaceName) {
                return true;
            }
        }

        return false;
    }

    static Quantized? FindQuantize(IParameterSymbol parameter) {
        foreach (var attribute in parameter.GetAttributes()) {
            if (attribute.AttributeClass?.ToDisplayString() != ReplicationGenerator.QuantizeAttribute
                || attribute.ConstructorArguments.Length != 3) {
                continue;
            }

            if (attribute.ConstructorArguments[0].Value is float min
                && attribute.ConstructorArguments[1].Value is float max
                && attribute.ConstructorArguments[2].Value is int bits) {
                return new(min, max, bits);
            }
        }

        return null;
    }

    static Settings ReadSettings(AttributeData attribute, bool isServer) {
        var requireOwnership = isServer;
        var channel = isServer ? "Reliable" : "Unreliable";
        var target = "Observers";

        foreach (var named in attribute.NamedArguments) {
            switch (named.Key) {
                case "RequireOwnership" when named.Value.Value is bool value:
                    requireOwnership = value;

                    break;

                case "Channel" when named.Value.Value is int value:
                    channel = value switch {
                        0 => "Reliable",
                        1 => "ReliableUnordered",
                        3 => "Sequenced",
                        _ => "Unreliable"
                    };

                    break;

                case "Target" when named.Value.Value is int value:
                    target = value switch {
                        1 => "Owner",
                        2 => "All",
                        _ => "Observers"
                    };

                    break;

                default:
                    break;
            }
        }

        return new(requireOwnership, channel, target);
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
            string.Format(
                CultureInfo.InvariantCulture,
                descriptor.MessageFormat.ToString(CultureInfo.InvariantCulture),
                arguments
            ),
            descriptor.DefaultSeverity
        );
    }

    static string Sanitize(string name) {
        var builder = new StringBuilder(name.Length);

        foreach (var character in name) {
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        }

        return builder.ToString();
    }

    readonly record struct Quantized(float Min, float Max, int Bits);

    readonly record struct Settings(bool RequireOwnership, string Channel, string Target);
}

/// <summary>One RPC handler, reduced to what emitting it needs.</summary>
readonly record struct HandlerModel(
    string DeclaringType,
    string Name,
    uint MethodId,
    string Signature,
    string Kind,
    bool RequireOwnership,
    string Channel,
    string Target,
    bool TakesContext,
    EquatableArray<WireValue> Arguments,
    EquatableArray<DiagnosticInfo> Diagnostics,
    EquatableArray<DiagnosticInfo> TypeDiagnostics
) {
    /// <summary>A handler that could not be read at all.</summary>
    public static HandlerModel None { get; } = new(
        string.Empty,
        string.Empty,
        0,
        string.Empty,
        "Server",
        false,
        "Reliable",
        "Observers",
        false,
        new(ImmutableArray<WireValue>.Empty),
        new(ImmutableArray<DiagnosticInfo>.Empty),
        new(ImmutableArray<DiagnosticInfo>.Empty)
    );
}

/// <summary>One type's worth of generated RPC code.</summary>
readonly record struct RpcTypeModel(
    string DeclaringType,
    string HintName,
    string Source,
    EquatableArray<DiagnosticInfo> Diagnostics
);
