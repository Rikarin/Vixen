// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Vixen.Core.Serialization.Generator;

/// <summary>Emits a serializer for every <c>[DataContract]</c> type in the compilation.</summary>
/// <remarks>
///     <para>
///         ADR-002 forbids IL weaving, which is how Stride does this. A source generator does the
///         same job with two advantages that matter more than they sound: the emitted code is C# a
///         person can read and step through, and it exists at compile time, so a type that cannot be
///         serialised is a build error rather than a crash on the machine that loads the save file.
///     </para>
///     <para>
///         Serializers are emitted into their own namespace rather than into the contract type, so
///         nothing has to be declared <c>partial</c>. That is the difference between a serialisation
///         library and a serialisation library that has an opinion about how every type in the
///         engine is declared.
///     </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class DataContractGenerator : IIncrementalGenerator {
    const string ContractAttribute = "Vixen.Core.DataContractAttribute";
    const string IgnoreAttribute = "DataMemberIgnoreAttribute";
    const string MemberAttribute = "DataMemberAttribute";
    const string GeneratedNamespace = "Vixen.Generated.Serialization";

    static readonly DiagnosticDescriptor CannotConstruct = new(
        "VXS0101",
        "A [DataContract] type cannot be reconstructed",
        "'{0}' cannot be deserialised: {1}",
        "Vixen.Serialization",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        "Deserialisation has to be able to produce the value it read. That needs either settable "
        + "members, or a constructor whose parameters match them."
    );

    static readonly DiagnosticDescriptor WillNotRoundTrip = new(
        "VXS0102",
        "A [DataContract] member will not round-trip",
        "'{0}.{1}' is written but cannot be read back, so it is skipped",
        "Vixen.Serialization",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        "A member with no way to set it can be written but not restored, which produces data that "
        + "silently loses a field."
    );

    // The types with a dedicated writer method. Everything else goes through the registry.
    static readonly Dictionary<string, string> Primitives = new(StringComparer.Ordinal) {
        ["bool"] = "Boolean",
        ["byte"] = "Byte",
        ["sbyte"] = "SByte",
        ["short"] = "Int16",
        ["ushort"] = "UInt16",
        ["int"] = "Int32",
        ["uint"] = "UInt32",
        ["long"] = "Int64",
        ["ulong"] = "UInt64",
        ["char"] = "Char",
        ["float"] = "Single",
        ["double"] = "Double",
        ["decimal"] = "Decimal",
        ["string"] = "String",
        ["global::System.Half"] = "Half",
        ["global::System.Guid"] = "Guid",
        ["global::System.DateTime"] = "DateTime",
        ["global::System.DateTimeOffset"] = "DateTimeOffset",
        ["global::System.TimeSpan"] = "TimeSpan"
    };

    // Bulk-copied element types: those whose in-memory bytes are the wire bytes on every target.
    // `bool` is excluded on purpose — a byte that is neither 0 nor 1 is a valid bool in memory and
    // would survive a bulk copy as one, which is a difference nothing downstream expects.
    static readonly HashSet<string> Blittable = new(StringComparer.Ordinal) {
        "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "char", "float", "double"
    };

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var contracts = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                ContractAttribute,
                static (node, _) => node is Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax,
                static (syntaxContext, _) => Describe((INamedTypeSymbol)syntaxContext.TargetSymbol)
            )
            .Where(static model => model.QualifiedName is not null);

        context.RegisterSourceOutput(contracts, static (production, model) => EmitContract(production, model));
        context.RegisterSourceOutput(contracts.Collect(), static (production, models) => EmitRegistration(production, models));
    }

    static ContractModel Describe(INamedTypeSymbol type) {
        var qualified = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        if (type.IsGenericType) {
            return Failed(type, qualified, "a generic type needs one serializer per instantiation, which the registry cannot express yet");
        }

        if (type.TypeKind is TypeKind.Interface or TypeKind.Enum || type.IsAbstract) {
            // An enum is serialised inline by whatever holds it, and an interface has no members of
            // its own to write. An abstract class is never the run-time type of anything, so the
            // polymorphic path always resolves to a concrete subclass — and that subclass's
            // serializer already writes the abstract base's members, because members are collected
            // from the base down. None of the three needs a serializer of its own.
            return default;
        }

        var members = ImmutableArray.CreateBuilder<MemberModel>();
        var skipped = new List<string>();
        CollectMembers(type, members, skipped);

        var ordered = members.ToImmutable()
            .Sort(static (left, right) => left.Order != right.Order
                ? left.Order.CompareTo(right.Order)
                : left.Sequence.CompareTo(right.Sequence)
            );

        var parameterless = type.IsValueType
            || type.InstanceConstructors.Any(constructor =>
                constructor.Parameters.Length == 0 && constructor.DeclaredAccessibility == Accessibility.Public
            );

        var constructorParameters = MatchConstructor(type, ordered);

        if (!ordered.All(member => member.IsSettable) && constructorParameters.IsDefault) {
            var unsettable = string.Join(", ", ordered.Where(member => !member.IsSettable).Select(member => member.Name));

            return Failed(
                type,
                qualified,
                $"it has no accessible constructor whose parameters match its members, and these cannot be assigned: {unsettable}"
            );
        }

        return new(
            type.ContainingNamespace.IsGlobalNamespace ? string.Empty : type.ContainingNamespace.ToDisplayString(),
            type.Name,
            AliasOf(type),
            FormerAliasesOf(type),
            qualified,
            SafeName(qualified),
            type.IsValueType,
            type.IsSealed || type.IsValueType,
            VersionOf(type),
            parameterless,
            HasMigrationHook(type),
            ordered,
            constructorParameters.IsDefault ? ImmutableArray<string>.Empty : constructorParameters,
            null
        ) { };
    }

    static ContractModel Failed(INamedTypeSymbol type, string qualified, string reason) =>
        new(string.Empty, type.Name, type.Name, [], qualified, SafeName(qualified), false, false, 0, false, false, [], [], reason);

    static void CollectMembers(INamedTypeSymbol type, ImmutableArray<MemberModel>.Builder members, List<string> skipped) {
        // Base first, so that adding a member to a base type appends to the stream rather than
        // shifting everything a derived type wrote.
        if (type.BaseType is { SpecialType: SpecialType.None } baseType) {
            CollectMembers(baseType, members, skipped);
        }

        var sequence = members.Count;

        foreach (var member in type.GetMembers()) {
            if (member.IsStatic || member.IsImplicitlyDeclared || member.DeclaredAccessibility != Accessibility.Public) {
                continue;
            }

            if (HasAttribute(member, IgnoreAttribute)) {
                continue;
            }

            ITypeSymbol memberType;
            bool settable;

            switch (member) {
                case IFieldSymbol { IsConst: false } field:
                    memberType = field.Type;
                    settable = !field.IsReadOnly;
                    break;

                case IPropertySymbol { IsIndexer: false, GetMethod: not null } property
                    when property.Name != "EqualityContract":
                    memberType = property.Type;
                    settable = property.SetMethod is { DeclaredAccessibility: Accessibility.Public, IsInitOnly: false };
                    break;

                default:
                    continue;
            }

            members.Add(Describe(member.Name, memberType, settable, OrderOf(member), sequence++));
        }
    }

    static MemberModel Describe(string name, ITypeSymbol type, bool settable, int order, int sequence) {
        var qualified = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var keyword = Keyword(type) ?? qualified;

        if (Primitives.TryGetValue(keyword, out var primitive)) {
            return new(name, keyword, MemberShape.Primitive, primitive, string.Empty, string.Empty, settable, order, sequence);
        }

        if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol { EnumUnderlyingType: { } underlying }) {
            var underlyingKeyword = Keyword(underlying) ?? underlying.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            return new(name, qualified, MemberShape.Enum, Primitives[underlyingKeyword], underlyingKeyword, string.Empty, settable, order, sequence);
        }

        if (type is IArrayTypeSymbol { Rank: 1 } array) {
            var element = Keyword(array.ElementType) ?? array.ElementType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var shape = Blittable.Contains(element) ? MemberShape.BlittableArray : MemberShape.Array;
            return new(name, qualified, shape, string.Empty, element, string.Empty, settable, order, sequence);
        }

        if (type is INamedTypeSymbol { IsGenericType: true } generic) {
            var definition = generic.ConstructedFrom.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var first = Argument(generic, 0);

            switch (definition) {
                case "global::System.Collections.Generic.List<T>":
                    return new(name, qualified, MemberShape.List, string.Empty, first, string.Empty, settable, order, sequence);

                case "global::System.Collections.Generic.Dictionary<TKey, TValue>":
                    return new(name, qualified, MemberShape.Dictionary, string.Empty, first, Argument(generic, 1), settable, order, sequence);

                case "global::System.Nullable<T>":
                    return new(name, qualified, MemberShape.Nullable, string.Empty, first, string.Empty, settable, order, sequence);
            }
        }

        // A sealed class cannot have a subtype, so its declared type is its run-time type and the
        // name in the stream would be a string spent to say what the reader already knows.
        var otherShape = type.IsValueType
            ? MemberShape.Value
            : type.IsSealed ? MemberShape.Reference : MemberShape.Polymorphic;

        return new(name, qualified, otherShape, string.Empty, string.Empty, string.Empty, settable, order, sequence);
    }

    static string Argument(INamedTypeSymbol generic, int index) {
        var argument = generic.TypeArguments[index];
        return Keyword(argument) ?? argument.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    static string? Keyword(ITypeSymbol type) =>
        type.SpecialType switch {
            SpecialType.System_Boolean => "bool",
            SpecialType.System_Byte => "byte",
            SpecialType.System_SByte => "sbyte",
            SpecialType.System_Int16 => "short",
            SpecialType.System_UInt16 => "ushort",
            SpecialType.System_Int32 => "int",
            SpecialType.System_UInt32 => "uint",
            SpecialType.System_Int64 => "long",
            SpecialType.System_UInt64 => "ulong",
            SpecialType.System_Char => "char",
            SpecialType.System_Single => "float",
            SpecialType.System_Double => "double",
            SpecialType.System_Decimal => "decimal",
            SpecialType.System_String => "string",
            _ => null
        };

    static ImmutableArray<string> MatchConstructor(INamedTypeSymbol type, ImmutableArray<MemberModel> members) {
        if (members.Length == 0) {
            return default;
        }

        foreach (var constructor in type.InstanceConstructors) {
            if (constructor.DeclaredAccessibility != Accessibility.Public || constructor.Parameters.Length != members.Length) {
                continue;
            }

            var mapping = ImmutableArray.CreateBuilder<string>(members.Length);
            var matched = true;

            foreach (var parameter in constructor.Parameters) {
                // Case-insensitive, because a primary constructor parameter is `PascalCase` on a
                // record and `camelCase` everywhere else, and both name the same member.
                var member = members.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, parameter.Name, StringComparison.OrdinalIgnoreCase)
                );

                if (member.Name is null) {
                    matched = false;
                    break;
                }

                mapping.Add(member.Name);
            }

            if (matched) {
                return mapping.ToImmutable();
            }
        }

        return default;
    }

    static bool HasMigrationHook(INamedTypeSymbol type) =>
        type.GetMembers("TryMigrate")
            .OfType<IMethodSymbol>()
            .Any(method => method is { IsStatic: true, Parameters.Length: 3, DeclaredAccessibility: Accessibility.Public });

    static bool HasAttribute(ISymbol symbol, string name) =>
        symbol.GetAttributes().Any(attribute => attribute.AttributeClass?.Name == name);

    static int OrderOf(ISymbol symbol) {
        foreach (var attribute in symbol.GetAttributes()) {
            if (attribute.AttributeClass?.Name != MemberAttribute) {
                continue;
            }

            foreach (var argument in attribute.NamedArguments) {
                if (argument.Key == "Order" && argument.Value.Value is int named) {
                    return named;
                }
            }

            if (attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is int positional) {
                return positional;
            }
        }

        return 0;
    }

    static string AliasOf(INamedTypeSymbol type) {
        foreach (var attribute in type.GetAttributes()) {
            if (attribute.AttributeClass?.ToDisplayString() != ContractAttribute) {
                continue;
            }

            foreach (var argument in attribute.NamedArguments) {
                if (argument.Key == "Alias" && argument.Value.Value is string named && named.Length > 0) {
                    return named;
                }
            }

            if (attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is string positional) {
                return positional;
            }
        }

        // The bare type name, not the namespace-qualified one. A type that moves between namespaces
        // is the ordinary refactor; a type that shares a simple name with another contract is the
        // rare one, and the registry rejects that collision by name at start-up.
        return type.Name;
    }

    static ImmutableArray<string> FormerAliasesOf(INamedTypeSymbol type) {
        var builder = ImmutableArray.CreateBuilder<string>();

        foreach (var attribute in type.GetAttributes()) {
            if (attribute.AttributeClass?.Name == "DataAliasAttribute"
                && attribute.ConstructorArguments.Length == 1
                && attribute.ConstructorArguments[0].Value is string former) {
                builder.Add(former);
            }
        }

        return builder.ToImmutable();
    }

    static int VersionOf(INamedTypeSymbol type) {
        foreach (var attribute in type.GetAttributes()) {
            if (attribute.AttributeClass?.ToDisplayString() != ContractAttribute) {
                continue;
            }

            foreach (var argument in attribute.NamedArguments) {
                if (argument.Key == "SerializedVersion" && argument.Value.Value is int version) {
                    return version;
                }
            }
        }

        return 0;
    }

    static string SafeName(string qualified) =>
        qualified.Replace("global::", string.Empty).Replace('.', '_').Replace('+', '_');

    static void EmitContract(SourceProductionContext context, ContractModel model) {
        if (model.Error is not null) {
            context.ReportDiagnostic(Diagnostic.Create(CannotConstruct, Location.None, model.QualifiedName, model.Error));
            return;
        }

        var source = new StringBuilder();
        source.AppendLine("// <auto-generated/>");
        source.AppendLine("#nullable disable");
        source.AppendLine();
        source.AppendLine($"namespace {GeneratedNamespace} {{");
        source.AppendLine($"    internal sealed class {model.SafeName}Serializer : global::Vixen.Core.Serialization.DataSerializer<{model.QualifiedName}> {{");
        source.AppendLine($"        internal const int Version = {model.Version};");
        source.AppendLine($"        internal const int MemberCount = {model.Members.Length};");
        source.AppendLine();

        EmitSerialize(source, model);
        source.AppendLine();
        EmitDeserialize(source, model);

        source.AppendLine("    }");
        source.AppendLine("}");

        context.AddSource($"{model.SafeName}Serializer.g.cs", SourceText.From(source.ToString(), Encoding.UTF8));
    }

    static void EmitSerialize(StringBuilder source, ContractModel model) {
        source.AppendLine($"        public override void Serialize(ref global::Vixen.Core.Serialization.SerializationWriter writer, in {model.QualifiedName} value) {{");

        if (!model.IsValueType && !model.IsSealed) {
            // A derived instance written through a base serializer would lose its own members
            // silently, and the loss would only surface when the data was read back somewhere else.
            source.AppendLine("            if (value is not null && value.GetType() != typeof(" + model.QualifiedName + ")) {");
            source.AppendLine("                throw new global::Vixen.Core.Serialization.SerializationException(");
            source.AppendLine($"                    $\"A {{value.GetType()}} was written through the serializer for {model.QualifiedName}, which would drop everything the derived type adds. Polymorphic serialisation needs a type table; see Vixen.Core.Reflection.\");");
            source.AppendLine("            }");
            source.AppendLine();
        }

        source.AppendLine("            writer.WriteVarUInt64(Version);");
        source.AppendLine("            writer.WriteVarUInt64(MemberCount);");

        foreach (var member in model.Members) {
            source.AppendLine("            " + WriteCall(member));
        }

        source.AppendLine("        }");
    }

    static void EmitDeserialize(StringBuilder source, ContractModel model) {
        source.AppendLine($"        public override void Deserialize(ref global::Vixen.Core.Serialization.SerializationReader reader, ref {model.QualifiedName} value) {{");
        source.AppendLine("            var version = (int)reader.ReadVarUInt64();");
        source.AppendLine("            var count = (int)reader.ReadVarUInt64();");
        source.AppendLine();
        source.AppendLine("            if (version != Version) {");

        if (model.HasMigrationHook) {
            source.AppendLine($"                if (!{model.QualifiedName}.TryMigrate(version, ref reader, ref value)) {{");
            source.AppendLine($"                    throw new global::Vixen.Core.Serialization.SerializationVersionException(\"{model.TypeName}\", version, Version);");
            source.AppendLine("                }");
            source.AppendLine();
            source.AppendLine("                return;");
        } else {
            source.AppendLine($"                throw new global::Vixen.Core.Serialization.SerializationVersionException(\"{model.TypeName}\", version, Version);");
        }

        source.AppendLine("            }");
        source.AppendLine();
        source.AppendLine("            if (count > MemberCount) {");
        source.AppendLine("                throw new global::Vixen.Core.Serialization.SerializationException(");
        source.AppendLine($"                    $\"{model.TypeName} data holds {{count}} members and this build knows {{MemberCount}}. Members can be appended and older data still read; they cannot be removed or reordered without a version bump.\");");
        source.AppendLine("            }");
        source.AppendLine();

        var useConstructor = !model.ConstructorParameters.IsEmpty && !model.Members.All(member => member.IsSettable);

        if (useConstructor) {
            foreach (var member in model.Members) {
                source.AppendLine($"            var member_{member.Name} = default({member.TypeName});");
            }

            source.AppendLine();

            for (var index = 0; index < model.Members.Length; index++) {
                source.AppendLine($"            if (count > {index}) member_{model.Members[index].Name} = {ReadCall(model.Members[index])};");
            }

            source.AppendLine();
            var arguments = string.Join(", ", model.ConstructorParameters.Select(name => $"member_{name}"));
            source.AppendLine($"            value = new {model.QualifiedName}({arguments});");
        } else {
            if (!model.IsValueType) {
                source.AppendLine($"            value ??= new {model.QualifiedName}();");
            }

            for (var index = 0; index < model.Members.Length; index++) {
                source.AppendLine($"            if (count > {index}) value.{model.Members[index].Name} = {ReadCall(model.Members[index])};");
            }
        }

        source.AppendLine("        }");
    }

    static string WriteCall(MemberModel member) =>
        member.Shape switch {
            MemberShape.Primitive => $"writer.Write{member.Primitive}(value.{member.Name});",
            MemberShape.Enum => $"writer.Write{member.Primitive}(({member.ElementType})value.{member.Name});",
            MemberShape.BlittableArray => $"writer.WriteBlittableArray<{member.ElementType}>(value.{member.Name});",
            MemberShape.Array => $"writer.WriteArray<{member.ElementType}>(value.{member.Name});",
            MemberShape.List => $"writer.WriteList<{member.ElementType}>(value.{member.Name});",
            MemberShape.Dictionary => $"writer.WriteDictionary<{member.ElementType}, {member.SecondElementType}>(value.{member.Name});",
            MemberShape.Nullable => $"writer.WriteNullable<{member.ElementType}>(value.{member.Name});",
            MemberShape.Value => $"writer.WriteValue<{member.TypeName}>(value.{member.Name});",
            MemberShape.Polymorphic => $"writer.WritePolymorphic<{member.TypeName}>(value.{member.Name});",
            _ => $"writer.WriteReference<{member.TypeName}>(value.{member.Name});"
        };

    static string ReadCall(MemberModel member) =>
        member.Shape switch {
            MemberShape.Primitive => $"reader.Read{member.Primitive}()",
            MemberShape.Enum => $"({member.TypeName})reader.Read{member.Primitive}()",
            MemberShape.BlittableArray => $"reader.ReadBlittableArray<{member.ElementType}>()",
            MemberShape.Array => $"reader.ReadArray<{member.ElementType}>()",
            MemberShape.List => $"reader.ReadList<{member.ElementType}>()",
            MemberShape.Dictionary => $"reader.ReadDictionary<{member.ElementType}, {member.SecondElementType}>()",
            MemberShape.Nullable => $"reader.ReadNullable<{member.ElementType}>()",
            MemberShape.Value => $"reader.ReadValue<{member.TypeName}>()",
            MemberShape.Polymorphic => $"reader.ReadPolymorphic<{member.TypeName}>()",
            _ => $"reader.ReadReference<{member.TypeName}>()"
        };

    static void EmitRegistration(SourceProductionContext context, ImmutableArray<ContractModel> models) {
        var valid = models.Where(model => model.Error is null && model.QualifiedName is not null).ToArray();

        if (valid.Length == 0) {
            return;
        }

        var source = new StringBuilder();
        source.AppendLine("// <auto-generated/>");
        source.AppendLine("#nullable disable");
        source.AppendLine();
        source.AppendLine($"namespace {GeneratedNamespace} {{");
        source.AppendLine("    /// <summary>Registers this assembly's generated serializers before any of its code runs.</summary>");
        source.AppendLine("    internal static class SerializerRegistration {");
        source.AppendLine("        [global::System.Runtime.CompilerServices.ModuleInitializer]");
        source.AppendLine("        internal static void Initialize() {");

        foreach (var model in valid.OrderBy(model => model.SafeName, StringComparer.Ordinal)) {
            var former = model.FormerAliases.IsDefaultOrEmpty
                ? string.Empty
                : ", " + string.Join(", ", model.FormerAliases.Select(alias => $"\"{alias}\""));

            source.AppendLine(
                $"            global::Vixen.Core.Serialization.SerializerRegistry.Register(\"{model.Alias}\", new {model.SafeName}Serializer(){former});"
            );
        }

        source.AppendLine("        }");
        source.AppendLine("    }");
        source.AppendLine("}");

        context.AddSource("SerializerRegistration.g.cs", SourceText.From(source.ToString(), Encoding.UTF8));
    }
}
