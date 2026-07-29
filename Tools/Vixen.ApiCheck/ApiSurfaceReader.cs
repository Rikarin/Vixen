// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Vixen.ApiCheck;

/// <summary>
///     Reads the publicly visible surface of a compiled assembly as a sorted list of one-line
///     entries, which is the form a baseline file can be diffed in.
/// </summary>
/// <remarks>
///     <para>
///         The surface is read from the <em>assembly</em> rather than from source. A source-based
///         reading would have to reproduce what the compiler does with partial types, generators,
///         conditional compilation and <c>InternalsVisibleTo</c>; the assembly is what consumers
///         actually reference, and it is the same artefact <c>Pack</c> ships.
///     </para>
///     <para>
///         One line per API element, sorted ordinally. That is the whole design: a line is either
///         there or it is not, an addition is one added line, and a signature change is one line
///         removed and one added — which makes the review of an API change a review of a diff.
///     </para>
/// </remarks>
public static class ApiSurfaceReader {
    /// <summary>How a type is written when it appears as a name, a base type or a signature part.</summary>
    static readonly SymbolDisplayFormat TypeFormat = new(
        SymbolDisplayGlobalNamespaceStyle.Omitted,
        SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeVariance,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
        | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
    );

    /// <summary>
    ///     How a member is written. Modifiers are included because <c>virtual</c> becoming
    ///     <c>sealed</c>, or an instance member becoming <c>static</c>, breaks callers exactly as
    ///     surely as deleting it does; default values are included for the same reason.
    /// </summary>
    static readonly SymbolDisplayFormat MemberFormat = new(
        SymbolDisplayGlobalNamespaceStyle.Omitted,
        SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        SymbolDisplayGenericsOptions.IncludeTypeParameters,
        SymbolDisplayMemberOptions.IncludeParameters
        | SymbolDisplayMemberOptions.IncludeContainingType
        | SymbolDisplayMemberOptions.IncludeExplicitInterface
        | SymbolDisplayMemberOptions.IncludeModifiers
        | SymbolDisplayMemberOptions.IncludeConstantValue
        | SymbolDisplayMemberOptions.IncludeRef,
        parameterOptions: SymbolDisplayParameterOptions.IncludeExtensionThis
        | SymbolDisplayParameterOptions.IncludeType
        | SymbolDisplayParameterOptions.IncludeName
        | SymbolDisplayParameterOptions.IncludeDefaultValue
        | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        propertyStyle: SymbolDisplayPropertyStyle.NameOnly,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
        | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
    );

    /// <summary>Reads the surface of the assembly at <paramref name="assemblyPath" />.</summary>
    public static IReadOnlyList<string> Read(string assemblyPath) {
        var target = MetadataReference.CreateFromFile(assemblyPath);
        var compilation = CSharpCompilation.Create("Vixen.ApiCheck.Surface", references: References(assemblyPath, target));

        if (compilation.GetAssemblyOrModuleSymbol(target) is not IAssemblySymbol assembly) {
            throw new InvalidOperationException($"'{assemblyPath}' is not a managed assembly.");
        }

        return Read(assembly);
    }

    /// <summary>Reads the surface of an already-resolved assembly symbol.</summary>
    public static IReadOnlyList<string> Read(IAssemblySymbol assembly) {
        ArgumentNullException.ThrowIfNull(assembly);

        // Sorted and de-duplicated on the way in: two members can produce the same entry only if
        // they are indistinguishable to a caller, in which case one line is the honest answer.
        var entries = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var type in VisibleTypes(assembly.GlobalNamespace)) {
            var name = type.ToDisplayString(TypeFormat);
            entries.Add($"{name} -> {Declaration(type)}");

            foreach (var relation in Relations(type, name)) {
                entries.Add(relation);
            }

            foreach (var entry in type.GetMembers().SelectMany(member => Members(type, member))) {
                entries.Add(entry);
            }
        }

