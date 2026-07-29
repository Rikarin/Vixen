// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Vixen.Editor.Inspector.Generator;

/// <summary>Turns <c>[Inspector]</c>-attributed members into a descriptor per type.</summary>
/// <remarks>
///     <para>
///         <b>What it writes.</b> One <c>InspectorDescriptor</c> per type that has annotated members,
///         holding the member list, the attribute metadata and the accessors; and a module
///         initializer that registers it. Referencing an assembly is therefore enough for its types
///         to be inspectable.
///     </para>
///     <para>
///         <b>Why generated rather than reflected.</b> The obvious implementation walks the loaded
///         assemblies looking for the attribute and costs a reflection pass at startup, an AOT
///         hazard, and an inspector whose contents depend on what happened to be loaded. This is the
///         same bet <c>Vixen.Core.Reflection</c> makes; what it adds is the accessor shape.
///     </para>
///     <para>
///         <b>Why a field gets a <c>ref</c> accessor and a property does not.</b>
///         <c>Vixen.Core.Reflection</c>'s accessors pass values as <c>object</c>, and doc 11's
///         inspector asks for "get/set accessors as delegates over <c>ref</c> access … it works for
///         <c>struct</c> members without boxing". A field can have one — <c>static (Foo o) =&gt; ref
///         o.Tint</c> — and a property cannot, because there is nothing to take a reference to. So
///         the generator emits the strongest accessor each member admits rather than the weakest one
///         both admit.
///     </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class InspectorDescriptorGenerator : IIncrementalGenerator {
    const string InspectorAttribute = "Vixen.Editor.Inspector.InspectorAttribute";
    const string Namespace = "Vixen.Editor.Inspector";

    static readonly DiagnosticDescriptor Unreadable = new(
        "VXI0101",
        "An [Inspector] member cannot be read",
        "{0}",
        "Vixen.Inspector",
        DiagnosticSeverity.Error,
        true
    );

    static readonly DiagnosticDescriptor BadCondition = new(
        "VXI0102",
        "A [ShowIf] or [HideIf] does not name a bool member of the same type",
        "{0}",
        "Vixen.Inspector",
        DiagnosticSeverity.Error,
        true
    );

    static readonly DiagnosticDescriptor ValueTypeOwner = new(
        "VXI0103",
        "[Inspector] is on a member of a value type",
        "{0}",
        "Vixen.Inspector",
        DiagnosticSeverity.Error,
        true
    );

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var types = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                InspectorAttribute,
                static (node, _) => true,
                static (syntax, _) => Read(syntax)
            )
            .Where(static model => model is not null)
            .Select(static (model, _) => model!);

        context.RegisterSourceOutput(types, static (production, model) => Emit(production, model));
    }

    /// <summary>
    ///     Reads the type an annotated member belongs to, once, from its first annotated member.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The attribute is on members and the descriptor is per type</b>, so this fires once per
    ///     annotated member and all but the first hand back nothing. Reading the whole type from one
    ///     of its members is what keeps the members in <i>declaration</i> order — grouping N
    ///     independently-read members in a later stage would put them in whatever order the pipeline
    ///     produced them.
    /// </remarks>
    static InspectedTypeModel? Read(GeneratorAttributeSyntaxContext syntax) {
        if (syntax.TargetSymbol.ContainingType is not { } type) {
            return null;
        }

        var annotated = Annotated(type);

        if (annotated.Count == 0 || !SymbolEqualityComparer.Default.Equals(annotated[0], syntax.TargetSymbol)) {
            return null;
        }

        var problems = ImmutableArray.CreateBuilder<DiagnosticModel>();
        var members = ImmutableArray.CreateBuilder<MemberModel>();

        if (type.IsValueType) {
            problems.Add(Problem(
                ValueTypeOwner.Id,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "'{0}' is a value type, and an inspector edits objects it holds references to. A "
                    + "struct handed to a drawer would be a copy, and every edit would be written to "
                    + "the copy. Make it a class, or expose it as a member of one.",
                    type.Name
                ),
                Where(type)
            ));
        }

        foreach (var member in annotated) {
            var model = ReadMember(type, member, problems);

            if (model is not null) {
                members.Add(model);
            }
        }

        // Declaration order, except where a member asked for a place. Sorted here rather than at
        // registration so that the descriptor a consumer reads is already in the order it is drawn —
        // there is then no second sort anywhere for the two to disagree about.
        var ordered = members
            .Select(static (member, index) => (member, index))
            .OrderBy(static pair => pair.member.Order)
            .ThenBy(static pair => pair.index)
            .Select(static pair => pair.member)
            .ToImmutableArray();

        var qualified = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        return new(
            type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString(),
            type.Name,
            qualified,
            Safe(qualified),
            CanCreate(type),
            ordered,
            problems.ToImmutable()
        );
    }

    static MemberModel? ReadMember(
        INamedTypeSymbol type,
        ISymbol member,
        ImmutableArray<DiagnosticModel>.Builder problems
    ) {
        ITypeSymbol memberType;
        bool isField;
        bool canWrite;

        switch (member) {
            case IFieldSymbol field:
                memberType = field.Type;
                isField = !field.IsReadOnly && !field.IsConst;
                canWrite = isField;

                break;

            case IPropertySymbol property when property.GetMethod is not null:
                memberType = property.Type;
                isField = false;
                canWrite = property.SetMethod is not null;

                break;

            case IPropertySymbol property:
                problems.Add(Problem(
                    Unreadable.Id,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "'{0}' is marked [Inspector] and has no getter. An inspector has to read a "
                        + "member before it can show a value to change.",
                        property.Name
                    ),
                    Where(property)
                ));

                return null;

            default:
                return null;
        }

        // A readonly field is readable and not writable, so it gets accessors rather than a `ref`.
        // Emitting `ref o.Field` for one would be a compile error in the generated file, which is the
        // worst place for an error to land.
        var readOnlyField = member is IFieldSymbol { IsReadOnly: true } or IFieldSymbol { IsConst: true };

        var marker = Attribute(member, InspectorAttribute);
        var attributes = ImmutableArray.CreateBuilder<string>();

        string? header = null;
        string? tooltip = null;
        double? minimum = null;
        double? maximum = null;
        var step = 0d;
        var logarithmic = false;
        bool? hdr = null;
        var showAlpha = true;
        string? assetType = null;
        var allowNull = true;
        float? curveMinimum = null;
        float? curveMaximum = null;
        var lines = 0;
        string? condition = null;
        var negated = false;
        var readOnly = readOnlyField;

        foreach (var attribute in member.GetAttributes()) {
            var name = attribute.AttributeClass?.Name;

            if (name is null) {
                continue;
            }

            attributes.Add(attribute.AttributeClass!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));

            switch (name) {
                case "HeaderAttribute" when attribute.ConstructorArguments.Length == 1:
                    header = attribute.ConstructorArguments[0].Value as string;
                    break;

                // Read by simple name, the same way Vixen.Core.Reflection's generator reads them, so
                // that this does not need a reference to the assembly the attribute lives in.
                case "TooltipAttribute" when attribute.ConstructorArguments.Length == 1:
                    tooltip = attribute.ConstructorArguments[0].Value as string;
                    break;

                case "RangeAttribute" when attribute.ConstructorArguments.Length == 2:
                    minimum = Number(attribute.ConstructorArguments[0]);
                    maximum = Number(attribute.ConstructorArguments[1]);
                    step = Named(attribute, "Step") is { } stepValue
                        ? Convert.ToDouble(stepValue, CultureInfo.InvariantCulture)
                        : 0d;

                    logarithmic = Named(attribute, "Logarithmic") is bool log && log;
                    break;

                case "ColorUsageAttribute":
                    hdr = attribute.ConstructorArguments.Length == 1
                        && attribute.ConstructorArguments[0].Value is bool flag
                        && flag;

                    showAlpha = Named(attribute, "ShowAlpha") is not bool alpha || alpha;
                    break;

                case "AssetPickerAttribute" when attribute.ConstructorArguments.Length == 1:
                    assetType = attribute.ConstructorArguments[0].Value is ITypeSymbol picked
                        ? picked.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                        : null;

                    allowNull = Named(attribute, "AllowNull") is not bool nullable || nullable;
                    break;

                case "CurveAttribute":
                    curveMinimum = Named(attribute, "Minimum") is { } low
                        ? Convert.ToSingle(low, CultureInfo.InvariantCulture)
                        : 0f;

                    curveMaximum = Named(attribute, "Maximum") is { } high
                        ? Convert.ToSingle(high, CultureInfo.InvariantCulture)
                        : 1f;

                    break;

                case "MultilineAttribute":
                    lines = Named(attribute, "Lines") is { } count
                        ? Convert.ToInt32(count, CultureInfo.InvariantCulture)
                        : 4;

                    break;

                case "ShowIfAttribute" when attribute.ConstructorArguments.Length == 1:
                    condition = attribute.ConstructorArguments[0].Value as string;
                    negated = false;

                    break;

                case "HideIfAttribute" when attribute.ConstructorArguments.Length == 1:
                    condition = attribute.ConstructorArguments[0].Value as string;
                    negated = true;

                    break;

                case "InspectorReadOnlyAttribute":
                    readOnly = true;
                    break;

                default:
                    break;
            }
        }

        if (condition is not null && !HasBoolean(type, condition)) {
            problems.Add(Problem(
                BadCondition.Id,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "'{0}' is shown conditionally on '{1}', and '{1}' is not a bool field or property "
                    + "of '{2}'. A condition is resolved by name at run time, so a typo here is a row "
                    + "that is always shown and never explains why.",
                    member.Name,
                    condition,
                    type.Name
                ),
                Where(member)
            ));
        }

        return new(
            member.Name,
            Named(marker, "Name") as string ?? "",

            // ⚠ With the nullable annotation, and the generated file is `#nullable enable`. A member
            // declared `Foo? Inner` produces `ref o.Inner` of type `Foo?`, which does not match a
            // `MemberReference<T, Foo>` — so dropping the `?` made every nullable reference member a
            // compile error in generated code the author of the type never sees. An optional asset,
            // an optional parent, an optional override block: none of them could be described.
            memberType.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat.AddMiscellaneousOptions(
                    SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
                )
            ),
            isField,
            canWrite,
            header,
            tooltip,
            minimum,
            maximum,
            step,
            logarithmic,
            hdr,
            showAlpha,
            assetType,
            allowNull,
            curveMinimum,
            curveMaximum,
            lines,
            condition,
            negated,
            readOnly,
            Named(marker, "Order") is { } order ? Convert.ToInt32(order, CultureInfo.InvariantCulture) : 0,
            attributes.ToImmutable()
        );
    }

    /// <summary>Writes one type's descriptor and the module initializer that registers it.</summary>
    static void Emit(SourceProductionContext production, InspectedTypeModel model) {
        foreach (var problem in model.Problems) {
            production.ReportDiagnostic(Diagnostic.Create(Descriptor(problem.Id), problem.Where(), problem.Message));
        }

        if (model.Problems.Length > 0 || model.Members.Length == 0) {
            return;
        }

        var text = new StringBuilder();

        text.AppendLine("// <auto-generated/>")
            .AppendLine("#nullable enable")
            .AppendLine()
            .AppendLine($"namespace {Namespace}.Generated;")
            .AppendLine()
            .AppendLine($"/// <summary>What the inspector knows about <c>{model.TypeName}</c>. Generated.</summary>")
            .AppendLine($"internal static class Describes_{model.SafeName} {{")
            .AppendLine("    /// <summary>The descriptor, registered before any of this assembly's code runs.</summary>")
            .AppendLine($"    internal static global::{Namespace}.InspectorDescriptor Descriptor {{ get; }} = new(")
            .AppendLine($"        typeof({model.QualifiedName}),")
            .AppendLine("        [");

        for (var index = 0; index < model.Members.Length; index++) {
            EmitMember(text, model, model.Members[index], index == model.Members.Length - 1);
        }

        text.AppendLine("        ],");

        text.AppendLine(
                model.CanCreate
                    ? $"        static () => new {model.QualifiedName}()"
                    : "        null"
            )
            .AppendLine("    );")
            .AppendLine()
            .AppendLine("    /// <summary>Registers it. Runs once, before anything in this assembly.</summary>")
            .AppendLine("    [global::System.Runtime.CompilerServices.ModuleInitializer]")
            .AppendLine("    internal static void Register() =>")
            .AppendLine($"        global::{Namespace}.InspectorRegistry.Register(Descriptor);")
            .AppendLine("}");

        production.AddSource($"{model.SafeName}.Inspector.g.cs", text.ToString());
    }

    static void EmitMember(StringBuilder text, InspectedTypeModel model, MemberModel member, bool last) {
        var owner = model.QualifiedName;
        var declared = $"global::{Namespace}.InspectorMember<{owner}, {member.TypeName}>";

        // A field gets a `ref` accessor, which is what lets a drawer edit one channel of a struct
        // member in place. A property gets a getter and a setter, because there is no reference to
        // take — see the type's remarks.
        var accessors = member.IsField
            ? $"static ({owner} o) => ref o.{member.Name}"
            : member.CanWrite
                ? $"static ({owner} o) => o.{member.Name}, static ({owner} o, {member.TypeName} v) => o.{member.Name} = v"
                : $"static ({owner} o) => o.{member.Name}, null";

        text.AppendLine($"            new {declared}(")
            .AppendLine($"                {Quote(member.Name)},")
            .AppendLine($"                {accessors},")
            .AppendLine($"                {Quote(member.DisplayName.Length == 0 ? null : member.DisplayName)}")
            .AppendLine("            ) {");

        text.AppendLine($"                Header = {Quote(member.Header)},");
        text.AppendLine($"                Tooltip = {Quote(member.Tooltip)},");

        text.AppendLine(
            member is { Minimum: { } low, Maximum: { } high }
                ? $"                Range = new({Double(low)}, {Double(high)}, {Double(member.Step)}, "
                + $"{Lower(member.Logarithmic)}),"
                : "                Range = null,"
        );

        text.AppendLine(
            member.Hdr is { } hdr
                ? $"                Color = new({Lower(hdr)}, {Lower(member.ShowAlpha)}),"
                : "                Color = null,"
        );

        text.AppendLine(
            member is { CurveMinimum: { } curveLow, CurveMaximum: { } curveHigh }
                ? $"                Curve = new({Single(curveLow)}, {Single(curveHigh)}),"
                : "                Curve = null,"
        );

        text.AppendLine(
            member.AssetType is { } asset
                ? $"                AssetType = typeof({asset}),"
                : "                AssetType = null,"
        );

        text.AppendLine($"                AllowNull = {Lower(member.AllowNull)},");
        text.AppendLine($"                Lines = {member.Lines.ToString(CultureInfo.InvariantCulture)},");
        text.AppendLine($"                Condition = {Quote(member.Condition)},");
        text.AppendLine($"                ConditionNegated = {Lower(member.ConditionNegated)},");
        text.AppendLine($"                IsReadOnly = {Lower(member.IsReadOnly)},");
        text.AppendLine($"                Order = {member.Order.ToString(CultureInfo.InvariantCulture)},");

        text.AppendLine(
            member.Attributes.Length == 0
                ? "                Attributes = []"
                : "                Attributes = [" + string.Join(", ", member.Attributes.Select(static name => $"typeof({name})")) + "]"
        );

        text.AppendLine(last ? "            }" : "            },");
    }

    static List<ISymbol> Annotated(INamedTypeSymbol type) {
        List<ISymbol> found = [];

        foreach (var member in type.GetMembers()) {
            if (member is (IFieldSymbol or IPropertySymbol) and { IsStatic: false }
                && Attribute(member, InspectorAttribute) is not null) {
                found.Add(member);
            }
        }

        return found;
    }

    static bool HasBoolean(INamedTypeSymbol type, string name) {
        for (var current = type; current is not null; current = current.BaseType) {
            foreach (var member in current.GetMembers(name)) {
                switch (member) {
                    case IFieldSymbol { Type.SpecialType: SpecialType.System_Boolean }:
                    case IPropertySymbol { Type.SpecialType: SpecialType.System_Boolean, GetMethod: not null }:
                        return true;

                    default:
                        continue;
                }
            }
        }

        return false;
    }

    /// <summary>Whether a fresh instance can be made, which is where reset-to-default comes from.</summary>
    static bool CanCreate(INamedTypeSymbol type) {
        if (type.IsAbstract || type.IsStatic) {
            return false;
        }

        foreach (var constructor in type.InstanceConstructors) {
            if (constructor.Parameters.Length == 0
                && constructor.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal) {
                return true;
            }
        }

        return false;
    }

    static DiagnosticDescriptor Descriptor(string id) => id switch {
        "VXI0101" => Unreadable,
        "VXI0102" => BadCondition,
        _ => ValueTypeOwner
    };

    static Location Where(ISymbol symbol) => symbol.Locations.Length > 0 ? symbol.Locations[0] : Location.None;

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

    static AttributeData? Attribute(ISymbol symbol, string name) {
        foreach (var attribute in symbol.GetAttributes()) {
            if (attribute.AttributeClass?.ToDisplayString() == name) {
                return attribute;
            }
        }

        return null;
    }

    static object? Named(AttributeData? attribute, string name) {
        if (attribute is null) {
            return null;
        }

        foreach (var argument in attribute.NamedArguments) {
            if (argument.Key == name) {
                return argument.Value.Value;
            }
        }

        return null;
    }

    static double Number(TypedConstant constant) =>
        constant.Value is null ? 0d : Convert.ToDouble(constant.Value, CultureInfo.InvariantCulture);

    /// <summary>A qualified name turned into something that can be a class name and a file name.</summary>
    static string Safe(string qualified) {
        // The `global::` prefix would become eight underscores at the front of every file name.
        var name = qualified.StartsWith("global::", StringComparison.Ordinal) ? qualified.Substring(8) : qualified;
        var text = new StringBuilder(name.Length);

        foreach (var character in name) {
            text.Append(char.IsLetterOrDigit(character) ? character : '_');
        }

        return text.ToString();
    }

    static string Quote(string? value) =>
        value is null ? "null" : "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    static string Lower(bool value) => value ? "true" : "false";

    static string Double(double value) => value.ToString("R", CultureInfo.InvariantCulture) + "d";

    static string Single(float value) => value.ToString("R", CultureInfo.InvariantCulture) + "f";
}
