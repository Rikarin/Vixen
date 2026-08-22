// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Vixen.Ui.Generators;

/// <summary>Implements the partial properties marked <c>[UiProperty]</c>.</summary>
/// <remarks>
///     <para>
///         What a UI property needs beyond an auto-property is an <i>identity</i> — a stylesheet, an
///         animation, a binding and an inspector all have to find it by name at runtime — plus a
///         change callback, coercion, and the option of taking an ancestor's value when it has none
///         of its own. Written by hand that is thirty lines per property that all look alike and one
///         of which will eventually be wrong.
///     </para>
///     <para>
///         Generated rather than reflected, and generated rather than rewritten: Stride builds the
///         equivalent with a runtime <c>DependencyPropertyFactory</c>, and ADR-002 rejects the whole
///         category. Output that can be read and stepped through is strictly better, and it survives
///         trimming because there is nothing to look up.
///     </para>
///     <para>
///         ⚠ <b>Inheritance is generated as a typed walk, not a dictionary lookup.</b> Each
///         inheriting property emits its own loop that tests <c>ancestor is TOwner</c> — so the
///         inherited value comes from the nearest ancestor that actually declares the property, and
///         a sibling type that happens to have a property of the same name is not it.
///     </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class UiPropertyGenerator : IIncrementalGenerator {
    const string Attribute = "Vixen.Ui.UiPropertyAttribute";
    const string ElementType = "Vixen.Ui.UiElement";

    static readonly DiagnosticDescriptor PropertyNotPartial = new(
        "VXS0301",
        "A UI property is not partial",
        "'{0}' is marked [UiProperty] but is not declared partial, so there is nothing for the generator to implement",
        "Vixen.Ui",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        "The generator supplies the accessors. A property that already has them has nowhere for them to go."
    );

    static readonly DiagnosticDescriptor TypeNotPartial = new(
        "VXS0302",
        "A type declaring a UI property is not partial",
        "'{0}' declares a [UiProperty] but is not declared partial",
        "Vixen.Ui",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        "Generated members are added in a second part of the type, which needs the first to be partial."
    );

    static readonly DiagnosticDescriptor NotAnElement = new(
        "VXS0303",
        "A type declaring a UI property is not a UI element",
        "'{0}' declares a [UiProperty] but does not derive from UiElement",
        "Vixen.Ui",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        "A UI property is addressed through an element — the registry's accessors take one — so a type "
        + "that is not one could declare a property nothing could ever read."
    );

    static readonly DiagnosticDescriptor CallbackMissing = new(
        "VXS0304",
        "A UI property names a callback that does not exist",
        "'{0}' names '{1}', which '{2}' does not declare",
        "Vixen.Ui",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        "Reported here rather than left to the compiler, because the error would otherwise point at a "
        + "line of generated code the author did not write."
    );

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var properties = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                Attribute,
                static (node, _) => node is PropertyDeclarationSyntax,
                static (syntaxContext, _) => Describe(syntaxContext)
            )
            .Where(static model => model is not null)
            .Select(static (model, _) => model!);

        context.RegisterSourceOutput(properties.Collect(), static (production, models) => Emit(production, models));
    }

    static PropertyModel? Describe(GeneratorAttributeSyntaxContext context) {
        if (context.TargetSymbol is not IPropertySymbol property || context.TargetNode is not PropertyDeclarationSyntax syntax) {
            return null;
        }

        var owner = property.ContainingType;
        var attribute = context.Attributes[0];

        var inherits = NamedArgument(attribute, "Inherits") is bool flag && flag;
        var changed = NamedArgument(attribute, "Changed") as string;
        var coerce = NamedArgument(attribute, "Coerce") as string;
        var defaultValue = NamedArgumentTyped(attribute, "Default");

        return new PropertyModel(
            property.Name,
            owner.Name,
            owner.ContainingNamespace.IsGlobalNamespace ? null : owner.ContainingNamespace.ToDisplayString(),
            owner.ToDisplayString(),
            property.Type.ToDisplayString(),
            property.Type.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString(),
            property.DeclaredAccessibility == Accessibility.Public ? "public" : "internal",
            inherits,
            defaultValue,
            changed,
            coerce,
            syntax.Modifiers.Any(static token => token.ValueText == "partial"),
            IsPartialType(owner),
            DerivesFromElement(owner),
            HasMember(owner, changed),
            HasMember(owner, coerce),
            property.Locations.FirstOrDefault() ?? Location.None
        );
    }

    static object? NamedArgument(AttributeData attribute, string name) {
        foreach (var argument in attribute.NamedArguments) {
            if (argument.Key == name) {
                return argument.Value.Value;
            }
        }

        return null;
    }

    static string? NamedArgumentTyped(AttributeData attribute, string name) {
        foreach (var argument in attribute.NamedArguments) {
            if (argument.Key == name && !argument.Value.IsNull) {
                return Literal(argument.Value);
            }
        }

        return null;
    }

    /// <summary>Writes an attribute argument back out as C# source.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Not <c>Value.ToString()</c>.</b> That turns <c>true</c> into <c>True</c>, writes
    ///         a float with whatever the current culture calls a decimal point, and reduces an enum
    ///         member to its underlying number — three ways to emit source that does not compile, or
    ///         worse, compiles to something else on one machine.
    ///     </para>
    ///     <para>
    ///         <see cref="SymbolDisplay.FormatPrimitive" /> handles the culture and the quoting, and
    ///         the suffixes are added here because it does not: a <c>float</c> field initialised
    ///         from the literal <c>4.5</c> is a compile error, since that literal is a
    ///         <c>double</c>.
    ///     </para>
    /// </remarks>
    static string Literal(TypedConstant constant) {
        var value = constant.Value!;

        if (constant.Type is { TypeKind: TypeKind.Enum } enumType) {
            return $"({enumType.ToDisplayString()}) {SymbolDisplay.FormatPrimitive(value, false, false)}";
        }

        return constant.Type?.SpecialType switch {
            // ⚠ The float is formatted as a float rather than widened. "R" on the double a float
            // widens to gives 0.10000000149011612 for 0.1f — which compiles, and is a different
            // number from the one that was written.
            SpecialType.System_Single => Special((float) value, "float")
                ?? ((float) value).ToString("R", CultureInfo.InvariantCulture) + "f",
            SpecialType.System_Double => Special((double) value, "double")
                ?? ((double) value).ToString("R", CultureInfo.InvariantCulture) + "d",
            SpecialType.System_Decimal => ((decimal) value).ToString(CultureInfo.InvariantCulture) + "m",
            _ => SymbolDisplay.FormatPrimitive(value, quoteStrings: true, useHexadecimalNumbers: false)
        };
    }

    /// <summary>Writes a floating-point default, including the three values that are not numbers.</summary>
    /// <remarks>
    ///     ⚠ <b>Infinity and NaN have no literal form in C#</b>, so the round-trip format that is
    ///     right for every other value produces <c>Infinityd</c> — which is not a compile error in
    ///     the generator, where nothing runs, but is one in every project that declares a property
    ///     with an unbounded default. A numeric range is exactly where somebody writes one, so this
    ///     is the first place the gap shows.
    /// </remarks>
    static string? Special(double value, string type) {
        if (double.IsNaN(value)) {
            return type + ".NaN";
        }

        if (double.IsPositiveInfinity(value)) {
            return type + ".PositiveInfinity";
        }

        return double.IsNegativeInfinity(value) ? type + ".NegativeInfinity" : null;
    }

    static bool IsPartialType(INamedTypeSymbol type) {
        foreach (var reference in type.DeclaringSyntaxReferences) {
            if (reference.GetSyntax() is TypeDeclarationSyntax declaration
                && declaration.Modifiers.Any(static token => token.ValueText == "partial")) {
                return true;
            }
        }

        return false;
    }

    static bool DerivesFromElement(INamedTypeSymbol type) {
        for (var current = type; current is not null; current = current.BaseType) {
            if (current.ToDisplayString() == ElementType) {
                return true;
            }
        }

        return false;
    }

    static bool HasMember(INamedTypeSymbol type, string? name) {
        if (name is null) {
            return true;
        }

        for (var current = type; current is not null; current = current.BaseType) {
            if (current.GetMembers(name).Length > 0) {
                return true;
            }
        }

        return false;
    }

    static void Emit(SourceProductionContext context, ImmutableArray<PropertyModel> models) {
        var byOwner = new Dictionary<string, List<PropertyModel>>(StringComparer.Ordinal);

        foreach (var model in models) {
            if (Report(context, model)) {
                continue;
            }

            if (!byOwner.TryGetValue(model.OwnerQualifiedName, out var list)) {
                list = [];
                byOwner[model.OwnerQualifiedName] = list;
            }

            list.Add(model);
        }

        foreach (var pair in byOwner) {
            var first = pair.Value[0];
            context.AddSource($"{Sanitise(pair.Key)}.UiProperties.g.cs", SourceText.From(Render(first, pair.Value), Encoding.UTF8));
        }
    }

    /// <summary>Reports whatever is wrong with a model. Returns whether it cannot be emitted.</summary>
    static bool Report(SourceProductionContext context, PropertyModel model) {
        var broken = false;

        if (!model.IsPartialProperty) {
            context.ReportDiagnostic(Diagnostic.Create(PropertyNotPartial, model.Location, model.Name));
            broken = true;
        }

        if (!model.IsPartialType) {
            context.ReportDiagnostic(Diagnostic.Create(TypeNotPartial, model.Location, model.OwnerName));
            broken = true;
        }

        if (!model.DerivesFromElement) {
            context.ReportDiagnostic(Diagnostic.Create(NotAnElement, model.Location, model.OwnerName));
            broken = true;
        }

        if (!model.HasChanged) {
            context.ReportDiagnostic(Diagnostic.Create(CallbackMissing, model.Location, model.Name, model.Changed, model.OwnerName));
            broken = true;
        }

        if (!model.HasCoerce) {
            context.ReportDiagnostic(Diagnostic.Create(CallbackMissing, model.Location, model.Name, model.Coerce, model.OwnerName));
            broken = true;
        }

        return broken;
    }

    static string Render(PropertyModel owner, List<PropertyModel> properties) {
        var builder = new StringBuilder();

        builder.Append("// SPDX-FileCopyrightText: Copyright (c) Rikarin\n");
        builder.Append("// SPDX-License-Identifier: Apache-2.0\n//\n// <auto-generated>\n");
        builder.Append("//     Generated by Vixen.Ui.Generators. Do not edit.\n// </auto-generated>\n\n");
        builder.Append("#nullable enable\n\n");
        builder.Append("using System;\n\nusing Vixen.Ui;\n\n");

        if (owner.Namespace is not null) {
            builder.Append("namespace ").Append(owner.Namespace).Append(";\n\n");
        }

        builder.Append("partial class ").Append(owner.OwnerName).Append(" {\n");

        // ⚠ Empty, and the whole point of it is that it exists. Without a static constructor the
        // class is `beforefieldinit`, which lets the CLR defer the field initialisers below until
        // something reads a static field of this exact type — and making an instance is not that.
        // So a freshly constructed control had registered none of its properties:
        // `UiPropertyRegistry.TryFindFor` found nothing, and `bind:Value` on a `<Slider />` threw
        // "'slider' has no property called 'Value'" unless some unrelated code had already touched
        // `Slider.ValueProperty`. Declaring one makes the initialiser run before the first instance
        // exists, which is the guarantee every lookup on an element was already assuming.
        //
        // ⚠ Here rather than a `RunClassConstructor` in the registry, which cannot be written: the
        // type there comes from `element.GetType()`, and the trimmer refuses a class constructor it
        // cannot name (IL2059).
        builder.Append("    /// <summary>Registers this type's properties before an instance of it can exist.</summary>\n");
        builder.Append("    /// <remarks>See the generator: without it the class is beforefieldinit and the\n");
        builder.Append("    ///     registrations below may not have run when something looks one up by name.</remarks>\n");
        builder.Append("    static ").Append(Bare(owner.OwnerName)).Append("() {\n    }\n");

        foreach (var property in properties) {
            RenderProperty(builder, property);
        }

        builder.Append("}\n");
        return builder.ToString();
    }

    /// <summary>A class name without its type parameters, which a constructor's name may not carry.</summary>
    static string Bare(string name) {
        var angle = name.IndexOf('<');
        return angle < 0 ? name : name.Substring(0, angle);
    }

    static void RenderProperty(StringBuilder builder, PropertyModel property) {
        var field = Camel(property.Name);
        var type = property.Type;
        var initial = property.Default ?? $"default({type})";

        builder.Append("\n    /// <summary>Identity for <see cref=\"").Append(property.Name).Append("\" />.</summary>\n");
        builder.Append("    public static readonly UiPropertyKey ").Append(property.Name).Append("Property =\n");
        builder.Append("        UiPropertyRegistry.Register(\n");
        builder.Append("            \"").Append(property.Name).Append("\",\n");
        builder.Append("            typeof(").Append(property.OwnerQualifiedName).Append("),\n");
        builder.Append("            typeof(").Append(property.TypeForTypeof).Append("),\n");
        builder.Append("            ").Append(property.Inherits ? "true" : "false").Append(",\n");
        builder.Append("            static element => ((").Append(property.OwnerQualifiedName).Append(") element).").Append(property.Name).Append(",\n");
        builder.Append("            static (element, value) => ((").Append(property.OwnerQualifiedName).Append(") element).")
            .Append(property.Name).Append(" = (").Append(type).Append(") value!\n");
        builder.Append("        );\n\n");

        builder.Append("    ").Append(type).Append(' ').Append(field).Append("Value = ").Append(initial).Append(";\n");
        builder.Append("    bool ").Append(field).Append("IsSet;\n\n");

        builder.Append("    /// <inheritdoc />\n");
        builder.Append("    ").Append(property.Accessibility).Append(" partial ").Append(type).Append(' ').Append(property.Name).Append(" {\n");
        builder.Append("        get {\n");
        builder.Append("            if (").Append(field).Append("IsSet) {\n");
        builder.Append("                return ").Append(field).Append("Value;\n            }\n\n");

        if (property.Inherits) {
            builder.Append("            for (var ancestor = Parent; ancestor is not null; ancestor = ancestor.Parent) {\n");
            builder.Append("                if (ancestor is ").Append(property.OwnerQualifiedName).Append(" owner && owner.")
                .Append(field).Append("IsSet) {\n");
            builder.Append("                    return owner.").Append(field).Append("Value;\n                }\n            }\n\n");
        }

        builder.Append("            return ").Append(initial).Append(";\n");
        builder.Append("        }\n\n");

        builder.Append("        set {\n");
        builder.Append("            var incoming = ");
        builder.Append(property.Coerce is null ? "value" : $"{property.Coerce}(value)").Append(";\n");

        // The old value is read *before* the write and through the property, so an inheriting
        // property reports the ancestor's value it was showing rather than its own empty field.
        builder.Append("            var previous = ").Append(property.Name).Append(";\n\n");
        builder.Append("            ").Append(field).Append("Value = incoming;\n");
        builder.Append("            ").Append(field).Append("IsSet = true;\n\n");
        builder.Append("            if (!System.Collections.Generic.EqualityComparer<").Append(type)
            .Append(">.Default.Equals(previous, incoming)) {\n");

        if (property.Changed is not null) {
            builder.Append("                ").Append(property.Changed).Append("(previous, incoming);\n");
        }

        builder.Append("                RaisePropertyChanged(").Append(property.Name).Append("Property);\n");
        builder.Append("            }\n");
        builder.Append("        }\n");
        builder.Append("    }\n\n");

        builder.Append("    /// <summary>Unsets <see cref=\"").Append(property.Name)
            .Append("\" />, so it takes its default or its ancestor's value again.</summary>\n");
        builder.Append("    ").Append(property.Accessibility).Append(" void Clear").Append(property.Name).Append("() {\n");
        builder.Append("        if (!").Append(field).Append("IsSet) {\n            return;\n        }\n\n");
        builder.Append("        var previous = ").Append(field).Append("Value;\n");
        builder.Append("        ").Append(field).Append("IsSet = false;\n\n");
        builder.Append("        var current = ").Append(property.Name).Append(";\n");
        builder.Append("        if (!System.Collections.Generic.EqualityComparer<").Append(type)
            .Append(">.Default.Equals(previous, current)) {\n");

        if (property.Changed is not null) {
            builder.Append("            ").Append(property.Changed).Append("(previous, current);\n");
        }

        builder.Append("            RaisePropertyChanged(").Append(property.Name).Append("Property);\n");
        builder.Append("        }\n");
        builder.Append("    }\n");
    }

    static string Camel(string name) =>
        name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);

    static string Sanitise(string name) => name.Replace('.', '_').Replace('<', '_').Replace('>', '_');

    sealed record PropertyModel(
        string Name,
        string OwnerName,
        string? Namespace,
        string OwnerQualifiedName,
        string Type,
        string TypeForTypeof,
        string Accessibility,
        bool Inherits,
        string? Default,
        string? Changed,
        string? Coerce,
        bool IsPartialProperty,
        bool IsPartialType,
        bool DerivesFromElement,
        bool HasChanged,
        bool HasCoerce,
        Location Location
    );
}