        return [.. entries];
    }

    /// <summary>
    ///     Every type a consumer of the assembly can name, nested types included.
    /// </summary>
    static IEnumerable<INamedTypeSymbol> VisibleTypes(INamespaceOrTypeSymbol container) {
        foreach (var member in container.GetMembers()) {
            switch (member) {
                case INamespaceSymbol @namespace:
                    foreach (var nested in VisibleTypes(@namespace)) {
                        yield return nested;
                    }

                    break;

                case INamedTypeSymbol type when IsVisibleOutside(type) && !IsCompilerGenerated(type):
                    yield return type;

                    foreach (var nested in VisibleTypes(type)) {
                        yield return nested;
                    }

                    break;
            }
        }
    }

    /// <summary>
    ///     What kind of type this is, and the modifiers that decide what a consumer may do with it.
    /// </summary>
    /// <remarks>
    ///     Carried on the type's own line rather than left implicit. A class that becomes
    ///     <c>sealed</c>, a struct that becomes a <c>ref struct</c>, an enum whose underlying type
    ///     narrows — each is a breaking change that adds and removes no member at all, and a
    ///     baseline that cannot see them is a baseline somebody has to remember to think past.
    /// </remarks>
    static string Declaration(INamedTypeSymbol type) {
        var declaration = new StringBuilder();

        if (type.IsStatic) {
            declaration.Append("static ");
        } else if (type.TypeKind == TypeKind.Class) {
            if (type.IsAbstract) {
                declaration.Append("abstract ");
            }

            if (type.IsSealed) {
                declaration.Append("sealed ");
            }
        }

        if (type.IsReadOnly && type.TypeKind == TypeKind.Struct) {
            declaration.Append("readonly ");
        }

        if (type.IsRefLikeType) {
            declaration.Append("ref ");
        }

        declaration.Append(
            type.TypeKind switch {
                TypeKind.Class => type.IsRecord ? "record" : "class",
                TypeKind.Struct => type.IsRecord ? "record struct" : "struct",
                TypeKind.Interface => "interface",
                TypeKind.Delegate => "delegate",
                TypeKind.Enum => $"enum : {type.EnumUnderlyingType?.ToDisplayString(TypeFormat) ?? "int"}",
                _ => type.TypeKind.ToString().ToLowerInvariant()
            }
        );

        return declaration.ToString();
    }

    /// <summary>
    ///     The base type and the directly implemented interfaces, one line each.
    /// </summary>
    /// <remarks>
    ///     A line each rather than a list on the type's own line, so that implementing one more
    ///     interface is one added line rather than a rewritten one. The implicit bases —
    ///     <c>object</c>, <c>ValueType</c>, <c>Enum</c>, <c>MulticastDelegate</c> — are omitted:
    ///     they are what the type kind already says.
    /// </remarks>
    static IEnumerable<string> Relations(INamedTypeSymbol type, string name) {
        if (type.BaseType is { } baseType && !IsImplicitBase(baseType)) {
            yield return $"{name} : {baseType.ToDisplayString(TypeFormat)}";
        }

        foreach (var contract in type.Interfaces.Where(IsVisibleOutside)) {
            yield return $"{name} : {contract.ToDisplayString(TypeFormat)}";
        }
    }

    static bool IsImplicitBase(INamedTypeSymbol type) =>
        type.SpecialType is SpecialType.System_Object
            or SpecialType.System_ValueType
            or SpecialType.System_Enum
            or SpecialType.System_Delegate
            or SpecialType.System_MulticastDelegate;

    /// <summary>
    ///     The entries a member contributes — usually one, two for a property with both accessors,
    ///     none for what is not surface: accessors (written with their property or event), the
    ///     static constructor and the finalizer (which no caller can invoke), nested types (walked
    ///     separately), and the compiler's own work.
    /// </summary>
    static IEnumerable<string> Members(INamedTypeSymbol type, ISymbol member) {
        if (member is INamedTypeSymbol || !IsVisibleOutside(member) || IsCompilerGenerated(member)) {
            yield break;
        }

        switch (member) {
            case IMethodSymbol method:
                if (method.MethodKind is MethodKind.PropertyGet
                    or MethodKind.PropertySet
                    or MethodKind.EventAdd
                    or MethodKind.EventRemove
                    or MethodKind.EventRaise
                    or MethodKind.StaticConstructor
                    or MethodKind.Destructor) {
                    yield break;
                }

                // A delegate's metadata carries a constructor and the two asynchronous halves the
                // runtime no longer supports. `Invoke` is the signature the declaration means, and
                // it is the only part of the four a change can be visible in.
                if (type.TypeKind == TypeKind.Delegate && !string.Equals(method.Name, "Invoke", StringComparison.Ordinal)) {
                    yield break;
                }

                yield return $"{method.ToDisplayString(MemberFormat)} -> "
                    + (method.ReturnsVoid ? "void" : method.ReturnType.ToDisplayString(TypeFormat));

                break;

            case IPropertySymbol property:
                // Accessors are separate entries because they are separately breaking: removing a
                // setter from a property that keeps its getter removes API, and a single line for
                // the property would not show it.
                var propertyName = property.ToDisplayString(MemberFormat);
                var propertyType = property.Type.ToDisplayString(TypeFormat);

                if (property.GetMethod is { } getter && IsVisibleOutside(getter)) {
                    yield return $"{propertyName}.get -> {propertyType}";
                }

                if (property.SetMethod is { } setter && IsVisibleOutside(setter)) {
                    yield return $"{propertyName}.{(setter.IsInitOnly ? "init" : "set")} -> {propertyType}";
                }

                break;

            case IEventSymbol @event:
                yield return $"{@event.ToDisplayString(MemberFormat)} -> {@event.Type.ToDisplayString(TypeFormat)}";

                break;

            case IFieldSymbol field:
                yield return $"{field.ToDisplayString(MemberFormat)} -> {field.Type.ToDisplayString(TypeFormat)}";

                break;
        }
    }

    /// <summary>
    ///     Whether a consumer outside the assembly can see this symbol at all.
    /// </summary>
    /// <remarks>
    ///     <c>protected</c> counts: a consumer can derive. <c>internal</c> and
    ///     <c>protected internal</c>'s internal half do not, and neither does anything nested inside
    ///     something invisible — <c>InternalsVisibleTo</c> is a test seam, not a published API.
    /// </remarks>
    static bool IsVisibleOutside(ISymbol symbol) =>
        symbol.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal
        && (symbol.ContainingType is null || IsVisibleOutside(symbol.ContainingType));

    /// <summary>
    ///     Members the compiler wrote. A record's <c>EqualityContract</c>, <c>PrintMembers</c> and
    ///     clone helper are a consequence of the <c>record</c> keyword, which the type's own line
    ///     already records; listing them would put three lines of noise under every record and
    ///     would change them whenever the compiler changed its mind.
    /// </summary>
    static bool IsCompilerGenerated(ISymbol symbol) =>
        symbol.GetAttributes().Any(attribute =>
            attribute.AttributeClass?.ToDisplayString(TypeFormat)
                is "System.Runtime.CompilerServices.CompilerGeneratedAttribute"
        );

    /// <summary>
    ///     Everything the target assembly might reference: its own output directory first, then the
    ///     runtime this tool is running on.
    /// </summary>
    /// <remarks>
    ///     Resolution is not optional here. Without the references, a base type in another assembly
    ///     reads as an error type and prints as a bare name — the surface would still be stable, but
    ///     it would be stably wrong, and the day the reference appeared every line mentioning it
    ///     would move.
    /// </remarks>
    static IReadOnlyList<MetadataReference> References(string assemblyPath, MetadataReference target) {
        var references = new List<MetadataReference> { target };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFileName(assemblyPath) };

        var directory = Path.GetDirectoryName(Path.GetFullPath(assemblyPath));

        if (directory is not null) {
            foreach (var path in Directory.EnumerateFiles(directory, "*.dll")) {
                Add(path);
            }
        }

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string platform) {
            foreach (var path in platform.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) {
                Add(path);
            }
        }

        return references;

        void Add(string path) {
            if (!seen.Add(Path.GetFileName(path))) {
                return;
            }

            try {
                references.Add(MetadataReference.CreateFromFile(path));
            } catch (BadImageFormatException) {
                // A native binary sitting beside the managed ones. Not a reference, not a problem.
            } catch (IOException) {
                // Likewise a file that went away between the enumeration and the read.
            }
        }
    }
}
