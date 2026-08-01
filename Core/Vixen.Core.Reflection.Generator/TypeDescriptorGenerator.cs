// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Vixen.Core.Reflection.Generator;

/// <summary>Describes every annotated type in the compilation, at compile time.</summary>
/// <remarks>
///     <para>
///         Stride injects module initialisers with Cecil to build the equivalent registry; ADR-002
///         forbids that and <c>[ModuleInitializer]</c> does the same job in the language. The
///         difference that matters is not the mechanism — it is that what gets registered is what a
///         generator saw in the source, so it survives trimming and NativeAOT, where an assembly
///         scan reads metadata the publisher has already deleted.
///     </para>
///     <para>
///         Accessors are emitted as lambdas over a cast, not as
///         <c>PropertyInfo.GetValue</c>. That is what lets the inspector read and write arbitrary
///         members on iOS.
///     </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class TypeDescriptorGenerator : IIncrementalGenerator {
    const string ContractAttribute = "Vixen.Core.DataContractAttribute";
    const string ComponentAttribute = "Vixen.Core.ComponentAttribute";
    const string EditorVisibleAttribute = "Vixen.Core.EditorVisibleAttribute";
    const string GeneratedNamespace = "Vixen.Generated.Reflection";

    static readonly DiagnosticDescriptor GenericNotDescribed = new(
        "VXS0201",
        "An annotated generic type has no descriptor",
        "'{0}' is generic, so it has no single descriptor and is not registered",
        "Vixen.Reflection",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        "A descriptor names one closed type. A generic definition would need one per instantiation, "
        + "and the generator cannot know which instantiations exist."
    );

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var contracts = Annotated(context, ContractAttribute);
        var components = Annotated(context, ComponentAttribute);
        var visible = Annotated(context, EditorVisibleAttribute);

        var all = contracts.Collect()
            .Combine(components.Collect())
            .Combine(visible.Collect())
            .Select(static (tuple, _) => Merge(tuple.Left.Left, tuple.Left.Right, tuple.Right));

        context.RegisterSourceOutput(all, static (production, models) => Emit(production, models));
    }

    static IncrementalValuesProvider<DescriptorModel> Annotated(
        IncrementalGeneratorInitializationContext context,
        string attribute
    ) =>
        context.SyntaxProvider
            .ForAttributeWithMetadataName(
                attribute,
                static (node, _) => node is Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax,
                static (syntaxContext, _) => Describe((INamedTypeSymbol)syntaxContext.TargetSymbol)
            )
            .Where(static model => model.QualifiedName is not null);

    static ImmutableArray<DescriptorModel> Merge(
        ImmutableArray<DescriptorModel> first,
        ImmutableArray<DescriptorModel> second,
        ImmutableArray<DescriptorModel> third
    ) {
        // Each provider produces the complete model — traits are read from every attribute on the
        // type, not just the one that matched — so a type annotated twice appears twice identically
        // and the merge is a deduplication rather than a combination.
        var seen = new Dictionary<string, DescriptorModel>(StringComparer.Ordinal);

        foreach (var model in first.Concat(second).Concat(third)) {
            seen[model.QualifiedName] = model;
        }

        return [.. seen.Values.OrderBy(model => model.SafeName, StringComparer.Ordinal)];
    }

    static DescriptorModel Describe(INamedTypeSymbol type) {
        var qualified = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        if (type.IsGenericType) {
            return new(qualified, SafeName(qualified), type.Name, [], "None", false, null, false, [], qualified);
        }

        var traits = new List<string>();

        if (Has(type, "DataContractAttribute")) {
            traits.Add("DataContract");
        }

        if (Has(type, "ComponentAttribute")) {
            traits.Add("Component");
        }

        if (IsEditorVisible(type)) {
            traits.Add("EditorVisible");
        }

        if (type.IsAbstract || type.TypeKind == TypeKind.Interface) {
            traits.Add("Abstract");
        }

        var members = ImmutableArray.CreateBuilder<DescribedMember>();
        CollectMembers(type, members);

        var canCreate = !type.IsAbstract
            && type.TypeKind != TypeKind.Interface
            && (type.IsValueType
                || type.InstanceConstructors.Any(constructor =>
                    constructor.Parameters.Length == 0 && constructor.DeclaredAccessibility == Accessibility.Public
                ));

        return new(
            qualified,
            SafeName(qualified),
            AliasOf(type),
            FormerAliasesOf(type),
            traits.Count == 0 ? "None" : string.Join(" | ", traits),
            type.IsValueType,
            CategoryOf(type),
            canCreate,
            members.ToImmutable(),
            null
        );
    }

    static void CollectMembers(INamedTypeSymbol type, ImmutableArray<DescribedMember>.Builder members) {
        if (type.BaseType is { SpecialType: SpecialType.None } baseType) {
            CollectMembers(baseType, members);
        }

        var order = members.Count;

        foreach (var member in type.GetMembers()) {
            if (member.IsStatic || member.IsImplicitlyDeclared || member.DeclaredAccessibility != Accessibility.Public) {
                continue;
            }

            ITypeSymbol memberType;
            bool canRead;
            bool canWrite;
            var isInitOnly = false;

            switch (member) {
                case IFieldSymbol { IsConst: false } field:
                    memberType = field.Type;
                    canRead = true;
                    canWrite = !field.IsReadOnly;
                    break;

                case IPropertySymbol { IsIndexer: false } property when property.Name != "EqualityContract":
                    memberType = property.Type;
                    canRead = property.GetMethod is { DeclaredAccessibility: Accessibility.Public };
                    canWrite = property.SetMethod is { DeclaredAccessibility: Accessibility.Public };

                    // An init-only setter is still a setter; it is only the *language* that refuses
                    // to call it outside an object initializer. [UnsafeAccessor] binds to it
                    // directly, which is what lets a `{ get; init; }` record — the shape doc 08 uses
                    // for every importer's settings — be built from a .meta file at all.
                    isInitOnly = canWrite && property.SetMethod!.IsInitOnly;
                    break;

                default:
                    continue;
            }

            if (!canRead && !canWrite) {
                continue;
            }

            // ⚠ A `ref struct` member is skipped, and it has to be rather than being reported: a
            // descriptor reads and writes through `object`, and a ref-like type cannot be boxed at
            // all. Emitting one produces a cast the compiler refuses — CS0030 out of generated code,
            // against a line nobody wrote — which is the worst place to learn about it. The case is a
            // façade over data that lives elsewhere, `Behavior.Transform` being the example: the
            // member is a *view* of the entity's components, and the components themselves are what
            // a descriptor was ever going to describe.
            if (memberType.IsRefLikeType) {
                continue;
            }

            var factories = ImmutableArray.CreateBuilder<string>();
            CollectFactories(memberType, factories, 0);

            members.Add(
                Describe(
                    member,
                    memberType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    order++,
                    canRead,
                    canWrite,
                    isInitOnly,
                    factories.ToImmutable()
                )
            );
        }
    }

    /// <summary>
    ///     Writes out the constructor for every collection type reachable from a member's declared
    ///     type, so that a data binder never has to build one at run time.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>Array.CreateInstance(elementType, n)</c>, <c>MakeGenericType</c> and
    ///         <c>Activator.CreateInstance(Type)</c> are all <c>RequiresDynamicCode</c>: a binder
    ///         built on them works on a desktop and throws on a phone. Here the element type is a
    ///         symbol the generator is holding, so the constructor is ordinary C# — bound at compile
    ///         time, and correct after trimming.
    ///     </para>
    ///     <para>
    ///         A list interface is registered backed by an array, which satisfies it with no copy.
    ///         Recursion is bounded because a member type nested four collections deep is a data
    ///         model problem rather than something to support.
    ///     </para>
    /// </remarks>
    static void CollectFactories(ITypeSymbol type, ImmutableArray<string>.Builder into, int depth) {
        if (depth > 3) {
            return;
        }

        if (type is IArrayTypeSymbol array) {
            var element = array.ElementType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            into.Add($"typeof({element}[]), static count => new {element}[count]");
            CollectFactories(array.ElementType, into, depth + 1);
            return;
        }

        if (type is not INamedTypeSymbol { IsGenericType: true } named) {
            return;
        }

        var self = named.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var definition = named.ConstructedFrom.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var arguments = named.TypeArguments;

        switch (definition) {
            case "global::System.Collections.Generic.List<T>":
                into.Add($"typeof({self}), static count => new {self}(count)");
                CollectFactories(arguments[0], into, depth + 1);
                return;

            case "global::System.Collections.Generic.IList<T>":
            case "global::System.Collections.Generic.ICollection<T>":
            case "global::System.Collections.Generic.IEnumerable<T>":
            case "global::System.Collections.Generic.IReadOnlyList<T>":
            case "global::System.Collections.Generic.IReadOnlyCollection<T>": {
                var element = arguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                into.Add($"typeof({self}), static count => new {element}[count]");
                CollectFactories(arguments[0], into, depth + 1);
                return;
            }

            case "global::System.Collections.Generic.Dictionary<TKey, TValue>":
            case "global::System.Collections.Generic.IDictionary<TKey, TValue>":
            case "global::System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>": {
                var key = arguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var value = arguments[1].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                into.Add(
                    $"typeof({self}), static count => new global::System.Collections.Generic.Dictionary<{key}, {value}>(count)"
                );

                CollectFactories(arguments[1], into, depth + 1);
                return;
            }
        }
    }

    static DescribedMember Describe(
        ISymbol member,
        string typeName,
        int order,
        bool canRead,
        bool canWrite,
        bool isInitOnly,
        ImmutableArray<string> collectionFactories
    ) {
        string? category = null;
        string? displayName = null;
        string? tooltip = null;
        double? minimum = null;
        double? maximum = null;
        var step = 0d;
        var logarithmic = false;
        string? assetType = null;
        var allowsNull = true;

        // A member is shown unless something says otherwise: [EditorVisible(false)] hides it, and
        // [DataMemberIgnore] does not — the two answer different questions, and conflating them is
        // how a cache field ends up in the inspector or a tuning knob ends up out of it.
        var editorVisible = true;
        var editorReadOnly = !canWrite;

        foreach (var attribute in member.GetAttributes()) {
            switch (attribute.AttributeClass?.Name) {
                case "CategoryAttribute" when attribute.ConstructorArguments.Length == 1:
                    category = attribute.ConstructorArguments[0].Value as string;
                    break;

                case "TooltipAttribute" when attribute.ConstructorArguments.Length == 1:
                    tooltip = attribute.ConstructorArguments[0].Value as string;
                    break;

                case "RangeAttribute" when attribute.ConstructorArguments.Length == 2:
                    minimum = ToDouble(attribute.ConstructorArguments[0].Value);
                    maximum = ToDouble(attribute.ConstructorArguments[1].Value);

                    foreach (var named in attribute.NamedArguments) {
                        if (named.Key == "Step") {
                            step = ToDouble(named.Value.Value) ?? 0d;
                        } else if (named.Key == "Logarithmic" && named.Value.Value is bool flag) {
                            logarithmic = flag;
                        }
                    }

                    break;

                // ⚠ The symbol rather than its name, so that what is emitted is the type the author
                // wrote and not a string that happens to match one. `typeof(AudioClip)` in a
                // component's source has to come out the other side as the same closed type, or the
                // editor joins a member to the wrong importer and offers the wrong list.
                case "AssetTypeAttribute" when attribute.ConstructorArguments.Length == 1:
                    assetType = (attribute.ConstructorArguments[0].Value as INamedTypeSymbol)?.ToDisplayString(
                        SymbolDisplayFormat.FullyQualifiedFormat
                    );

                    foreach (var named in attribute.NamedArguments) {
                        if (named.Key == "AllowNull" && named.Value.Value is bool nullable) {
                            allowsNull = nullable;
                        }
                    }

                    break;

                case "EditorVisibleAttribute":
                    editorVisible = attribute.ConstructorArguments.Length != 1
                        || attribute.ConstructorArguments[0].Value is not bool visible
                        || visible;

                    foreach (var named in attribute.NamedArguments) {
                        if (named.Key == "ReadOnly" && named.Value.Value is bool readOnly) {
                            editorReadOnly = editorReadOnly || readOnly;
                        } else if (named.Key == "DisplayName") {
                            displayName = named.Value.Value as string;
                        }
                    }

                    break;
            }
        }

        return new(
            member.Name,
            typeName,
            order,
            canRead,
            canWrite,
            isInitOnly,
            category,
            displayName,
            tooltip,
            minimum,
            maximum,
            step,
            logarithmic,
            editorVisible,
            editorReadOnly,
            assetType,
            allowsNull,
            collectionFactories
        );
    }

    static double? ToDouble(object? value) =>
        value is null ? null : Convert.ToDouble(value, CultureInfo.InvariantCulture);

    static bool Has(ISymbol symbol, string name) =>
        symbol.GetAttributes().Any(attribute => attribute.AttributeClass?.Name == name);

    static bool IsEditorVisible(INamedTypeSymbol type) {
        foreach (var attribute in type.GetAttributes()) {
            if (attribute.AttributeClass?.Name != "EditorVisibleAttribute") {
                continue;
            }

            return attribute.ConstructorArguments.Length != 1
                || attribute.ConstructorArguments[0].Value is not bool visible
                || visible;
        }

        return false;
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

    static string? CategoryOf(INamedTypeSymbol type) {
        foreach (var attribute in type.GetAttributes()) {
            if (attribute.AttributeClass?.Name == "CategoryAttribute" && attribute.ConstructorArguments.Length == 1) {
                return attribute.ConstructorArguments[0].Value as string;
            }
        }

        return null;
    }

    static string SafeName(string qualified) =>
        qualified.Replace("global::", string.Empty).Replace('.', '_').Replace('+', '_');

    static string Quote(string? value) =>
        value is null ? "null" : "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    static string Number(double? value) =>
        value is null ? "null" : value.Value.ToString("R", CultureInfo.InvariantCulture) + "d";

    static void Emit(SourceProductionContext context, ImmutableArray<DescriptorModel> models) {
        var valid = new List<DescriptorModel>();

        foreach (var model in models) {
            if (model.Warning is not null) {
                context.ReportDiagnostic(Diagnostic.Create(GenericNotDescribed, Location.None, model.Warning));
            } else {
                valid.Add(model);
            }
        }

        if (valid.Count == 0) {
            return;
        }

        var source = new StringBuilder();
        source.AppendLine("// <auto-generated/>");
        source.AppendLine("#nullable disable");
        source.AppendLine();
        source.AppendLine($"namespace {GeneratedNamespace} {{");
        source.AppendLine("    /// <summary>Registers this assembly's type descriptors before any of its code runs.</summary>");
        source.AppendLine("    internal static class TypeRegistration {");
        source.AppendLine("        [global::System.Runtime.CompilerServices.ModuleInitializer]");
        source.AppendLine("        internal static void Initialize() {");

        foreach (var model in valid) {
            source.AppendLine($"            global::Vixen.Core.Reflection.TypeRegistry.Register(Describe_{model.SafeName}());");
        }

        // Distinct, because two members of two types routinely declare the same List<T>, and
        // ordered, because a generator whose output moves for no reason makes every build a diff.
        var factories = valid
            .SelectMany(model => model.Members)
            .SelectMany(member => member.CollectionFactories)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(entry => entry, StringComparer.Ordinal);

        foreach (var factory in factories) {
            source.AppendLine($"            global::Vixen.Core.Reflection.CollectionFactory.Register({factory});");
        }

        source.AppendLine("        }");

        foreach (var model in valid) {
            source.AppendLine();
            EmitDescriptor(source, model);
        }

        source.AppendLine("    }");
        source.AppendLine("}");

        context.AddSource("TypeRegistration.g.cs", SourceText.From(source.ToString(), Encoding.UTF8));
    }

    static void EmitDescriptor(StringBuilder source, DescriptorModel model) {
        source.AppendLine($"        static global::Vixen.Core.Reflection.TypeDescriptor Describe_{model.SafeName}() => new(");
        source.AppendLine($"            typeof({model.QualifiedName}),");
        source.AppendLine($"            {Quote(model.Alias)},");
        source.AppendLine($"            {string.Join(" | ", model.Traits.Split([" | "], StringSplitOptions.None).Select(trait => "global::Vixen.Core.Reflection.TypeTraits." + trait))},");
        source.AppendLine("            [");

        foreach (var member in model.Members) {
            var getter = member.CanRead
                ? $"static instance => (object)(({model.QualifiedName})instance).{member.Name}"
                : "null";

            // A struct arrives here boxed, and assigning through a cast would modify a temporary
            // copy and silently do nothing. Unbox gives a reference into the box itself, so the
            // caller sees the change on the object it handed over — which is what an inspector
            // editing a boxed component needs.
            var target = model.IsValueType
                ? $"global::System.Runtime.CompilerServices.Unsafe.Unbox<{model.QualifiedName}>(instance)"
                : $"(({model.QualifiedName})instance)";

            // An init-only setter cannot be called from an assignment — the language forbids it
            // outside an object initializer — so it goes through the [UnsafeAccessor] emitted below.
            // Same setter, reached the only way there is; nothing here is reflection, and it survives
            // trimming and NativeAOT like the rest of this file.
            var setter = member.CanWrite
                ? member.IsInitOnly
                    ? $"static (instance, value) => Init_{model.SafeName}_{member.Name}("
                    + $"{(model.IsValueType ? "ref " : string.Empty)}{target}, ({member.TypeName})value)"
                    : $"static (instance, value) => {target}.{member.Name} = ({member.TypeName})value"
                : "null";

            source.AppendLine("                new(");
            source.AppendLine($"                    {Quote(member.Name)},");
            source.AppendLine($"                    typeof({member.TypeName}),");
            source.AppendLine($"                    {member.Order},");
            source.AppendLine($"                    {getter},");
            source.AppendLine($"                    {setter},");
            source.AppendLine(
                "                    new("
                + $"{Quote(member.Category)}, {Quote(member.DisplayName)}, {Quote(member.Tooltip)}, "
                + $"{Number(member.Minimum)}, {Number(member.Maximum)}, "
                + $"{member.Step.ToString("R", CultureInfo.InvariantCulture)}d, "
                + $"{Lower(member.Logarithmic)}, {Lower(member.IsEditorVisible)}, {Lower(member.IsEditorReadOnly)}, "
                + $"{(member.AssetType is null ? "null" : $"typeof({member.AssetType})")}, {Lower(member.AllowsNull)}),"
            );

            source.AppendLine($"                    {Lower(member.IsInitOnly)}");
            source.AppendLine("                ),");
        }

        source.AppendLine("            ],");
        source.AppendLine($"            {(model.CanCreate ? $"static () => new {model.QualifiedName}()" : "null")},");
        source.AppendLine($"            {Quote(model.Category)},");

        source.AppendLine(
            model.FormerAliases.IsDefaultOrEmpty
                ? "            null"
                : $"            [{string.Join(", ", model.FormerAliases.Select(Quote))}]"
        );

        source.AppendLine("        );");
        EmitInitAccessors(source, model);
    }

    /// <summary>
    ///     One <c>[UnsafeAccessor]</c> per init-only member, bound to the setter the language will
    ///     not let anyone call.
    /// </summary>
    /// <remarks>
    ///     A value type takes <c>ref</c>, so the write lands in the box the caller handed over rather
    ///     than in a copy — the same reason the ordinary setter unboxes.
    /// </remarks>
    static void EmitInitAccessors(StringBuilder source, DescriptorModel model) {
        foreach (var member in model.Members) {
            if (member is not { CanWrite: true, IsInitOnly: true }) {
                continue;
            }

            var receiver = model.IsValueType ? $"ref {model.QualifiedName}" : model.QualifiedName;

            source.AppendLine();
            source.AppendLine(
                "        [global::System.Runtime.CompilerServices.UnsafeAccessor("
                + "global::System.Runtime.CompilerServices.UnsafeAccessorKind.Method, "
                + $"Name = \"set_{member.Name}\")]"
            );

            source.AppendLine(
                $"        static extern void Init_{model.SafeName}_{member.Name}("
                + $"{receiver} instance, {member.TypeName} value);"
            );
        }
    }

    static string Lower(bool value) => value ? "true" : "false";
}
